using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MaintenanceWindowsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-mw-registration-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public MaintenanceWindowsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
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
