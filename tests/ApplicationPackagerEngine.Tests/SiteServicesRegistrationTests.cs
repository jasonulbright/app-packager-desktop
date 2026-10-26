using System.Data.SQLite;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteServicesRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-siteservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SiteServicesRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ThePackagerServicesRegisterOneLogOneHistoryAndOneSessionUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        var log = services.GetRequiredService<IAppLog>();
        Assert.Same(log, services.GetRequiredService<IAppLog>());
        log.Write(AppLogLevel.Info, "Test", "registered");
        Assert.StartsWith(Path.Combine(AppDataPaths.LegacyRoot, "Logs"), log.SessionFile);

        var history = services.GetRequiredService<ISiteOperationHistory>();
        Assert.Same(history, services.GetRequiredService<ISiteOperationHistory>());
        history.Record(new SiteOperationEntry("Test", "Check", "", SiteOperationOutcome.Succeeded, new Dictionary<string, int>()));
        Assert.True(File.Exists(Path.Combine(AppDataPaths.LegacyRoot, "history.db")));

        var session = services.GetRequiredService<IConfigMgrSession>();
        Assert.IsType<ConfigMgrSessionHost>(session);
        Assert.Same(session, services.GetRequiredService<IConfigMgrSession>());
    }

    [Fact]
    public void TheSessionConnectionComesFromTheSavedSiteSettings()
    {
        using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var settings = services.GetRequiredService<SettingsService>();
        var saved = settings.Load();
        saved.SiteCode = "LAB";
        saved.ProviderMachine = "cm01.lab.test";
        settings.Save(saved);

        var connection = PackagerServiceRegistration.SessionConnection(settings);
        Assert.Equal(new ConfigMgrConnection("LAB", "cm01.lab.test"), connection);
    }
}
