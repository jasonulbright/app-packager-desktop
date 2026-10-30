using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.AddFlow;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AddFlowRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-add-registration-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public AddFlowRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        TestCleanup.DeleteDirectory(_root);
    }

    [Fact]
    public void BothAddPagesGetTheRealAnalysisService()
    {
        var services = new ServiceCollection().AddPackagerServices().AddAddFlows();
        services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();
        services.AddSingleton<Wpf.Ui.ISnackbarService, Wpf.Ui.SnackbarService>();
        using var provider = services.BuildServiceProvider();

        var service = Assert.IsType<AddFromInstallerService>(provider.GetRequiredService<IAddFromInstallerService>());
        Assert.Equal([".msi", ".exe", ".msp", ".msix", ".appx", ".msixbundle", ".appxbundle"], service.AcceptedExtensions);
        var addFile = provider.GetRequiredService<AddFileViewModel>();
        Assert.Equal(".msi, .exe, .msp, .msix, .appx, .msixbundle or .appxbundle", addFile.FormatsText);
        Assert.NotSame(addFile.Result, provider.GetRequiredService<AddAppViewModel>().Result);
        Assert.IsType<AddFileRoute>(provider.GetRequiredService<IAddFileRoute>());
    }

    [Fact]
    public void TheRealServiceRefusesAnotherFileTypeWithThePlainMessage()
    {
        using var provider = new ServiceCollection().AddPackagerServices().BuildServiceProvider();
        var service = provider.GetRequiredService<IAddFromInstallerService>();
        Assert.Null(service.RefusalReason(@"C:\in\app.appxbundle"));
        Assert.Contains("does not take .zip files", service.RefusalReason(@"C:\in\payload.zip"));
    }
}
