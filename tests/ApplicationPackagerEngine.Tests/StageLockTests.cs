using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Stages of one application and version run one at a time, so one stage never deletes the folder of
/// another. Stages of other applications or versions run in parallel. A lock left by a process that
/// ended does not block.
/// </summary>
public sealed class StageLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StageLockTests-" + Guid.NewGuid().ToString("N"));

    public StageLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string DownloadRoot => Path.Combine(_root, "download");

    private static StagingService Staging() =>
        new(new ScraperRegistry(), new DownloadService(), new MsiInspectionService(), new WrapperGeneratorService(), new ManifestService());

    private string Installer(string name)
    {
        var path = Path.Combine(_root, name + ".exe");
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z', 1, 2]);
        return path;
    }

    private static StageManifest Manifest(string app, string version = "1.0") => new()
    {
        AppName = app, DisplayName = app, Publisher = "Contoso", SoftwareVersion = version,
        InstallerFile = app + ".exe", InstallerType = "EXE", InstallArgs = "/S"
    };

    private string StageFolder(string app, string version = "1.0") =>
        Path.Combine(StagingRootGuard.Prepare(DownloadRoot), "Contoso", app, version);

    [Fact]
    public async Task TwoStagesOfOneApplicationAndVersion_RunOneAfterTheOther()
    {
        var folder = StageFolder("Tool");
        var installer = Installer("Tool");
        var progress = new List<string>();
        IDisposable held = await StageLock.AcquireAsync(folder, "Tool 1.0", TimeSpan.FromSeconds(5), null, CancellationToken.None);

        var stage = Staging().StageDroppedInstallerAsync(Manifest("Tool"), installer, DownloadRoot, progress.Add);
        await Task.Delay(1000);

        Assert.False(stage.IsCompleted);
        Assert.False(Directory.Exists(folder));

        held.Dispose();
        var result = await stage;

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(folder, "stage-manifest.json")));
        Assert.Contains(progress, line => line.Contains("Another stage of Tool 1.0 is running"));
    }

    [Fact]
    public async Task ConcurrentStagesOfOneVersion_OneFailing_NeverDeleteTheFolderOfTheOther()
    {
        var folder = StageFolder("Tool");
        var installer = Installer("Tool");
        var wrongHash = new ExpectedInstaller(Sha256: new string('0', 64), Source: "the saved revision");

        for (var round = 0; round < 5; round++)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            var good = Task.Run(() => Staging().StageDroppedInstallerAsync(Manifest("Tool"), installer, DownloadRoot));
            var bad = Task.Run(() => Staging().StageDroppedInstallerAsync(Manifest("Tool"), installer, DownloadRoot, expected: wrongHash));
            var results = await Task.WhenAll(good, bad);

            Assert.True(results[0].Success, results[0].ErrorMessage);
            Assert.False(results[1].Success);
            Assert.True(File.Exists(Path.Combine(folder, "stage-manifest.json")));
            Assert.True(File.Exists(Path.Combine(folder, "Tool.exe")));
        }
    }

    [Fact]
    public async Task AStageThatWaitsTooLong_RefusesWithAMessageAndTouchesNothing()
    {
        var folder = StageFolder("Tool");
        var installer = Installer("Tool");
        using var held = await StageLock.AcquireAsync(folder, "Tool 1.0", TimeSpan.FromSeconds(5), null, CancellationToken.None);
        var staging = Staging();
        staging.StageLockTimeout = TimeSpan.FromMilliseconds(600);

        var result = await staging.StageDroppedInstallerAsync(Manifest("Tool"), installer, DownloadRoot);

        Assert.False(result.Success);
        Assert.Contains("Another stage of Tool 1.0", result.ErrorMessage);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task StagesOfDifferentApplicationsOrVersions_RunInParallel()
    {
        var installer = Installer("Other");
        using var held = await StageLock.AcquireAsync(StageFolder("Tool"), "Tool 1.0", TimeSpan.FromSeconds(5), null, CancellationToken.None);

        var otherApplication = await Staging().StageDroppedInstallerAsync(Manifest("Other"), installer, DownloadRoot);
        var otherVersion = await Staging().StageDroppedInstallerAsync(Manifest("Tool", "2.0"), Installer("Tool"), DownloadRoot);

        Assert.True(otherApplication.Success, otherApplication.ErrorMessage);
        Assert.True(otherVersion.Success, otherVersion.ErrorMessage);
    }

    [Fact]
    public async Task ALockFileLeftByAnEndedProcess_DoesNotBlock()
    {
        var folder = StageFolder("Tool");
        Directory.CreateDirectory(StageLock.LockFolder);
        await File.WriteAllTextAsync(StageLock.LockPath(folder), "left by a process that ended");

        var result = await Staging().StageDroppedInstallerAsync(Manifest("Tool"), Installer("Tool"), DownloadRoot);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task TheLock_IsFreeAgainAfterDispose_AndLeavesNoFile()
    {
        var folder = StageFolder("Tool");

        (await StageLock.AcquireAsync(folder, "Tool 1.0", TimeSpan.FromSeconds(2), null, CancellationToken.None)).Dispose();
        using var again = await StageLock.AcquireAsync(folder, "Tool 1.0", TimeSpan.FromSeconds(2), null, CancellationToken.None);

        Assert.NotNull(again);
        again.Dispose();
        Assert.False(File.Exists(StageLock.LockPath(folder)));
    }
}
