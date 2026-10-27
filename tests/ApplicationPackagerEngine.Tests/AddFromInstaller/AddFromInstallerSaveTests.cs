using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>SaveAsync writes the reviewed proposal as a new user definition and never a rule with an empty registry key.</summary>
public sealed class AddFromInstallerSaveTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private AddSaveOptions Options => new(DefinitionsFolder: _bed.Definitions);

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    [Fact]
    public async Task Msi_SavesTheProductCodeTemplateAndTheVersionToken()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);

        var saved = await service.SaveAsync(r, Options, CancellationToken.None);
        var def = Read(saved.FilePath);

        Assert.Equal(Path.Combine(_bed.Definitions, "contosotool.json"), saved.FilePath);
        Assert.Equal(def.Id, saved.DefinitionId);
        Assert.Equal("MSI", def.InstallerType);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}", def.Detection.RegistryKeyTemplate);
        Assert.Equal(StageTokens.ProductVersionToken, def.Detection.ExpectedValue);
        Assert.Equal("HKLM", def.Detection.RegistryHive);
        Assert.False(saved.NeedsReview);
    }

    [Fact]
    public async Task Save_NeverOverwritesAnExistingDefinition()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var first = await service.SaveAsync(r, Options, CancellationToken.None);
        var before = File.ReadAllText(first.FilePath);

        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() => service.SaveAsync(r, Options, CancellationToken.None));

        Assert.Contains("already exists", ex.Message);
        Assert.Equal(before, File.ReadAllText(first.FilePath));
    }

    [Fact]
    public async Task Nsis_CopiesTheExpectedValueAsTheScriptVersionToken()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe"), NoProgress, CancellationToken.None);

        var def = Read((await service.SaveAsync(r, Options, CancellationToken.None)).FilePath);

        Assert.Equal(StageTokens.HeaderDisplayVersionToken, def.Detection.ExpectedValue);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TestApp", def.Detection.RegistryKeyTemplate);
        Assert.False(def.Detection.Is64Bit);
        Assert.Equal("/S", StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, null));
    }

    [Fact]
    public async Task EditedVersion_IsSavedAsALiteral()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var edited = r with
        {
            Version = new ProposedValue<string?>(Value: "4.5.6.1", Evidence: Evidence.Manual, Source: null),
            Detection = r.Detection with { Values = new Dictionary<string, string>(r.Detection.Values) { ["Expected"] = "4.5.6.1" } }
        };

        var def = Read((await service.SaveAsync(edited, Options, CancellationToken.None)).FilePath);

        Assert.Equal("4.5.6.1", def.Detection.ExpectedValue);
    }

    [Fact]
    public async Task UnknownInstaller_WithoutArguments_IsRefused_WithManualArguments_IsSavedWithAReviewWarning()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Marked("mystery.exe", null), NoProgress, CancellationToken.None);
        r = r with { Name = new ProposedValue<string?>(Value: "Mystery Tool", Evidence: Evidence.Manual, Source: null), Version = new ProposedValue<string?>(Value: "1.0", Evidence: Evidence.Manual, Source: null) };

        var refused = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() => service.SaveAsync(r, Options, CancellationToken.None));
        Assert.Contains(AddFromInstallerService.NoSilentSwitchReason, refused.Message);

        var manual = r with { InstallArguments = new ProposedValue<string>(Value: "/quiet", Evidence: Evidence.Manual, Source: null) };
        var saved = await service.SaveAsync(manual, Options, CancellationToken.None);
        var def = Read(saved.FilePath);

        Assert.Equal("/quiet", def.InstallArgs);
        Assert.True(saved.NeedsReview);
        Assert.Contains(saved.Warnings, w => w.StartsWith("Detection needs review", StringComparison.Ordinal));
        Assert.Null(def.Detection.RegistryKeyTemplate);
    }

    [Fact]
    public async Task Patch_SavesAPatchDefinitionWithThePatchCodeDetection()
    {
        const string PatchCode = "{AAAABBBB-0000-4000-8000-000000000002}";
        var path = _bed.PathOf("contoso-update.msp");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "MSP", [], metadata:
        [
            new MetadataBlock(Title: "MSI patch (contoso-update.msp)", Facts:
            [
                Fact("DisplayName", "Contoso Tool Update (4.5.7)", "Confirmed", "msi-patch-metadata"),
                Fact("Primary PatchCode", PatchCode, "Confirmed", "msi-summary-information"),
                Fact("Target ProductCodes", ProductCode, "Confirmed", "msi-summary-information"),
                Fact("Post-patch version", "4.5.7", "Predicted", "msi-patch-metadata")
            ])
        ]);
        var service = _bed.Over(fake);
        var r = await service.AnalyzeAsync(path, NoProgress, CancellationToken.None);

        var saved = await service.SaveAsync(r, Options, CancellationToken.None);
        var def = Read(saved.FilePath);

        Assert.Equal("MSP", def.InstallerType);
        Assert.Equal("Script", def.Detection.Type);
        Assert.Contains(PatchCode, def.Detection.ScriptText);
        Assert.Contains($"PatchesEx('{ProductCode}', $null, 4, 1)", def.Detection.ScriptText);
        Assert.Contains("'/uninstall', '" + PatchCode + "'", def.UninstallCommand);
        Assert.Contains(saved.Warnings, w => w.Contains("base product", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Msix_SavesAnMsixDefinitionWithTheProvisioningRule()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msix(), NoProgress, CancellationToken.None);

        var saved = await service.SaveAsync(r, Options, CancellationToken.None);
        var def = Read(saved.FilePath);

        Assert.Equal("MSIX", def.InstallerType);
        Assert.Equal("x64", def.Architecture);
        Assert.Equal("RegistryKey", def.Detection.Type);
        Assert.Contains(saved.Warnings, w => w.StartsWith("The package is not signed.", StringComparison.Ordinal));
        Assert.True(saved.NeedsReview);
    }

    [Fact]
    public async Task WebOptions_AreWrittenToTheDefinition()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var options = Options with
        {
            DownloadUrl = "https://example.invalid/contoso-{version}.msi",
            VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = "https://example.invalid/", Pattern = "v(\\d+)" },
            CMName = "Contoso"
        };

        var def = Read((await service.SaveAsync(r, options, CancellationToken.None)).FilePath);

        Assert.Equal("https://example.invalid/contoso-{version}.msi", def.DownloadUrl);
        Assert.Equal("HttpRegex", def.VersionScraper.Type);
        Assert.Equal("Contoso", def.CMName);
    }

    [Theory]
    [InlineData(DetectionKind.RegistryVersion)]
    [InlineData(DetectionKind.RegistryExists)]
    [InlineData(DetectionKind.MsiProductCode)]
    public void RegistryRule_WithAnEmptyKey_IsNeverSaved(DetectionKind kind)
    {
        var proposal = new DetectionProposal(Kind: kind, Description: "x", Values: new Dictionary<string, string> { ["Key"] = "  ", ["Hive"] = "HKLM" }, Evidence: Evidence.Manual, Source: null);

        var ex = Assert.Throws<AddFromInstallerRefusedException>(() => AddFromInstallerService.ToDetectionConfig(proposal, null, null, "1.0"));
        Assert.Contains("no registry key", ex.Message);
    }

    [Fact]
    public void FileRules_And_Compound_BecomeDefinitionRules()
    {
        var file = new DetectionProposal(Kind: DetectionKind.FileVersion, Description: "file",
            Values: new Dictionary<string, string> { ["FilePath"] = @"%ProgramFiles%\Contoso", ["FileName"] = "contoso.exe", ["Expected"] = "4.5.6" }, Evidence: Evidence.Manual, Source: null);
        var key = new DetectionProposal(Kind: DetectionKind.RegistryExists, Description: "key",
            Values: new Dictionary<string, string> { ["Hive"] = "HKCU", ["Key"] = @"Software\Contoso", ["Is64Bit"] = "true" }, Evidence: Evidence.Confirmed, Source: "msi-registry-table");

        var compound = AddFromInstallerService.Compound("And", file, key);
        var config = AddFromInstallerService.ToDetectionConfig(compound, null, null, "4.5.6");

        Assert.Equal(DetectionKind.Compound, compound.Kind);
        Assert.Equal(Evidence.Manual, compound.Evidence);
        Assert.Equal("Compound", config.Type);
        Assert.Equal("And", config.Connector);
        Assert.Equal("File", config.Clauses![0].Type);
        Assert.Equal("GreaterEquals", config.Clauses[0].Operator);
        Assert.Equal("Version", config.Clauses[0].PropertyType);
        Assert.Equal("RegistryKey", config.Clauses[1].Type);
        Assert.Equal("HKCU", config.Clauses[1].RegistryHive);

        var exists = AddFromInstallerService.ToDetectionConfig(file with { Kind = DetectionKind.FileExists }, null, null, "4.5.6");
        Assert.Null(exists.PropertyType);
        Assert.Null(exists.ExpectedValue);
        Assert.Throws<ArgumentException>(() => AddFromInstallerService.Compound("And", file));
    }

    [Fact]
    public void DownloadUrl_TokensResolveAsTheStageResolvesThem() =>
        Assert.Equal("https://example.invalid/app-1.2.3-build-1.2.3.4-1-2-3.exe",
            AddFromInstallerService.ResolveDownloadUrl("https://example.invalid/app-{display_version}-build-{version}-{display_version_dashed}.exe", "1.2.3", "1.2.3.4"));

    [Theory]
    [InlineData("https://example.invalid/app-{version}.exe", null, "version token")]
    [InlineData("ftp://example.invalid/app.exe", null, "not a valid http or https URL")]
    [InlineData("https://example.invalid/download", null, "names no installer file")]
    [InlineData("https://example.invalid/app.zip", null, "does not take .zip files")]
    [InlineData("https://example.invalid/latest", "app.ps1", "does not take .ps1 files")]
    public async Task Fetch_RefusesWithPlainMessages(string url, string? fileName, string expected)
    {
        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() =>
            _bed.Real().FetchInstallerAsync(new DownloadRequest(Url: url, FileName: fileName, DownloadRoot: _bed.Root), NoProgress, CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }
}
