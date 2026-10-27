using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// Add from web saves through SaveAsync: the wizard values reach the definition, a value edited in the review wins,
/// and a value nobody edited gives the JSON the Add App wizard wrote. Detection, InstallerSettings and UninstallCommand
/// are the analysis additions and are compared separately.
/// </summary>
public sealed class AddFromWebSaveTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();
    private static readonly string[] AnalysisAdditions = ["Id", "Detection", "InstallerSettings", "UninstallCommand"];

    public void Dispose() => _bed.Dispose();

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    private static AddSaveOptions Wizard(string definitions, VersionScraperConfig scraper) => new(DefinitionsFolder: definitions)
    {
        ApplicationName = "Test App",
        Vendor = "Test Co",
        CMName = "Test App CM",
        Architecture = "x86",
        VendorUrl = "https://example.invalid/testapp",
        InstallerType = "EXE",
        InstallerFramework = "NSIS",
        InstallContext = "Machine",
        DownloadUrl = "https://example.invalid/TestApp-Setup-{version}.exe",
        InstallerFileName = "TestApp-Setup-{version}.exe",
        InstallArgs = "/S /allusers",
        UninstallArgs = "/S",
        VersionScraper = scraper
    };

    /// <summary>The definition the Add App wizard built from the same inputs.</summary>
    private static AppDefinition AddAppWizardDefinition(AddSaveOptions o) => new()
    {
        Schema = 1,
        Id = "",
        Vendor = o.Vendor!,
        Application = o.ApplicationName!,
        CMName = o.CMName!,
        Architecture = o.Architecture!,
        VendorUrl = o.VendorUrl!,
        InstallerType = o.InstallerType!,
        InstallerFramework = o.InstallerFramework,
        InstallContext = o.InstallContext!,
        VersionScraper = o.VersionScraper!,
        DownloadUrl = o.DownloadUrl!,
        InstallerFileName = o.InstallerFileName,
        InstallArgs = o.InstallArgs!,
        UninstallArgs = o.UninstallArgs!,
        Detection = new DetectionConfig(),
        RunningProcess = []
    };

    private static JsonObject WithoutAdditions(AppDefinition d)
    {
        var node = JsonSerializer.SerializeToNode(d)!.AsObject();
        foreach (var name in AnalysisAdditions) node.Remove(name);
        return node;
    }

    public static TheoryData<string> ScraperTypes => new() { "HttpRegex", "JsonQuery", "FileVersion" };

    [Theory]
    [MemberData(nameof(ScraperTypes))]
    public async Task UneditedReview_GivesTheAddAppWizardDefinition(string scraperType)
    {
        var scraper = scraperType switch
        {
            "HttpRegex" => new VersionScraperConfig { Type = "HttpRegex", Url = "https://example.invalid/releases", Pattern = @"TestApp-Setup-(\d+\.\d+\.\d+)\.exe" },
            "JsonQuery" => new VersionScraperConfig { Type = "JsonQuery", Url = "https://example.invalid/api/latest", Pattern = "tag_name", VersionTransform = "^v" },
            _ => new VersionScraperConfig { Type = "FileVersion", Url = "https://example.invalid/TestApp-Setup.exe", Pattern = "" }
        };
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe"), NoProgress, CancellationToken.None);
        var options = Wizard(_bed.Definitions, scraper);

        var saved = Read((await service.SaveAsync(r, options, CancellationToken.None)).FilePath);

        Assert.Equal(WithoutAdditions(AddAppWizardDefinition(options)).ToJsonString(), WithoutAdditions(saved).ToJsonString());
        Assert.Null(saved.InstallerSettings);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", saved.Detection.RegistryKeyTemplate);
    }

    [Fact]
    public async Task ValuesEditedInTheReview_WinOverTheWizardValues()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe"), NoProgress, CancellationToken.None);
        var edited = r with
        {
            Name = new ProposedValue<string?>(Value: "Edited App", Evidence: Evidence.Manual, Source: null),
            InstallArguments = new ProposedValue<string>(Value: "/S /edited", Evidence: Evidence.Manual, Source: null),
            InstallContext = new ProposedValue<string>(Value: "User", Evidence: Evidence.Manual, Source: null)
        };
        var options = Wizard(_bed.Definitions, new VersionScraperConfig { Type = "HttpRegex", Url = "https://example.invalid/", Pattern = "x" });

        var saved = Read((await service.SaveAsync(edited, options, CancellationToken.None)).FilePath);

        Assert.Equal("Edited App", saved.Application);
        Assert.Equal("/S /edited", saved.InstallArgs);
        Assert.Equal("User", saved.InstallContext);
        Assert.Equal("x86", saved.Architecture);

        var msi = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var msiEdited = msi with
        {
            Architecture = new ProposedValue<string>(Value: "x86", Evidence: Evidence.Manual, Source: null),
            UninstallArguments = new ProposedValue<string>(Value: "/x {ProductCode} /qn", Evidence: Evidence.Manual, Source: null)
        };
        var msiSaved = Read((await service.SaveAsync(msiEdited, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None)).FilePath);
        Assert.Equal("x86", msiSaved.Architecture);
        Assert.Equal("/x {ProductCode} /qn", msiSaved.UninstallArgs);
        Assert.Equal("TestApp-Setup-{version}.exe", saved.InstallerFileName);
        Assert.Equal("https://example.invalid/testapp", saved.VendorUrl);
    }

    [Fact]
    public async Task AddFromFile_LeavesTheDownloadAndScraperUrlsEmpty_SoTheSourceIsLocalFile()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);

        var saved = Read((await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None)).FilePath);

        Assert.Equal("", saved.DownloadUrl);
        Assert.Equal("", saved.VersionScraper.Url);
        Assert.True(AddFromInstallerService.IsLocalFileDefinition(saved));
        Assert.False(AddFromInstallerService.IsLocalFileDefinition(new AppDefinition { DownloadUrl = "https://example.invalid/a.msi" }));
        Assert.False(AddFromInstallerService.IsLocalFileDefinition(new AppDefinition { VersionScraper = new VersionScraperConfig { Url = "https://example.invalid/" } }));
    }

    [Fact]
    public async Task TheArchitectureOfAnExeInstaller_IsPredicted_AndTheArchitectureOfAnMsi_IsConfirmed()
    {
        var path = _bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe");
        var fake = new ApplicationPackagerEngine.Tests.InstallerAnalysis.FakeInstallerAnalysisService();
        fake.Reports[path] = AddFromInstallerTestBed.Report(path, "NSIS", [], architecture: "x86");

        var exe = await _bed.Over(fake).AnalyzeAsync(path, NoProgress, CancellationToken.None);
        var msi = await _bed.Real().AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);

        Assert.Equal(Evidence.Predicted, exe.Architecture!.Evidence);
        Assert.Equal(Evidence.Confirmed, msi.Architecture!.Evidence);
    }

    public static TheoryData<string, string?> WebFileNames => new()
    {
        { "https://example.invalid/TestApp-Setup-{version}.exe", null },
        { "https://example.invalid/download?id=testapp", "TestApp-Setup-{version}.exe" }
    };

    [Theory]
    [MemberData(nameof(WebFileNames))]
    public async Task AWebDefinition_DoesNotKeepTheFileNameOfTheAnalysedVersion(string downloadUrl, string? typedFileName)
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup-1.2.3.exe"), NoProgress, CancellationToken.None);
        var options = Wizard(_bed.Definitions, new VersionScraperConfig { Type = "HttpRegex", Url = "https://example.invalid/", Pattern = "x" })
            with { DownloadUrl = downloadUrl, InstallerFileName = typedFileName };

        var saved = Read((await service.SaveAsync(r, options, CancellationToken.None)).FilePath);

        Assert.Equal(typedFileName, saved.InstallerFileName);
    }

    [Fact]
    public void CompoundWithNoClauses_IsTheDefault()
    {
        var empty = new DetectionProposal(Kind: DetectionKind.Compound, Description: "", Values: new Dictionary<string, string> { ["Connector"] = "And" }, Evidence: Evidence.Manual, Source: null);

        Assert.True(empty.IsDefault);
        Assert.Throws<AddFromInstallerRefusedException>(() => AddFromInstallerService.ToDetectionConfig(empty, null, null, "1.0"));
    }

    [Fact]
    public async Task CompoundWithNoClauses_IsSavedAsNoRuleAndNeedsReview()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var edited = r with { Detection = new DetectionProposal(Kind: DetectionKind.Compound, Description: "", Values: new Dictionary<string, string>(), Evidence: Evidence.Manual, Source: null) };

        var saved = await service.SaveAsync(edited, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        var def = Read(saved.FilePath);

        Assert.True(saved.NeedsReview);
        Assert.Contains(AddFromInstallerService.DefaultDetectionReason, saved.ReviewReason);
        Assert.Null(def.Detection.RegistryKeyTemplate);
        Assert.Null(def.Detection.Clauses);
    }
}
