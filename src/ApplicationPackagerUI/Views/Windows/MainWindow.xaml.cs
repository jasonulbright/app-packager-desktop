using System.Windows;
using System.Windows.Input;
using ApplicationPackagerEngine.Models;
using Microsoft.Extensions.DependencyInjection;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class MainWindow : IDialogHostWindow
{
    public ContentDialogHost DialogHost => RootContentDialog;

    private readonly IServiceProvider _serviceProvider;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _serviceProvider = serviceProvider;
        DataContext = viewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialog);
        RootNavigation.SetServiceProvider(serviceProvider);

        // Pages set CanContentScroll to False, which disables the NavigationView's
        // DynamicScrollViewer; DataGrid and ScrollViewer controls scroll on the mouse wheel themselves.
    }

    public Wpf.Ui.Controls.NavigationView GetNavigation() => RootNavigation;

    /// <summary>Opens the Application Workbench on an application, or on the first one listed.</summary>
    public void OpenWorkbench(string? applicationId)
    {
        var workbench = _serviceProvider.GetRequiredService<WorkbenchWindow>();
        workbench.Owner = this;
        workbench.PreselectApplicationId = applicationId;
        workbench.ShowDialog();
    }

    private void WorkbenchNavItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenWorkbenchForGridSelection();
    }

    /// <summary>
    /// The navigation view raises no Click for the item it already treats as current, so a
    /// second mouse click on this entry would do nothing. The mouse opens the window here
    /// and the item never becomes current; keyboard and automation invoke reach Click.
    /// </summary>
    private void WorkbenchNavItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dispatcher.BeginInvoke(OpenWorkbenchForGridSelection);
    }

    private void OpenWorkbenchForGridSelection()
    {
        var selected = _serviceProvider.GetRequiredService<Views.Pages.PackagesPage>().SelectedPackager;
        OpenWorkbench(selected is null ? null : WorkbenchApplicationCatalog.ApplicationIdFor(selected));
    }

    private void FluentWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        var settings = settingsService.Load();

        if (settings.WindowLeft >= 0 && settings.WindowTop >= 0)
        {
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
        RootNavigation.IsPaneOpen = settings.PaneOpen;

        // Setup opens once the main window has rendered: opened from Loaded, it shows over
        // an empty frame and the main window paints only after Setup closes.
        if (FirstRunDecision.IsWizardDue(settings.FirstRunCompleted))
        {
            void ShowSetup(object? sender, EventArgs args)
            {
                ContentRendered -= ShowSetup;
                var setup = _serviceProvider.GetRequiredService<SetupWindow>();
                setup.Owner = this;
                setup.ShowDialog();
            }
            ContentRendered += ShowSetup;
        }

        Closing += (_, _) =>
        {
            _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>().StopDiscovery();
            var s = settingsService.Load();
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
            s.PaneOpen = RootNavigation.IsPaneOpen;
            settingsService.Save(s);
        };
    }
}
