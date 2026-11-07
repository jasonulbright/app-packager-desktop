using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MaintenanceWindowsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-mwservices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public MaintenanceWindowsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [WindowFact]
    public async Task ThePackagerServicesRegisterOneOfEachMaintenanceWindowsServiceUnderTheDataRoot()
    {
        await using var services = new ServiceCollection().AddPackagerServices().BuildServiceProvider();

        Assert.IsType<ScheduleProjector>(services.GetRequiredService<IScheduleProjector>());
        var windows = services.GetRequiredService<IMaintenanceWindowService>();
        Assert.IsType<MaintenanceWindowService>(windows);
        Assert.Same(windows, services.GetRequiredService<IMaintenanceWindowService>());

        var templates = services.GetRequiredService<IMaintenanceTemplateStore>();
        Assert.Same(templates, services.GetRequiredService<IMaintenanceTemplateStore>());
        var shipped = templates.All()[0];
        templates.Save(shipped with { Id = "mine", IsShipped = false });
        Assert.True(File.Exists(Path.Combine(AppDataPaths.Root, "MaintenanceWindows", "Templates", "mine.json")));

        var report = services.GetRequiredService<IMaintenanceReportWriter>().WriteHtml([]);
        Assert.StartsWith(Path.Combine(AppDataPaths.Root, "MaintenanceWindows", "Reports"), report);
    }

    [WindowFact]
    public void ThePageServicesResolveAndARegisteredServiceIsKept()
    {
        var service = new FakeMaintenanceService();
        var services = new ServiceCollection().AddPackagerServices();
        services.AddSingleton<IMaintenanceWindowService>(service);
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<IChangePlanDialogs>(new ScriptedPlanDialogs());
        services.AddMaintenanceWindowsPage();
        using var provider = services.BuildServiceProvider();

        Assert.Same(service, provider.GetRequiredService<IMaintenanceWindowService>());
        Assert.Same(provider.GetRequiredService<MaintenanceWindowsViewModel>(), provider.GetRequiredService<MaintenanceWindowsViewModel>());
        Assert.NotNull(provider.GetRequiredService<MaintenanceWindowWriter>());
        Assert.NotNull(provider.GetRequiredService<IScheduleProjector>());
        Assert.NotNull(provider.GetRequiredService<IMaintenanceTemplateStore>());
        Assert.NotNull(provider.GetRequiredService<IMaintenanceReportWriter>());
        Assert.IsType<MaintenanceWindowsPrompts>(provider.GetRequiredService<IMaintenanceWindowsPrompts>());
    }
}
