using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using MenuItem = System.Windows.Controls.MenuItem;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TabControl = System.Windows.Controls.TabControl;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>The Installer Analysis page in real windows over fake services, with long values, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class InstallerAnalysisRenderTests
{
    /// <summary>The main window gives this width to the navigation pane and its border; the page gets the rest.</summary>
    private const double NavigationPaneWidth = 230;

    /// <summary>The width of the collapsed navigation pane.</summary>
    private const double CollapsedPaneWidth = 48;

    private static readonly string LongText = string.Join(" ", Enumerable.Repeat("Contoso Enterprise Deployment Toolkit long value", 8));

    private static readonly string[] GridIds =
        ["dgSource", "dgDeployment", "dgModes", "dgPostPatch", "dgMetadata", "dgMsi", "dgPayload", "dgInner", "dgFiles", "dgRegistry", "dgStrings"];

    private static int TabOf(string gridId) => gridId switch
    {
        "dgMsi" => InstallerAnalysisViewModel.MsiTab,
        "dgPayload" => InstallerAnalysisViewModel.PayloadTab,
        "dgInner" => InstallerAnalysisViewModel.InnerTab,
        "dgFiles" => InstallerAnalysisViewModel.FilesTab,
        "dgRegistry" => InstallerAnalysisViewModel.RegistryTab,
        "dgStrings" => InstallerAnalysisViewModel.StringsTab,
        _ => InstallerAnalysisViewModel.OverviewTab
    };

    private static void Fill(InstallerAnalysisHarness h, string path)
    {
        var report = FakeInstallerAnalysisService.Report(path, "WiX Burn",
            notes: ["The NSIS header uses bzip2 compression and is not decoded.", "The payload list stops at the entry limit. " + LongText],
            postPatch: new EffectiveDetection(ProductCode: "{11111111-2222-3333-4444-555555555555}", DisplayName: "Contoso Tool " + LongText, DisplayVersion: "1.2.4.5",
                ArpKey: @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}", Explanation: "The patch raises the version. " + LongText)
            { BaseVersion = "1.2.0.0", PatchFile = "Contoso-Update-" + LongText.Replace(' ', '-') + ".msp" });
        h.Service.Reports[path] = report with
        {
            Deployment = [.. report.Deployment, new AnalysisFact("SilentInstall", LongText, "Predicted", "Switch database " + LongText)],
            Modes = [.. report.Modes.Select(m => m with { InstallFolder = m.InstallFolder + @"\" + LongText, UninstallCommand = m.UninstallCommand + " " + LongText, InstallCommand = m.InstallCommand + " " + LongText })],
            MsiProperties = [.. report.MsiProperties, new AnalysisFact("ARPCOMMENTS", LongText, "Confirmed", "Property table")],
            DetectionHint = report.DetectionHint + " " + LongText
        };
        h.Service.Payload =
        [
            .. InstallerAnalysisHarness.SamplePayload,
            FakeInstallerAnalysisService.Entry(@"app\" + string.Join(@"\", Enumerable.Repeat("very long folder name", 10)) + @"\tool.exe", 12345),
            FakeInstallerAnalysisService.Entry(@"redist\" + LongText.Replace(' ', '_') + ".msi", 999, kind: "InnerInstaller", innerType: "MSI " + LongText)
        ];
        h.Service.Registry =
        [
            FakeInstallerAnalysisService.Write("MsiRegistryTable", "HKLM", @"SOFTWARE\Contoso\" + LongText, "Version", "1.2.3", condition: "NOT Installed AND " + LongText)
                with { View = "64", Section = "MainComponent " + LongText },
            FakeInstallerAnalysisService.Write("NsisScript", "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run", "Contoso", LongText, "REG_EXPAND_SZ") with { Section = "Startup" },
            FakeInstallerAnalysisService.Write("InnoSetup", "HKLM", @"SOFTWARE\Classes\.contoso", "", "") with { Action = "delete key", View = "32", Section = "uninsdeletekey" }
        ];
        h.Service.RegistryNotes = ["The NSIS header uses bzip2 compression and is not decoded; registry changes may be missing. " + LongText];
        h.Service.Strings =
        [
            FakeInstallerAnalysisService.Found("https://contoso.example/update?channel=" + LongText.Replace(' ', '-'), "Ascii", 4096, "Url"),
            FakeInstallerAnalysisService.Found(@"C:\Program Files\Contoso\" + LongText, "Utf16", 8192, "Path"),
            FakeInstallerAnalysisService.Found("{11111111-2222-3333-4444-555555555555}", "Ascii", 16384, "Guid")
        ];
    }

    private static void WithPage(ApplicationTheme theme, Action<Window, InstallerAnalysisPage, InstallerAnalysisViewModel> test,
        double width = 1440 - NavigationPaneWidth, double height = 900 - 48, bool analyze = true)
    {
        WithApplication(theme, () =>
        {
            using var h = new InstallerAnalysisHarness();
            var path = h.Installer("Contoso-Enterprise-Setup-x64.exe");
            Fill(h, path);
            var viewModel = h.Create();
            var page = new InstallerAnalysisPage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                if (analyze) Run(viewModel.OpenAsync(path));
                Settle(window);
                test(window, page, viewModel);
            }
            finally
            {
                Run(page.OnNavigatedFromAsync());
                window.Close();
            }
        });
    }

    private static void ShowTab(Window window, InstallerAnalysisViewModel viewModel, int tab)
    {
        viewModel.SelectedTabIndex = tab;
        StaTestHelper.PumpUntil(() => !viewModel.IsBusy);
        Settle(window);
    }

    private static DataGrid ShowGrid(Window window, InstallerAnalysisPage page, InstallerAnalysisViewModel viewModel, string id)
    {
        ShowTab(window, viewModel, TabOf(id));
        var grid = ById<DataGrid>(page, id);
        grid.BringIntoView();
        Settle(window);
        return grid;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryTabRendersItsRowsWithLongValues(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            var tabs = ById<TabControl>(page, "tabInstallerAnalysis");
            Assert.Equal(["Overview", "MSI Properties", "Payload", "Inner Installers", "File Browser", "Registry Settings", "Strings"],
                tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));
            Assert.True(ById<FrameworkElement>(page, "pnlNotes").IsVisible);
            Assert.True(ById<FrameworkElement>(page, "pnlPostPatch").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "pnlBreadcrumb").IsVisible);
            var expected = new Dictionary<string, int>
            {
                ["dgSource"] = 12, ["dgDeployment"] = 4, ["dgModes"] = 2, ["dgPostPatch"] = 6, ["dgMetadata"] = 2, ["dgMsi"] = 4,
                ["dgPayload"] = 7, ["dgInner"] = 3, ["dgFiles"] = 6, ["dgRegistry"] = 3, ["dgStrings"] = 3
            };
            foreach (var id in GridIds)
            {
                var grid = ShowGrid(window, page, viewModel, id);
                Assert.Equal(expected[id], grid.Items.Count);
                Assert.True(grid.IsVisible, id);
            }
            Pixels(window, ById<FrameworkElement>(page, "dgStrings"), $"IA-Strings-{theme}");
            ShowTab(window, viewModel, InstallerAnalysisViewModel.PayloadTab);
            Assert.Equal("7 entries", ById<TextBlock>(page, "txtPayloadStatus").Text);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryGridShowsReadableRowsAndAVisibleSelection(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            foreach (var id in GridIds)
            {
                var grid = ShowGrid(window, page, viewModel, id);
                AssertReadableRowsAndVisibleSelection(window, grid, $"IA-{id}-{theme}");
            }
        });
    }

    public static TheoryData<ApplicationTheme, double> Widths() => new()
    {
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth },
        { ApplicationTheme.Dark, 1440 - NavigationPaneWidth },
        { ApplicationTheme.Light, 1440 - CollapsedPaneWidth },
        { ApplicationTheme.Dark, 1440 - CollapsedPaneWidth }
    };

    [Theory]
    [MemberData(nameof(Widths))]
    public void EveryGridShowsAllItsColumnsAt1440Pixels(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            foreach (var id in GridIds)
            {
                var grid = ShowGrid(window, page, viewModel, id);
                AssertResizableReadableColumns(grid);
                AssertNoSidewaysScroll(grid, id);
            }
        }, width);
    }

    /// <summary>The element lies inside the scroller's width and is reachable by a vertical scroll.</summary>
    private static void AssertReachable(ScrollViewer scroller, FrameworkElement element, string id, double minHeight)
    {
        Assert.True(element.IsVisible, $"{id} is not visible.");
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"{id} spans {bounds.Left:0} to {bounds.Right:0} px in a {scroller.ViewportWidth:0} px tab.");
        var bottom = bounds.Bottom + scroller.VerticalOffset;
        Assert.True(bottom <= scroller.ExtentHeight + 0.5, $"{id} ends at {bottom:0} px below the {scroller.ExtentHeight:0} px the tab scrolls to.");
        Assert.True(element.ActualHeight >= minHeight - 0.5, $"{id} is {element.ActualHeight:0} px high, below {minHeight} px.");
    }

    public static TheoryData<ApplicationTheme, double, double> Sizes() => new()
    {
        { ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Dark, 900 - NavigationPaneWidth, 600 - 48 },
        { ApplicationTheme.Light, 1440 - NavigationPaneWidth, 900 - 48 },
        { ApplicationTheme.Dark, 1440 - NavigationPaneWidth, 900 - 48 }
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EveryControlFitsTheWidthAndEveryTabScrollsToItsControls(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            var large = width > 1000;
            viewModel.LastReportFolder = @"C:\Reports";
            viewModel.LastExtractFolder = @"C:\Extracted";
            Settle(window);
            foreach (var id in new[] { "txtInstallerPath", "btnAnalyze", "btnOpenInstaller", "btnReanalyze", "cboRecentFiles", "btnOpenRecent", "btnClearRecent",
                         "btnCopySummary", "btnCopyJson", "btnExportCsv", "btnExportHtml", "btnOpenReportsFolder", "txtAnalysisStatus" })
                AssertInside(window, ById<FrameworkElement>(page, id), window.ActualHeight, id);
            var tabs = ById<FrameworkElement>(page, "tabInstallerAnalysis");
            Assert.True(tabs.ActualHeight >= 220, $"The tabs get {tabs.ActualHeight:0} px at {width}x{height}.");

            void Check(int tab, string scrollId, params (string Id, double MinHeight)[] elements)
            {
                ShowTab(window, viewModel, tab);
                var scroller = ById<ScrollViewer>(page, scrollId);
                Assert.True(scroller.ScrollableWidth < 1, $"{scrollId} scrolls sideways by {scroller.ScrollableWidth:0} px.");
                if (large && tab != InstallerAnalysisViewModel.OverviewTab)
                    Assert.True(scroller.ScrollableHeight < 1, $"{scrollId} needs {scroller.ScrollableHeight:0} px more height at {width}x{height}.");
                foreach (var (id, minHeight) in elements)
                {
                    var element = ById<FrameworkElement>(page, id);
                    element.BringIntoView();
                    Settle(window);
                    AssertReachable(scroller, element, id, minHeight);
                }
            }

            Check(InstallerAnalysisViewModel.OverviewTab, "scrollOverview", ("pnlNotes", 20), ("dgSource", 100), ("dgModes", 60), ("dgMetadata", 60));
            Check(InstallerAnalysisViewModel.MsiTab, "scrollMsiTab", ("txtMsiFilter", 20), ("dgMsi", 140));
            Check(InstallerAnalysisViewModel.PayloadTab, "scrollPayloadTab", ("txtPayloadFilter", 20), ("btnPayloadAnalyze", 20), ("btnPayloadHash", 20),
                ("btnExtractPayload", 20), ("btnPayloadOpenFolder", 20), ("dgPayload", 140));
            Check(InstallerAnalysisViewModel.InnerTab, "scrollInnerTab", ("btnInnerAnalyze", 20), ("btnInnerExtract", 20), ("btnInnerOpenFolder", 20), ("dgInner", 140));
            Check(InstallerAnalysisViewModel.FilesTab, "scrollFilesTab", ("cboFileFolder", 20), ("txtFileFilter", 20), ("btnFilesHash", 20), ("dgFiles", 140));
            Check(InstallerAnalysisViewModel.RegistryTab, "scrollRegistryTab", ("txtRegistryFilter", 20), ("btnRegistryCopyRows", 20), ("pnlRegistryNotes", 20), ("dgRegistry", 140));
            Check(InstallerAnalysisViewModel.StringsTab, "scrollStringsTab", ("txtStringsFilter", 20), ("cboStringsCategory", 20), ("btnCopyStrings", 20), ("dgStrings", 140));
            Pixels(window, ById<FrameworkElement>(page, "tabInstallerAnalysis"), $"IA-{width}x{height}-{theme}");
        }, width, height);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheModesRegistryAndOverviewShowTheContractFieldsAndTheRegistryNotes(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            static List<string> Headers(DataGrid grid) => grid.Columns.Select(c => (string)c.Header).ToList();
            var modes = ShowGrid(window, page, viewModel, "dgModes");
            Assert.Equal(["Mode", "Default", "Switch", "Install command", "Install folder", "Uninstall command", "ARP key", "Hive", "View", "Context"], Headers(modes));
            Assert.Equal(["Yes", "No"], viewModel.ModeRows.Select(r => r.Default));
            var source = ShowGrid(window, page, viewModel, "dgSource");
            Assert.Contains(source.Items.OfType<NameValueRow>(), r => r.Name == "Detection hint");
            Assert.Contains(source.Items.OfType<NameValueRow>(), r => r.Name == "Requested execution level" && r.Value == "requireAdministrator");
            Assert.Contains(ShowGrid(window, page, viewModel, "dgPostPatch").Items.OfType<NameValueRow>(), r => r.Name == "Base version" && r.Value == "1.2.0.0");

            var registry = ShowGrid(window, page, viewModel, "dgRegistry");
            Assert.Equal(["Origin", "Action", "Hive", "Key", "Name", "Value", "Type", "View", "Section", "Condition"], Headers(registry));
            Assert.Equal(["Write", "Write", "Delete key"], registry.Items.OfType<RegistryRow>().Select(r => r.Action));
            var notes = ById<FrameworkElement>(page, "pnlRegistryNotes");
            Assert.True(notes.IsVisible);
            AssertInside(window, notes, window.ActualHeight, "pnlRegistryNotes");
            var note = Descendants(notes).OfType<TextBlock>().Single();
            Assert.StartsWith("The NSIS header uses bzip2", note.Text);
            Assert.True(note.ActualHeight > note.FontSize * 1.5, "The long registry note does not wrap.");
            Pixels(window, notes, $"IA-RegistryNotes-{theme}");
        });
    }

    [Fact]
    public void TheRegistryNotesPanelIsHiddenWhenTheReadHasNoNote()
    {
        WithPage(ApplicationTheme.Light, (window, page, viewModel) =>
        {
            ShowTab(window, viewModel, InstallerAnalysisViewModel.RegistryTab);
            Assert.False(ById<FrameworkElement>(page, "pnlRegistryNotes").IsVisible);
            Assert.True(ById<FrameworkElement>(page, "txtRegistryStatus").IsVisible);
        }, analyze: false);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheBreadcrumbShowsEveryLevelAfterADrillDownAndBackGoesUp(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            ShowTab(window, viewModel, InstallerAnalysisViewModel.InnerTab);
            viewModel.SelectedInner = viewModel.InnerRows.Single(r => r.Name == "setup.msi");
            Run(viewModel.AnalyzeSelectedCommand.ExecuteAsync(null));
            StaTestHelper.PumpUntil(() => !viewModel.IsBusy);
            Settle(window);

            Assert.True(ById<FrameworkElement>(page, "pnlBreadcrumb").IsVisible);
            var levels = ById<ItemsControl>(page, "lstBreadcrumb");
            var buttons = Descendants(levels).OfType<System.Windows.Controls.Button>().ToList();
            Assert.Equal(["Contoso-Enterprise-Setup-x64.exe", "setup.msi"], buttons.Select(b => AutomationProperties.GetName(b)));
            Assert.True(buttons[0].IsEnabled);
            Assert.False(buttons[1].IsEnabled);
            AssertInside(window, ById<FrameworkElement>(page, "btnBack"), window.ActualHeight, "btnBack");
            Pixels(window, ById<FrameworkElement>(page, "pnlBreadcrumb"), $"IA-Breadcrumb-{theme}");

            buttons[0].Command.Execute(buttons[0].CommandParameter);
            Settle(window);
            Assert.Equal(0, viewModel.Depth);
            Assert.False(ById<FrameworkElement>(page, "pnlBreadcrumb").IsVisible);
        });
    }

    [Fact]
    public void ScreenReadersHearPlainNamesForRowsAndControls()
    {
        WithPage(ApplicationTheme.Dark, (window, page, viewModel) =>
        {
            static List<string> Names(DataGrid grid) =>
                UIElementAutomationPeer.CreatePeerForElement(grid).GetChildren().OfType<DataGridItemAutomationPeer>().Select(p => p.GetName()).ToList();
            foreach (var id in GridIds)
            {
                var grid = ShowGrid(window, page, viewModel, id);
                var names = Names(grid);
                Assert.NotEmpty(names);
                Assert.All(names, n => Assert.DoesNotContain("{ ", n));
                Assert.All(names, n => Assert.DoesNotContain("ApplicationPackager", n));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(grid)), id);
            }
            Assert.Contains("vc_redist.x64.exe, Inner installer", Names(ShowGrid(window, page, viewModel, "dgPayload")));
            Assert.Contains("Display name, " + "Contoso Tool " + LongText, Names(ShowGrid(window, page, viewModel, "dgPostPatch")));
            Assert.Equal("Installer path", AutomationProperties.GetName(ById<FrameworkElement>(page, "txtInstallerPath")));
            Assert.Equal("Recent files", AutomationProperties.GetName(ById<FrameworkElement>(page, "cboRecentFiles")));
        });
    }

    [Fact]
    public void BeforeAnAnalysisTheOverviewSaysHowToStartAndNothingIsRead()
    {
        WithPage(ApplicationTheme.Light, (window, page, viewModel) =>
        {
            Assert.True(ById<FrameworkElement>(page, "txtOverviewEmpty").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "pnlOverview").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "btnCancelAnalysis").IsVisible);
            Assert.True(page.AllowDrop);
        }, analyze: false);
    }

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
        for (var i = 0; i < 40; i++)
        {
            Tab(window);
            var focused = Keyboard.FocusedElement as DependencyObject;
            path.Add(focused is FrameworkElement f ? $"{f.GetType().Name}({AutomationProperties.GetAutomationId(f)})" : focused?.GetType().Name ?? "none");
            if (focused is not null && Ancestor<DataGrid>(focused) == grid) return Assert.IsType<DataGridRow>(Ancestor<DataGridRow>(focused), exactMatch: false);
        }
        Assert.Fail($"Tab never reaches {AutomationProperties.GetAutomationId(grid)}: {string.Join(" > ", path)}");
        return null!;
    }

    private static string StartOf(string gridId) => gridId switch
    {
        "dgMsi" => "txtMsiFilter",
        "dgPayload" => "txtPayloadFilter",
        "dgInner" => "btnInnerAnalyze",
        "dgFiles" => "txtFileFilter",
        "dgRegistry" => "txtRegistryFilter",
        "dgStrings" => "txtStringsFilter",
        _ => "txtInstallerPath"
    };

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabReachesEveryGridAndTheFocusedRowIsTheSelectedRow(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            foreach (var id in GridIds)
            {
                var grid = ShowGrid(window, page, viewModel, id);
                grid.SelectedItem = null;
                Settle(window);
                var row = TabInto(window, ById<UIElement>(page, StartOf(id)), grid);
                Assert.True(row.IsSelected, id);
                Assert.Same(row.Item, grid.SelectedItem);
                Press(window, Key.Down);
                Assert.Same(grid.Items[grid.Items.IndexOf(row.Item) + 1], grid.SelectedItem);
            }
            Assert.NotNull(viewModel.SelectedPayload);
            Assert.NotNull(viewModel.SelectedInner);
            Assert.NotNull(viewModel.SelectedFile);
            Assert.NotNull(viewModel.SelectedRegistry);
            Assert.NotNull(viewModel.SelectedString);
        });
    }

    [Fact]
    public void EnterInThePathBoxAnalyzesTheTypedPath()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            using var h = new InstallerAnalysisHarness();
            var path = h.Installer("typed.msi");
            var viewModel = h.Create();
            var page = new InstallerAnalysisPage(viewModel);
            var window = new Window { Width = 1210, Height = 852, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                var box = ById<UIElement>(page, "txtInstallerPath");
                Assert.True(Keyboard.Focus(box) == box || box.IsKeyboardFocusWithin);
                viewModel.PathText = path;
                Settle(window);
                var source = PresentationSource.FromVisual(Assert.IsAssignableFrom<Visual>(Keyboard.FocusedElement))!;
                ((UIElement)Keyboard.FocusedElement).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Return) { RoutedEvent = Keyboard.KeyDownEvent });
                StaTestHelper.PumpUntil(() => viewModel.HasReport && !viewModel.IsBusy);
                Assert.Equal("typed.msi", viewModel.Report!.FileName);
            }
            finally
            {
                Run(page.OnNavigatedFromAsync());
                window.Close();
            }
        });
    }

    private static void RightClick(Window window, DataGrid grid, object item)
    {
        grid.ScrollIntoView(item);
        Settle(window);
        var row = Assert.IsAssignableFrom<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(item));
        var cell = Descendants(row).OfType<System.Windows.Controls.DataGridCell>().First();
        // The input manager raises the tunnelling and the bubbling mouse-down; each element turns them into its right-button events.
        foreach (var routed in new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent })
            cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = routed, Source = cell });
        Settle(window);
    }

    private static MenuItem OpenMenuItem(Window window, DataGrid grid, string header)
    {
        var menu = grid.ContextMenu!;
        menu.PlacementTarget = grid;
        menu.IsOpen = true;
        Settle(window);
        return menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ARightClickSelectsTheRowUnderThePointerAndTheMenuActsOnThatRow(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            foreach (var (id, select, pick) in new (string, Action<PayloadRow>, Func<PayloadRow?>)[]
                     {
                         ("dgPayload", r => viewModel.SelectedPayload = r, () => viewModel.SelectedPayload),
                         ("dgInner", r => viewModel.SelectedInner = r, () => viewModel.SelectedInner),
                         ("dgFiles", r => viewModel.SelectedFile = r, () => viewModel.SelectedFile)
                     })
            {
                var grid = ShowGrid(window, page, viewModel, id);
                var rows = grid.Items.OfType<PayloadRow>().Where(r => !r.Entry.IsDirectory).ToList();
                select(rows[0]);
                Settle(window);
                RightClick(window, grid, rows[1]);
                Assert.Same(rows[1], pick());
                Assert.Same(rows[1], grid.SelectedItem);
                var item = OpenMenuItem(window, grid, "Copy name");
                item.Command.Execute(item.CommandParameter);
                grid.ContextMenu!.IsOpen = false;
                Assert.Equal($"Copied the name {rows[1].Name}.", viewModel.StatusMessage);
            }

            var registry = ShowGrid(window, page, viewModel, "dgRegistry");
            viewModel.SelectedRegistry = viewModel.RegistryRows[0];
            Settle(window);
            RightClick(window, registry, viewModel.RegistryRows[2]);
            Assert.Same(viewModel.RegistryRows[2], viewModel.SelectedRegistry);
            var strings = ShowGrid(window, page, viewModel, "dgStrings");
            viewModel.SelectedString = viewModel.StringRows[0];
            Settle(window);
            RightClick(window, strings, viewModel.StringRows[2]);
            Assert.Same(viewModel.StringRows[2], viewModel.SelectedString);
            var copy = OpenMenuItem(window, strings, "Copy string");
            copy.Command.Execute(copy.CommandParameter);
            strings.ContextMenu!.IsOpen = false;
            Assert.Equal("Copied the string.", viewModel.StatusMessage);
        });
    }

    [Fact]
    public void TheFiltersWaitForTypingToPauseAndThePathBoxDoesNot()
    {
        WithPage(ApplicationTheme.Light, (window, page, viewModel) =>
        {
            foreach (var (id, tab) in new[] { ("txtMsiFilter", InstallerAnalysisViewModel.MsiTab), ("txtPayloadFilter", InstallerAnalysisViewModel.PayloadTab),
                         ("txtFileFilter", InstallerAnalysisViewModel.FilesTab), ("txtRegistryFilter", InstallerAnalysisViewModel.RegistryTab), ("txtStringsFilter", InstallerAnalysisViewModel.StringsTab) })
            {
                ShowTab(window, viewModel, tab);
                Assert.Equal(250, BindingOperations.GetBinding(ById<System.Windows.Controls.TextBox>(page, id), System.Windows.Controls.TextBox.TextProperty)!.Delay);
            }
            Assert.Equal(0, BindingOperations.GetBinding(ById<System.Windows.Controls.TextBox>(page, "txtInstallerPath"), System.Windows.Controls.TextBox.TextProperty)!.Delay);
        }, analyze: false);
    }

    [Fact]
    public void TheSizeColumnsSortByTheByteCount()
    {
        WithPage(ApplicationTheme.Light, (window, page, viewModel) =>
        {
            foreach (var id in new[] { "dgPayload", "dgInner", "dgFiles" })
            {
                ShowTab(window, viewModel, TabOf(id));
                var column = ById<DataGrid>(page, id).Columns.Single(c => (string)c.Header == "Size");
                Assert.Equal("Entry.Size", column.SortMemberPath);
            }
        }, analyze: false);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth, 900 - 48)]
    public void TheHashShowsInAReadOnlyBoxWithACopyButton(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel) =>
        {
            ShowTab(window, viewModel, InstallerAnalysisViewModel.PayloadTab);
            Assert.False(ById<FrameworkElement>(page, "pnlHash").IsVisible);
            Run(viewModel.HashCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe")));
            StaTestHelper.PumpUntil(() => !viewModel.IsBusy);
            Settle(window);
            var box = ById<System.Windows.Controls.TextBox>(page, "txtHash");
            Assert.True(box.IsReadOnly);
            Assert.Equal(new string('F', 64), box.Text);
            Assert.Equal("SHA-256 of contoso.exe", AutomationProperties.GetName(box));
            AssertInside(window, box, window.ActualHeight, "txtHash");
            AssertInside(window, ById<FrameworkElement>(page, "btnCopyHash"), window.ActualHeight, "btnCopyHash");
            var tabs = ById<FrameworkElement>(page, "tabInstallerAnalysis");
            Assert.True(tabs.ActualHeight >= 220, $"The tabs get {tabs.ActualHeight:0} px at {width}x{height} with the SHA-256 shown.");
            Pixels(window, ById<FrameworkElement>(page, "pnlHash"), $"IA-Hash-{theme}");
        }, width, height);
    }

    [Fact]
    public void ADropOfOneFileIsReadFromTheFileListAndTextIsIgnored()
    {
        StaTestHelper.Run(() =>
        {
            var files = new DataObject(DataFormats.FileDrop, new[] { @"C:\drop\setup.exe" });
            Assert.Equal([@"C:\drop\setup.exe"], InstallerAnalysisPage.DroppedPaths(files));
            Assert.Null(InstallerAnalysisPage.DroppedPaths(new DataObject(DataFormats.Text, @"C:\drop\setup.exe")));
        });
    }
}
