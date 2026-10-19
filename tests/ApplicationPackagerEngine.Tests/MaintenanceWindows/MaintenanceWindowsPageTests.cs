using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using DataGrid = System.Windows.Controls.DataGrid;
using TabControl = System.Windows.Controls.TabControl;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>The compiled Maintenance Windows page and its dialogs in real windows, over fake services.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MaintenanceWindowsPageTests
{
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Lines { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Lines.Add(message);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static T ById<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);

    /// <summary>
    /// Runs the test with the theme and control dictionaries merged into an application object,
    /// as App.xaml merges them; a binding error fails the test.
    /// </summary>
    private static void WithApplication(ApplicationTheme theme, Action test)
    {
        StaTestHelper.Run(() =>
        {
            Assert.Null(Application.Current);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var errors = new BindingErrors();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try
            {
                application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
                application.Resources.MergedDictionaries.Add(new ControlsDictionary());
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                test();
                Assert.Empty(errors.Lines);
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
                application.Resources = new ResourceDictionary();
                var statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                typeof(Application).GetField("_appInstance", statics)!.SetValue(null, null);
                typeof(Application).GetField("_appCreatedInThisAppDomain", statics)!.SetValue(null, false);
            }
        });
    }

    private static void Show(Window window)
    {
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
        StaTestHelper.Flush();
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, MaintenanceWindowsPage, MaintenanceWindowsHarness> test, double width = 1400)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var viewModel = harness.Create();
            var page = new MaintenanceWindowsPage(viewModel);
            var window = new Window { Width = width, Height = 900, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                var opened = page.OnNavigatedToAsync();
                StaTestHelper.PumpUntil(() => opened.IsCompleted);
                window.UpdateLayout();
                StaTestHelper.Flush();
                test(window, page, harness);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheThreeTabsRenderInTheTheme(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = (MaintenanceWindowsViewModel)page.DataContext;
            var tabs = ById<TabControl>(page, "tabMaintenanceWindows");
            Assert.Equal(["Windows", "Coverage", "Templates"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));
            var title = Descendants(page).OfType<TextBlock>().Single(t => t.Text == "Maintenance Windows");
            Assert.Same(Application.Current.FindResource("TextFillColorPrimaryBrush"), title.Foreground);

            var grid = ById<DataGrid>(page, "dgWindows");
            Assert.Equal(["Status", "Collection", "Window", "Type", "Recurrence", "Schedule", "Duration", "Next occurrence"], grid.Columns.Select(c => (string)c.Header));
            Assert.Equal(3, grid.Items.Count);
            Assert.False(ById<FrameworkElement>(page, "tabWindowDetails").IsVisible);
            grid.SelectedIndex = 0;
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "tabWindowDetails").IsVisible);
            Assert.Equal(["Patch Tuesday"], viewModel.SelectedWindows.Select(r => r.WindowName));
            Assert.True(ById<System.Windows.Controls.Button>(page, "btnEditWindow").IsEnabled);
            Assert.True(ById<System.Windows.Controls.Button>(page, "btnRemoveWindow").IsEnabled);
            grid.SelectAll();
            StaTestHelper.Flush();
            Assert.Equal(3, viewModel.SelectedWindows.Count);
            viewModel.DetailTabIndex = 1;
            StaTestHelper.Flush();
            Assert.Equal(5, ById<ItemsControl>(page, "lstNextOccurrences").Items.Count);

            tabs.SelectedIndex = 1;
            StaTestHelper.Flush();
            var coverage = ById<DataGrid>(page, "dgCoverage");
            Assert.Equal(4, coverage.Items.Count);
            window.UpdateLayout();
            StaTestHelper.Flush();
            var glyphs = Descendants(coverage).OfType<Wpf.Ui.Controls.SymbolIcon>().Where(i => i.IsVisible).Select(i => i.Symbol).ToList();
            Assert.Equal(2, glyphs.Count(s => s == Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24));
            Assert.Equal(2, glyphs.Count(s => s == Wpf.Ui.Controls.SymbolRegular.DismissCircle24));

            tabs.SelectedIndex = 2;
            StaTestHelper.Flush();
            var templates = ById<DataGrid>(page, "dgTemplates");
            Assert.Equal(2, templates.Items.Count);
            viewModel.SelectedTemplate = viewModel.Templates[0];
            StaTestHelper.Flush();
            Assert.Equal("Every Sunday.", ById<TextBlock>(page, "txtTemplateDescription").Text);
            Assert.False(ById<System.Windows.Controls.Button>(page, "btnDeleteTemplate").IsEnabled);
        });
    }

    [Fact]
    public void AConnectionProblemShowsItsMessageAboveTheTabs()
    {
        WithApplication(ApplicationTheme.Dark, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            harness.Settings.SiteCode = "";
            var page = new MaintenanceWindowsPage(harness.Create());
            var window = new Window { Width = 1200, Height = 800, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                var opened = page.OnNavigatedToAsync();
                StaTestHelper.PumpUntil(() => opened.IsCompleted);
                StaTestHelper.Flush();
                Assert.True(ById<FrameworkElement>(page, "pnlConnection").IsVisible);
                Assert.Equal(MaintenanceWindowsViewModel.SiteCodeMessage, ById<TextBlock>(page, "txtConnectionMessage").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheResultsPanelAndUndoShowAfterAWrite()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var viewModel = (MaintenanceWindowsViewModel)page.DataContext;
            Assert.False(ById<FrameworkElement>(page, "pnlResults").IsVisible);
            var grid = ById<DataGrid>(page, "dgWindows");
            grid.SelectedIndex = 1;
            StaTestHelper.Flush();
            var toggle = viewModel.ToggleEnabledCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => toggle.IsCompleted);
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "pnlResults").IsVisible);
            Assert.True(ById<FrameworkElement>(page, "btnUndoLast").IsVisible);
            Assert.Single(ById<DataGrid>(page, "dgResults").Items);
        });
    }

    [Fact]
    public void ANarrowPageMovesTheDetailsUnderTheGrid()
    {
        Assert.True(MaintenanceWindowsPage.DetailsBelow(900));
        Assert.False(MaintenanceWindowsPage.DetailsBelow(1400));
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            Assert.Equal(1, Grid.GetRow(ById<FrameworkElement>(page, "pnlWindowDetails")));
        }, width: 900);
    }

    public static TheoryData<MaintenanceScheduleKind, ApplicationTheme> KindsAndThemes()
    {
        var data = new TheoryData<MaintenanceScheduleKind, ApplicationTheme>();
        foreach (var kind in Enum.GetValues<MaintenanceScheduleKind>())
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                data.Add(kind, theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndThemes))]
    public void TheEditorRendersEachScheduleKind(MaintenanceScheduleKind kind, ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, harness.Projector, _ => Task.FromResult(true),
                () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows);
            var window = new MaintenanceWindowEditorWindow(editor);
            Show(window);
            try
            {
                editor.Kind = kind;
                StaTestHelper.Flush();
                Assert.Equal("New maintenance window", window.Title);
                Assert.Equal(3, ById<System.Windows.Controls.ListBox>(window, "lstCollections").Items.Count);
                Assert.Equal(editor.ShowInterval, ById<FrameworkElement>(window, "cboInterval").IsVisible);
                Assert.Equal(editor.ShowDayOfWeek, ById<FrameworkElement>(window, "cboDayOfWeek").IsVisible);
                Assert.Equal(editor.ShowDayOfMonth, ById<FrameworkElement>(window, "cboDayOfMonth").IsVisible);
                Assert.Equal(editor.ShowWeekOrder, ById<FrameworkElement>(window, "cboWeekOrder").IsVisible);
                Assert.Equal(editor.ShowPatchOffset, ById<FrameworkElement>(window, "cboPatchOffset").IsVisible);
                Assert.Equal(kind == MaintenanceScheduleKind.OneTime ? 1 : 5, ById<ItemsControl>(window, "lstPreview").Items.Count);
                Assert.Equal(editor.ScheduleText, ById<TextBlock>(window, "txtScheduleText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheTemplateEditorAndTheApplyDialogRender(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewTemplate, harness.Projector, _ => Task.FromResult(true), () => MaintenanceWindowsHarness.Now);
            var editorWindow = new MaintenanceWindowEditorWindow(editor);
            Show(editorWindow);
            try
            {
                Assert.False(ById<FrameworkElement>(editorWindow, "pnlEditorCollection").IsVisible);
                Assert.True(ById<FrameworkElement>(editorWindow, "txtTemplateDescription").IsVisible);
            }
            finally { editorWindow.Close(); }

            var dialog = new MaintenanceApplyTemplateViewModel(harness.Store.Templates, harness.Service.Collections,
                new Dictionary<string, int> { ["LAB00010"] = 2 }, _ => Task.FromResult(true), checkedCollectionIds: ["LAB00012"]);
            var applyWindow = new MaintenanceApplyTemplateWindow(dialog);
            Show(applyWindow);
            try
            {
                Assert.Equal(3, ById<DataGrid>(applyWindow, "dgApplyTargets").Items.Count);
                Assert.Equal(1, dialog.CheckedCount);
                Assert.Equal("Every Sunday.", ById<TextBlock>(applyWindow, "txtApplyTemplateDescription").Text);
            }
            finally { applyWindow.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanWindowShowsALongBulkPlan(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var items = Enumerable.Range(1, 150).Select(i => i % 10 == 0
                ? new ChangePlanItem("Skip", $"Collection {i}: Weekly Sunday 2 AM", "The collection already has a window named Weekly Sunday 2 AM. Not changed.")
                : new ChangePlanItem("Create", $"Collection {i}: Weekly Sunday 2 AM", "From template Weekly Sunday 2 AM. All deployments. Occurs every Sunday at 02:00."));
            var plan = new ChangePlan("Apply template Weekly Sunday 2 AM", "Create 135 windows", items, hasUndo: true);
            var viewModel = new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult(
                c.Plan.Items.Select(i => i.Action == "Skip" ? ChangePlanItemResult.Skip(i, "Not changed.") : new ChangePlanItemResult(i, true, "Created.")).ToList())),
                (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                Assert.Equal(150, ById<DataGrid>(window, "gridChangePlan").Items.Count);
                var confirm = viewModel.ConfirmCommand.ExecuteAsync(null);
                StaTestHelper.PumpUntil(() => confirm.IsCompleted);
                StaTestHelper.Flush();
                Assert.Equal("135 changed, 0 failed, 15 skipped.", viewModel.ResultText);
                Assert.Equal(150, ById<DataGrid>(window, "gridChangePlanResult").Items.Count);
            }
            finally { window.Close(); }
        });
    }
}
