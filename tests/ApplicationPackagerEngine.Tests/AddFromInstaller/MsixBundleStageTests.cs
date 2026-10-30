using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// A bundle has no AppxManifest.xml. Its identity is in AppxMetadata\AppxBundleManifest.xml, the bundle Identity has
/// no architecture, and Windows provisions it under the bundle full name Name_Version_neutral_~_PublisherId.
/// A failed stage leaves no folder it made in the download root.
/// </summary>
public sealed class MsixBundleStageTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();
    private static readonly IProgress<string> NoProgress = new Progress<string>();

    public void Dispose() => _bed.Dispose();

    private string DownloadRoot => Path.Combine(_bed.Root, "download");

    private static AppDefinition Read(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    private static StagingService Staging() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());

    private async Task<(AddAnalysisResult Review, AppDefinition Definition)> SavedAsync(string installer)
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(installer, NoProgress, CancellationToken.None);
        var saved = await service.SaveAsync(r, new AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        return (r, Read(saved.FilePath));
    }

    private static byte[] Signature() => MsixFixture.SignatureBlob().P7x;

    private string[] DownloadRootEntries() =>
        Directory.Exists(DownloadRoot) ? Directory.GetFileSystemEntries(DownloadRoot, "*", SearchOption.AllDirectories) : [];

    // ---- identity ----------------------------------------------------------------------------

    [Fact]
    public void ReadIdentity_OfABundle_ReadsTheBundleManifestAndTheArchitecturesOfItsApplicationPackages()
    {
        var identity = new MsixPackageReader().ReadIdentity(_bed.MsixBundle());

        Assert.True(identity.IsBundle);
        Assert.Equal("Contoso.Tool", identity.Name);
        Assert.Equal("3001.2.3.0", identity.Version);
        Assert.Equal("CN=Contoso Ltd", identity.Publisher);
        Assert.Equal("neutral", identity.ProcessorArchitecture);
        Assert.Equal("~", identity.ResourceId);
        Assert.Equal(["x64", "x86"], identity.PackageArchitectures);
    }

    [Fact]
    public void ReadIdentity_OfAPackage_IsNoBundleAndListsItsOwnArchitecture()
    {
        var identity = new MsixPackageReader().ReadIdentity(_bed.Msix(withDependency: false));

        Assert.False(identity.IsBundle);
        Assert.Equal("x64", identity.ProcessorArchitecture);
        Assert.Equal("", identity.ResourceId);
        Assert.Equal(["x64"], identity.PackageArchitectures);
    }

    [Fact]
    public void ProvisioningKey_OfABundle_IsTheBundleFullName()
    {
        var detection = StagingService.BuildMsixProvisioningKeyDetection(new StageMsixPackage
        {
            Name = "Microsoft.WindowsTerminalPreview",
            Version = "3001.26.2734.0",
            Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
            ProcessorArchitecture = "neutral",
            ResourceId = "~"
        });

        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications\Microsoft.WindowsTerminalPreview_3001.26.2734.0_neutral_~_8wekyb3d8bbwe",
            detection.RegistryKeyRelative);
    }

    // ---- review ------------------------------------------------------------------------------

    [Theory]
    [InlineData("x64,x86", "x64")]
    [InlineData("x86,x64", "x64")]
    [InlineData("x86", "x86")]
    [InlineData("arm64", "ARM64")]
    [InlineData("neutral", "x64")]
    public async Task TheReviewOfABundle_ProposesTheArchitectureOfItsApplicationPackages(string architectures, string expected)
    {
        var r = await _bed.Real().AnalyzeAsync(_bed.MsixBundle(architectures: architectures.Split(',')), NoProgress, CancellationToken.None);

        Assert.Equal(expected, r.Architecture?.Value);
    }

    // ---- stage -------------------------------------------------------------------------------

    [Fact]
    public async Task ABundle_StagesFromTheSavedInstaller_WithTheBundleProvisioningKeyAsDetection()
    {
        var (_, def) = await SavedAsync(_bed.MsixBundle(signature: Signature()));

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("x64", def.Architecture);
        var manifest = new ManifestService().Read(result.ManifestPath!);
        Assert.Equal("Contoso.Tool", manifest.MsixPackage!.Name);
        Assert.Equal("3001.2.3.0", manifest.MsixPackage.Version);
        Assert.Equal(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications\Contoso.Tool_3001.2.3.0_neutral_~_{MsixPackageReader.PublisherHash("CN=Contoso Ltd")}",
            manifest.Detection.RegistryKeyRelative);
        Assert.True(File.Exists(Path.Combine(result.ContentPath!, "install.ps1")));
        Assert.True(File.Exists(Path.Combine(result.ContentPath!, "uninstall.ps1")));
    }

    [Fact]
    public async Task ABundleWithNoApplicationPackageOfTheDefinitionArchitecture_IsRefused()
    {
        var (_, def) = await SavedAsync(_bed.MsixBundle(architectures: ["x64"], signature: Signature()));
        def.Architecture = "ARM64";

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains("ARM64", result.ErrorMessage);
        Assert.Contains("x64", result.ErrorMessage);
    }

    // ---- a failed stage leaves no partial folder ----------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedStage_LeavesNoFolderInTheDownloadRoot(bool bundle)
    {
        var (_, def) = await SavedAsync(bundle ? _bed.MsixBundle() : _bed.Msix(withDependency: false));

        var result = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains("carries no signature", result.ErrorMessage);
        Assert.Empty(DownloadRootEntries());
    }

    [Fact]
    public async Task AStageThatReturnsAFailure_LeavesNoFolderInTheDownloadRoot()
    {
        var installer = _bed.PathOf("tool.exe");
        File.WriteAllBytes(installer, [(byte)'M', (byte)'Z', 1, 2]);
        var manifest = new StageManifest
        {
            AppName = "Tool", DisplayName = "Tool", Publisher = "Contoso", SoftwareVersion = "1.0",
            InstallerFile = "tool.exe", InstallerType = "EXE", InstallArgs = "/S"
        };

        var result = await Staging().StageDroppedInstallerAsync(manifest, installer, DownloadRoot,
            expected: new ExpectedInstaller(Sha256: new string('0', 64), Source: "the saved revision"));

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.ErrorMessage);
        Assert.Empty(DownloadRootEntries());
    }

    [Fact]
    public async Task AFailedStage_KeepsAnotherVersionThatAConcurrentStageWritesUnderTheSameApplicationFolder()
    {
        var (_, def) = await SavedAsync(_bed.Msix(withDependency: false));
        var appFolder = Path.Combine(DownloadRoot, $"{def.Application.Replace(" ", "")}-{def.Architecture}");
        var other = Path.Combine(appFolder, "9.9.9.9", "stage-manifest.json");

        var result = await Staging().StageAsync(def, DownloadRoot, onProgress: line =>
        {
            if (!line.StartsWith("Reading MSIX package identity", StringComparison.Ordinal)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(other, "{}");
        });

        Assert.False(result.Success);
        Assert.True(File.Exists(other));
        Assert.False(Directory.Exists(Path.Combine(appFolder, "2.3.4.0")));
    }

    [Fact]
    public async Task AFailedStage_KeepsAVersionFolderThatHoldsAStageManifest()
    {
        var (_, def) = await SavedAsync(_bed.Msix(withDependency: false));
        var manifest = Path.Combine(DownloadRoot, $"{def.Application.Replace(" ", "")}-{def.Architecture}", "2.3.4.0", "stage-manifest.json");

        var result = await Staging().StageAsync(def, DownloadRoot, onProgress: line =>
        {
            if (line.StartsWith("Reading MSIX package identity", StringComparison.Ordinal)) File.WriteAllText(manifest, "{}");
        });

        Assert.False(result.Success);
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public async Task AFailedStageOfAVersionStagedBefore_KeepsTheEarlierStage()
    {
        var msi = _bed.Msi();
        var (_, def) = await SavedAsync(msi);
        var first = await Staging().StageAsync(def, DownloadRoot);
        Assert.True(first.Success, first.ErrorMessage);
        File.AppendAllText(msi, "changed");

        var second = await Staging().StageAsync(def, DownloadRoot);

        Assert.False(second.Success);
        Assert.True(File.Exists(first.ManifestPath));
        Assert.True(File.Exists(Path.Combine(first.ContentPath!, "install.ps1")));
    }
}
