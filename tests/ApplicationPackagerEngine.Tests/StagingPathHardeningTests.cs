using System.Diagnostics;
using System.Security.Cryptography;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The staged installer is the verified local source byte for byte, a side file never
/// takes the place of the payload or a generated file, and a version or file name
/// reaches the stage folder only as one plain path segment.
/// </summary>
public sealed class StagingPathHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-stagepaths-" + Guid.NewGuid().ToString("N"));

    public StagingPathHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    private static AppDefinition Definition(params SideFileConfig[] sideFiles) => new()
    {
        Id = "stagepathtest",
        Vendor = "Contoso",
        Application = "Widget (x64)",
        Architecture = "x64",
        InstallerType = "EXE",
        InstallArgs = "/silent",
        VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = "http://127.0.0.1:9/never", Pattern = "(x)" },
        DownloadUrl = "http://127.0.0.1:9/never.exe",
        InstallerFileName = "Widget-{version}.exe",
        LocalSource = new LocalSourceConfig { Mode = "Required", Filter = "Widget*.exe" },
        SideFiles = sideFiles,
        Detection = new DetectionConfig
        {
            Type = "RegistryKeyValue",
            RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Widget",
            PropertyType = "Version",
            Operator = "GreaterEquals"
        }
    };

    private (string Source, string Version) SignedSource()
    {
        var signed = typeof(object).Assembly.Location;
        var folder = Folder("source");
        var source = Path.Combine(folder, "Widget-setup.exe");
        File.Copy(signed, source, overwrite: true);
        return (source, FileVersionInfo.GetVersionInfo(signed).FileVersion!.Trim());
    }

    // ── Local installer ─────────────────────────────────────────

    [Fact]
    public async Task AFileAlreadyAtTheStagedNameIsReplacedByTheVerifiedSource()
    {
        var (source, version) = SignedSource();
        var root = Folder("root");
        var stale = Path.Combine(root, "Widget(x64)-x64", version, $"Widget-{version}.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), stale);
        var logs = new List<string>();

        var result = await Service().StageAsync(Definition(), root, logs.Add, knownVersion: version,
            inputs: new StageInputs { LocalSourceFolder = Path.GetDirectoryName(source) });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(source)), SHA256.HashData(File.ReadAllBytes(stale)));
        Assert.Contains(logs, l => l.StartsWith("Staged installer differed from the local source; replaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIdenticalStagedFileIsKept()
    {
        var (source, version) = SignedSource();
        var root = Folder("root");
        var staged = Path.Combine(root, "Widget(x64)-x64", version, $"Widget-{version}.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(source, staged);
        var logs = new List<string>();

        var result = await Service().StageAsync(Definition(), root, logs.Add, knownVersion: version,
            inputs: new StageInputs { LocalSourceFolder = Path.GetDirectoryName(source) });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains($"Installer already staged: Widget-{version}.exe", logs);
    }

    // ── Side files ──────────────────────────────────────────────

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("INSTALL.BAT")]
    [InlineData("stage-manifest.json")]
    [InlineData("app-icon.ico")]
    [InlineData("{installer}")]
    public async Task ASideFileNamedLikeTheInstallerOrAGeneratedFileRefusesTheStage(string name)
    {
        var (source, version) = SignedSource();
        var fileName = name.Replace("{installer}", $"Widget-{version}.exe");
        var side = Path.Combine(Folder("side"), "license.txt");
        File.WriteAllText(side, "Write-Output 'replaced'");

        var result = await Service().StageAsync(Definition(new SideFileConfig { Key = "lic", FileName = fileName, Label = "license" }),
            Folder("root"), knownVersion: version,
            inputs: new StageInputs
            {
                LocalSourceFolder = Path.GetDirectoryName(source),
                SideFiles = new Dictionary<string, string> { ["lic"] = side }
            });

        Assert.False(result.Success);
        Assert.Contains($"Side file '{fileName}'", result.ErrorMessage);
    }

    [Fact]
    public async Task TwoSideFilesWithOneNameRefuseTheStage()
    {
        var (source, version) = SignedSource();
        var side = Path.Combine(Folder("side"), "license.txt");
        File.WriteAllText(side, "x");

        var result = await Service().StageAsync(Definition(
                new SideFileConfig { Key = "a", FileName = "license.lic", Label = "license" },
                new SideFileConfig { Key = "b", FileName = "LICENSE.lic", Label = "other license" }),
            Folder("root"), knownVersion: version,
            inputs: new StageInputs
            {
                LocalSourceFolder = Path.GetDirectoryName(source),
                SideFiles = new Dictionary<string, string> { ["a"] = side, ["b"] = side }
            });

        Assert.False(result.Success);
        Assert.Contains("Side file 'LICENSE.lic'", result.ErrorMessage);
    }

    [Fact]
    public async Task ASideFileWithAPlainNameIsPlacedAndAReStageReplacesIt()
    {
        var (source, version) = SignedSource();
        var side = Path.Combine(Folder("side"), "license.txt");
        File.WriteAllText(side, "first");
        var root = Folder("root");
        StageInputs Inputs() => new()
        {
            LocalSourceFolder = Path.GetDirectoryName(source),
            SideFiles = new Dictionary<string, string> { ["lic"] = side }
        };
        var def = Definition(new SideFileConfig { Key = "lic", FileName = "license.lic", Label = "license" });

        var first = await Service().StageAsync(def, root, knownVersion: version, inputs: Inputs());
        File.WriteAllText(side, "second");
        var second = await Service().StageAsync(def, root, knownVersion: version, inputs: Inputs());

        Assert.True(first.Success, first.ErrorMessage);
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("second", File.ReadAllText(Path.Combine(second.ContentPath, "license.lic")));
    }

    // ── Path segments ───────────────────────────────────────────

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData("..")]
    [InlineData("1.0/../../x")]
    [InlineData(@"C:\Windows")]
    [InlineData("1.0.")]
    [InlineData("1.0 ")]
    [InlineData("1.0:ads")]
    [InlineData("1.0*")]
    [InlineData("")]
    public void AVersionThatIsNotOneSafeFolderNameIsRefused(string version)
    {
        Assert.Throws<InvalidOperationException>(() => StagingService.AssertVersionSegment(version));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("2026.10.02.0103")]
    [InlineData("7.0 Build 12")]
    [InlineData("1.0.0-beta+5")]
    public void AnOrdinaryVersionPasses(string version) =>
        Assert.Equal(version, StagingService.AssertVersionSegment(version));

    [Theory]
    [InlineData(@"..\setup.exe")]
    [InlineData("sub/setup.exe")]
    [InlineData("..")]
    [InlineData("setup.exe:hidden")]
    [InlineData("setup.exe.")]
    public void AnInstallerNameThatIsNotAPlainFileNameIsRefused(string name)
    {
        Assert.Throws<InvalidOperationException>(() => StagingService.AssertPlainFileName(name));
    }

    [Fact]
    public async Task AScrapedVersionThatClimbsOutOfTheApplicationFolderRefusesTheStage()
    {
        var root = Folder("root");
        var def = Definition();
        def.LocalSource = null;

        var result = await Service().StageAsync(def, root, knownVersion: @"..\..\escaped");

        Assert.False(result.Success);
        Assert.Contains(@"Version '..\..\escaped' is not usable as a folder name.", result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(_dir, "escaped")));
    }
}
