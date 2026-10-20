using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DatePicker = System.Windows.Controls.DatePicker;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using TabControl = System.Windows.Controls.TabControl;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The Deployment Helper page and its dialogs in real windows, over fake services, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentHelperRenderTests
{
    /// <summary>The main window gives this width to the navigation pane and its border; the page gets the rest.</summary>
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;

    private const string LongName = "Contoso Enterprise Line of Business Application Suite with a very long display name for wrapping checks";

    // The checks come first: a selection in the object or collection grid changes the form, which clears the checks.
    private static readonly string[] AllGrids = ["dgChecks", "dgDpGroups", "dgObjects", "dgCollections"];

    private static DeploymentPageHarness LongHarness()
    {
        var h = new DeploymentPageHarness();
        h.Service.Add(DeploymentObjectType.Application, "16777399", LongName, "2026.10.20.1234-preview");
        h.Collections.Add("LAB00099", "Pilot devices of the finance department in the north-west region building seven", @"Deploy\Finance\North-West\Building 7", members: 1234);
        h.Service.Groups.Add(new DpGroupInfo(Name: "Distribution points of the branch offices in the southern region", Id: "{GRP-3}", Status: "Failed",
            Targeted: 12, Success: 3, InProgress: 4, Error: 5));
        for (var i = 0; i < 6; i++)
            h.Audit.Records.Add(h.Record(i % 2 == 0 ? "Create" : "Undo", DeploymentObjectType.Application, i == 0 ? LongName : $"Application {i}", i % 2 == 0 ? "LAB00010" : "LAB00011",
                $"{{DEP-{i:0000}-AAAA-BBBB-CCCC-DDDDEEEEFFFF}}", purpose: i % 3 == 0 ? "Required" : "Available", when: new DateTime(2026, 10, 19, 8, i, 0)));
        return h;
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, DeploymentHelperPage, DeploymentHelperViewModel, DeploymentPageHarness> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48)
    {
        WithApplication(theme, () =>
        {
            var harness = LongHarness();
            var viewModel = harness.Create();
            var page = new DeploymentHelperPage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Settle(window);
                test(window, page, viewModel, harness);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Fills the Deploy tab: an object with its DP groups, a collection, the checks and the result of one deploy.</summary>
    private static void Fill(Window window, DeploymentHelperViewModel viewModel, DeploymentPageHarness harness)
    {
        var form = viewModel.Deploy;
        form.SelectedObject = form.Objects.Single(o => o.Name == LongName);
        form.SelectedCollection = form.Collections.Single(c => c.CollectionId == "LAB00099");
        StaTestHelper.PumpUntil(() => form.DpGroups.Count == 3);
        harness.Service.Distributed.Add("16777399");
        form.DpGroups[0].IsChecked = true;
        form.DistributeFirst = true;
        harness.Plans.RunUndo = true;
        Run(form.DeployCommand.ExecuteAsync(null));
        Run(form.ValidateCommand.ExecuteAsync(null));
        Settle(window);
    }

    private static void ShowTab(Window window, DeploymentHelperViewModel viewModel, int tab)
    {
        viewModel.SelectedTabIndex = tab;
        Run(viewModel.CurrentTab.LoadTask);
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheThreeTabsRenderWithLongValues(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var tabs = ById<TabControl>(page, "tabDeploymentHelper");
            Assert.Equal(["Deploy", "History", "Options"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));
            Assert.Equal(["Applications", "Packages", "Task Sequences", "Software Update Groups"], ById<ListBox>(page, "lstObjectType").Items.Cast<string>());
            Fill(window, viewModel, harness);
            Assert.Equal(2, ById<DataGrid>(page, "dgDeploymentResults").Items.Count);
            Assert.Equal(5, ById<DataGrid>(page, "dgChecks").Items.Count);
            Assert.True(ById<FrameworkElement>(page, "btnRingDeployment").IsVisible);

            viewModel.Deploy.IsRequired = true;
            Settle(window);
            Assert.True(ById<FrameworkElement>(page, "dpDeadlineDate").IsVisible);
            viewModel.Deploy.TypeIndex = (int)DeploymentObjectType.Package;
            Settle(window);
            Assert.True(ById<FrameworkElement>(page, "cboProgram").IsVisible);
            Assert.True(ById<FrameworkElement>(page, "cboRerun").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "chkShowProgress").IsVisible);

            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            Assert.Equal(6, ById<DataGrid>(page, "dgHistory").Items.Count);
            ShowTab(window, viewModel, DeploymentHelperViewModel.OptionsTab);
            Assert.Equal(3, ById<DataGrid>(page, "dgTemplates").Items.Count);
            Assert.True(ById<FrameworkElement>(page, "txtAuditProbe").IsVisible);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - CollapsedPaneWidth)]
    public void EveryGridShowsAllItsColumnsAt1440Pixels(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel, harness);
            Assert.True(page.PickersSideBySide);
            foreach (var id in AllGrids.Append("dgDeploymentResults"))
            {
                var grid = ById<DataGrid>(page, id);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, id);
            }
            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            AssertResizableReadableColumns(ById<DataGrid>(page, "dgHistory"));
            AssertNoSidewaysScroll(ById<DataGrid>(page, "dgHistory"), "dgHistory");
            ShowTab(window, viewModel, DeploymentHelperViewModel.OptionsTab);
            AssertResizableReadableColumns(ById<DataGrid>(page, "dgTemplates"));
            AssertNoSidewaysScroll(ById<DataGrid>(page, "dgTemplates"), "dgTemplates");
        }, width);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryGridShowsReadableRowsAndAVisibleSelection(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel, harness);
            foreach (var id in AllGrids.Append("dgDeploymentResults"))
            {
                var grid = ById<DataGrid>(page, id);
                grid.BringIntoView();
                Settle(window);
                AssertReadableRowsAndVisibleSelection(window, grid, $"{id}-{theme}");
            }
            var types = ById<ListBox>(page, "lstObjectType");
            types.BringIntoView();
            Settle(window);
            AssertReadableListAndVisibleSelection(window, types, $"lstObjectType-{theme}");
            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgHistory"), $"dgHistory-{theme}");
            ShowTab(window, viewModel, DeploymentHelperViewModel.OptionsTab);
            AssertReadableRowsAndVisibleSelection(window, ById<DataGrid>(page, "dgTemplates"), $"dgTemplates-{theme}");
        });
    }

    /// <summary>The element lies inside the scroller's width and is reachable by a vertical scroll.</summary>
    private static void AssertReachable(ScrollViewer scroller, FrameworkElement element, string id)
    {
        Assert.True(element.IsVisible, $"{id} is not visible.");
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"{id} spans {bounds.Left:0} to {bounds.Right:0} px in a {scroller.ViewportWidth:0} px tab.");
        Assert.True(bounds.Bottom + scroller.VerticalOffset <= scroller.ExtentHeight + 0.5, $"{id} ends below the area the tab scrolls to.");
    }

    public static TheoryData<ApplicationTheme, double, double> Sizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48 }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryTabFitsItsWidthAndScrollsToEveryControl(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel, harness);
            Assert.Equal(width >= 1000 + 48, page.PickersSideBySide);
            void Check(int tab, string scrollId, params string[] ids)
            {
                ShowTab(window, viewModel, tab);
                var scroller = ById<ScrollViewer>(page, scrollId);
                Assert.True(scroller.ScrollableWidth < 1, $"{scrollId} scrolls sideways by {scroller.ScrollableWidth:0} px at {width}x{height}.");
                foreach (var id in ids)
                {
                    var element = ById<FrameworkElement>(page, id);
                    element.BringIntoView();
                    Settle(window);
                    AssertReachable(scroller, element, id);
                }
            }

            Check(DeploymentHelperViewModel.DeployTab, "scrollDeployTab", "lstObjectType", "cboTemplate", "btnSaveTemplate", "btnDeployRefresh", "btnDeployCancel", "dgObjects", "dgCollections", "cboNotification",
                "chkRebootOutside", "txtRequiredOnlyNote", "dgDpGroups", "btnDistribute", "chkDistributeFirst", "dgChecks", "btnValidate", "btnDeploy");
            Check(DeploymentHelperViewModel.HistoryTab, "scrollHistoryTab", "txtHistoryFilter", "btnHistoryCancel", "btnHistoryFolder", "btnRemoveDeployment", "dgHistory");
            Check(DeploymentHelperViewModel.OptionsTab, "scrollOptionsTab", "btnOptionsRefresh", "btnOptionsCancel", "txtAuditLogPath", "btnCheckAuditLog", "btnDeleteTemplate", "dgTemplates", "txtConnection");
            var header = ById<FrameworkElement>(page, "btnRingDeployment");
            Assert.True(header.TransformToAncestor(window).TransformBounds(new Rect(header.RenderSize)).Right <= window.ActualWidth + 0.5);
        }, width, height);
    }

    /// <summary>Every visible check box shows its whole text: no text line runs past the right or bottom edge of its box.</summary>
    private static int AssertCheckBoxTextFits(Window window, DependencyObject root, string where)
    {
        var count = 0;
        foreach (var box in Descendants(root).OfType<CheckBox>().Where(b => b.IsVisible))
        {
            var boxBounds = box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize));
            foreach (var text in Descendants(box).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0))
            {
                count++;
                var bounds = text.TransformToAncestor(window).TransformBounds(new Rect(text.RenderSize));
                Assert.True(bounds.Right <= boxBounds.Right + 0.5 && bounds.Bottom <= boxBounds.Bottom + 0.5,
                    $"{where}: the text of {AutomationProperties.GetAutomationId(box)} ends at {bounds.Right:0},{bounds.Bottom:0} px, its box at {boxBounds.Right:0},{boxBounds.Bottom:0} px.");
                Assert.True(boxBounds.Right <= window.ActualWidth + 0.5, $"{where}: {AutomationProperties.GetAutomationId(box)} ends at {boxBounds.Right:0} px in a {window.ActualWidth:0} px window.");
            }
        }
        return count;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryCheckBoxOfThePageShowsItsWholeText(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel, harness);
            viewModel.Deploy.IsRequired = true;
            var seen = 0;
            foreach (var type in Enum.GetValues<DeploymentObjectType>())
            {
                viewModel.Deploy.TypeIndex = (int)type;
                Settle(window);
                seen += AssertCheckBoxTextFits(window, ById<FrameworkElement>(page, "scrollDeployTab"), $"{type} at {width}x{height}");
            }
            Assert.True(seen >= 12, $"Only {seen} check box texts were measured.");
            AssertCheckBoxTextFits(window, page, $"page at {width}x{height}");
        }, width, height);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryCheckBoxOfTheTemplateEditorShowsItsWholeTextAtTheMinimumSize(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = new DeploymentPageHarness();
            var window = new DeploymentTemplateEditorWindow(new DeploymentTemplateEditorViewModel(h.Templates.Templates[2], h.Templates.All(), (_, _) => Task.FromResult(true)));
            Show(window);
            try
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Settle(window);
                Assert.True(AssertCheckBoxTextFits(window, window, "template editor") >= 7);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDeployAndOptionsTabsHaveRefreshAndCancel(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Assert.Same(viewModel.Deploy.RefreshCommand, ById<System.Windows.Controls.Primitives.ButtonBase>(page, "btnDeployRefresh").Command);
            Assert.Same(viewModel.Deploy.CancelLoadCommand, ById<System.Windows.Controls.Primitives.ButtonBase>(page, "btnDeployCancel").Command);
            ShowTab(window, viewModel, DeploymentHelperViewModel.OptionsTab);
            Assert.Same(viewModel.Options.RefreshCommand, ById<System.Windows.Controls.Primitives.ButtonBase>(page, "btnOptionsRefresh").Command);
            Assert.Same(viewModel.Options.CancelLoadCommand, ById<System.Windows.Controls.Primitives.ButtonBase>(page, "btnOptionsCancel").Command);
            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            Assert.Same(viewModel.History.CancelLoadCommand, ById<System.Windows.Controls.Primitives.ButtonBase>(page, "btnHistoryCancel").Command);
            Assert.True(ById<FrameworkElement>(page, "txtHistoryCap").Visibility != Visibility.Visible);
        });
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
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
        for (var i = 0; i < 40; i++)
        {
            ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Settle(window);
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
    public void TabReachesEveryGridAndTheArrowKeysSelectTheFocusedRow(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var form = viewModel.Deploy;
            form.SelectedObject = null;
            Settle(window);
            var objects = TabInto(window, ById<UIElement>(page, "txtObjectFilter"), ById<DataGrid>(page, "dgObjects"));
            Assert.Same(objects.Item, form.SelectedObject);
            Press(window, Key.Down);
            Assert.Same(form.Objects[1], form.SelectedObject);

            var collections = TabInto(window, ById<UIElement>(page, "txtCollectionFilter"), ById<DataGrid>(page, "dgCollections"));
            Assert.Same(collections.Item, form.SelectedCollection);

            StaTestHelper.PumpUntil(() => form.DpGroups.Count == 3);
            Settle(window);
            Assert.True(TabInto(window, ById<UIElement>(page, "cboNotification"),
                ById<DataGrid>(page, "dgDpGroups")).IsSelected);
            Run(form.ValidateCommand.ExecuteAsync(null));
            Settle(window);
            Assert.True(TabInto(window, ById<UIElement>(page, "chkDistributeFirst"),
                ById<DataGrid>(page, "dgChecks")).IsSelected);

            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            var history = TabInto(window, ById<UIElement>(page, "btnHistoryRefresh"), ById<DataGrid>(page, "dgHistory"));
            Assert.Same(history.Item, viewModel.History.SelectedRow);
            Assert.True(viewModel.History.RemoveDeploymentCommand.CanExecute(null));

            ShowTab(window, viewModel, DeploymentHelperViewModel.OptionsTab);
            var template = TabInto(window, ById<UIElement>(page, "btnNewTemplate"), ById<DataGrid>(page, "dgTemplates"));
            Assert.Same(template.Item, viewModel.Options.SelectedTemplate);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AConfirmedCreateKeepsTheObjectAndTheProgramOfThePackageAndClearsOnlyTheChecks(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var form = viewModel.Deploy;
            form.TypeIndex = (int)DeploymentObjectType.Package;
            StaTestHelper.PumpUntil(() => !form.IsLoading && form.Objects.Count > 0);
            form.SelectedObject = form.Objects.Single(o => o.Object.Id == harness.Office.Id);
            StaTestHelper.PumpUntil(() => !form.IsLoadingDetails && form.Programs.Count == 2);
            Settle(window);
            form.SelectedProgram = "Uninstall";
            form.SelectedCollection = form.Collections.Single(c => c.CollectionId == "LAB00010");
            Settle(window);
            Run(form.ValidateCommand.ExecuteAsync(null));
            Assert.NotEmpty(form.Checks);

            Run(form.DeployCommand.ExecuteAsync(null));
            StaTestHelper.PumpUntil(() => !form.IsLoadingDetails);
            Settle(window);

            Assert.Single(harness.Service.Deployments);
            Assert.Equal(harness.Office.Id, form.SelectedObject?.Object.Id);
            Assert.Equal("LAB00010", form.SelectedCollection?.CollectionId);
            Assert.Equal("Uninstall", form.SelectedProgram);
            Assert.Equal("Uninstall", ById<ComboBox>(page, "cboProgram").SelectedItem);
            Assert.Empty(form.Checks);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ValidateKeepsTheFocusOnItsButtonAndDeployIsTheNextTabStop(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel, harness);
            var validate = ById<Button>(page, "btnValidate");
            var deploy = ById<Button>(page, "btnDeploy");
            validate.BringIntoView();
            Settle(window);
            Assert.True(Keyboard.Focus(validate) == validate);
            harness.Service.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            validate.Command.Execute(null);
            Settle(window);
            Assert.False(validate.IsEnabled);
            harness.Service.PrepareGate.SetResult();
            StaTestHelper.PumpUntil(() => !viewModel.Deploy.IsValidating && validate.IsEnabled);
            Settle(window);

            Assert.NotEmpty(viewModel.Deploy.Checks);
            Assert.Same(validate, Keyboard.FocusedElement);
            var presses = 0;
            while (!ReferenceEquals(Keyboard.FocusedElement, deploy) && presses < 10)
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Settle(window);
                presses++;
            }
            Assert.True(presses <= 3, $"Deploy is {presses} Tab presses after Validate.");
        });
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheCheckBoxOfEachDpGroupFitsItsCell(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel, harness);
            var grid = ById<DataGrid>(page, "dgDpGroups");
            grid.BringIntoView();
            Settle(window);
            var boxes = Descendants(grid).OfType<CheckBox>().Where(b => b.IsVisible).ToList();
            Assert.Equal(3, boxes.Count);
            foreach (var box in boxes)
            {
                var cell = Assert.IsType<DataGridCell>(Ancestor<DataGridCell>(box), exactMatch: false);
                var cellBounds = cell.TransformToAncestor(window).TransformBounds(new Rect(cell.RenderSize));
                var boxBounds = box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize));
                Assert.True(boxBounds.Left >= cellBounds.Left - 0.5 && boxBounds.Right <= cellBounds.Right + 0.5,
                    $"The check box spans {boxBounds.Left:0} to {boxBounds.Right:0} px in a cell from {cellBounds.Left:0} to {cellBounds.Right:0} px.");
                var needed = box.DesiredSize.Width - box.Margin.Left - box.Margin.Right;
                Assert.True(needed <= box.ActualWidth + 0.5, $"The check box needs {needed:0} px and has {box.ActualWidth:0} px.");
                foreach (var part in Descendants(box).OfType<FrameworkElement>().Where(e => e.IsVisible && e.ActualWidth > 0))
                {
                    var partBounds = part.TransformToAncestor(window).TransformBounds(new Rect(part.RenderSize));
                    Assert.True(partBounds.Left >= cellBounds.Left - 0.5 && partBounds.Right <= cellBounds.Right + 0.5,
                        $"{part.Name} of the check box spans {partBounds.Left:0} to {partBounds.Right:0} px, the cell {cellBounds.Left:0} to {cellBounds.Right:0} px.");
                    var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(part)?.Bounds;
                    Assert.True(clip is null || (clip.Value.Width >= part.ActualWidth - 0.5 && clip.Value.Height >= part.ActualHeight - 0.5),
                        $"{part.Name} of the check box is {part.ActualWidth:0}x{part.ActualHeight:0} px and shows {clip?.Width:0}x{clip?.Height:0} px.");
                }
            }
        }, width, height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheHistoryShowsResultAndDeploymentIdWithoutASidewaysScroll(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            var grid = ById<DataGrid>(page, "dgHistory");
            grid.BringIntoView();
            Settle(window);
            Assert.True(grid.FrozenColumnCount >= 1);
            var scroller = Descendants(grid).OfType<ScrollViewer>().First();
            Assert.Equal(0, scroller.HorizontalOffset);
            var gridBounds = grid.TransformToAncestor(window).TransformBounds(new Rect(grid.RenderSize));
            foreach (var name in new[] { "Time", "Event", "Result", "Object", "Deployment ID" })
            {
                var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => Equals(h.Column?.Header, name));
                var bounds = header.TransformToAncestor(window).TransformBounds(new Rect(header.RenderSize));
                Assert.True(header.IsVisible && bounds.Right <= gridBounds.Left + scroller.ViewportWidth + 0.5,
                    $"{name} ends at {bounds.Right - gridBounds.Left:0} px in a {scroller.ViewportWidth:0} px grid at {width}x{height}.");
            }
        }, width, height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheHistoryShowsTheVerifiedMarkAndACopyableDetailsLine(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var records = harness.Audit.Records;
            records[0] = records[0] with { Verified = true, HasChain = true, Message = "Created the deployment of " + LongName + "." };
            records[2] = records[2] with
            {
                Verified = false, HasChain = false, Source = DeploymentAuditLog.SourceName, Result = "Unknown",
                UnclearDeploymentIds = "{DEP-0007-AAAA-BBBB-CCCC-DDDDEEEEFFFF}; {DEP-0008-AAAA-BBBB-CCCC-DDDDEEEEFFFF}"
            };
            records[4] = records[4] with { Verified = false, HasChain = true };
            ShowTab(window, viewModel, DeploymentHelperViewModel.HistoryTab);
            var grid = ById<DataGrid>(page, "dgHistory");
            grid.BringIntoView();
            Settle(window);

            var scroller = Descendants(grid).OfType<ScrollViewer>().First();
            var gridBounds = grid.TransformToAncestor(window).TransformBounds(new Rect(grid.RenderSize));
            var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => Equals(h.Column?.Header, "Verified"));
            var headerBounds = header.TransformToAncestor(window).TransformBounds(new Rect(header.RenderSize));
            Assert.True(header.IsVisible && headerBounds.Right <= gridBounds.Left + scroller.ViewportWidth + 0.5,
                $"Verified ends at {headerBounds.Right - gridBounds.Left:0} px in a {scroller.ViewportWidth:0} px grid at {width}x{height}.");
            var marks = Descendants(grid).OfType<TextBlock>().Where(t => t.IsVisible && t.Text is "✓" or "!").ToList();
            Assert.Equal(["!", "!", "✓"], marks.Select(t => t.Text).Order(StringComparer.Ordinal));
            Assert.All(marks, t => Assert.True(t.ActualWidth > 2 && t.ActualHeight > 8, $"The mark {t.Text} is {t.ActualWidth:0}x{t.ActualHeight:0} px."));
            Assert.Contains(marks, t => Equals(t.ToolTip, "Verified"));

            var details = ById<System.Windows.Controls.TextBox>(page, "txtHistoryDetails");
            Assert.False(details.IsVisible);
            viewModel.History.SelectedRow = viewModel.History.Rows.Single(r => ReferenceEquals(r.Record, records[2]));
            Settle(window);
            Assert.True(details.IsReadOnly);
            Assert.Equal("Not verified (written before the chain)" + Environment.NewLine
                + "Unclear deployment IDs: {DEP-0007-AAAA-BBBB-CCCC-DDDDEEEEFFFF}; {DEP-0008-AAAA-BBBB-CCCC-DDDDEEEEFFFF}", details.Text);
            details.BringIntoView();
            Settle(window);
            AssertReachable(ById<ScrollViewer>(page, "scrollHistoryTab"), details, "txtHistoryDetails");
            Assert.True(details.ExtentHeight <= details.ViewportHeight + 0.5, $"The details need {details.ExtentHeight:0} px and show {details.ViewportHeight:0} px at {width}x{height}.");
            details.SelectAll();
            Assert.Equal(details.Text, details.SelectedText);
            AssertReadableRowsAndVisibleSelection(window, grid, $"dgHistory-verified-{theme}-{width}");
        }, width, height);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheDatePickersShowTheirWholeDate(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            viewModel.Deploy.IsRequired = true;
            viewModel.Deploy.AvailableDate = new DateTime(2026, 9, 30);
            viewModel.Deploy.DeadlineDate = new DateTime(2026, 9, 30);
            Settle(window);
            foreach (var id in new[] { "dpAvailableDate", "dpDeadlineDate" })
            {
                var picker = ById<DatePicker>(page, id);
                picker.BringIntoView();
                Settle(window);
                var text = Descendants(picker).OfType<System.Windows.Controls.Primitives.DatePickerTextBox>().Single();
                Assert.NotEmpty(text.Text);
                Assert.True(text.ExtentWidth <= text.ViewportWidth + 0.5, $"{id} shows '{text.Text}' in {text.ViewportWidth:0} px; it needs {text.ExtentWidth:0} px.");
                Assert.Contains("2026", picker.ToolTip as string ?? "");
                Assert.Contains(new DateTime(2026, 9, 30).ToString("dddd", System.Globalization.CultureInfo.CurrentCulture), picker.ToolTip as string ?? "");
            }
        }, width, height);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheRequiredPlanShowsTheSecondCheckBoxAndConfirmStaysOffUntilItIsChecked(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = LongHarness();
            var create = new DeploymentChange(Kind: DeploymentChangeKind.Create, Object: h.SevenZip,
                Collection: new DeploymentCollection("LAB00099", "Pilot devices of the finance department", "", 1234),
                Options: new DeploymentOptions(Purpose: "Required", AvailableTime: new DateTime(2026, 10, 20, 7, 0, 0), Deadline: new DateTime(2026, 10, 22, 8, 0, 0),
                    UtcBasis: false, UserNotification: "DisplayAll", OverrideServiceWindow: true, RebootOutsideServiceWindow: true, AllowMetered: false,
                    NetworkBehavior: "", RerunBehavior: "", ShowProgress: true, AllowFallback: false, FallbackToDp: true, PostRebootScan: true, SuppressRestart: false),
                DpGroups: ["All DPs"], CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: null,
                Checks: DeploymentText.CheckNames.Select(n => new PreflightCheck(Name: n, Result: "Pass", Message: "")).ToList(), Impact: ["The audit log can be written."], CreatedId: null);
            var (plan, problem) = DeploymentText.BuildDeployPlan([create], h.Clock);
            Assert.Null(problem);
            var dialog = new ChangePlanViewModel(plan!.Plan, h.Writer().RunFor(plan), h.Writer().UndoFor(plan));
            var window = new ChangePlanWindow(dialog);
            Show(window);
            try
            {
                var extra = (CheckBox)window.FindName("chkExtra");
                Assert.True(extra.IsVisible);
                Assert.Contains("Required deployment installs 7-Zip", dialog.ExtraConfirmationText);
                Assert.Contains("is in the past", dialog.ExtraConfirmationText);
                AssertInside(window, extra, window.ActualHeight, "chkExtra");
                Assert.False(dialog.ConfirmCommand.CanExecute(null));
                extra.IsChecked = true;
                Assert.True(dialog.ConfirmCommand.CanExecute(null));
                var option = (CheckBox)window.FindName("chkOption");
                Assert.True(option.IsVisible);
                Assert.False(option.IsChecked);
                Assert.Equal(DeploymentText.CreateNowChoice, dialog.OptionalChoiceText);
                AssertInside(window, option, window.ActualHeight, "chkOption");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryCellOfATallPlanRowStartsAtTheTopOfTheRow(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var detail = string.Join(Environment.NewLine, Enumerable.Range(1, 60).Select(i => $"Line {i}: {LongName}."));
            var plan = new ChangePlan("Deploy", "Create deployment", [new ChangePlanItem("Create deployment", "7-Zip to Pilot", detail), new ChangePlanItem("Distribute content", "7-Zip", "One line.")]);
            var window = new ChangePlanWindow(new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult([]))));
            Show(window);
            try
            {
                var grid = (DataGrid)window.FindName("gridPlan");
                Settle(window);
                var row = Row(grid, 0);
                Assert.True(row.ActualHeight > window.ActualHeight, $"The row is {row.ActualHeight:0} px high.");
                var cells = Descendants(row).OfType<DataGridCell>().ToList();
                Assert.Equal(3, cells.Count);
                foreach (var cell in cells)
                {
                    var text = Descendants(cell).OfType<TextBlock>().First(t => t.Text.Length > 0);
                    var top = text.TranslatePoint(new System.Windows.Point(0, 0), row).Y;
                    Assert.True(top < 16, $"'{text.Text[..Math.Min(20, text.Text.Length)]}' starts {top:0} px below the top of a {row.ActualHeight:0} px row.");
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheTemplateEditorShowsEveryInputAtItsMinimumSize(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var h = new DeploymentPageHarness();
            var editor = new DeploymentTemplateEditorViewModel(h.Templates.Templates[2], h.Templates.All(), (_, _) => Task.FromResult(true));
            var window = new DeploymentTemplateEditorWindow(editor);
            Show(window);
            try
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Settle(window);
                var footer = ById<FrameworkElement>(window, "btnTemplateSave");
                AssertInside(window, footer, window.ActualHeight, "btnTemplateSave");
                var scroller = ById<ScrollViewer>(window, "scrollTemplateEditor");
                foreach (var id in new[] { "txtTemplateName", "txtTemplateOffset", "cboTemplateNotification", "chkTemplateScan" })
                {
                    var element = ById<FrameworkElement>(window, id);
                    element.BringIntoView();
                    Settle(window);
                    AssertReachable(scroller, element, id);
                }
                Assert.Equal("Lab Nightly", editor.Name);
            }
            finally { window.Close(); }
        });
    }
}
