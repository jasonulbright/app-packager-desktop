using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class OneClickPlanWindow : ApplicationPackagerUI.Services.IDialogHostWindow
{
    public ContentDialogHost DialogHost => PromptHost;

    private readonly OneClickPlanViewModel _viewModel;
    private readonly string _reportFolder;

    /// <summary>The rows whose height the table keeps, whatever stands above it.</summary>
    public const int LeastRows = 5;

    private const double LeastRowHeight = 34, LeastTableFrame = 95;

    public OneClickPlanWindow(OneClickPlanViewModel viewModel, string reportFolder)
    {
        _viewModel = viewModel;
        _reportFolder = reportFolder;
        DataContext = viewModel;
        InitializeComponent();
        Controls.Mock.DialogFrame.Attach(this);
        // The two sizes of the mock.
        Width = viewModel.IsPublishRun ? 1336 : 1240;
        Height = viewModel.IsPublishRun ? 625 : 600;
        Controls.Mock.DialogFrame.RoundAs(this, Height);
        SourceInitialized += (_, _) => Controls.Mock.DialogFrame.FitToWorkArea(this, SystemParameters.WorkArea);
        // The least height of the table with its note: the margins, the header and the note, and up to five rows.
        pnlBody.LeastFill = LeastTableFrame + Math.Min(viewModel.Rows.Count, LeastRows) * LeastRowHeight;
        Loaded += (_, _) => FitBody();
        // A later change of the lines above the table, or of the size of the window, places the body again.
        pnlBody.NeededHeightChanged += (_, _) => Dispatcher.BeginInvoke(FitBody, System.Windows.Threading.DispatcherPriority.Loaded);
        BodyHost.SizeChanged += (_, _) => Dispatcher.BeginInvoke(PlaceBody, System.Windows.Threading.DispatcherPriority.Loaded);
        Controls.Mock.Mk.SetFaceDrop(btnOneClickSettings, viewModel.IsPublishRun ? -1 : 0);
        Controls.Mock.Mk.SetIconDrop(cboPlanAction, -1);
        // Places where the mock rounds to another pixel than the layout does.
        Controls.Mock.Mk.SetDrop(txtFootNote, -1);
        if (!viewModel.IsPublishRun)
        {
            Resources["FlowShift"] = new System.Windows.Media.TranslateTransform(0, 1);
            Controls.Mock.Mk.SetIconDrop(btnOneClickSettings, 1);
            gridPlan.LoadingRow += (_, e) => e.Row.Resources["CellShift"] = new System.Windows.Media.TranslateTransform(0, e.Row.GetIndex() == 2 ? 1 : 0);
            // The check boxes of the first two rows stand one unit higher.
            gridPlan.LoadingRow += (_, e) => e.Row.Resources["CheckDrop"] = e.Row.GetIndex() < 2 ? -1d : 0d;
        }
        if (viewModel.IsPublishRun)
        {
            gridPlan.LoadingRow += (_, e) =>
            {
                // The first row of the mock is one unit higher than the others. It is a least height, so a wrapped text still makes a row higher.
                var index = e.Row.GetIndex();
                e.Row.Resources["RowContent"] = index == 0 ? 29d : 0d;
                // So do some of their texts and boxes: the names, the boxes of the first row, and the mark and the texts of the second.
                static System.Windows.Media.TranslateTransform Down(int units) => new(0, units);
                e.Row.Resources["CheckDrop"] = index == 0 ? -1d : 0d;
                e.Row.Resources["NameShift"] = Down(index <= 2 ? -1 : 0);
                e.Row.Resources["MarkShift"] = Down(index == 1 ? -1 : 0);
                e.Row.Resources["CellShift"] = Down(index == 1 ? -1 : 0);
            };
            foreach (var button in new[] { btnPlanOnly, btnHistory, btnOpenReport, btnClose, btnRun }) Controls.Mock.Mk.SetTextDrop(button, -1);
        }
        viewModel.PropertyChanged += OnViewModelChanged;
        Closing += OnClosing;
        // The dialog opens after the run has left its running state, so it does not run inside the run command.
        viewModel.ResultsReady += (_, _) => Dispatcher.BeginInvoke(ShowResults);
        ApplyColumns();
    }

    private void ShowResults()
    {
        if (!IsVisible || !_viewModel.HasResults) return;
        var results = _viewModel.BuildResults(owner => new OneClickHistoryWindow(_reportFolder) { Owner = owner }.ShowDialog());
        new PublishResultsWindow(results) { Owner = this }.ShowDialog();
        if (results.ReviewUid.Length > 0) ReviewFailed_Click(this, new RoutedEventArgs());
    }

    /// <summary>Opens Settings on the One Click page after this window closed; null uses the main window.</summary>
    public Action? OpenOneClickSettings { get; set; }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning) return;
        var main = Owner as MainWindow ?? System.Windows.Application.Current?.MainWindow as MainWindow;
        var open = OpenOneClickSettings ?? (() => main?.OpenSettingsPage("oneclick"));
        // This window is owned by the main window and stays above it, so it closes first. Its plan holds the action it was opened with.
        Close();
        open();
    }

    private void Results_Click(object sender, RoutedEventArgs e) => ShowResults();

    private void ReviewFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.FirstFailed is not { } row) return;
        gridPlan.SelectedItem = row;
        gridPlan.ScrollIntoView(row);
        gridPlan.Focus();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OneClickPlanViewModel.IsRunView) or nameof(OneClickPlanViewModel.ShowResultConfigMgr)
            or nameof(OneClickPlanViewModel.ShowResultWsus) or nameof(OneClickPlanViewModel.ShowResultIntune))
            ApplyColumns();
    }

    /// <summary>The scroll area of the body, while the window cannot show the lines above the table and the least rows of the table.</summary>
    public ScrollViewer BodyScroll { get; } = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false
    };

    /// <summary>Makes the window taller where the work area has room, and then places the body.</summary>
    private void FitBody()
    {
        if (!IsLoaded) return;
        UpdateLayout();
        Controls.Mock.DialogFrame.Grow(this, pnlBody.NeededHeight - BodyHost.ActualHeight);
        PlaceBody();
    }

    private void PlaceBody()
    {
        if (!IsLoaded) return;
        UpdateLayout();
        var scrolls = pnlBody.NeededHeight > BodyHost.ActualHeight + 0.5;
        if (scrolls == (BodyHost.Child == BodyScroll)) return;
        var focused = System.Windows.Input.Keyboard.FocusedElement;
        BodyHost.Child = null;
        if (scrolls)
        {
            BodyScroll.Content = pnlBody;
            pnlBody.SetBinding(Controls.LeastFillPanel.ViewportHeightProperty,
                new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = BodyScroll });
            BodyHost.Child = BodyScroll;
        }
        else
        {
            BodyScroll.Content = null;
            BodyHost.Child = pnlBody;
        }
        UpdateLayout();
        focused?.Focus();
    }

    /// <summary>
    /// A run that can publish shows a box for each system and one column for the action and its reason. A run that only checks shows the action
    /// and the reason in two columns. During a run the result columns of the destinations in use take the place of the boxes and the plan.
    /// </summary>
    private void ApplyColumns()
    {
        var run = _viewModel.IsRunView;
        var publish = _viewModel.IsPublishRun;
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
        colApplication.Visibility = Show(publish);
        colPlainApplication.Visibility = Show(!publish);
        colVersion.Header = publish ? "Latest" : "Latest version";
        colMecm.Visibility = colWsus.Visibility = colIntune.Visibility = Show(!run && publish);
        colLast.Visibility = Show(!run);
        colPlanned.Visibility = Show(!run && publish);
        colWord.Visibility = Show(!run && !publish);
        colStep.Visibility = Show(run);
        colResultMecm.Visibility = Show(run && _viewModel.ShowResultConfigMgr);
        colResultWsus.Visibility = Show(run && _viewModel.ShowResultWsus);
        colResultIntune.Visibility = Show(run && _viewModel.ShowResultIntune);
        colReason.Visibility = Show(run || !publish);
        // The two tables of the mock give the shared columns different widths.
        static DataGridLength Star(double weight) => new(weight, DataGridLengthUnitType.Star);
        colInclude.Width = Star(65.4);
        colVersion.Width = Star(publish ? 93 : 148.1);
        colLast.Width = Star(publish ? 187.5 : 185.3);
        colPlainApplication.Width = Star(301.8);
        colWord.Width = Star(156.7);
        colReason.Width = Star(run ? 320 : 342.7);
        gridPlan.MinRowHeight = publish ? 30 : 33;
    }

    /// <summary>Choosing another action opens the page that saves it: the action of a run is the saved one.</summary>
    private void Action_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || cboPlanAction.SelectedItem is not string chosen || chosen == _viewModel.ActionChoice) return;
        cboPlanAction.SelectedItem = _viewModel.ActionChoice;
        Settings_Click(sender, new RoutedEventArgs());
    }

    private void PlanMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        menu.Items.Clear();
        var row = gridPlan.SelectedItem as Models.OneClickPlanItem;
        var details = new System.Windows.Controls.MenuItem
        {
            Header = "Change details", IsCheckable = true, IsChecked = row?.ShowDetails ?? false, IsEnabled = row?.HasChangeDetails ?? false,
            ToolTip = "Show what this run creates in each destination for the application."
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(details, "btnChangeDetails");
        details.Click += (_, _) => { if (row is not null) row.ShowDetails = details.IsChecked; };
        menu.Items.Add(details);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsRunning) e.Cancel = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsRunning) Close();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var path = _viewModel.ReportPath;
        if (path.Length == 0 || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private void History_Click(object sender, RoutedEventArgs e) =>
        new OneClickHistoryWindow(_reportFolder) { Owner = this }.ShowDialog();
}
