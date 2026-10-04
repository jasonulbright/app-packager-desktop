using System.IO.Compression;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Beyond Compare 5 ships as a ZIP holding one Inno Setup installer, and the
/// installer registers the product from BC5Key.txt in its own folder. Stage
/// refuses to run until the operator has chosen that key file.
/// </summary>
public sealed class BeyondCompare5DefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-bc5-" + Guid.NewGuid().ToString("N"));

    public BeyondCompare5DefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void TheVersionAndZipPathComeFromTheAlternateDownloadPage()
    {
        var def = DefinitionFixture.Load("beyondcompare5.json");
        var html = "<p>Current version 5.2.5</p><a href=\"/files/BCompareSetup-5.2.5.32528.zip\">BCompareSetup-5.2.5.32528.zip</a> (27299kb)";

        Assert.Equal("https://www.scootersoftware.com/kb/dl5_winalternate", def.VersionScraper.Url);
        Assert.Equal("5.2.5.32528", HttpRegexScraper.ExtractVersion(html, def.VersionScraper.Pattern, false));
        var url = System.Text.RegularExpressions.Regex.Match(html,
            def.VersionScraper.DownloadUrlQuery!.Replace("{version}", System.Text.RegularExpressions.Regex.Escape("5.2.5.32528")));
        Assert.Equal("/files/BCompareSetup-5.2.5.32528.zip", url.Groups[1].Value);
    }

    [Fact]
    public void APageWithoutASetupZipYieldsNoVersion()
    {
        Assert.Null(HttpRegexScraper.ExtractVersion("<html>maintenance</html>",
            DefinitionFixture.Load("beyondcompare5.json").VersionScraper.Pattern, false));
    }

    [Fact]
    public void InstallIsPerMachineAndUninstallReadsTheArpEntry()
    {
        var def = DefinitionFixture.Load("beyondcompare5.json");

        Assert.Equal("/VERYSILENT /NORESTART /ALLUSERS /DISABLEUPDATES /SUPPRESSMSGBOXES", def.InstallArgs);
        Assert.Contains(@"OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BeyondCompare5_is1')", def.UninstallCommand);
        Assert.Contains("[Microsoft.Win32.RegistryView]::Registry64", def.UninstallCommand);
        Assert.Contains("@('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES')", def.UninstallCommand);
        Assert.Equal(["BCompare"], def.RunningProcess);
    }

    [Fact]
    public void DetectionReadsTheAllUsersArpKeyAgainstTheHeaderDisplayVersion()
    {
        var def = DefinitionFixture.Load("beyondcompare5.json");
        var tokens = new StageTokens { Version = "5.2.6.32774", HeaderDisplayVersion = "5.2.6.32774" };

        var staged = StagingService.BuildDetection(def, null, "5.2.6.32774", "5.2.6.32774", tokens: tokens);

        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BeyondCompare5_is1", staged.RegistryKeyRelative);
        Assert.Equal("5.2.6.32774", staged.ExpectedValue);
        Assert.Equal("GreaterEquals", staged.Operator);
        Assert.True(staged.Is64Bit);
    }

    [Fact]
    public void AnUnparseableHeaderDisplayVersionFallsBackToTheScrapedVersion()
    {
        var def = DefinitionFixture.Load("beyondcompare5.json");
        var staged = StagingService.BuildDetection(def, null, "5.2.6.32774",
            tokens: new StageTokens { Version = "5.2.6.32774", HeaderDisplayVersion = "{#AppVersion}" });

        Assert.Equal("5.2.6.32774", staged.ExpectedValue);
    }

    [Fact]
    public void TheKeyFileIsDeclaredAsASideFile()
    {
        var side = Assert.Single(DefinitionFixture.Load("beyondcompare5.json").SideFiles!);
        Assert.Equal("BeyondCompareKeyFile", side.Key);
        Assert.Equal("BC5Key.txt", side.FileName);
    }

    [Fact]
    public void NoChosenKeyFileRefusesTheStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StagingService.ResolveSideFiles(DefinitionFixture.Load("beyondcompare5.json"), null));
        Assert.Contains("requires a license key file", ex.Message);
        Assert.Contains("BC5Key.txt", ex.Message);
    }

    [Fact]
    public void AChosenKeyFileThatNoLongerExistsRefusesTheStage()
    {
        var chosen = new Dictionary<string, string> { ["BeyondCompareKeyFile"] = Path.Combine(_dir, "missing", "BC5Key.txt") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StagingService.ResolveSideFiles(DefinitionFixture.Load("beyondcompare5.json"), chosen));
        Assert.Contains("license key file not found", ex.Message);
    }

    [Fact]
    public void ADefinitionWithoutSideFilesNeedsNone()
    {
        Assert.Empty(StagingService.ResolveSideFiles(DefinitionFixture.Load("7zip.json"), null));
    }

    private static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    private static byte[] ZipWith(string entryName, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(entryName).Open();
            stream.Write(content);
        }
        return buffer.ToArray();
    }

    private static byte[] FakeExe() => [(byte)'M', (byte)'Z', .. new byte[510]];

    private AppDefinition LoopbackDefinition(LoopbackFileServer server)
    {
        var def = DefinitionFixture.Load("beyondcompare5.json");
        def.VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = server.Url("page"), Pattern = "(x)" };
        def.DownloadUrl = server.Url("files/BCompareSetup-{version}.zip");
        return def;
    }

    [Fact]
    public async Task TheStageRefusesBeforeAnyDownloadWithoutTheKeyFile()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>());

        var result = await Service().StageAsync(LoopbackDefinition(server), Path.Combine(_dir, "root"), knownVersion: "5.2.6.32774");

        Assert.False(result.Success);
        Assert.Contains("requires a license key file", result.ErrorMessage);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TheKeyFileIsPlacedBesideTheExtractedInstallerAndCoveredByTheHashes()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>
        {
            ["files/BCompareSetup-5.2.6.32774.zip"] = ZipWith("BCompareSetup.exe", FakeExe())
        });
        var key = Path.Combine(_dir, "chosen-key.txt");
        File.WriteAllText(key, "placeholder");

        var result = await Service().StageAsync(LoopbackDefinition(server), Path.Combine(_dir, "root"), knownVersion: "5.2.6.32774",
            inputs: new StageInputs { SideFiles = new Dictionary<string, string> { ["BeyondCompareKeyFile"] = key } });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("placeholder", File.ReadAllText(Path.Combine(result.ContentPath, "BC5Key.txt")));
        var manifest = new ManifestService().Read(result.ManifestPath);
        Assert.Equal("BCompareSetup.exe", manifest.InstallerFile);
        Assert.Contains(manifest.FileHashes!, h => h.RelativePath == "BC5Key.txt");
        Assert.False(File.Exists(Path.Combine(result.ContentPath, "BCompareSetup-5.2.6.32774.zip")));
    }

    [Fact]
    public async Task AZipWhoseInstallerIsNotAnExecutableIsRefused()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>
        {
            ["files/BCompareSetup-5.2.6.32774.zip"] = ZipWith("BCompareSetup.exe", "<html>not an exe</html>"u8.ToArray())
        });
        var key = Path.Combine(_dir, "key.txt");
        File.WriteAllText(key, "x");

        var result = await Service().StageAsync(LoopbackDefinition(server), Path.Combine(_dir, "root"), knownVersion: "5.2.6.32774",
            inputs: new StageInputs { SideFiles = new Dictionary<string, string> { ["BeyondCompareKeyFile"] = key } });

        Assert.False(result.Success);
        Assert.Contains("not a valid EXE", result.ErrorMessage);
    }
}
