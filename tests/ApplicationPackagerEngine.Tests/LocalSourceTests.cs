using System.Diagnostics;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A local-source definition stages the highest file version in the chosen
/// folder, refuses an installer without a valid signature, and with Mode
/// Required never downloads.
/// </summary>
public sealed class LocalSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-localsource-" + Guid.NewGuid().ToString("N"));

    public LocalSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly LocalSourceConfig Widget = new() { Mode = "Required", Filter = "Widget*.exe" };

    [Fact]
    public void NoFolderIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve("", Widget, "Widget"));
        Assert.Contains("No installer source folder is set for Widget", ex.Message);
        Assert.Contains(LocalSourceResolver.SettingsLocation, ex.Message);
    }

    [Fact]
    public void AMissingFolderIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve(Path.Combine(_dir, "missing"), Widget, "Widget"));
        Assert.Contains("folder for Widget not found", ex.Message);
    }

    [Fact]
    public void AFolderWithoutAMatchingInstallerIsRefused()
    {
        var folder = Folder("empty");
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "x");
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve(folder, Widget, "Widget"));
        Assert.Contains("No installer matching 'Widget*.exe'", ex.Message);
    }

    [Fact]
    public void AFileVersionTieIsBrokenByWriteTime()
    {
        var folder = Folder("sources");
        var old = Path.Combine(folder, "Widget-1.0.exe");
        var current = Path.Combine(folder, "Widget-1.1.exe");
        File.WriteAllText(old, "x");
        File.WriteAllText(current, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

        Assert.Equal(current, LocalSourceResolver.Resolve(folder, Widget, "Widget"));
    }

    [Fact]
    public void TheHigherFileVersionWinsOverTheNewerWriteTime()
    {
        var sources = Directory.GetFiles(Environment.SystemDirectory, "*.exe")
            .Select(f => (Path: f, Version: LocalSourceResolver.RankVersion(f)))
            .Where(f => f.Version > new Version(0, 0))
            .GroupBy(f => f.Version).Select(g => g.First())
            .OrderBy(f => f.Version)
            .ToList();
        var (low, high) = (sources[0], sources[^1]);
        Assert.True(low.Version < high.Version);

        var folder = Folder("versions");
        var a = Path.Combine(folder, "Widget-a.exe");
        var b = Path.Combine(folder, "Widget-b.exe");
        File.Copy(high.Path, a);
        File.Copy(low.Path, b);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow);

        Assert.Equal(a, LocalSourceResolver.Resolve(folder, Widget, "Widget"));
    }

    [Fact]
    public void AnExcludedFileIsSkipped()
    {
        var folder = Folder("exclude");
        File.WriteAllText(Path.Combine(folder, "Widget.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "Widget_x64.exe"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "Widget.exe"), DateTime.UtcNow.AddDays(-2));

        var config = new LocalSourceConfig { Filter = "Widget*.exe", Exclude = ["*_x64*"] };
        Assert.Equal(Path.Combine(folder, "Widget.exe"), LocalSourceResolver.Resolve(folder, config, "Widget"));
    }

    [Fact]
    public void EveryMatchExcludedIsRefused()
    {
        var folder = Folder("allexcluded");
        File.WriteAllText(Path.Combine(folder, "Widget_x64.exe"), "x");

        var config = new LocalSourceConfig { Filter = "Widget*.exe", Exclude = ["*_x64*"] };
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.Resolve(folder, config, "Widget"));
        Assert.Contains("No installer matching", ex.Message);
    }

    [Fact]
    public void ANonNumericFileVersionIsRefused()
    {
        var path = Path.Combine(Folder("plain"), "Widget.exe");
        File.WriteAllText(path, "x");
        var ex = Assert.Throws<InvalidOperationException>(() => LocalSourceResolver.ReadVersion(path));
        Assert.Contains("no numeric file version", ex.Message);
    }

    // ── Staging ─────────────────────────────────────────────────

    private static StagingService Service() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(),
            new WrapperGeneratorService(), new ManifestService());

    private static AppDefinition Definition(string mode) => new()
    {
        Id = "localsourcetest",
        Vendor = "Contoso",
        Application = "Widget (x64)",
        Architecture = "x64",
        InstallerType = "EXE",
        InstallArgs = "/silent",
        VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = "http://127.0.0.1:9/never", Pattern = "(x)" },
        DownloadUrl = "http://127.0.0.1:9/never.exe",
        InstallerFileName = "Widget-{version}.exe",
        LocalSource = new LocalSourceConfig { Mode = mode, Filter = "Widget*.exe" },
        Detection = new DetectionConfig
        {
            Type = "RegistryKeyValue",
            RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Widget",
            PropertyType = "Version",
            Operator = "GreaterEquals"
        }
    };

    [Fact]
    public async Task ARequiredSourceWithoutAFolderRefusesTheStage()
    {
        var result = await Service().StageAsync(Definition("Required"), Folder("root"), knownVersion: "1.0.0");

        Assert.False(result.Success);
        Assert.Contains("No installer source folder is set for Widget (x64)", result.ErrorMessage);
    }

    [Fact]
    public async Task AnUnsignedLocalInstallerIsRefused()
    {
        var folder = Folder("unsigned");
        File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), Path.Combine(folder, "Widget-1.exe"));

        var result = await Service().StageAsync(Definition("Required"), Folder("root"), knownVersion: "1.0.0",
            inputs: new StageInputs { LocalSourceFolder = folder });

        Assert.False(result.Success);
        Assert.Contains("Installer signature is", result.ErrorMessage);
        Assert.Contains("not Valid", result.ErrorMessage);
    }

    [Fact]
    public async Task ASignedLocalInstallerStagesUnderItsOwnFileVersionWithoutADownload()
    {
        var signed = typeof(object).Assembly.Location;
        Assert.Equal(0, AuthenticodeVerifier.Verify(signed));
        var fileVersion = FileVersionInfo.GetVersionInfo(signed).FileVersion!.Trim();
        var folder = Folder("signed");
        File.Copy(signed, Path.Combine(folder, "Widget-setup.exe"));
        var logs = new List<string>();

        var result = await Service().StageAsync(Definition("Optional"), Folder("root"), logs.Add, knownVersion: "999.0.0",
            inputs: new StageInputs { LocalSourceFolder = folder });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(fileVersion, Path.GetFileName(result.ContentPath));
        Assert.True(File.Exists(Path.Combine(result.ContentPath, $"Widget-{fileVersion}.exe")));
        Assert.Contains(logs, l => l.Contains("is older than the published 999.0.0"));
        Assert.DoesNotContain(logs, l => l.StartsWith("Downloading", StringComparison.Ordinal));

        var manifest = new ManifestService().Read(result.ManifestPath);
        Assert.Equal(fileVersion, manifest.SoftwareVersion);
        Assert.Equal(fileVersion, manifest.Detection.ExpectedValue);
    }
}
