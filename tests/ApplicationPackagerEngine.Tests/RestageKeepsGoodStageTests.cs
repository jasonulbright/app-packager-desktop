using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A stage run over a version folder that already holds a good stage must leave that stage as it was
/// when the run fails. The launchers, the generated scripts and the manifest come back.
/// </summary>
public sealed class RestageKeepsGoodStageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-restage-" + Guid.NewGuid().ToString("N"));

    public RestageKeepsGoodStageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string GoodStage()
    {
        var version = Path.Combine(_root, "Tool-x64", "1.0");
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "install.ps1"), "old install");
        File.WriteAllText(Path.Combine(version, "uninstall.ps1"), "old uninstall");
        File.WriteAllText(Path.Combine(version, "install.bat"), "old bat");
        File.WriteAllText(Path.Combine(version, "uninstall.bat"), "old bat");
        File.WriteAllText(Path.Combine(version, "Tool-generated.ps1"), "old generated");
        File.WriteAllText(Path.Combine(version, "stage-manifest.json"), "old manifest");
        File.WriteAllText(Path.Combine(version, "tool.exe"), "installer");
        return version;
    }

    private static void ChangeLikeARun(string version)
    {
        foreach (var name in new[] { "install.ps1", "uninstall.ps1", "install.bat", "uninstall.bat", "Tool-generated.ps1" })
            File.Delete(Path.Combine(version, name));
        File.WriteAllText(Path.Combine(version, "stage-manifest.json"), "half written");
    }

    [Fact]
    public void AFailedRunOverAGoodStage_RestoresTheLaunchersTheGeneratedScriptsAndTheManifest()
    {
        var version = GoodStage();
        var partial = new StagingService.PartialStage();
        partial.Track(_root, version);
        partial.WritesManifest = true;

        ChangeLikeARun(version);
        partial.RemoveUnlessCompleted(null);

        Assert.Equal("old install", File.ReadAllText(Path.Combine(version, "install.ps1")));
        Assert.Equal("old uninstall", File.ReadAllText(Path.Combine(version, "uninstall.ps1")));
        Assert.Equal("old bat", File.ReadAllText(Path.Combine(version, "install.bat")));
        Assert.Equal("old bat", File.ReadAllText(Path.Combine(version, "uninstall.bat")));
        Assert.Equal("old generated", File.ReadAllText(Path.Combine(version, "Tool-generated.ps1")));
        Assert.Equal("old manifest", File.ReadAllText(Path.Combine(version, "stage-manifest.json")));
        Assert.Equal("installer", File.ReadAllText(Path.Combine(version, "tool.exe")));
    }

    [Fact]
    public void ACompletedRunOverAGoodStage_KeepsTheNewFilesAndLeavesNoBackup()
    {
        var version = GoodStage();
        var before = BackupFolders();
        var partial = new StagingService.PartialStage();
        partial.Track(_root, version);

        ChangeLikeARun(version);
        File.WriteAllText(Path.Combine(version, "install.ps1"), "new install");
        partial.Completed = true;
        partial.RemoveUnlessCompleted(null);

        Assert.Equal(before, BackupFolders());

        Assert.Equal("new install", File.ReadAllText(Path.Combine(version, "install.ps1")));
        Assert.Equal("half written", File.ReadAllText(Path.Combine(version, "stage-manifest.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Tool-x64"), "*", SearchOption.TopDirectoryOnly)
            .Where(d => !string.Equals(d, version, StringComparison.OrdinalIgnoreCase)));
        Assert.Single(Directory.GetDirectories(_root));
    }

    // The backup folders of the temp path, sorted, so a test sees only what its own run adds.
    private static string[] BackupFolders() =>
        Directory.GetDirectories(Path.GetTempPath(), "apd-restage-*").Order().ToArray();

    [Fact]
    public void AFailedRunWhoseSnapshotCouldNotBeMade_SaysThatTheEarlierStageIsNotKept()
    {
        var version = GoodStage();
        var messages = new List<string>();
        // A file another process holds open cannot be copied, so the snapshot fails.
        using var held = new FileStream(Path.Combine(version, "install.ps1"), FileMode.Open, FileAccess.Read, FileShare.None);
        var partial = new StagingService.PartialStage();
        partial.Track(_root, version);

        partial.RemoveUnlessCompleted(messages.Add);

        Assert.Contains(messages, m => m.Contains("could not be kept"));
    }

    [Fact]
    public void AFailedRunOverAFreshFolder_StillRemovesTheFolder()
    {
        var version = Path.Combine(_root, "Tool-x64", "2.0");
        var partial = new StagingService.PartialStage();
        partial.Track(_root, version);
        Directory.CreateDirectory(version);
        File.WriteAllText(Path.Combine(version, "tool.exe"), "partial");
        partial.WritesManifest = true;

        partial.RemoveUnlessCompleted(null);

        Assert.False(Directory.Exists(version));
    }
}
