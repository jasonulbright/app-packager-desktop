using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The settings file holds the One Click history and refusal memory: a file that cannot be
/// read is never overwritten by an automatic save, an operator save keeps a copy of it first,
/// writes replace the file atomically, a save keeps entries the grid does not list, and the
/// stored deployment target keeps every target name. Each test has its own data root.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-settings-durable-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public SettingsDurabilityTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Directory.CreateDirectory(AppDataPaths.Root);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private const string Unreadable = """{ "AppStates": { "package-a": { "Published": { "WSUS": { "Version": "1.0" """;

    [Fact]
    public void AnUnreadableFileIsNotOverwrittenByAnAutomaticSave()
    {
        File.WriteAllText(SettingsService.SettingsPath, Unreadable);
        var service = new SettingsService();

        var s = service.Load();
        Assert.True(service.IsFileUnreadable);
        s.AppStates["package-b"] = new AppCheckState { LatestVersion = "2.0" };
        service.Save(s);

        Assert.Equal(Unreadable, File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public void TheOperatorIsToldOnceWithThePath()
    {
        File.WriteAllText(SettingsService.SettingsPath, Unreadable);
        var service = new SettingsService();

        service.Load();
        service.Load();

        var warning = service.TakeLoadWarning();
        Assert.NotNull(warning);
        Assert.Contains(SettingsService.SettingsPath, warning);
        Assert.Null(service.TakeLoadWarning());
    }

    [Fact]
    public void AnOperatorSaveKeepsTheUnreadableFileBeforeReplacingIt()
    {
        File.WriteAllText(SettingsService.SettingsPath, Unreadable);
        var service = new SettingsService();
        var s = service.Load();
        s.DownloadRoot = @"C:\dl";

        service.SaveOperatorEdit(s);

        var kept = Assert.Single(Directory.GetFiles(AppDataPaths.Root, "settings.json.unreadable-*"));
        Assert.Equal(Unreadable, File.ReadAllText(kept));
        Assert.Equal(@"C:\dl", new SettingsService().Load().DownloadRoot);
        Assert.False(service.IsFileUnreadable);
    }

    [Fact]
    public void AWriteReplacesTheFileAndLeavesNoSideFile()
    {
        var service = new SettingsService();
        var s = service.Load();
        s.DownloadRoot = @"C:\one";
        service.Save(s);
        s.DownloadRoot = @"C:\two";
        service.Save(s);

        Assert.Equal(@"C:\two", (string?)JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!["DownloadRoot"]);
        Assert.Equal(["settings.json"], Directory.GetFiles(AppDataPaths.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ALoadDuringAnotherInstancesSavesNeverReturnsFirstRunDefaults()
    {
        var writer = new SettingsService();
        var s = writer.Load();
        s.DownloadRoot = @"C:\kept";
        s.FirstRunCompleted = true;
        writer.SaveOperatorEdit(s);

        var reader = new SettingsService();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var saving = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                s.M365Channel = "channel-" + n++;
                writer.Save(s);
            }
        });
        var defaults = 0;
        var loads = 0;
        while (!saving.IsCompleted)
        {
            var loaded = reader.Load();
            loads++;
            if (loaded.DownloadRoot != @"C:\kept" || reader.IsFileUnreadable) defaults++;
        }
        await saving;

        Assert.True(loads > 0);
        Assert.Equal(0, defaults);
    }

    [Fact]
    public void AMissingFileWithoutASideFileThatReappearsIsWaitedFor()
    {
        // The file appears on the second attempt of the load itself, so the load sees the name absent first and present later, with
        // one short sleep between the two (the quiet period of a load without a side file is 100 ms).
        SettingsService.ReadAttemptHook = attempt =>
        {
            if (attempt == 2)
                File.WriteAllText(SettingsService.SettingsPath, """{ "DownloadRoot": "C:\\new", "FirstRunCompleted": true }""");
        };
        try
        {
            var loaded = new SettingsService().Load();

            Assert.Equal(@"C:\new", loaded.DownloadRoot);
        }
        finally { SettingsService.ReadAttemptHook = null; }
    }

    [Fact]
    public void AMissingFileWithAReplaceSideFilePresentIsWaitedFor()
    {
        var backup = Path.Combine(AppDataPaths.Root, "settings.json." + Guid.NewGuid().ToString("N") + ".bak");
        File.WriteAllText(backup, """{ "DownloadRoot": "C:\\old" }""");
        // The file arrives complete, by a rename, as a replace delivers it: a reader can
        // open a file that is written in place before it has its text.
        var incoming = Path.Combine(AppDataPaths.Root, "incoming-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(incoming, """{ "DownloadRoot": "C:\\new", "FirstRunCompleted": true }""");
        // The file arrives on the load's third attempt, as a replace delivers it, with the side file still in place until then. The
        // second attempt is held for longer than the quiet period of a load without a side file, so only the side file keeps this
        // load waiting: without it the load returns first-run defaults after the second attempt. Time can only pass, never run short.
        SettingsService.ReadAttemptHook = attempt =>
        {
            if (attempt == 2) Thread.Sleep(250);
            if (attempt != 3) return;
            File.Move(incoming, SettingsService.SettingsPath);
            File.Delete(backup);
        };
        try
        {
            var loaded = new SettingsService().Load();

            Assert.Equal(@"C:\new", loaded.DownloadRoot);
        }
        finally { SettingsService.ReadAttemptHook = null; }
    }

    [Fact]
    public void AGenuineFirstRunLoadsDefaultsWithoutWaiting()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var loaded = new SettingsService().Load();
        watch.Stop();

        Assert.False(loaded.FirstRunCompleted);
        Assert.True(watch.Elapsed < SettingsService.MissingFileWait, $"first-run load took {watch.Elapsed}");
    }

    [Fact]
    public void AnUnreadableFileStillTakesTheRunOverrides()
    {
        File.WriteAllText(SettingsService.SettingsPath, Unreadable);
        var service = new SettingsService();
        service.ApplyRunOverrides(s => s.DownloadRoot = @"D:\override");

        var loaded = service.Load();

        Assert.Equal(@"D:\override", loaded.DownloadRoot);
        Assert.True(service.IsFileUnreadable);
        Assert.NotNull(service.TakeLoadWarning());
    }

    [Fact]
    public void AnInstanceThatReadsTheFileOncePerRunKeepsTheFirstRead()
    {
        File.WriteAllText(SettingsService.SettingsPath, """{ "DownloadRoot": "C:\\first", "FirstRunCompleted": true }""");
        var service = new SettingsService();
        service.ReadFileOncePerRun();
        Assert.Equal(@"C:\first", service.Load().DownloadRoot);

        File.WriteAllText(SettingsService.SettingsPath, """{ "DownloadRoot": "C:\\second", "FirstRunCompleted": true }""");
        var again = service.Load();

        Assert.Equal(@"C:\first", again.DownloadRoot);
        Assert.NotSame(service.Load(), again);
    }

    [Fact]
    public async Task ASaveKeepsTheFileNamePresentWhileAReaderHoldsIt()
    {
        var service = new SettingsService();
        var s = service.Load();
        s.DownloadRoot = @"C:\one";
        service.Save(s);

        var release = Task.Run(async () =>
        {
            using var held = new FileStream(SettingsService.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await Task.Delay(300);
        });
        await Task.Delay(50);
        s.DownloadRoot = @"C:\two";
        service.Save(s);
        await release;

        Assert.Equal(@"C:\two", new SettingsService().Load().DownloadRoot);
        Assert.Equal(["settings.json"], Directory.GetFiles(AppDataPaths.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void ASaveWithAnEmptyGridKeepsTheTrackedListAndTheCadenceOverrides()
    {
        var stored = new OneClickSettings
        {
            TrackedApplications = ["package-a", "package-b"],
            CadenceOverrides = new Dictionary<string, int> { ["package-a"] = 3 }
        };

        var (tracked, cadence) = OneClickSettingsMap.TrackedAndCadence([], 7, stored);

        Assert.Equal(["package-a", "package-b"], tracked);
        Assert.Equal(3, cadence["package-a"]);
    }

    [Fact]
    public void AListedRowDecidesItsOwnTrackingAndCadence()
    {
        var stored = new OneClickSettings
        {
            TrackedApplications = ["package-a", "package-hidden"],
            CadenceOverrides = new Dictionary<string, int> { ["package-a"] = 3, ["package-hidden"] = 14 }
        };

        var (tracked, cadence) = OneClickSettingsMap.TrackedAndCadence([("package-a", false, null), ("package-c", true, 5)], 7, stored);

        Assert.Equal(["package-c", "package-hidden"], tracked);
        Assert.Equal(new Dictionary<string, int> { ["package-c"] = 5, ["package-hidden"] = 14 }, cadence);
    }

    [Theory]
    [InlineData("MECMAndWSUS")]
    [InlineData("WSUSOnly")]
    [InlineData("IntuneOnly")]
    [InlineData("MECMAndIntune")]
    public void TheStoredTargetKeepsEveryTargetNameOnLoadAndSave(string target)
    {
        File.WriteAllText(SettingsService.SettingsPath, $$"""{ "Intune": { "DeploymentTarget": "{{target}}" } }""");
        var service = new SettingsService();
        var s = service.Load();
        Assert.Equal(target, s.Intune.DeploymentTarget);

        service.Save(s);
        Assert.Equal(target, (string?)JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!["Intune"]!["DeploymentTarget"]);
    }

    [Fact]
    public void TheTargetNamesMeanWhatUpstreamMeans()
    {
        Assert.Equal(DeploymentTargets.WsusOnly, DeploymentTargets.Normalize("WSUSOnly"));
        Assert.True(DeploymentTargets.SkipsMecm("WSUSOnly"));
        Assert.False(DeploymentTargets.PublishesToIntune("MECMAndWSUS"));
        Assert.True(DeploymentTargets.PublishesToWsus("MECMAndWSUS"));
        Assert.Equal(DeploymentTargets.Mecm, DeploymentTargets.Normalize("nonsense"));
    }
}
