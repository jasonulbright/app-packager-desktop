using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The package result carries the CI_ID the site keeps after the revision cleanup, a
/// file on the share that the manifest does not list refuses the run before anything is
/// written, and a deployment type with no profile carries no user interaction.
/// </summary>
public sealed class MecmPackageResultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-mecm-result-" + Guid.NewGuid().ToString("N"));

    public MecmPackageResultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static StageManifest Manifest(string? behavior = null) => new()
    {
        AppName = "Widget",
        Publisher = "Contoso",
        SoftwareVersion = "1.0",
        InstallerType = "EXE",
        InstallerFile = "setup.exe",
        InstallationBehaviorType = behavior,
        Detection = new StageDetection { Type = "File", FilePath = @"%ProgramFiles%\Widget", FileName = "widget.exe", ExpectedValue = "1.0" }
    };

    // ── CI_ID ────────────────────────────────────────────────────

    [Fact]
    public void TheScriptReReadsTheCiIdAfterTheRevisionCleanup()
    {
        var script = MecmTarget.BuildPackageScript(Manifest(), "Widget", @"\\s\c", "MCM", "", 15, 30);

        var cleanup = script.IndexOf("Get-CMApplicationRevisionHistory", StringComparison.Ordinal);
        var reread = script.IndexOf("$finalApp = @(Get-CMApplication -Name 'Widget' -DisableWildcardHandling", StringComparison.Ordinal);
        Assert.True(cleanup > 0 && reread > cleanup);
        Assert.Contains("Write-Host \"Final CI_ID: $($finalApp.CI_ID)\"", script);
    }

    [Theory]
    [InlineData("CI_ID: 16779648\r\nCleaned revision history\r\nFinal CI_ID: 16779651\r\nApplication created successfully", "16779651")]
    [InlineData("Application already exists    : Widget (v1.0, unchanged)\r\nCI_ID: 16779648", "16779648")]
    [InlineData("no id printed", "")]
    public void TheResultTakesTheFinalCiId(string output, string expected) =>
        Assert.Equal(expected, MecmTarget.ReadCiId(output));

    // ── Unexpected files on the share ────────────────────────────

    [Fact]
    public async Task AnUnexpectedFileOnTheShareRefusesTheRunBeforeAnyWrite()
    {
        var stage = Path.Combine(_dir, "stage");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "install.ps1"), "new wrapper");
        File.WriteAllText(Path.Combine(stage, "setup.exe"), "MZ payload");
        var manifest = Manifest();
        manifest.FileHashes = ManifestService.ComputeFileHashes(stage, ContentSyncService.ManifestFileName);

        var share = Path.Combine(_dir, "share");
        var network = ContentLayouts.ContentPath(share, ContentLayouts.Nested, "Contoso", "Widget", "1.0");
        Directory.CreateDirectory(network);
        File.WriteAllText(Path.Combine(network, "install.ps1"), "old wrapper");
        File.WriteAllText(Path.Combine(network, "stray-0003.txt"), "stray");
        var log = new List<string>();

        var result = await new MecmTarget().PackageAsync(manifest, stage, "MCM", share, "", onProgress: log.Add,
            onExisting: OnExistingBehavior.Overwrite);

        Assert.False(result.Success);
        Assert.Contains("unexpected file: stray-0003.txt", result.ErrorMessage);
        Assert.Contains("nothing was written to the share", result.ErrorMessage);
        Assert.Equal("old wrapper", File.ReadAllText(Path.Combine(network, "install.ps1")));
        Assert.False(File.Exists(Path.Combine(network, "setup.exe")));
        Assert.DoesNotContain(log, l => l.StartsWith("Refreshed on network", StringComparison.Ordinal));
    }

    [Fact]
    public void UnexpectedFilesIgnoreTheManifestAndAManifestWithoutHashes()
    {
        var root = Path.Combine(_dir, "net");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "setup.exe"), "x");
        File.WriteAllText(Path.Combine(root, ContentSyncService.ManifestFileName), "{}");
        File.WriteAllText(Path.Combine(root, "sub", "extra.txt"), "x");
        var manifest = Manifest();
        manifest.FileHashes = [new StageFileHash { RelativePath = "setup.exe", Sha256 = "00", Size = 1 }];

        Assert.Equal([@"sub\extra.txt"], ContentSyncService.UnexpectedFiles(root, manifest));
        Assert.Empty(ContentSyncService.UnexpectedFiles(root, Manifest()));
        Assert.Empty(ContentSyncService.UnexpectedFiles(Path.Combine(_dir, "absent"), manifest));
    }

    // ── User interaction ─────────────────────────────────────────

    [Theory]
    [InlineData("InstallForUser")]
    [InlineData("InstallForSystem")]
    public void ADeploymentTypeWithNoProfileIsHiddenAndRequiresNoInteraction(string behavior)
    {
        var script = MecmTarget.BuildPackageScript(Manifest(behavior), "Widget", @"\\s\c", "MCM", "", 15, 30);

        Assert.Contains($" -InstallationBehaviorType {behavior}", script);
        Assert.Contains(" -UserInteractionMode Hidden", script);
        Assert.DoesNotContain("-RequireUserInteraction", script);
    }
}
