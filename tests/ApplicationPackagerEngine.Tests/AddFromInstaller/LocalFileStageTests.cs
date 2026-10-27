using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// A definition that Add from file saved has no download source. It stages from the installer the analysis read:
/// the stage copies that file, checks its SHA-256 and takes the reviewed version, and no version source runs.
/// </summary>
public sealed class LocalFileStageTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private string DownloadRoot => Path.Combine(_bed.Root, "download");

    private AddSaveOptions Options => new(DefinitionsFolder: _bed.Definitions);

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    /// <summary>The definitions have no scraper URL and no download URL, so a version or download request finds nothing.</summary>
    private static StagingService Staging() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());

    private async Task<(AppDefinition Definition, string Installer)> SavedAsync(string installer, AddFromInstallerService? service = null)
    {
        service ??= _bed.Real();
        var r = await service.AnalyzeAsync(installer, NoProgress, CancellationToken.None);
        var saved = await service.SaveAsync(r, Options, CancellationToken.None);
        return (Read(saved.FilePath), installer);
    }

    private string Patch()
    {
        const string patchCode = "{AAAABBBB-0000-4000-8000-000000000002}";
        var path = _bed.PathOf("contoso-update.msp");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        return path;
    }

    private AddFromInstallerService PatchService(string path)
    {
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "MSP", [], metadata:
        [
            new MetadataBlock(Title: "MSI patch (contoso-update.msp)", Facts:
            [
                Fact("DisplayName", "Contoso Tool Update (4.5.7)", "Confirmed", "msi-patch-metadata"),
                Fact("Primary PatchCode", "{AAAABBBB-0000-4000-8000-000000000002}", "Confirmed", "msi-summary-information"),
                Fact("Target ProductCodes", ProductCode, "Confirmed", "msi-summary-information"),
                Fact("Post-patch version", "4.5.7", "Predicted", "msi-patch-metadata")
            ])
        ]);
        return _bed.Over(fake);
    }

    [Fact]
    public async Task TheSavedDefinition_RecordsThePathTheHashAndTheVersionOfTheAnalysedInstaller()
    {
        var (def, msi) = await SavedAsync(_bed.Msi());

        Assert.True(AddFromInstallerService.IsLocalFileDefinition(def));
        Assert.NotNull(def.LocalInstaller);
        Assert.Equal(Path.GetFullPath(msi), def.LocalInstaller!.Path);
        Assert.Equal(StagingService.ComputeSha256(msi), def.LocalInstaller.Sha256, ignoreCase: true);
        Assert.Equal("4.5.6", def.LocalInstaller.Version);
    }

    [Fact]
    public async Task AnMsi_StagesFromTheSavedInstaller_WithNoVersionSourceAndNoDownload()
    {
        var (def, msi) = await SavedAsync(_bed.Msi());

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FromLocalSource);
        Assert.Equal("4.5.6", Path.GetFileName(result.ContentPath));
        var manifest = new ManifestService().Read(result.ManifestPath!);
        Assert.Equal("MSI", manifest.InstallerType);
        Assert.Equal("contoso.msi", manifest.InstallerFile);
        Assert.Equal(ProductCode, manifest.ProductCode);
        Assert.Equal(File.ReadAllBytes(msi), File.ReadAllBytes(Path.Combine(result.ContentPath!, "contoso.msi")));
        Assert.Contains("msiexec.exe", File.ReadAllText(Path.Combine(result.ContentPath!, "install.ps1")));
    }

    [Fact]
    public async Task AnNsisExe_StagesFromTheSavedInstaller()
    {
        var (def, _) = await SavedAsync(_bed.Fixture("nsis-u-pf-hklm-wow.bin", "TestApp-Setup.exe"));

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(def.LocalInstaller!.Version, Path.GetFileName(result.ContentPath));
        Assert.True(File.Exists(Path.Combine(result.ContentPath!, "TestApp-Setup.exe")));
        Assert.Contains("TestApp-Setup.exe", File.ReadAllText(Path.Combine(result.ContentPath!, "install.ps1")));
    }

    [Fact]
    public async Task AnMsp_StagesFromTheSavedInstaller()
    {
        var path = Patch();
        var (def, _) = await SavedAsync(path, PatchService(path));

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("4.5.7", Path.GetFileName(result.ContentPath));
        Assert.True(File.Exists(Path.Combine(result.ContentPath!, "contoso-update.msp")));
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("neutral")]
    public async Task AnMsix_ReachesTheSignatureCheckOfTheStage_NotTheVersionSource(string architecture)
    {
        var (def, _) = await SavedAsync(_bed.Msix(withDependency: false, architecture: architecture));

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains("carries no signature", result.ErrorMessage);
    }

    [Fact]
    public async Task CheckLatest_ReturnsTheSavedVersion_WithNoVersionSource()
    {
        var (def, _) = await SavedAsync(_bed.Msi());

        Assert.Equal("4.5.6", await Staging().GetLatestVersionAsync(def));
    }

    [Fact]
    public async Task AnInstallerThatChangedAfterTheSave_IsRefused_AndNothingIsStaged()
    {
        var (def, msi) = await SavedAsync(_bed.Msi());
        File.AppendAllText(msi, "changed");

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.ErrorMessage);
        Assert.Contains(msi, result.ErrorMessage);
        Assert.Empty(Directory.Exists(DownloadRoot) ? Directory.GetFiles(DownloadRoot, "*.msi", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task AnInstallerThatIsGone_IsRefusedWithItsPath()
    {
        var (def, msi) = await SavedAsync(_bed.Msi());
        File.Delete(msi);

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains(msi, result.ErrorMessage);
        Assert.Contains("Add from file", result.ErrorMessage);
    }

    [Fact]
    public async Task ALocalFileDefinitionWithNoSavedInstaller_SaysWhatToDo_NotThatNoVersionWasFound()
    {
        var (def, _) = await SavedAsync(_bed.Msi());
        def.LocalInstaller = null;

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.DoesNotContain("Could not determine latest version", result.ErrorMessage);
        Assert.Contains("Add from file", result.ErrorMessage);
    }

    [Fact]
    public async Task ASavedInstallerWithANonAsciiFileName_IsRefused()
    {
        var (def, _) = await SavedAsync(_bed.Msi("contoso-ü.msi"));

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains("ASCII", result.ErrorMessage);
    }

    [Fact]
    public async Task AnInstallerThatChangedBetweenTheAnalysisAndTheSave_IsNotSaved()
    {
        var service = _bed.Real();
        var msi = _bed.Msi();
        var r = await service.AnalyzeAsync(msi, NoProgress, CancellationToken.None);
        File.AppendAllText(msi, "changed");

        var ex = await Assert.ThrowsAsync<AddFromInstallerRefusedException>(() => service.SaveAsync(r, Options, CancellationToken.None));

        Assert.Contains("changed after the analysis", ex.Message);
        Assert.False(Directory.Exists(_bed.Definitions) && Directory.GetFiles(_bed.Definitions).Length > 0);
    }

    [Fact]
    public async Task ALocalSourceFolderDefinitionWithNoDownloadUrl_StagesFromTheFolder_NotFromASavedInstaller()
    {
        var folder = Path.Combine(_bed.Root, "folder");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(object).Assembly.Location, Path.Combine(folder, "Widget-setup.exe"));
        var def = new AppDefinition
        {
            Id = "localsourcetest", Vendor = "Contoso", Application = "Widget", Architecture = "x64", InstallerType = "EXE", InstallArgs = "/silent",
            LocalSource = new LocalSourceConfig { Mode = "Required", Filter = "Widget*.exe" },
            Detection = new DetectionConfig
            {
                Type = "RegistryKeyValue", RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Widget", PropertyType = "Version", Operator = "GreaterEquals"
            }
        };

        var result = await Staging().StageAsync(def, DownloadRoot, knownVersion: "1.0.0", inputs: new StageInputs { LocalSourceFolder = folder });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.FromLocalSource);
    }

    [Fact]
    public async Task AnAddFromWebDefinition_RecordsNoLocalInstaller()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), NoProgress, CancellationToken.None);
        var options = Options with
        {
            DownloadUrl = "https://example.invalid/contoso-{version}.msi",
            VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = "https://example.invalid/", Pattern = "v(\\d+)" }
        };

        var def = Read((await service.SaveAsync(r, options, CancellationToken.None)).FilePath);

        Assert.Null(def.LocalInstaller);
    }
}
