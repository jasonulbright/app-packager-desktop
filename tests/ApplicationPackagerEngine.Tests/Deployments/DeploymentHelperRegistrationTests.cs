using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.Deployments;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentHelperRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dh-reg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public DeploymentHelperRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        System.Data.SQLite.SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<IChangePlanDialogs, ChangePlanDialogs>();
        services.AddDeploymentHelperPage();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    [Fact]
    public void TheApplicationServicesBuildOnePageViewModelThatReadsNothing()
    {
        using var provider = Provider();
        var viewModel = provider.GetRequiredService<DeploymentHelperViewModel>();
        Assert.Same(viewModel, provider.GetRequiredService<DeploymentHelperViewModel>());
        var services = provider.GetRequiredService<DeploymentPageServices>();
        Assert.Same(provider.GetRequiredService<DeploymentWriter>(), services.Writer);
        Assert.Same(provider.GetRequiredService<ICollectionService>(), services.Collections);
        Assert.NotNull(provider.GetRequiredService<IDeploymentService>());
        Assert.NotNull(provider.GetRequiredService<IDeploymentAuditLog>());
        Assert.NotNull(provider.GetRequiredService<IDeploymentTemplateStore>());
        Assert.NotNull(provider.GetRequiredService<IDeploymentReportWriter>());
        Assert.Empty(viewModel.Deploy.Objects);
        Assert.False(File.Exists(SettingsService.SettingsPath));
    }

    [Fact]
    public void TheRegisteredAuditLogFollowsThePageSettingAtEveryCall()
    {
        using var provider = Provider();
        Assert.IsType<SettingsDeploymentAuditPath>(provider.GetRequiredService<IDeploymentAuditPathProvider>());
        var log = provider.GetRequiredService<IDeploymentAuditLog>();
        Assert.Equal(DeploymentAuditLog.DefaultPath(), log.Path);

        var settings = provider.GetRequiredService<SettingsService>();
        var custom = Path.Combine(_root, "custom", "deployment-audit.jsonl");
        Assert.True(DeploymentHelperPreferences.Save(settings, new ApplicationPackagerUI.Models.DeploymentHelperSettings { AuditLogPath = custom }));
        Assert.Equal(custom, log.Path);

        Assert.True(DeploymentHelperPreferences.Save(settings, new ApplicationPackagerUI.Models.DeploymentHelperSettings { AuditLogPath = "" }));
        Assert.Equal(DeploymentAuditLog.DefaultPath(), log.Path);
    }

    [Fact]
    public void EveryPageTypeHasOneConstructorSoTheContainerCanBuildIt()
    {
        foreach (var type in new[] { typeof(DeploymentHelperViewModel), typeof(DeploymentHelperPage), typeof(DeploymentWriter), typeof(DeploymentHelperDialogs) })
            Assert.Single(type.GetConstructors());
    }

    [Fact]
    public void TheSavedChoicesRoundTripThroughTheSettingsFileAndKeepOtherValues()
    {
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.SiteCode = "LAB";
        settings.Save(stored);
        Assert.Null(DeploymentHelperPreferences.Load(settings));
        Assert.Equal("", DeploymentHelperPreferences.AuditLogPath(settings));
        Assert.True(DeploymentHelperPreferences.Save(settings, new ApplicationPackagerUI.Models.DeploymentHelperSettings
        {
            SelectedTab = 1, ObjectType = 3, TemplateId = "lab-nightly", AuditLogPath = @"\\fs01\deploy$\audit.jsonl"
        }));
        var loaded = DeploymentHelperPreferences.Load(new SettingsService())!;
        Assert.Equal((1, 3, "lab-nightly"), (loaded.SelectedTab, loaded.ObjectType, loaded.TemplateId));
        Assert.Equal(@"\\fs01\deploy$\audit.jsonl", DeploymentHelperPreferences.AuditLogPath(new SettingsService()));
        Assert.Equal("LAB", new SettingsService().Load().SiteCode);
    }
}
