using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The staging folder defaults to the per-user data root. A settings file that still
/// holds the default of earlier versions moves to it once; a root the operator sets
/// afterwards is kept. Each test has its own data root.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class StagingRootMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-stage-root-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public StagingRootMigrationTests()
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

    private static void WriteSettings(string downloadRoot, bool? migrated = null)
    {
        var node = new JsonObject { ["SiteCode"] = "LAB", ["DownloadRoot"] = downloadRoot };
        if (migrated is not null) node["StagingRootMigrated"] = migrated;
        File.WriteAllText(SettingsService.SettingsPath, node.ToJsonString());
    }

    [Fact]
    public void ANewInstallationStagesUnderThePerUserDataRoot()
    {
        var settings = new SettingsService().Load();

        Assert.Equal(Path.Combine(AppDataPaths.Root, "Staging"), settings.DownloadRoot);
        Assert.True(AppDataPaths.IsWithin(settings.DownloadRoot, AppDataPaths.Root));
        Assert.True(settings.StagingRootMigrated);
    }

    [Theory]
    [InlineData(@"C:\temp\ap")]
    [InlineData(@"c:\TEMP\AP\")]
    public void TheEarlierDefaultMovesToThePerUserFolderOnceAndTheOperatorIsTold(string stored)
    {
        WriteSettings(stored);
        var service = new SettingsService();

        var settings = service.Load();
        service.Load();

        Assert.Equal(AppDataPaths.DefaultStagingRoot, settings.DownloadRoot);
        var notice = service.TakeLoadWarning();
        Assert.NotNull(notice);
        Assert.Contains(AppDataPaths.DefaultStagingRoot, notice);
        Assert.Null(service.TakeLoadWarning());
    }

    [Fact]
    public void TheMoveIsStoredByTheLoadSoARunThatNeverSavesDoesNotRepeatIt()
    {
        WriteSettings(AppDataPaths.LegacyStagingRoot);
        var run = new SettingsService();
        run.ApplyRunOverrides(s => s.SiteCode = "RUN");

        run.Load();

        var stored = JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!;
        Assert.Equal(AppDataPaths.DefaultStagingRoot, (string?)stored["DownloadRoot"]);
        Assert.True((bool?)stored["StagingRootMigrated"]);
        Assert.Equal("LAB", (string?)stored["SiteCode"]);
        var next = new SettingsService();
        Assert.Equal(AppDataPaths.DefaultStagingRoot, next.Load().DownloadRoot);
        Assert.Null(next.TakeLoadWarning());
    }

    [Fact]
    public void ARootTheOperatorSetsAfterTheMoveIsKept()
    {
        WriteSettings(AppDataPaths.LegacyStagingRoot, migrated: true);
        var service = new SettingsService();

        Assert.Equal(AppDataPaths.LegacyStagingRoot, service.Load().DownloadRoot);
        Assert.Null(service.TakeLoadWarning());
    }

    [Fact]
    public void AnotherStoredRootIsNotChanged()
    {
        WriteSettings(@"D:\ap");
        var service = new SettingsService();

        var settings = service.Load();

        Assert.Equal(@"D:\ap", settings.DownloadRoot);
        Assert.True(settings.StagingRootMigrated);
        Assert.Null(service.TakeLoadWarning());
    }
}
