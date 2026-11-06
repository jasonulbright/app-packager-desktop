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
    private readonly ISnackbarService _snackbarService;
    private readonly SidebarPresenter _sidebar;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        SidebarService sidebarService)
    {
        _serviceProvider = serviceProvider;
        _snackbarService = snackbarService;
        DataContext = viewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialog);
        RootNavigation.SetServiceProvider(serviceProvider);
        _sidebar = new SidebarPresenter(RootNavigation, sidebarService, item => item.Activate?.Invoke(_serviceProvider));
        sidebarService.Changed += OnSidebarChanged;

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

    private void OnSidebarChanged(object? sender, SidebarChangedEventArgs e)
    {
        if (e.Saved) return;
        _snackbarService.Show("Sidebar", "The sidebar layout could not be saved. The change lasts until the application closes.",
            ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(6));
    }

    public void OpenWorkbenchForGridSelection()
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
        else
        {
            // The check runs in the background after the first render and never blocks the app.
            void StartUpdateCheck(object? sender, EventArgs args)
            {
                ContentRendered -= StartUpdateCheck;
                _ = _serviceProvider.GetRequiredService<UpdateCoordinator>().RunStartupCheckAsync(this);
            }
            ContentRendered += StartUpdateCheck;
        }

        Closing += (_, _) =>
        {
            _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>().StopDiscovery();
            _serviceProvider.GetService<ViewModels.Pages.PivotsScriptsViewModel>()?.OnAppClosing();
            _serviceProvider.GetService<ViewModels.Pages.SiteHygieneViewModel>()?.OnAppClosing();
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
