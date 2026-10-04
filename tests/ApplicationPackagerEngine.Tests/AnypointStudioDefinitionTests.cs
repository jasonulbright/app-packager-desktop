using System.IO.Compression;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Anypoint Studio has no installer: the ZIP is the product. The install
/// wrapper extracts it to C:\AnypointStudio, detection checks the versioned
/// Studio feature folder read from the ZIP, and the icon comes from the
/// launcher inside the ZIP.
/// </summary>
public sealed class AnypointStudioDefinitionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-anypoint-" + Guid.NewGuid().ToString("N"));

    public AnypointStudioDefinitionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private const string Manifest = """
        [
          { "id": 0, "name": "studio", "version": "latest", "os": "linux", "source": "https://www.mulesoft.com/downloads/studio/latest/AnypointStudio-7.28.0-linux64.tar.gz", "integrity": "3a97a7da3cfd1f354b2db069f034f0e6bae10e09c47e64e82e4d06cb66f71669", "packaging": "tar" },
          { "id": 1, "name": "studio", "version": "latest", "os": "windows", "source": "https://www.mulesoft.com/downloads/studio/latest/AnypointStudio-7.28.0-win64.zip", "integrity": "bec2ef0a0d1c0f60276e9bb0683d7131ad8cb5bb9b277c5bfa94ec43115f40ac", "packaging": "zip" },
          { "id": 4, "name": "studio", "version": "previous", "os": "windows", "source": "https://www.mulesoft.com/downloads/studio/previous/AnypointStudio-7.20.1-win64.zip", "integrity": "cd09b75448183d1a2f755b6dc38d0a4fd91a9d922b4a57aa75b53f5acd95f062", "packaging": "zip" },
          { "id": 6, "name": "mule", "version": "latest", "source": "https://www.mulesoft.com/downloads/mule/latest/mule-ee-distribution-standalone-4.12.3.zip", "integrity": "ace17d7b2313abb9e386b2b2695f475a78145a2168ad0564c2cfa6ccf500cd2b", "packaging": "zip" }
        ]
        """;

    private static string? Query(string json, string query) =>
        StagingService.ExtractFeedHash(json, new VersionScraperConfig { Type = "JsonQuery", PayloadHashQuery = query }, null);

    private static string? Transform(string raw, string transform) =>
        typeof(ScraperRegistry).GetMethod("ApplyTransform",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [raw, transform]) as string;

    [Fact]
    public void TheLatestWindowsStudioEntryGivesTheVersionSourceAndSha256()
    {
        var config = DefinitionFixture.Load("anypointstudio.json").VersionScraper;
        var source = Query(Manifest, config.Pattern);
        Assert.Equal("https://www.mulesoft.com/downloads/studio/latest/AnypointStudio-7.28.0-win64.zip", source);
        Assert.Equal(source, Query(Manifest, config.DownloadUrlQuery!));
        Assert.Equal("7.28.0", Transform(source!, config.VersionTransform!));
        Assert.Equal("bec2ef0a0d1c0f60276e9bb0683d7131ad8cb5bb9b277c5bfa94ec43115f40ac", StagingService.ExtractFeedHash(Manifest, config, source));
    }

    [Fact]
    public void AManifestWithoutALatestWindowsEntryYieldsNoVersion()
    {
        var config = DefinitionFixture.Load("anypointstudio.json").VersionScraper;
        Assert.Null(Query("""[{ "name": "studio", "version": "previous", "os": "windows", "source": "https://x/AnypointStudio-7.20.1-win64.zip", "integrity": "00" }]""", config.Pattern));
    }

    [Fact]
    public void EveryRequestCarriesTheBrowserUserAgentAndTheDownloadPageReferer()
    {
        var def = DefinitionFixture.Load("anypointstudio.json");
        Assert.Contains(def.VersionScraper.RequestHeaders!, h => h.StartsWith("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)", StringComparison.Ordinal));
        Assert.Contains("Referer: https://www.mulesoft.com/lp/dl/anypoint-mule-studio", def.VersionScraper.RequestHeaders!);
        Assert.True(def.RequirePayloadHash);
    }

    [Fact]
    public void DetectionChecksTheStudioFeatureFolderReadFromTheZip()
    {
        var def = DefinitionFixture.Load("anypointstudio.json");
        var tokens = new StageTokens
        {
            Version = "7.28.0",
            Zip = new Dictionary<string, string> { ["StudioBuild"] = "7.28.0.202608121221" }
        };

        var staged = StagingService.BuildDetection(def, null, "7.28.0", tokens: tokens);

        Assert.Equal("File", staged.Type);
        Assert.Equal(@"C:\AnypointStudio\features\org.mule.tooling.studio_7.28.0.202608121221", staged.FilePath);
        Assert.Equal("feature.xml", staged.FileName);
        Assert.Equal("Existence", staged.PropertyType);
        Assert.True(staged.Is64Bit);
    }

    private string NewZip(string name, params (string Entry, byte[] Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(content);
        }
        return path;
    }

    private static readonly byte[] X = "x"u8.ToArray();

    [Fact]
    public void TheFullBuildIsReadFromTheStudioFeatureFolder()
    {
        var zip = NewZip("ok.zip",
            ("AnypointStudio/AnypointStudio.exe", X),
            ("AnypointStudio/features/org.mule.tooling.studio_7.25.0.202605141228/feature.xml", X),
            ("AnypointStudio/features/org.mule.tooling.p2_7.25.0.202605141228/feature.xml", X));

        var tokens = ZipPayload.Inspect(zip, DefinitionFixture.Load("anypointstudio.json").ZipPayload!);

        Assert.Equal("7.25.0.202605141228", tokens["StudioBuild"]);
    }

    [Fact]
    public void AZipWithoutTheLauncherIsRefused()
    {
        var zip = NewZip("noexe.zip", ("AnypointStudio/features/org.mule.tooling.studio_7.25.0.1/feature.xml", X));
        var ex = Assert.Throws<InvalidOperationException>(() => ZipPayload.Inspect(zip, DefinitionFixture.Load("anypointstudio.json").ZipPayload!));
        Assert.Contains("AnypointStudio.exe", ex.Message);
    }

    [Fact]
    public void AZipWithoutTheStudioFeatureIsRefused()
    {
        var zip = NewZip("nofeature.zip", ("AnypointStudio/AnypointStudio.exe", X));
        var ex = Assert.Throws<InvalidOperationException>(() => ZipPayload.Inspect(zip, DefinitionFixture.Load("anypointstudio.json").ZipPayload!));
        Assert.Contains("StudioBuild", ex.Message);
    }

    [Fact]
    public void AZipWithAnEntryOutsideTheInstallFolderIsRefused()
    {
        var zip = NewZip("stray.zip",
            ("AnypointStudio/AnypointStudio.exe", X),
            ("AnypointStudio/features/org.mule.tooling.studio_7.25.0.1/feature.xml", X),
            ("readme.txt", X));
        var ex = Assert.Throws<InvalidOperationException>(() => ZipPayload.Inspect(zip, DefinitionFixture.Load("anypointstudio.json").ZipPayload!));
        Assert.Contains("readme.txt", ex.Message);
    }

    [Fact]
    public void InstallReplacesTheFolderAndUninstallRemovesIt()
    {
        var output = Path.Combine(_dir, "wrappers");
        Directory.CreateDirectory(output);

        ZipPayload.GenerateWrappers(new WrapperGeneratorService(), output, "AnypointStudio-7.28.0-win64.zip", DefinitionFixture.Load("anypointstudio.json").ZipPayload!);

        Assert.Equal(string.Join("\r\n",
            "$ErrorActionPreference = 'Stop'",
            "$zipPath = Join-Path $PSScriptRoot 'AnypointStudio-7.28.0-win64.zip'",
            @"$installDir = 'C:\AnypointStudio'",
            "try {",
            "    Add-Type -AssemblyName System.IO.Compression.FileSystem",
            @"    $parent = [System.IO.Path]::GetFullPath((Split-Path -Path $installDir -Parent)).TrimEnd('\') + '\'",
            @"    $root = [System.IO.Path]::GetFullPath($installDir).TrimEnd('\') + '\'",
            "    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)",
            "    try {",
            "        foreach ($entry in $zip.Entries) {",
            @"            $name = $entry.FullName.Replace('/', '\')",
            "            $full = $null",
            @"            if (-not ($name.Contains(':') -or $name.StartsWith('\') -or ($name.Split('\') -contains '..'))) {",
            "                $full = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($parent, $name))",
            "            }",
            "            if ($null -eq $full -or -not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "                [Console]::Error.WriteLine(\"ZIP entry '$($entry.FullName)' is outside $installDir; nothing was changed.\")",
            "                exit 1",
            "            }",
            "        }",
            "    }",
            "    finally { $zip.Dispose() }",
            "    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }",
            "    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, (Split-Path -Path $installDir -Parent))",
            "    exit 0",
            "}",
            "catch {",
            "    Write-Error $_.Exception.Message",
            "    exit 1",
            "}"), File.ReadAllText(Path.Combine(output, "install.ps1")));
        var uninstall = File.ReadAllText(Path.Combine(output, "uninstall.ps1"));
        Assert.Contains(@"$installDir = 'C:\AnypointStudio'", uninstall);
        Assert.Contains("Remove-Item -LiteralPath $installDir -Recurse -Force", uninstall);
        Assert.DoesNotContain("ExtractToDirectory", uninstall);
        Assert.True(File.Exists(Path.Combine(output, "install.bat")));
    }

    [Fact]
    public void TheIconComesFromTheLauncherInsideTheZip()
    {
        var stage = Path.Combine(_dir, "stage");
        Directory.CreateDirectory(stage);
        var launcher = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        using (var zip = ZipFile.Open(Path.Combine(stage, "AnypointStudio-7.28.0-win64.zip"), ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("AnypointStudio/AnypointStudio.exe").Open();
            stream.Write(launcher);
        }
        var def = DefinitionFixture.Load("anypointstudio.json");
        def.SourcePath = Path.Combine(_dir, "Definitions", "anypointstudio.json");
        var manifest = new StageManifest { InstallerFile = "AnypointStudio-7.28.0-win64.zip" };

        IconExtractionService.StageIcon(def, stage, manifest);

        Assert.Equal("app-icon.ico", manifest.Icon);
        Assert.True(File.Exists(Path.Combine(stage, "app-icon.ico")));
    }

    private static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    [Fact]
    public async Task TheZipStagesWholeWithItsWrappersAndTokenisedDetection()
    {
        var zipBytes = File.ReadAllBytes(NewZip("payload.zip",
            ("AnypointStudio/AnypointStudio.exe", X),
            ("AnypointStudio/features/org.mule.tooling.studio_7.28.0.202608121221/feature.xml", X)));
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["AnypointStudio-7.28.0-win64.zip"] = zipBytes });
        var def = DefinitionFixture.Load("anypointstudio.json");
        def.VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = server.Url("page"), Pattern = "(x)" };
        def.DownloadUrl = server.Url("AnypointStudio-{version}-win64.zip");
        def.PayloadSha256 = StagingService.ComputeSha256(Path.Combine(_dir, "payload.zip"));

        var result = await Service().StageAsync(def, Path.Combine(_dir, "root"), knownVersion: "7.28.0");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(result.ContentPath, "AnypointStudio-7.28.0-win64.zip")));
        Assert.Contains("ExtractToDirectory", File.ReadAllText(Path.Combine(result.ContentPath, "install.ps1")));
        var manifest = new ManifestService().Read(result.ManifestPath);
        Assert.Equal(@"C:\AnypointStudio\features\org.mule.tooling.studio_7.28.0.202608121221", manifest.Detection.FilePath);
        Assert.Equal("AnypointStudio-7.28.0-win64.zip", manifest.InstallerFile);
    }

    [Fact]
    public async Task APayloadWithoutAPublishedHashIsRefusedWhenTheDefinitionRequiresOne()
    {
        var zipBytes = File.ReadAllBytes(NewZip("payload.zip", ("AnypointStudio/AnypointStudio.exe", X)));
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]> { ["AnypointStudio-7.28.0-win64.zip"] = zipBytes });
        var def = DefinitionFixture.Load("anypointstudio.json");
        def.VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = server.Url("page"), Pattern = "(x)" };
        def.DownloadUrl = server.Url("AnypointStudio-{version}-win64.zip");

        var result = await Service().StageAsync(def, Path.Combine(_dir, "root"), knownVersion: "7.28.0");

        Assert.False(result.Success);
        Assert.Contains("carries no SHA256", result.ErrorMessage);
    }
}
