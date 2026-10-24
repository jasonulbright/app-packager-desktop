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
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridRow = System.Windows.Controls.DataGridRow;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The Pivots and Scripts page and its dialogs in real windows, over the fake service, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PivotsScriptsRenderTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;
    private const string LongText = "workstation-of-the-branch-office-in-the-north-west-region-building-seven.contoso.example";

    /// <summary>Room each longest value must leave: a lab window cut a value that a test measured as just fitting.</summary>
    private const double Room = 1.1;

    private static readonly (int Tab, int ResultTab, string Grid)[] AllGrids =
    [
        (VM.PivotTab, VM.ResultsView, "dgResults"), (VM.PivotTab, VM.DevicesView, "dgDevices"), (VM.PivotTab, VM.PipelineView, "dgPipeline"),
        (VM.ScriptTab, VM.DevicesView, "dgApproved"), (VM.SiteScriptsTab, VM.ResultsView, "dgSiteScripts"),
        (VM.HistoryTab, VM.ResultsView, "dgSessions"), (VM.HistoryTab, VM.ResultsView, "dgPivotsHistory")
    ];

    private static PivotsScriptsHarness LongHarness(int devices = 8)
    {
        var h = new PivotsScriptsHarness();
        var ids = new List<int>();
        for (var i = 0; i < devices; i++)
        {
            h.Service.AddDevice(40_000_000 + i, $"WKS-{i:00000}-{LongText}", online: i % 4 != 0);
            ids.Add(40_000_000 + i);
        }
        h.Service.Collections["LAB00099"] = ("Branch office workstations of the north west region building seven", ids);
        h.Service.FinalState = id => id % 7 == 0 ? PivotsDeviceStates.NoResponse : id % 5 == 0 ? PivotsDeviceStates.TimedOut : PivotsDeviceStates.Completed;
        for (var i = 0; i < 6; i++)
            h.Service.AddScript($"Collect the inventory of the {LongText} {i}", "Get-Date", i % 2 == 0 ? PivotsScriptStates.Approved : PivotsScriptStates.Waiting,
                new ScriptParameter("Path", "System.String", false, null));
        for (var i = 0; i < 6; i++)
        {
            h.Audit.Records.Add(new PivotsAuditRecord(new DateTime(2026, 12, 30, 23, 59, 0), @"CONTOSO\labadmin", i % 2 == 0 ? "ApproveScript" : "RunScript", "Finished",
                i == 1 ? "Requested" : "Succeeded", "run-" + i, 20_000, "Collection " + LongText, Guid.NewGuid().ToString().ToUpperInvariant(), new string('A', 64),
                "Sent to 20,000 devices of the collection " + LongText + ". The page waits for the results.") { Verified = i != 2, HasChain = true });
            h.Sessions.Sessions.Add(new PivotsSession("session-" + i, new DateTime(2026, 12, 30, 23, 59, 0), i % 2 == 0 ? PivotsChangeKind.RemoveScript : PivotsChangeKind.RunPivot,
                "Collection " + LongText, null, null, null, null, null, null));
        }
        return h;
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, PivotsScriptsPage, VM, PivotsScriptsHarness> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48, Func<PivotsScriptsHarness>? harness = null)
    {
        WithApplication(theme, () =>
        {
            var h = (harness ?? (() => LongHarness()))();
            var viewModel = h.Create();
            var page = new PivotsScriptsPage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Settle(window);
                test(window, page, viewModel, h);
            }
            finally { window.Close(); }
        });
    }

    /// <summary>Resolves the long collection, runs one query to the end and selects a row in each list, so every grid has rows.</summary>
    private static void Fill(Window window, VM viewModel)
    {
        viewModel.TargetKind = RunTargetKind.Collection;
        viewModel.TargetText = "LAB00099";
        Run(viewModel.ResolveCommand.ExecuteAsync(null));
        viewModel.QueryText = "OS | project Caption, Version";
        for (var i = 0; i < 2; i++)
        {
            Run(viewModel.RunPivotCommand.ExecuteAsync(null));
            Run(viewModel.WaitTask);
        }
        ShowTab(window, viewModel, VM.ScriptTab);
        viewModel.SelectedApproved = viewModel.ApprovedScripts[0];
        ShowTab(window, viewModel, VM.HistoryTab);
        viewModel.SelectedHistory = viewModel.HistoryRows[0];
        ShowTab(window, viewModel, VM.PivotTab);
        Settle(window);
    }

    private static void ShowTab(Window window, VM viewModel, int tab, int? resultTab = null)
    {
        viewModel.SelectedTabIndex = tab;
        if (resultTab is { } r) viewModel.ResultTabIndex = r;
        Run(viewModel.EnsureTabLoadedAsync());
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheFiveTabsRenderAndTheResultGridsVirtualizeTwentyThousandRows(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Assert.Equal(["Pivot", "Script", "Site scripts", "History", "Options"], ById<TabControl>(page, "tabPivots").Items.OfType<TabItem>().Select(t => (string)t.Header));
            Fill(window, viewModel);
            void Virtualized(string id, int rows)
            {
                var grid = ById<DataGrid>(page, id);
                Assert.Equal(rows, grid.Items.Count);
                Assert.InRange(Descendants(grid).OfType<DataGridRow>().Count(), 1, 100);
            }
            var completed = viewModel.DeviceRows.Count(r => r.State == "Completed");
            ShowTab(window, viewModel, VM.PivotTab, VM.ResultsView);
            Virtualized("dgResults", completed);
            ShowTab(window, viewModel, VM.PivotTab, VM.DevicesView);
            Virtualized("dgDevices", 20_000);
            viewModel.SetSelectedDevices(viewModel.DeviceRows);
            Settle(window);
            Assert.Equal(20_000, ById<DataGrid>(page, "dgDevices").SelectedItems.Count);
            ShowTab(window, viewModel, VM.ScriptTab, VM.DevicesView);
            Virtualized("dgDevices", 20_000);
            foreach (var tab in new[] { VM.SiteScriptsTab, VM.HistoryTab, VM.OptionsTab, VM.PivotTab })
            {
                ShowTab(window, viewModel, tab);
                Assert.True(ById<FrameworkElement>(page, "txtPivotsSummary").IsVisible);
            }
            Assert.Contains("Targets: 20,000 devices.".Replace("20,000", PivotsLabels.Number(20_000)), viewModel.PageSummary);
        }, harness: () => LongHarness(20_000));
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
            Fill(window, viewModel);
            foreach (var (tab, resultTab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab, resultTab);
                var grid = ById<DataGrid>(page, id);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, $"{id} at {width}");
            }
        }, width);
    }

    /// <summary>The longest values the columns hold on a real site, by grid and column header.</summary>
    private static readonly (string Grid, string Column, string Text)[] LongValues =
    [
        ("dgDevices", "ResourceID", "2097152999"), ("dgDevices", "State", "No response"), ("dgDevices", "State", "Timed out"), ("dgDevices", "State", "Completed"),
        ("dgDevices", "Online", "Not known"),
        ("dgPipeline", "Run", "999"), ("dgPipeline", "Action", "Run CMPivot"), ("dgPipeline", "Started", "12/30/2026 11:59 PM"), ("dgPipeline", "Targets", "25,000"),
        ("dgPipeline", "State", "Waiting for results"), ("dgPipeline", "State", "Stopped waiting"), ("dgPipeline", "Results", "25,000 results"),
        ("dgApproved", "Version", "123"), ("dgApproved", "Parameters", "12"),
        ("dgApproved", "Author", @"CONTOSO\labadmin"), ("dgApproved", "Approver", @"CONTOSO\labadmin"),
        ("dgSiteScripts", "State", "Waiting for approval"), ("dgSiteScripts", "Version", "123"), ("dgSiteScripts", "Approver", @"CONTOSO\labadmin"),
        ("dgSiteScripts", "GUID", "8F2E3D4C-5B6A-4789-8ABC-DEF012345678"), ("dgSiteScripts", "Author", @"CONTOSO\labadmin"),
        ("dgSessions", "Started", "12/30/2026 11:59 PM"), ("dgSessions", "Action", "Approve script"), ("dgSessions", "Action", "Remove script"), ("dgSessions", "Devices", "25,000"),
        ("dgSessions", "State", "Saved before send"),
        ("dgPivotsHistory", "Time", "12/30/2026 11:59 PM"), ("dgPivotsHistory", "Action", "Approve script"), ("dgPivotsHistory", "Action", "Submit script"),
        ("dgPivotsHistory", "Result", "Succeeded"), ("dgPivotsHistory", "Result", "Requested"), ("dgPivotsHistory", "Verified", "! Not verified"),
        ("dgPivotsHistory", "Verified", "✓ Verified"), ("dgPivotsHistory", "Devices", "25,000"), ("dgPivotsHistory", "User", @"CONTOSO\labadmin")
    ];

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - CollapsedPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    public void TheLongestRealValuesFitTheirColumnsAt1440PixelsWithRoom(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            foreach (var (tab, resultTab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab, resultTab);
                var grid = ById<DataGrid>(page, id);
                foreach (var (_, header, text) in LongValues.Where(v => v.Grid == id))
                {
                    var column = grid.Columns.Single(c => Equals(c.Header, header));
                    var cell = Descendants(grid).OfType<DataGridCell>().First(c => ReferenceEquals(c.Column, column));
                    var block = Descendants(cell).OfType<TextBlock>().First();
                    var padding = cell.ActualWidth - block.ActualWidth;
                    var needed = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace * Room;
                    Assert.True(needed + padding <= column.ActualWidth + 0.5, $"{id} {header} at {width}: \"{text}\" needs {needed + padding:0} px and the column has {column.ActualWidth:0} px.");
                }
            }
        }, width);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryGridShowsReadableRowsAndAVisibleSelection(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            foreach (var (tab, resultTab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab, resultTab);
                var grid = ById<DataGrid>(page, id);
                grid.BringIntoView();
                Settle(window);
                AssertReadableRowsAndVisibleSelection(window, grid, $"pas-{id}-{theme}");
            }
        });
    }

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
        { ApplicationTheme.Light, 900 - CollapsedPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth, 900 - 48 }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryTabFitsItsWidthAndScrollsToEveryControl(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
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
                    AssertReachable(scroller, element, $"{id} at {width}x{height}");
                }
            }

            string[] targets = ["cboTargetKind", "txtTarget", "btnResolve", "btnImportTargets", "txtTargetSummary"];
            string[] results = ["btnStopWaiting", "btnExportCsv", "btnExportJson", "btnCopyPowerShell", "btnSelectionToPivot", "btnSelectionToScript", "btnResultsFolder",
                "txtResultFilter", "tabResults", "dgResults"];
            Check(VM.PivotTab, "scrollPivotTab", [.. targets, "txtQuery", "lstQueryLibrary", "btnUseQuery", "btnRunPivot", .. results]);
            Check(VM.ScriptTab, "scrollScriptTab", [.. targets, "btnRefreshScripts", "dgApproved", "txtScriptDetail", "btnParameters", "btnRunScript", .. results]);
            Check(VM.SiteScriptsTab, "scrollSiteScriptsTab", "txtSiteScriptFilter", "btnRefreshSiteScripts", "dgSiteScripts", "btnApproveScript", "btnDenyScript", "btnRemoveScript",
                "txtDraftName", "txtDraft", "lstScriptLibrary", "btnUseScript", "btnCheckDraft", "btnSubmitDraft");
            Check(VM.HistoryTab, "scrollPivotsHistoryTab", "txtPivotsHistoryFilter", "btnPivotsHistoryRefresh", "btnOpenSession", "dgSessions", "txtPivotsHistoryPath", "dgPivotsHistory",
                "txtPivotsHistoryDetails");
            Check(VM.OptionsTab, "scrollPivotsOptionsTab", "btnTestConnection", "txtLibraryFolder", "btnBrowseLibraryFolder", "btnClearLibraryFolder", "cboWaitMinutes");
            foreach (var id in new[] { "txtPivotsIntro", "txtPivotsSummary", "txtPivotsStatus" })
            {
                var element = ById<FrameworkElement>(page, id);
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Right <= window.ActualWidth + 0.5 && bounds.Bottom <= window.ActualHeight + 0.5,
                    $"{id} ends at {bounds.Right:0},{bounds.Bottom:0} px in a {window.ActualWidth:0}x{window.ActualHeight:0} px window.");
            }
            var tabs = ById<TabControl>(page, "tabPivots");
            Assert.True(tabs.ActualHeight >= 240, $"The tabs get {tabs.ActualHeight:0} px at {width}x{height}.");
        }, width, height);
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

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

    private static string NextFocus(Window window)
    {
        ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Settle(window);
        return FocusedId();
    }

    private static string FocusedId()
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        return focused is null ? "none" : AutomationProperties.GetAutomationId(focused) is { Length: > 0 } id ? id : focused.GetType().Name + " in " + (Ancestor<DataGrid>(focused)?.Name ?? "-");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabReachesEveryGridAsOneStopAndTheFocusedRowIsSelected(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            ShowTab(window, viewModel, VM.PivotTab, VM.DevicesView);
            viewModel.SetSelectedDevices([]);
            Settle(window);
            var device = TabInto(window, ById<UIElement>(page, "txtResultFilter"), ById<DataGrid>(page, "dgDevices"));
            Assert.True(device.IsSelected);
            Assert.Single(viewModel.SelectedDevices);
            var after = NextFocus(window);
            Assert.DoesNotContain("dgDevices", after, StringComparison.Ordinal);

            ShowTab(window, viewModel, VM.PivotTab, VM.ResultsView);
            Assert.True(TabInto(window, ById<UIElement>(page, "txtResultFilter"), ById<DataGrid>(page, "dgResults")).IsSelected);
            ShowTab(window, viewModel, VM.PivotTab, VM.PipelineView);
            Assert.True(TabInto(window, ById<UIElement>(page, "txtResultFilter"), ById<DataGrid>(page, "dgPipeline")).IsSelected);

            ShowTab(window, viewModel, VM.ScriptTab);
            viewModel.SelectedApproved = null;
            Settle(window);
            var approved = TabInto(window, ById<UIElement>(page, "btnRefreshScripts"), ById<DataGrid>(page, "dgApproved"));
            Assert.Same(approved.Item, viewModel.SelectedApproved);
            var afterApproved = NextFocus(window);
            Assert.True(afterApproved == "btnParameters", $"After the approved scripts Tab reaches {afterApproved}.");

            ShowTab(window, viewModel, VM.SiteScriptsTab);
            var site = TabInto(window, ById<UIElement>(page, "btnRefreshSiteScripts"), ById<DataGrid>(page, "dgSiteScripts"));
            Assert.Same(site.Item, viewModel.SelectedSiteScript);

            ShowTab(window, viewModel, VM.HistoryTab);
            Assert.Same(TabInto(window, ById<UIElement>(page, "txtPivotsHistoryFilter"), ById<DataGrid>(page, "dgSessions")).Item, viewModel.SelectedSession);
            Assert.Same(TabInto(window, ById<UIElement>(page, "btnPivotsHistoryRefresh"), ById<DataGrid>(page, "dgPivotsHistory")).Item, viewModel.SelectedHistory);

            foreach (var (tab, resultTab, id) in AllGrids)
            {
                ShowTab(window, viewModel, tab, resultTab);
                var grid = ById<DataGrid>(page, id);
                Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(grid));
                Assert.True(ApplicationPackagerUI.Helpers.DataGridRightClick.GetSelectsRow(grid));
            }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSummaryLineShowsOnEveryTabAfterAReturnToThePage(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            Fill(window, viewModel);
            foreach (var tab in new[] { VM.PivotTab, VM.ScriptTab, VM.SiteScriptsTab, VM.HistoryTab, VM.OptionsTab })
            {
                ShowTab(window, viewModel, tab);
                Run(page.OnNavigatedFromAsync());
                Run(page.OnNavigatedToAsync());
                Settle(window);
                var summary = ById<TextBlock>(page, "txtPivotsSummary");
                Assert.True(summary.IsVisible);
                Assert.StartsWith("Site LAB on cm01.lab.local.", summary.Text);
                Assert.Contains("Last run:", summary.Text);
                Assert.Equal(tab, viewModel.SelectedTabIndex);
            }
        });
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
                Assert.True(bounds.Right <= boxBounds.Right + 0.5 && bounds.Bottom <= boxBounds.Bottom + 0.5, $"{where}: the text of {AutomationProperties.GetAutomationId(box)} is cut.");
                var need = new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, text.ActualWidth + 1) };
                Assert.True(need.Height <= text.ActualHeight + 0.5, $"{where}: the text of {AutomationProperties.GetAutomationId(box)} needs {need.Height:0} px and shows {text.ActualHeight:0} px.");
            }
        }
        return count;
    }

    /// <summary>A script run on a built-in collection: the plan with all three boxes and the script text.</summary>
    private static PivotsPlan ScriptPlan(int devices)
    {
        var h = LongHarness(devices);
        h.Service.Collections["SMS00001"] = ("All Systems", h.Service.Devices.Select(d => d.ResourceId).ToList());
        var targets = h.Service.ResolveTargetsAsync(new RunTarget(RunTargetKind.Collection, "SMS00001"), CancellationToken.None).Result;
        var script = h.Service.AddScript("Collect the inventory", string.Join(Environment.NewLine, Enumerable.Range(1, 60).Select(i => $"Get-ItemProperty 'HKLM:\\SOFTWARE\\Contoso\\Key{i}' # {LongText}")));
        var prepared = h.Service.PrepareAsync(new PivotsChange(PivotsChangeKind.RunScript, targets, null, script.Guid, script.Hash, new Dictionary<string, string> { ["Path"] = LongText },
            null, null, null, false, false, [], [], null), CancellationToken.None).Result;
        var (plan, problem) = PivotsScriptsText.BuildPlan(prepared);
        Assert.Null(problem);
        Assert.NotNull(plan!.Plan.ExtraConfirmation);
        Assert.NotNull(plan.Plan.SecondConfirmation);
        return plan;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheScriptPlanOfFiveHundredDevicesShowsTheTextAndKeepsItsBoxesAndButtonsInsideA1366By768WorkArea(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = ScriptPlan(500);
            var dialog = new ChangePlanViewModel(plan.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                Assert.True(window.ActualHeight <= 728 && window.ActualWidth <= 1366, $"The plan window is {window.ActualWidth:0}x{window.ActualHeight:0} px.");
                foreach (var id in new[] { "chkDestructive", "chkExtra", "chkSecond", "btnConfirm", "btnCancel", "txtNotes", "txtBody" })
                    AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);
                var body = (TextBox)window.FindName("txtBody");
                Assert.Equal(plan.Plan.Body, body.Text);
                Assert.True(body.IsReadOnly);
                Assert.Equal("Script text", AutomationProperties.GetName(body));
                Assert.Contains("Cascadia Mono", body.FontFamily.Source, StringComparison.Ordinal);
                Assert.Equal(PivotsScriptsText.ScriptRunBox, AutomationProperties.GetName((CheckBox)window.FindName("chkDestructive")));
                var grid = (DataGrid)window.FindName("gridPlan");
                Assert.Equal(507, grid.Items.Count);
                Assert.True(grid.ActualHeight >= 60, $"The plan list gets {grid.ActualHeight:0} px.");
                Assert.False(dialog.ConfirmCommand.CanExecute(null));
                dialog.DestructiveAcknowledged = dialog.ExtraAcknowledged = true;
                Assert.False(dialog.ConfirmCommand.CanExecute(null));
                dialog.SecondAcknowledged = true;
                Assert.True(dialog.ConfirmCommand.CanExecute(null));
                window.Width = window.MinWidth;
                Settle(window);
                Assert.True(AssertCheckBoxTextFits(window, window, "plan window at its minimum width") >= 3);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanWindowOpensOnTheFirstLineAndReachesConfirmAfterItsBoxes(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var plan = ScriptPlan(5);
            var dialog = new ChangePlanViewModel(plan.Plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                window.FocusFirstControl();
                Settle(window);
                Assert.Equal("DataGridCell in gridPlan", FocusedId());
                Assert.Equal("chkChangePlanDestructive", NextFocus(window));
                Assert.Equal("chkChangePlanExtra", NextFocus(window));
                Assert.Equal("chkChangePlanSecond", NextFocus(window));
                dialog.DestructiveAcknowledged = dialog.ExtraAcknowledged = dialog.SecondAcknowledged = true;
                Settle(window);
                Assert.Equal("btnChangePlanConfirm", NextFocus(window));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheParametersAndCommentWindowsOpenOnTheirFirstFieldAndKeepTheirButtonsInside(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var parameters = Enumerable.Range(1, 12).Select(i => new ScriptParameter($"Parameter{i}{LongText}", i % 2 == 0 ? "System.Int32" : "System.String", i == 1, i == 3 ? "x" : null)).ToArray();
            var script = new SiteScript("G", "Collect the inventory", "a", "b", PivotsScriptStates.Approved, 1, "PowerShell", "H", parameters, null);
            var dialog = new PivotsParametersViewModel(script, null);
            var window = new PivotsParametersWindow(dialog);
            Show(window);
            try
            {
                Settle(window);
                window.FocusFirstControl();
                Settle(window);
                Assert.Equal("Parameter1" + LongText, AutomationProperties.GetName((DependencyObject)Keyboard.FocusedElement));
                Assert.False(dialog.OkCommand.CanExecute(null));
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Settle(window);
                foreach (var id in new[] { "btnOk", "btnCancel" }) AssertInside(window, (FrameworkElement)window.FindName(id), window.ActualHeight, id);
                var scroller = ById<ScrollViewer>(window, "scrollParameters");
                Assert.True(scroller.ScrollableWidth < 1);
                Assert.True(scroller.ExtentHeight > scroller.ViewportHeight);
            }
            finally { window.Close(); }

            var comment = new PivotsCommentViewModel("Deny Collect the inventory", "Type the reason for the denial.");
            var commentWindow = new PivotsCommentWindow(comment);
            Show(commentWindow);
            try
            {
                Settle(commentWindow);
                ((TextBox)commentWindow.FindName("txtComment")).Focus();
                Settle(commentWindow);
                Assert.Equal("txtComment", FocusedId());
                Assert.False(comment.OkCommand.CanExecute(null));
                comment.Text = "Removes user files.";
                Assert.True(comment.OkCommand.CanExecute(null));
                Assert.Equal("btnCommentOk", NextFocus(commentWindow));
                commentWindow.Width = commentWindow.MinWidth;
                commentWindow.Height = commentWindow.MinHeight;
                Settle(commentWindow);
                AssertInside(commentWindow, (FrameworkElement)commentWindow.FindName("btnOk"), commentWindow.ActualHeight, "btnOk");
            }
            finally { commentWindow.Close(); }
        });
    }
}
