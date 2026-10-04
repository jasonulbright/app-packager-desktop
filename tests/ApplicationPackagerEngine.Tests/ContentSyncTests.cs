using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Content already on the share is not necessarily current. Re-staging the same
/// SoftwareVersion after editing a wrapper writes a file of the same name with a
/// different body, so a copy that skips existing destinations leaves the old
/// wrapper deployed. These tests drive the production synchronization against
/// temporary local directories.
/// </summary>
public sealed class ContentSyncTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "packr-sync-" + Guid.NewGuid().ToString("N"));

    private string Local => Path.Combine(_root, "stage");
    private string Network => Path.Combine(_root, "share");

    public ContentSyncTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(Network);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private void WriteLocal(string relativePath, string content)
    {
        var path = Path.Combine(Local, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteNetwork(string relativePath, string content)
    {
        var path = Path.Combine(Network, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadNetwork(string relativePath) =>
        File.ReadAllText(Path.Combine(Network, relativePath));

    /// <summary>A manifest whose hashes describe the local stage as it stands.</summary>
    private StageManifest ManifestForLocal() => new()
    {
        AppName = "Contoso App",
        SoftwareVersion = "1.0.0",
        FileHashes = ManifestService.ComputeFileHashes(Local, ContentSyncService.ManifestFileName)
    };

    [Fact]
    public void AChangedWrapperAtTheSameVersionIsRefreshed()
    {
        WriteLocal("install.ps1", "Start-Process -FilePath 'setup.exe' -ArgumentList '/S'");
        WriteNetwork("install.ps1", "Start-Process -FilePath 'setup.exe'");
        var manifest = ManifestForLocal();

        var result = ContentSyncService.Sync(Local, Network, manifest);

        Assert.Equal("Start-Process -FilePath 'setup.exe' -ArgumentList '/S'", ReadNetwork("install.ps1"));
        Assert.Equal(["install.ps1"], result.Refreshed);
        Assert.Empty(result.Copied);
        Assert.Empty(result.Unchanged);
    }

    [Fact]
    public void AnIdenticalFileIsLeftUntouched()
    {
        WriteLocal("payload.exe", "same bytes");
        WriteNetwork("payload.exe", "same bytes");
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(Network, "payload.exe"));
        var manifest = ManifestForLocal();

        var result = ContentSyncService.Sync(Local, Network, manifest);

        Assert.Equal(["payload.exe"], result.Unchanged);
        Assert.Empty(result.Refreshed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(Network, "payload.exe")));
    }

    [Fact]
    public void AMissingNestedFileIsCopiedWithItsDirectory()
    {
        WriteLocal(Path.Combine("Files", "config", "settings.xml"), "<config />");
        var manifest = ManifestForLocal();

        var result = ContentSyncService.Sync(Local, Network, manifest);

        Assert.Equal("<config />", ReadNetwork(Path.Combine("Files", "config", "settings.xml")));
        Assert.Equal([Path.Combine("Files", "config", "settings.xml")], result.Copied);
    }

    [Fact]
    public void TheRootStageManifestIsNeverCopied()
    {
        WriteLocal("install.ps1", "exit 0");
        WriteLocal(ContentSyncService.ManifestFileName, "{}");
        var manifest = ManifestForLocal();

        var result = ContentSyncService.Sync(Local, Network, manifest);

        Assert.False(File.Exists(Path.Combine(Network, ContentSyncService.ManifestFileName)));
        Assert.DoesNotContain(ContentSyncService.ManifestFileName, result.Copied);
        Assert.Equal(["install.ps1"], result.Copied);
    }

    /// <summary>
    /// A pre-v3 stage carries no FileHashes. The comparison falls back to hashing the
    /// staged file, so re-staging still refreshes a changed wrapper.
    /// </summary>
    [Fact]
    public void ALegacyManifestWithNoHashesStillRefreshes()
    {
        WriteLocal("install.ps1", "new body");
        WriteNetwork("install.ps1", "old body");
        var legacy = new StageManifest { AppName = "Legacy", SoftwareVersion = "1.0.0", FileHashes = null };

        var result = ContentSyncService.Sync(Local, Network, legacy);

        Assert.True(result.UsedSourceHashes);
        Assert.Equal("new body", ReadNetwork("install.ps1"));
        Assert.Equal(["install.ps1"], result.Refreshed);
    }

    [Fact]
    public void ALegacyManifestLeavesAnIdenticalFileAlone()
    {
        WriteLocal("install.ps1", "same");
        WriteNetwork("install.ps1", "same");
        var legacy = new StageManifest { AppName = "Legacy", SoftwareVersion = "1.0.0" };

        var result = ContentSyncService.Sync(Local, Network, legacy);

        Assert.True(result.UsedSourceHashes);
        Assert.Equal(["install.ps1"], result.Unchanged);
    }

    /// <summary>A legacy stage has nothing to verify against, so verification passes.</summary>
    [Fact]
    public void ALegacyManifestVerifiesAsAPass()
    {
        WriteNetwork("install.ps1", "anything");

        Assert.Empty(ContentSyncService.Verify(Network, new StageManifest()));
    }

    [Fact]
    public void AChangedLocalPayloadIsRejectedAgainstTheManifest()
    {
        WriteLocal("payload.exe", "staged bytes");
        var manifest = ManifestForLocal();
        WriteLocal("payload.exe", "tampered bytes");

        var problems = ContentSyncService.Verify(Local, manifest);

        Assert.Contains(problems, p => p.Contains("hash mismatch") && p.Contains("payload.exe"));
    }

    [Fact]
    public void AMissingLocalPayloadIsRejectedAgainstTheManifest()
    {
        WriteLocal("payload.exe", "staged bytes");
        var manifest = ManifestForLocal();
        File.Delete(Path.Combine(Local, "payload.exe"));

        var problems = ContentSyncService.Verify(Local, manifest);

        Assert.Contains(problems, p => p.Contains("missing") && p.Contains("payload.exe"));
    }

    [Fact]
    public void AnUnexpectedDestinationFileIsReportedAndLeftInPlace()
    {
        WriteLocal("install.ps1", "exit 0");
        var manifest = ManifestForLocal();
        ContentSyncService.Sync(Local, Network, manifest);
        WriteNetwork("leftover.ps1", "from an older package");

        var problems = ContentSyncService.Verify(Network, manifest);

        Assert.Contains(problems, p => p.Contains("unexpected file") && p.Contains("leftover.ps1"));
        Assert.True(File.Exists(Path.Combine(Network, "leftover.ps1")));
    }

    [Fact]
    public void ASyncedTreeVerifiesAgainstItsManifest()
    {
        WriteLocal("install.ps1", "exit 0");
        WriteLocal(Path.Combine("Files", "payload.exe"), "bytes");
        var manifest = ManifestForLocal();

        ContentSyncService.Sync(Local, Network, manifest);

        Assert.Empty(ContentSyncService.Verify(Network, manifest));
    }

    [Fact]
    public void ACancelledSyncStopsRatherThanContinuing()
    {
        WriteLocal("a.txt", "one");
        WriteLocal("b.txt", "two");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ContentSyncService.Sync(Local, Network, ManifestForLocal(), null, cts.Token));
    }

    [Fact]
    public void AMissingLocalStageIsReportedRatherThanCreatingAnEmptyShare()
    {
        var absent = Path.Combine(_root, "not-staged");

        Assert.Throws<DirectoryNotFoundException>(
            () => ContentSyncService.Sync(absent, Network, null));
    }
}
