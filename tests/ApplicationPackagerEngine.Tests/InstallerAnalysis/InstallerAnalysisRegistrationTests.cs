using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class InstallerAnalysisRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ia-reg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public InstallerAnalysisRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        System.Data.SQLite.SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheApplicationServicesBuildOnePageViewModelThatReadsNothing()
    {
        using var services = new ServiceCollection().AddPackagerServices().AddSidebar().AddInstallerAnalysisPage()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
        var viewModel = services.GetRequiredService<InstallerAnalysisViewModel>();
        Assert.Same(viewModel, services.GetRequiredService<InstallerAnalysisViewModel>());
        Assert.NotNull(services.GetRequiredService<IInstallerAnalysisService>());
        Assert.NotNull(services.GetRequiredService<IInstallerReportWriter>());
        Assert.Contains(services.GetServices<IHostedService>(), s => s is InstallerAnalysisSandboxLifetime);
        Assert.False(viewModel.HasReport);
        Assert.False(File.Exists(SettingsService.SettingsPath));
        Assert.False(Directory.Exists(Path.Combine(AppDataPaths.Root, "InstallerAnalysis")));
    }

    [Fact]
    public void ThePageUsesTheContractLimitsAndTheRootOfTheRegisteredSandboxAndNothingCleansAtConstruction()
    {
        using var services = new ServiceCollection().AddPackagerServices().AddSidebar().AddInstallerAnalysisPage().BuildServiceProvider();
        var page = services.GetRequiredService<InstallerAnalysisPageServices>();
        var sandbox = services.GetRequiredService<IAnalysisSandbox>();
        Assert.Same(AnalysisLimits.Default, page.Limits);
        Assert.Same(sandbox, page.Sandbox);
        Assert.Equal(sandbox.Root, page.SandboxRoot());
        Assert.Equal(InstallerAnalysisFolders.Temp(), page.SandboxRoot());
        Assert.False(Directory.Exists(Path.Combine(AppDataPaths.Root, "InstallerAnalysis")));
    }

    [Fact]
    public async Task TheSandboxLifetimeRemovesOldSessionsAtStartAndEverySessionAtExit()
    {
        var sandbox = new FakeAnalysisSandbox(_root);
        var abandoned = Directory.CreateDirectory(Path.Combine(_root, "20260101-000000-0123abcd")).FullName;
        InstallerAnalysisSessions.WriteOwner(abandoned, processId: 4242, startTicks: 1);
        var log = new RecordingAnalysisLog();
        var lifetime = new InstallerAnalysisSandboxLifetime(sandbox, log, (_, _) => false);
        await lifetime.StartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
        Assert.Equal([abandoned], sandbox.Released);
        Assert.Empty(sandbox.Cleans);
        Assert.Equal(1, sandbox.ReleaseAllCalls);
        Assert.Empty(log.Entries);
    }

    private sealed class ThrowingSandbox : IAnalysisSandbox
    {
        public string NewSession() => throw new IOException();
        public void Release(string session) => throw new IOException();
        public void ReleaseAll() => throw new UnauthorizedAccessException(@"C:\Users\op\secret");
        public void CleanOldSessions(TimeSpan olderThan) => throw new IOException(@"C:\Users\op\secret");
    }

    [Fact]
    public async Task TheSandboxLifetimeRemovesStale7ZipListFilesFromTheTempFolderAtStart()
    {
        var temp = Directory.CreateDirectory(Path.Combine(_root, "user-temp")).FullName;
        var stale = Path.Combine(temp, "apd-7z-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(stale, "a.txt");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var lifetime = new InstallerAnalysisSandboxLifetime(new FakeAnalysisSandbox(_root), new RecordingAnalysisLog(), (_, _) => false, temp);

        await lifetime.StartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task ASandboxThatCannotBeRemovedIsLoggedByTypeAndNeverStopsTheApplication()
    {
        var log = new RecordingAnalysisLog();
        var lifetime = new InstallerAnalysisSandboxLifetime(new ThrowingSandbox(), log);
        await lifetime.StartAsync(CancellationToken.None);
        await lifetime.StopAsync(CancellationToken.None);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.DoesNotContain("secret", e.Message));
        Assert.Contains(log.Entries, e => e.Message.Contains("IOException"));
        Assert.Contains(log.Entries, e => e.Message.Contains("UnauthorizedAccessException"));
    }

    [Fact]
    public void TheRegisteredSandboxKeepsSessionsUnderTheDataRootAndRemovesThem()
    {
        using var services = new ServiceCollection().AddPackagerServices().AddSidebar().AddInstallerAnalysisPage().BuildServiceProvider();
        var sandbox = services.GetRequiredService<IAnalysisSandbox>();
        var session = sandbox.NewSession();
        Assert.True(AppDataPaths.IsWithin(session, Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Temp")));
        File.WriteAllText(Path.Combine(session, "x.bin"), "x");
        sandbox.Release(session);
        Assert.False(Directory.Exists(session));
    }
}
