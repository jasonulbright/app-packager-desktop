using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Content integrity gates sit between the title probe and the CM write. Both cases
/// here return before any PowerShell runs, so they assert the ordering without a site.
/// </summary>
public sealed class MecmContentSyncOrderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "packr-mecm-" + Guid.NewGuid().ToString("N"));

    private string Local => Path.Combine(_root, "stage");
    private string Server => Path.Combine(_root, "server");

    public MecmContentSyncOrderTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Server);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private StageManifest Manifest() => new()
    {
        AppName = "Contoso App",
        Publisher = "Contoso",
        SoftwareVersion = "1.0.0",
        InstallerFile = "setup.exe",
        FileHashes = ManifestService.ComputeFileHashes(Local, ContentSyncService.ManifestFileName)
    };

    private string ExpectedContentPath() =>
        ContentLayouts.ContentPath(Server, ContentLayouts.Nested, "Contoso", "Contoso App", "1.0.0");

    private Task<PackagingResult> Package(StageManifest manifest) =>
        new MecmTarget().PackageAsync(
            manifest, Local, "P01", Server, "comment",
            onExisting: OnExistingBehavior.Overwrite);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationPropagatesWithoutBecomingAPackagingFailure(bool cancelledBeforeStart)
    {
        File.WriteAllText(Path.Combine(Local, "install.ps1"), "exit 0");
        using var cancellation = new CancellationTokenSource();
        if (cancelledBeforeStart) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MecmTarget().PackageAsync(
            Manifest(), Local, "P01", Server, "comment", onExisting: OnExistingBehavior.Overwrite,
            ct: cancellation.Token, onProgress: line =>
            {
                if (line.StartsWith("Network content path:", StringComparison.Ordinal)) cancellation.Cancel();
            }));

        Assert.False(File.Exists(Path.Combine(ExpectedContentPath(), "install.ps1")));
        if (cancelledBeforeStart) Assert.False(Directory.Exists(ExpectedContentPath()));
    }

    /// <summary>
    /// A stage that no longer matches its own manifest is refused before the share is
    /// written, so a corrupted stage cannot half-populate the content folder.
    /// </summary>
    [Fact]
    public async Task AStageThatDoesNotMatchItsManifestIsRefusedBeforeAnyCopy()
    {
        File.WriteAllText(Path.Combine(Local, "install.ps1"), "exit 0");
        var manifest = Manifest();
        File.WriteAllText(Path.Combine(Local, "install.ps1"), "tampered");

        var result = await Package(manifest);

        Assert.False(result.Success);
        Assert.Contains("does not match its manifest", result.ErrorMessage);
        Assert.Contains("install.ps1", result.ErrorMessage);
        Assert.False(Directory.Exists(ExpectedContentPath()));
    }

    /// <summary>
    /// An unexpected file on the share stops the run before the copy and before the
    /// application is created, rather than being deployed.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedFileOnTheShareStopsTheRunBeforeTheCmWrite()
    {
        File.WriteAllText(Path.Combine(Local, "install.ps1"), "exit 0");
        var manifest = Manifest();

        var contentPath = ExpectedContentPath();
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(Path.Combine(contentPath, "leftover.ps1"), "from an older package");

        var result = await Package(manifest);

        Assert.False(result.Success);
        Assert.Contains("unexpected file: leftover.ps1", result.ErrorMessage);
        // Reported, never removed.
        Assert.True(File.Exists(Path.Combine(contentPath, "leftover.ps1")));
    }

    /// <summary>A refusal the verification would reach is decided before the first write to the share.</summary>
    [Fact]
    public async Task TheShareIsNotWrittenWhenAnUnexpectedFileWouldFailVerification()
    {
        File.WriteAllText(Path.Combine(Local, "install.ps1"), "new body");
        var manifest = Manifest();

        var contentPath = ExpectedContentPath();
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(Path.Combine(contentPath, "install.ps1"), "old body");
        File.WriteAllText(Path.Combine(contentPath, "leftover.ps1"), "unexpected");

        var result = await Package(manifest);

        Assert.False(result.Success);
        Assert.Equal("old body", File.ReadAllText(Path.Combine(contentPath, "install.ps1")));
    }
}
