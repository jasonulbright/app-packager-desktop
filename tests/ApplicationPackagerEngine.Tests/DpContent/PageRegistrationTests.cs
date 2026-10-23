using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.DpContent;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PageRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dpc-reg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PageRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

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
        services.AddDpContentManagerPage();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    [Fact]
    public void TheApplicationServicesBuildOnePageViewModelThatReadsNothing()
    {
        using var provider = Provider();
        var viewModel = provider.GetRequiredService<DpContentManagerViewModel>();
        Assert.Same(viewModel, provider.GetRequiredService<DpContentManagerViewModel>());
        var services = provider.GetRequiredService<DpContentPageServices>();
        Assert.Same(provider.GetRequiredService<DpContentWriter>(), services.Writer);
        Assert.NotNull(provider.GetRequiredService<IDpContentService>());
        Assert.NotNull(provider.GetRequiredService<IDpContentAuditLog>());
        Assert.NotNull(provider.GetRequiredService<IDpContentReportWriter>());
        Assert.Equal(TimeSpan.FromMilliseconds(250), services.FilterDelay);
        Assert.Empty(viewModel.DpRows);
        Assert.False(File.Exists(SettingsService.SettingsPath));
    }

    [Fact]
    public void EveryPageTypeHasOneConstructorSoTheContainerCanBuildIt()
    {
        foreach (var type in new[] { typeof(DpContentManagerViewModel), typeof(DpContentManagerPage), typeof(DpContentWriter), typeof(DpContentDialogs), typeof(DpContentPairsViewModel) })
            Assert.Single(type.GetConstructors());
    }

    [Fact]
    public void TheSavedChoicesRoundTripThroughTheSettingsFileAndKeepOtherValues()
    {
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.SiteCode = "LAB";
        stored.DeploymentHelper = new DeploymentHelperSettings { TemplateId = "lab-nightly" };
        settings.Save(stored);
        Assert.Null(DpContentPreferences.Load(settings));
        Assert.True(DpContentPreferences.Save(settings, new DpContentSettings
        {
            SelectedTab = 2, IssueState = "Failed only", DpText = "DP0", Sorts = new() { ["dps"] = "Failed|Descending" }
        }));
        var loaded = DpContentPreferences.Load(new SettingsService())!;
        Assert.Equal((2, "Failed only", "DP0"), (loaded.SelectedTab, loaded.IssueState, loaded.DpText));
        Assert.Equal("Failed|Descending", loaded.Sorts!["dps"]);
        Assert.Equal("LAB", new SettingsService().Load().SiteCode);
        Assert.Equal("lab-nightly", new SettingsService().Load().DeploymentHelper?.TemplateId);
    }

    [Fact]
    public void ASettingsFileFromBeforeThePageLoadsWithoutThePageChoices()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, "{ \"FirstRunCompleted\": true, \"SiteCode\": \"LAB\", \"DpContent\": null }");
        var settings = new SettingsService();
        Assert.Null(settings.Load().DpContent);
        Assert.Equal("LAB", settings.Load().SiteCode);
        File.WriteAllText(SettingsService.SettingsPath, "{ \"SiteCode\": \"LAB\", \"DpContent\": { \"SelectedTab\": 1, \"Sorts\": null, \"DpText\": null } }");
        var loaded = new SettingsService().Load().DpContent!;
        Assert.Equal(1, loaded.SelectedTab);
        Assert.Null(loaded.Sorts);
    }
}
