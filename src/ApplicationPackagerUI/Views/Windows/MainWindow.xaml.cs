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
    private readonly SitePages _sitePages;
    /// <summary>The type of the page that the window shows.</summary>
    private Type? _shownPage;
    private readonly ISnackbarService _snackbarService;
    private readonly SidebarPresenter _sidebar;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IServiceProvider serviceProvider,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        SidebarService sidebarService,
        ContextStripViewModel? contextStrip = null)
    {
        _serviceProvider = serviceProvider;
        _snackbarService = snackbarService;
        DataContext = viewModel;

        InitializeComponent();
        // A host that builds the window without the strip state (a test) shows an empty strip.
        ContextStripHost.DataContext = contextStrip ?? new ContextStripViewModel();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);
        contentDialogService.SetDialogHost(RootContentDialog);
        // A provider with no site pages, as a window test builds it, serves every page itself.
        _sitePages = serviceProvider.GetService<SitePages>() ?? new SitePages(serviceProvider, []);
        RootNavigation.SetServiceProvider(_sitePages);
        RootNavigation.Navigated += (_, e) => _shownPage = e.Page?.GetType();
        _sidebar = new SidebarPresenter(RootNavigation, sidebarService, item => item.Activate?.Invoke(_serviceProvider));
        sidebarService.Changed += OnSidebarChanged;

        // The template of the navigation exists after the window loaded.
        Loaded += (_, _) => Helpers.NavigationNames.Apply(RootNavigation);

        // Pages set CanContentScroll to False, which disables the NavigationView's
        // DynamicScrollViewer; DataGrid and ScrollViewer controls scroll on the mouse wheel themselves.
    }

    public Wpf.Ui.Controls.NavigationView GetNavigation() => RootNavigation;

    /// <summary>Opens the Settings page, where the site, the host and the systems in use are set (the "Connections" button of the strip).</summary>
    public void OpenConnections() => RootNavigation.Navigate(typeof(Views.Pages.SettingsPage));

    /// <summary>Opens Settings on one page of its tree, such as "oneclick".</summary>
    public void OpenSettingsPage(string pageId)
    {
        _serviceProvider.GetRequiredService<Views.Pages.SettingsPage>().SelectPage(pageId);
        RootNavigation.Navigate(typeof(Views.Pages.SettingsPage));
        Activate();
    }

    /// <summary>Opens the Setup window (a step of the setup checklist, or More, Setup). It needs no stored setting.</summary>
    public void OpenSetup()
    {
        var packages = _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>();
        if (packages.IsOperationRunning) return;
        var setup = _serviceProvider.GetRequiredService<SetupWindow>();
        setup.Owner = this;
        var saved = setup.ShowDialog() == true;
        packages.OnSetupClosed(saved, setup.ViewModel.StopsTheChecklist(saved));
    }

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
        var import = new System.Windows.Controls.MenuItem { Header = "Import settings from another site..." };
        var importRefusal = manager.ImportRefusal();
        import.IsEnabled = importRefusal is null;
        import.ToolTip = importRefusal ?? "Copy the site-scoped settings of another site into the active site. You choose the settings and see a preview first. Secrets are never copied, and the other site is not changed.";
        System.Windows.Automation.AutomationProperties.SetAutomationId(import, "miImportSiteSettings");
        import.Click += (_, _) => ImportSiteSettingsFromWindow(manager);
        import.Icon = new SymbolIcon(SymbolRegular.DocumentArrowLeft24);
        menu.Items.Add(import);
        menu.Items.Add(new Separator());
        var export = new System.Windows.Controls.MenuItem { Header = "Export site file...", Icon = new SymbolIcon(SymbolRegular.DocumentArrowRight24) };
        var exportRefusal = manager.ExportRefusal();
        export.IsEnabled = exportRefusal is null;
        export.ToolTip = exportRefusal ?? "Save the settings of the active site in one file (zip) to take to another computer. Names and options only: no secrets, tokens or credentials, and no results or history.";
        System.Windows.Automation.AutomationProperties.SetAutomationId(export, "miExportSiteFile");
        export.Click += (_, _) => ExportSiteFileFromWindow(manager);
        menu.Items.Add(export);
        var importFile = new System.Windows.Controls.MenuItem { Header = "Import site file...", Icon = new SymbolIcon(SymbolRegular.DocumentArrowLeft24) };
        var importFileRefusal = manager.Refusal();
        importFile.IsEnabled = importFileRefusal is null;
        importFile.ToolTip = importFileRefusal ?? "Open a site file from another computer. You see what it holds before anything is written, then merge it into a site or create a new site. A value that a site already has is kept unless you choose Replace.";
        System.Windows.Automation.AutomationProperties.SetAutomationId(importFile, "miImportSiteFile");
        importFile.Click += (_, _) => ImportSiteFileFromWindow(manager);
        menu.Items.Add(importFile);
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

    private void ImportSiteSettingsFromWindow(SiteManager manager)
    {
        if (manager.ImportRefusal() is { } refusal)
        {
            ShowSiteMessage(refusal, ControlAppearance.Caution);
            return;
        }
        var target = manager.ActiveFolderName!;
        var viewModel = new ViewModels.Windows.ImportSiteSettingsViewModel(manager.ImportSources(), target, manager.DataRoot);
        var window = new ImportSiteSettingsWindow(viewModel) { Owner = this };
        window.ShowDialog();
        if (viewModel.Result is not { } report) return;
        var ok = report.Outcome != ApplicationPackagerEngine.Services.SiteImportReport.Refused && report.Outcome != ApplicationPackagerEngine.Services.SiteImportReport.FailedOutcome;
        ShowSiteMessage(ViewModels.Windows.ImportSiteSettingsViewModel.ResultMessage(report, target), ok ? ControlAppearance.Success : ControlAppearance.Caution);
        if (report.TargetChanged || report.Written > 0) manager.NotifyActiveSiteSettingsChanged();
    }

    private void ExportSiteFileFromWindow(SiteManager manager)
    {
        if (manager.ExportRefusal() is { } refusal)
        {
            ShowSiteMessage(refusal, ControlAppearance.Caution);
            return;
        }
        var folder = manager.ActiveFolderName!;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export site file",
            Filter = "Site file (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = ApplicationPackagerEngine.Services.SiteFile.SuggestedFileName(folder, DateTime.UtcNow),
        };
        if (dialog.ShowDialog(this) != true) return;
        var result = manager.ExportSiteFile(folder, dialog.FileName);
        ShowSiteMessage(result.Ok
            ? $"Site {folder} exported to {result.Path}: {result.ItemCount} setting groups. The file holds no secrets, tokens or credentials."
            : "The site file was not written. " + result.Reason, result.Ok ? ControlAppearance.Success : ControlAppearance.Caution);
    }

    private void ImportSiteFileFromWindow(SiteManager manager)
    {
        if (manager.Refusal() is { } refusal)
        {
            ShowSiteMessage(refusal, ControlAppearance.Caution);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import site file",
            Filter = "Site file (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var read = ApplicationPackagerEngine.Services.SiteFile.Read(dialog.FileName);
        if (!read.Ok)
        {
            ShowSiteMessage("The file was refused and nothing was written. " + read.Reason, ControlAppearance.Caution);
            return;
        }
        var viewModel = new ViewModels.Windows.ImportSiteSettingsViewModel(read.Content!, manager.ActiveFolderName, manager.DataRoot);
        var window = new ImportSiteSettingsWindow(viewModel) { Owner = this };
        window.ShowDialog();
        if (viewModel.Result is not { } report) return;
        var ok = report.Outcome != ApplicationPackagerEngine.Services.SiteImportReport.Refused && report.Outcome != ApplicationPackagerEngine.Services.SiteImportReport.FailedOutcome;
        var target = report.TargetFolder;
        var text = ViewModels.Windows.ImportSiteSettingsViewModel.ResultMessage(report, target, fromFile: true);
        if (ok && viewModel.SelectedTarget is { IsNew: true } && report.Written > 0) text += $" Switch to site {target} from the site list to use it.";
        ShowSiteMessage(text, ok ? ControlAppearance.Success : ControlAppearance.Caution);
        if (string.Equals(target, manager.ActiveFolderName, StringComparison.Ordinal) && (report.TargetChanged || report.Written > 0))
            manager.NotifyActiveSiteSettingsChanged();
    }

    private void ShowSiteResult(SiteSwitchResult result) =>
        ShowSiteMessage(result.Message, result.Succeeded ? ControlAppearance.Success : ControlAppearance.Caution);

    private void ShowSiteMessage(string message, ControlAppearance appearance) =>
        _snackbarService.Show("Site", message, appearance,
            new SymbolIcon(appearance == ControlAppearance.Success ? SymbolRegular.Checkmark24 : SymbolRegular.Warning24), TimeSpan.FromSeconds(6));

    /// <summary>
    /// Every tool page of the old site goes, with all it held. The page that the window shows is left and opened again, so it is built
    /// new and reads the new site.
    /// </summary>
    private void RenewSitePages()
    {
        var shown = _shownPage;
        _sitePages.Retire();
        if (shown is null || !_sitePages.Holds(shown)) return;
        RootNavigation.Navigate(typeof(Views.Pages.PackagesPage));
        RootNavigation.Navigate(shown);
    }

    /// <summary>True for a window that stays open beside the main window and shows data or a plan of one site.</summary>
    public static bool ShowsOneSite(Window window) => window is OneClickPlanWindow or CveWindow or CatalogWindow or ConvertFromCmWindow;

    /// <summary>After a switch every page that holds site data reads the new site again. Nothing is written.</summary>
    private void OnSiteChanged()
    {
        void Reload()
        {
            // An open window that shows the site as it was closes. A switch is refused while any of them runs.
            foreach (var window in System.Windows.Application.Current.Windows.OfType<Window>().Where(ShowsOneSite).ToList())
                window.Close();
            _serviceProvider.GetRequiredService<ViewModels.Pages.SettingsViewModel>().ReloadFromStore();
            var packages = _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>();
            packages.FollowSiteChange();
            _serviceProvider.GetService<ViewModels.Windows.CatalogViewModel>()?.FollowSiteChange();
            RenewSitePages();
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

        // The grid shows first. Setup never opens by itself: the setup checklist of the App Packager page opens it.
        // The update check runs in the background after the first render and never blocks the app.
        void StartUpdateCheck(object? sender, EventArgs args)
        {
            ContentRendered -= StartUpdateCheck;
            if (_serviceProvider.GetService<UpdateCoordinator>() is { } updates) _ = updates.RunStartupCheckAsync(this);
            _ = Services.IconPackStartup.RunAsync();
        }
        ContentRendered += StartUpdateCheck;

        Closing += (_, _) =>
        {
            _serviceProvider.GetRequiredService<ViewModels.Pages.PackagesViewModel>().StopRun();
            _sitePages.Existing<ViewModels.Pages.PivotsScriptsViewModel>()?.OnAppClosing();
            _sitePages.Existing<ViewModels.Pages.SiteHygieneViewModel>()?.OnAppClosing();
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
