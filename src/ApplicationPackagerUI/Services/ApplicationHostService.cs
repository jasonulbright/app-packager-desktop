using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Services;

public class ApplicationHostService(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mainWindow = serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        var nav = mainWindow.GetNavigation();
        nav.Navigate(typeof(PackagesPage));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
