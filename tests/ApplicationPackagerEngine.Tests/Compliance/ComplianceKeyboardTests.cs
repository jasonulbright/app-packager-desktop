using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.ComplianceRenderTests;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>Keyboard-only use of the Compliance section: Tab reaches every grid, and the arrow keys select the focused row.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ComplianceKeyboardTests
{
    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    private static void Tab(Window window)
    {
        var current = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
        current.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Settle(window);
    }

    private static void Press(Window window, Key key)
    {
        var target = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
        var source = PresentationSource.FromVisual(target)!;
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        Settle(window);
    }

    /// <summary>Presses Tab from <paramref name="start"/> until the focus is inside the grid; fails with the focus path when it never gets there.</summary>
    private static DataGridRow TabInto(Window window, UIElement start, DataGrid grid)
    {
        Assert.True(Keyboard.Focus(start) == start || start.IsKeyboardFocusWithin, $"{AutomationProperties.GetAutomationId(start)} takes no focus.");
        Settle(window);
        var path = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            Tab(window);
            var focused = Keyboard.FocusedElement as DependencyObject;
            path.Add(focused is FrameworkElement f ? $"{f.GetType().Name}({AutomationProperties.GetAutomationId(f)})" : focused?.GetType().Name ?? "none");
            if (focused is not null && Ancestor<DataGrid>(focused) == grid) return Assert.IsType<DataGridRow>(Ancestor<DataGridRow>(focused), exactMatch: false);
        }
        Assert.Fail($"Tab never reaches {AutomationProperties.GetAutomationId(grid)}: {string.Join(" > ", path)}");
        return null!;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabReachesTheCatalogAndTheArrowKeysSelectAnEntryForDeploy(ApplicationTheme theme)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            var catalog = viewModel.Catalog;
            catalog.SelectedRow = null;
            Settle(window);
            Assert.False(catalog.DeployCommand.CanExecute(null));
            var grid = ById<DataGrid>(page, "dgCatalog");

            var row = TabInto(window, ById<UIElement>(page, "chkCatalogDetails"), grid);
            Assert.True(row.IsSelected);
            Assert.Same(row.Item, catalog.SelectedRow);
            Assert.True(catalog.DeployCommand.CanExecute(null));

            Press(window, Key.Down);
            Assert.Same(catalog.Rows[grid.Items.IndexOf(row.Item) + 1], catalog.SelectedRow);
            Assert.Same(catalog.SelectedRow, Ancestor<DataGridRow>((DependencyObject)Keyboard.FocusedElement)!.Item);

            var last = catalog.Rows[^1];
            catalog.SelectedRow = last;
            Settle(window);
            Assert.Same(last, TabInto(window, ById<UIElement>(page, "chkCatalogDetails"), grid).Item);
            Assert.Same(last, catalog.SelectedRow);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabReachesTheSettingsAndTheArrowKeysSelectASettingForEdit(ApplicationTheme theme)
    {
        WithSection(theme, (window, page, viewModel, harness) =>
        {
            ShowTab(window, viewModel, ComplianceViewModel.EditorTab);
            var editor = viewModel.Editor;
            editor.SelectedItem = editor.Items.Single(i => i.CiId == "CI-SMB");
            Run(editor.SettingsLoad);
            editor.SelectedSetting = null;
            Settle(window);
            Assert.False(editor.EditSettingCommand.CanExecute(null));
            var grid = ById<DataGrid>(page, "dgSettings");

            var row = TabInto(window, ById<UIElement>(page, "btnEditorRefresh"), grid);
            Assert.True(row.IsSelected);
            Assert.Same(row.Item, editor.SelectedSetting);
            Assert.True(editor.EditSettingCommand.CanExecute(null));

            Press(window, Key.Down);
            Assert.Same(editor.Settings[grid.Items.IndexOf(row.Item) + 1], editor.SelectedSetting);
        });
    }

    [Fact]
    public void TabReachesEveryGridOfTheLiveAndAuditingTabs()
    {
        WithSection(ApplicationTheme.Light, (window, page, viewModel, harness) =>
        {
            ShowTab(window, viewModel, ComplianceViewModel.LiveTab);
            viewModel.Live.SelectedItem = null;
            viewModel.Live.SelectedBaseline = null;
            Settle(window);
            var filter = ById<UIElement>(page, "txtLiveFilter");
            var item = TabInto(window, filter, ById<DataGrid>(page, "dgLiveItems"));
            Assert.Same(item.Item, viewModel.Live.SelectedItem);
            var baseline = TabInto(window, filter, ById<DataGrid>(page, "dgLiveBaselines"));
            Assert.Same(baseline.Item, viewModel.Live.SelectedBaseline);
            var deployment = TabInto(window, filter, ById<DataGrid>(page, "dgDeployments"));
            Assert.True(deployment.IsSelected);

            ShowTab(window, viewModel, ComplianceViewModel.AuditingTab);
            var drift = TabInto(window, ById<UIElement>(page, "txtAuditingFilter"), ById<DataGrid>(page, "dgDrift"));
            Assert.Same(drift.Item, viewModel.Auditing.SelectedDrift);
            viewModel.Auditing.SelectedTabIndex = ComplianceAuditingViewModel.ComplianceTab;
            Settle(window);
            var counts = TabInto(window, ById<UIElement>(page, "txtAuditingFilter"), ById<DataGrid>(page, "dgComplianceCounts"));
            Assert.Same(counts.Item, viewModel.Auditing.SelectedCompliance);
        });
    }
}
