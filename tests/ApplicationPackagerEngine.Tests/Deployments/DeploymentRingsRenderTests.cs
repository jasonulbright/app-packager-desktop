using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using ListBox = System.Windows.Controls.ListBox;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The Rings tab, the ring plan editor and the collection picker in real windows, in both themes, with 2, 13 and 100 rings.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentRingsRenderTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;
    private const string LongName = "Contoso Enterprise Line of Business Application Suite with a very long display name for wrapping checks";

    private static readonly string[] RingGrids = ["dgRingPreview", "dgRingChecks", "dgRingRunRings", "dgRingRuns", "dgRingObjects", "dgRingPlans"];

    private static string Id(int i) => "LABR" + i.ToString("0000", CultureInfo.InvariantCulture);

    private static DeploymentPageHarness Harness(int rings)
    {
        var h = new DeploymentPageHarness();
        h.Service.Add(DeploymentObjectType.Application, "16777399", LongName, "2026.10.20.1234-preview");
        var definitions = Enumerable.Range(1, rings).Select(i => FakeRingService.Ring($"Wave {i} of the finance department rollout", Id(i),
            $"Finance devices in the north-west region building {i}", i == 1 ? "Available" : "Required", i, i == 1 ? null : i + 2, threshold: 90, dpGroup: "Distribution points of the branch offices")).ToList();
        foreach (var i in Enumerable.Range(1, rings)) h.Rings.MemberCounts[Id(i)] = 1234;
        h.Rings.Plans.Add(new RingPlan(Name: "Finance rollout in waves with a long plan name", Description: "Ten 10 % collections, a pilot and a final ring, over many weeks.",
            HoldLaterRings: true, TimeBasedOn: "Utc", Rings: definitions, IsSeed: false));
        var statuses = Enumerable.Range(1, rings).Select(i => (Name: $"Wave {i}", CollectionId: Id(i), Status: i <= rings / 2 ? "Created" : "Held", Threshold: (int?)90, Purpose: "Required")).ToArray();
        h.Rings.AddRun("RUN-20261020-0900", h.SevenZip, statuses);
        h.Rings.AddRun("RUN-20261019-1700", h.Notepad, statuses);
        h.Rings.Counts["RUN-20261020-0900"] = Enumerable.Range(1, rings / 2).Select(i => new RingLiveCounts(Index: i, DeploymentId: $"MCM2{i - 1:0000}", State: "Summarized",
            Targeted: 1234, Success: 1200, Error: 4, InProgress: 30, Unknown: 0, CountsKnown: i % 2 == 1, Message: "")).ToList();
        return h;
    }

    private static void WithRings(ApplicationTheme theme, int rings, Action<Window, DeploymentHelperPage, DeploymentHelperViewModel, DeploymentPageHarness> test,
        double width = 1440 - CollapsedPaneWidth, double height = 900 - 48)
    {
        WithApplication(theme, () =>
        {
            var harness = Harness(rings);
            var viewModel = harness.Create();
            var page = new DeploymentHelperPage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                viewModel.SelectedTabIndex = DeploymentHelperViewModel.RingsTab;
                Run(viewModel.Rings.LoadTask);
                Settle(window);
                Fill(window, viewModel.Rings);
                test(window, page, viewModel, harness);
            }
            finally { window.Close(); }
        });
    }

    private static void Fill(Window window, DeploymentRingsViewModel rings)
    {
        rings.SelectedObject = rings.Objects.Single(o => o.Name == LongName);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name.StartsWith("Finance", StringComparison.Ordinal));
        Run(rings.ExpandCommand.ExecuteAsync(null));
        Run(rings.ValidateCommand.ExecuteAsync(null));
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-20261020-0900");
        Run(rings.CountsTask);
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 13)]
    [InlineData(ApplicationTheme.Dark, 13)]
    [InlineData(ApplicationTheme.Light, 100)]
    [InlineData(ApplicationTheme.Dark, 100)]
    public void EveryRingsGridShowsReadableRowsAndAVisibleSelection(ApplicationTheme theme, int count)
    {
        WithRings(theme, count, (window, page, viewModel, harness) =>
        {
            Assert.Equal(count, viewModel.Rings.PreviewRows.Count);
            Assert.Equal(count, viewModel.Rings.RunRings.Count);
            foreach (var id in RingGrids)
            {
                var grid = ById<DataGrid>(page, id);
                grid.BringIntoView();
                Settle(window);
                Assert.True(grid.EnableRowVirtualization, $"{id} does not virtualize its rows.");
                AssertReadableRowsAndVisibleSelection(window, grid, $"{id}-{theme}-{count}");
            }
            var types = ById<ListBox>(page, "lstRingObjectType");
            types.BringIntoView();
            Settle(window);
            AssertReadableListAndVisibleSelection(window, types, $"lstRingObjectType-{theme}");
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 2)]
    [InlineData(ApplicationTheme.Dark, 13)]
    [InlineData(ApplicationTheme.Light, 100)]
    public void EveryRingsGridShowsAllItsColumnsAt1440PixelsWithTheSidebarCollapsed(ApplicationTheme theme, int count)
    {
        WithRings(theme, count, (window, page, viewModel, harness) =>
        {
            foreach (var id in RingGrids)
            {
                var grid = ById<DataGrid>(page, id);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, id);
            }
        });
    }

    private static readonly string[] DateHeaders = ["Available", "Deadline", "Started", "Time"];
    private const int RingCharacters = 18;

    private static double TextWidth(TextBlock t, string s) =>
        new FormattedText(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch),
            t.FontSize, System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(t).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>Every date cell shows its whole text and every ring cell at least its first 18 characters; returns the number of cells checked.</summary>
    private static int AssertDatesAndRingsFit(Window window, DataGrid grid, string where, HashSet<string> seen)
    {
        var checkedCells = 0;
        for (var i = 0; i < Math.Min(grid.Items.Count, 3); i++)
        {
            var row = Row(grid, i);
            Settle(window);
            foreach (var cell in Descendants(row).OfType<System.Windows.Controls.DataGridCell>())
            {
                var header = cell.Column?.Header as string;
                var isDate = DateHeaders.Contains(header);
                if (!isDate && header is not ("Ring" or "Plan" or "Run")) continue;
                var text = Descendants(cell).OfType<TextBlock>().FirstOrDefault(t => t.Text.Length > 0);
                if (text is null) continue;
                var needed = TextWidth(text, isDate ? text.Text : text.Text[..Math.Min(RingCharacters, text.Text.Length)]);
                Assert.True(text.ActualWidth + 0.5 >= needed, $"{where}: the {header} cell '{text.Text}' shows {text.ActualWidth:0} px of the {needed:0} px it needs.");
                checkedCells++;
                seen.Add($"{AutomationProperties.GetAutomationId(grid)}.{header}");
            }
        }
        return checkedCells;
    }

    public static TheoryData<ApplicationTheme, double, double> GridSizes() => new()
    {
        { ApplicationTheme.Light, 1440 - CollapsedPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48 },
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 }
    };

    [Theory]
    [MemberData(nameof(GridSizes))]
    public void EveryDateAndRingColumnShowsAFullDateAndEighteenCharactersOfARing(ApplicationTheme theme, double width, double height)
    {
        WithRings(theme, 13, (window, page, viewModel, harness) =>
        {
            var cells = 0;
            var seen = new HashSet<string>();
            foreach (var id in RingGrids)
            {
                var grid = ById<DataGrid>(page, id);
                grid.BringIntoView();
                Settle(window);
                cells += AssertDatesAndRingsFit(window, grid, $"{id} at {width}x{height}", seen);
            }
            Assert.True(cells >= 20, $"Only {cells} date and ring cells were checked.");
            Assert.Superset(new HashSet<string> { "dgRingPreview.Available", "dgRingPreview.Deadline", "dgRingPreview.Ring", "dgRingChecks.Ring", "dgRingRuns.Started",
                "dgRingRuns.Run", "dgRingRuns.Plan", "dgRingRunRings.Available", "dgRingRunRings.Deadline", "dgRingRunRings.Ring" }, seen);
        }, width, height);
    }

    private static void AssertReachable(ScrollViewer scroller, FrameworkElement element, string id)
    {
        Assert.True(element.IsVisible, $"{id} is not visible.");
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"{id} spans {bounds.Left:0} to {bounds.Right:0} px in a {scroller.ViewportWidth:0} px tab.");
        Assert.True(bounds.Bottom + scroller.VerticalOffset <= scroller.ExtentHeight + 0.5, $"{id} ends below the area the tab scrolls to.");
    }

    public static TheoryData<ApplicationTheme, double, double, int> Sizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48, 13 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48, 100 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48, 2 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48, 100 }
    };

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
    public void TheRingsTabFitsItsWidthScrollsToEveryControlAndWrapsItsCheckBoxes(ApplicationTheme theme, double width, double height, int count)
    {
        WithRings(theme, count, (window, page, viewModel, harness) =>
        {
            var scroller = ById<ScrollViewer>(page, "scrollRingsTab");
            Assert.True(scroller.ScrollableWidth < 1, $"The Rings tab scrolls sideways by {scroller.ScrollableWidth:0} px at {width}x{height}.");
            foreach (var id in new[] { "lstRingObjectType", "btnRingsRefresh", "btnRingsCancel", "txtRingObjectFilter", "dgRingObjects", "cboRingPlan", "dpRingStart", "txtRingStartTime",
                         "chkRingHold", "btnRingExpand", "btnRingSetCollection", "dgRingPreview", "dgRingChecks", "btnRingValidate", "btnRingCreate", "btnRingPlanNew",
                         "btnRingPlanDelete", "dgRingPlans", "btnRingReconcile", "btnRingPromote", "btnRingUndo", "btnRingClose", "chkRingShowClosed", "dgRingRuns", "dgRingRunRings" })
            {
                var element = ById<FrameworkElement>(page, id);
                element.BringIntoView();
                Settle(window);
                AssertReachable(scroller, element, id);
            }
            Assert.True(AssertCheckBoxTextFits(window, scroller, $"Rings tab at {width}x{height}") >= 2);
            var preview = ById<DataGrid>(page, "dgRingPreview");
            Assert.True(preview.ActualHeight <= preview.MaxHeight + 0.5, $"The preview grid is {preview.ActualHeight:0} px high with {count} rings.");
        }, width, height);
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    private static DataGridRow TabInto(Window window, UIElement start, DataGrid grid)
    {
        (start as FrameworkElement)?.BringIntoView();
        Settle(window);
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
    public void TabReachesEveryRingsGridAndSelectsTheFocusedRow(ApplicationTheme theme)
    {
        WithRings(theme, 13, (window, page, viewModel, harness) =>
        {
            var rings = viewModel.Rings;
            Assert.Same(TabInto(window, ById<UIElement>(page, "txtRingObjectFilter"), ById<DataGrid>(page, "dgRingObjects")).Item, rings.SelectedObject);
            Run(rings.ExpandCommand.ExecuteAsync(null));
            Run(rings.ValidateCommand.ExecuteAsync(null));
            Settle(window);
            Assert.Same(TabInto(window, ById<UIElement>(page, "btnRingExpand"), ById<DataGrid>(page, "dgRingPreview")).Item, rings.SelectedPreviewRow);
            Assert.True(TabInto(window, ById<UIElement>(page, "dgRingPreview"), ById<DataGrid>(page, "dgRingChecks")).IsSelected);
            Assert.Same(TabInto(window, ById<UIElement>(page, "btnRingPlanDelete"), ById<DataGrid>(page, "dgRingPlans")).Item, rings.SelectedPlan);
            Assert.Same(TabInto(window, ById<UIElement>(page, "chkRingShowClosed"), ById<DataGrid>(page, "dgRingRuns")).Item, rings.SelectedRun);
            Assert.Same(TabInto(window, ById<UIElement>(page, "dgRingRuns"), ById<DataGrid>(page, "dgRingRunRings")).Item, rings.SelectedRunRing);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 2)]
    [InlineData(ApplicationTheme.Dark, 2)]
    [InlineData(ApplicationTheme.Light, 100)]
    [InlineData(ApplicationTheme.Dark, 100)]
    public void ThePlanEditorShowsEveryInputAtItsMinimumSize(ApplicationTheme theme, int count)
    {
        WithApplication(theme, () =>
        {
            var h = Harness(count);
            var plan = h.Rings.Plans.Single(p => p.Name.StartsWith("Finance", StringComparison.Ordinal));
            var collections = Enumerable.Range(1, 3).Select(i => new CollectionRow(CollectionId: Id(i), Name: $"Finance {i}", FolderPath: "", LimitingCollectionId: "SMS00001",
                LimitingCollectionName: "All Systems", MemberCount: 9, IsBuiltIn: false, RefreshType: "Periodic", Comment: "", DirectRuleCount: 0, QueryRuleCount: 0,
                IncludeRuleCount: 0, ExcludeRuleCount: 0)).ToList();
            var editor = new RingPlanEditorViewModel(plan, false, h.Rings.Plans, collections, (_, _) => Task.FromResult(true), _ => null);
            var window = new RingPlanEditorWindow(editor);
            Show(window);
            try
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                editor.SelectedRing = editor.Rings[count == 100 ? 56 : 1];
                Settle(window);
                Assert.Equal(count == 100 ? "Ring 57 of 100" : "Ring 2 of 2", editor.RingPosition);
                AssertInside(window, ById<FrameworkElement>(window, "btnRingPlanSave"), window.ActualHeight, "btnRingPlanSave");
                var scroller = ById<ScrollViewer>(window, "scrollRingPlanEditor");
                foreach (var id in new[] { "txtRingPlanName", "chkRingPlanHold", "btnRingsFromCollections", "btnFillOffsets", "dgEditorRings", "btnRingApplyAll", "txtRingName",
                             "btnRingPickCollection", "txtRingThreshold", "cboRingNotification", "chkRingMetered", "cboRingRerun" })
                {
                    var element = ById<FrameworkElement>(window, id);
                    element.BringIntoView();
                    Settle(window);
                    AssertReachable(scroller, element, id);
                }
                Assert.True(AssertCheckBoxTextFits(window, window, "plan editor") >= 7);
                var grid = ById<DataGrid>(window, "dgEditorRings");
                Assert.True(grid.ActualHeight <= grid.MaxHeight + 0.5);
                AssertResizableReadableColumns(grid);
                AssertReadableRowsAndVisibleSelection(window, grid, $"dgEditorRings-{theme}-{count}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheMultiSelectPickerShowsItsCheckBoxesAndReadableRows(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var collections = Enumerable.Range(1, 100).Select(i => new CollectionRow(CollectionId: Id(i), Name: $"Finance devices of building {i} in the north-west region",
                FolderPath: @"Deploy\Finance\North-West", LimitingCollectionId: "SMS00001", LimitingCollectionName: "All Systems", MemberCount: 9, IsBuiltIn: false,
                RefreshType: "Periodic", Comment: "", DirectRuleCount: 0, QueryRuleCount: 0, IncludeRuleCount: 0, ExcludeRuleCount: 0)).ToList();
            var picker = new RingCollectionPickerViewModel("Check the collections", collections, null, multiSelect: true);
            var window = new RingCollectionPickerWindow(picker);
            Show(window);
            try
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                picker.CheckShownCommand.Execute(null);
                Settle(window);
                var grid = ById<DataGrid>(window, "dgPickerCollections");
                Assert.True(Descendants(grid).OfType<CheckBox>().Count(b => b.IsVisible && b.IsChecked == true) >= 2);
                AssertResizableReadableColumns(grid);
                AssertReadableRowsAndVisibleSelection(window, grid, $"dgPickerCollections-{theme}");
                AssertInside(window, ById<FrameworkElement>(window, "btnPickerPick"), window.ActualHeight, "btnPickerPick");
                AssertInside(window, ById<FrameworkElement>(window, "btnPickerCheckShown"), window.ActualHeight, "btnPickerCheckShown");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheOptionsTabShowsTheRingFoldersAndTheHistoryItsRingColumns(ApplicationTheme theme)
    {
        WithRings(theme, 13, (window, page, viewModel, harness) =>
        {
            harness.Audit.Records.Add(harness.Record("Create", DeploymentObjectType.Application, LongName, "LAB00010", "{DEP-1}") with
            {
                Extra = [new("PlanName", "Finance rollout"), new("RingIndex", "1"), new("RingName", "Wave 1"), new("RunId", "RUN-20261020-0900")]
            });
            harness.Audit.Records.Add(harness.Record("Create", DeploymentObjectType.Application, "7-Zip", "LAB00011", "{DEP-2}"));
            viewModel.SelectedTabIndex = DeploymentHelperViewModel.HistoryTab;
            Run(viewModel.History.LoadTask);
            Settle(window);
            var history = ById<DataGrid>(page, "dgHistory");
            Assert.All(history.Columns, c => Assert.Equal(Visibility.Visible, c.Visibility));
            AssertResizableReadableColumns(history);
            AssertNoSidewaysScroll(history, "dgHistory");
            Assert.True(ById<FrameworkElement>(page, "cboHistoryRun").IsVisible);

            viewModel.SelectedTabIndex = DeploymentHelperViewModel.OptionsTab;
            Run(viewModel.Options.LoadTask);
            Settle(window);
            foreach (var id in new[] { "txtRingsFolder", "txtRunsFolder", "btnSaveRingFolders", "btnCheckRingFolders", "txtRingFolderProbe", "txtStaleLockAge" })
            {
                var element = ById<FrameworkElement>(page, id);
                element.BringIntoView();
                Settle(window);
                AssertReachable(ById<ScrollViewer>(page, "scrollOptionsTab"), element, id);
            }
            Assert.Contains("2 hours", viewModel.Options.RingFolders!.StaleLockText);
        });
    }
}
