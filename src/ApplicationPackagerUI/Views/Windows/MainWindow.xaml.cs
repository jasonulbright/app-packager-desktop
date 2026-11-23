using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
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

    // ---- site switcher (Q-112d) ----

    private void SiteSwitcher_Click(object sender, RoutedEventArgs e)
    {
        var manager = _serviceProvider.GetRequiredService<SiteManager>();
        if (manager.Refusal() is { } refusal)
        {
            ShowSiteMessage(refusal, ControlAppearance.Caution);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = btnSiteSwitcher, Placement = PlacementMode.Top };
        foreach (var site in manager.Sites())
        {
            var folder = site.FolderName;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = site.DisplayName,
                IsChecked = string.Equals(folder, manager.ActiveFolderName, StringComparison.Ordinal),
                ToolTip = $"Switch to site {folder}. Its settings and data are loaded; the data of the current site is not shown.",
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "miSite" + folder.Replace("~", ""));
            item.Click += (_, _) => ShowSiteResult(manager.Switch(folder));
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var add = new System.Windows.Controls.MenuItem { Header = "Add site...", ToolTip = "Add a site by its site code and SMS Provider. If the code is already in use, you choose between the same site with another provider and a different environment." };
        System.Windows.Automation.AutomationProperties.SetAutomationId(add, "miAddSite");
        add.Click += (_, _) => AddSiteFromWindow(manager);
        menu.Items.Add(add);
        menu.IsOpen = true;
    }

    private void AddSiteFromWindow(SiteManager manager)
    {
        var window = _serviceProvider.GetRequiredService<AddSiteWindow>();
        window.Owner = this;
        window.ShowDialog();
        if (!window.ViewModel.Accepted) return;
        var vm = window.ViewModel;
        ShowSiteResult(manager.AddSite(vm.SiteCode, vm.Provider, vm.Label, existing => SiteReusePrompt.Ask(this, existing)));
    }

    private void ShowSiteResult(SiteSwitchResult result) =>
        ShowSiteMessage(result.Message, result.Succeeded ? ControlAppearance.Success : ControlAppearance.Caution);

    private void ShowSiteMessage(string message, ControlAppearance appearance) =>
        _snackbarService.Show("Site", message, appearance,
            new SymbolIcon(appearance == ControlAppearance.Success ? SymbolRegular.Checkmark24 : SymbolRegular.Warning24), TimeSpan.FromSeconds(6));

    /// <summary>After a switch every page that holds site data reads the new site again. Nothing is written.</summary>
    private void OnSiteChanged()
    {
        void Reload()
        {
            _serviceProvider.GetRequiredService<ViewModels.Pages.SettingsViewModel>().ReloadFromStore();
            var packages = _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>();
            ViewModels.Pages.PackagesViewModel.ClearSiteResults(packages.Packagers);
            _serviceProvider.GetService<ViewModels.Pages.DeploymentHelperViewModel>()?.Shared.ClearForSiteChange();
            _serviceProvider.GetService<ViewModels.Pages.ComplianceViewModel>()?.Shared.ClearForSiteChange();
            _serviceProvider.GetService<ViewModels.Pages.DpContentManagerViewModel>()?.ClearForSiteChange();
            packages.LoadPackagersCommand.Execute(null);
        }
        if (Dispatcher.CheckAccess()) Reload();
        else Dispatcher.BeginInvoke(Reload);
    }

    private void FluentWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
        var settings = settingsService.Load();
        if (_serviceProvider.GetService<SiteManager>() is { } siteManager) siteManager.Changed += OnSiteChanged;
        if (SiteStartup.Last?.Notice is { } startupNotice)
            ShowSiteMessage(startupNotice, ControlAppearance.Caution);

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
                // The first-run scan asks first. It helps the user pick the applicable products; nothing runs on a No.
                if (setup.DialogResult == true)
                {
                    var packages = _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>();
                    if (packages.IsDiscoverAvailable
                        && System.Windows.MessageBox.Show(this,
                            "Scan the ConfigMgr site inventory now? The scan helps you pick the products that apply to your site. You can run it later in Settings, ConfigMgr Site Settings (Discover not selected).",
                            "Discover unmanaged software", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes)
                        _ = packages.RunFirstRunDiscoveryAsync();
                }
            }
            ContentRendered += ShowSetup;
        }
        else
        {
            // The check runs in the background after the first render and never blocks the app.
            void StartUpdateCheck(object? sender, EventArgs args)
            {
                ContentRendered -= StartUpdateCheck;
                if (_serviceProvider.GetService<UpdateCoordinator>() is { } updates) _ = updates.RunStartupCheckAsync(this);
                _ = Services.IconPackStartup.RunAsync();
            }
            ContentRendered += StartUpdateCheck;
        }

        Closing += (_, _) =>
        {
            _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>().StopRun();
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
