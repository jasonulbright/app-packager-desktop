using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Workbench rows that end in a button, the headers of the One Click plan and the date columns, measured at the default and the smallest sizes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ClippedColumnAndRowTests
{
    private const string Inherited = "Inherited: Packager default";

    private static double TextWidth(TextBlock block) => new FormattedText(block.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
        VisualTreeHelper.GetDpi(block).PixelsPerDip).Width;

    /// <summary>
    /// No layout clip between the element and the holder cuts the part at its left or right side, and the part lies inside the
    /// width of the holder. A clipped element reports its unclipped size, so its ActualWidth alone does not show the cut.
    /// </summary>
    private static void AssertUnclipped(FrameworkElement element, Rect part, FrameworkElement holder, double holderWidth, string where)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement current) continue;
            var bounds = ReferenceEquals(current, element) ? part : element.TransformToAncestor(current).TransformBounds(part);
            if (LayoutInformation.GetLayoutClip(current) is { } clip)
                Assert.True(bounds.Left >= clip.Bounds.Left - 0.5 && bounds.Right <= clip.Bounds.Right + 0.5,
                    $"{where}: {current.GetType().Name} '{current.Name}' shows {clip.Bounds.Left:0} to {clip.Bounds.Right:0}; the content needs {bounds.Left:0} to {bounds.Right:0}.");
            if (!ReferenceEquals(current, holder)) continue;
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= holderWidth + 0.5,
                $"{where}: the content ends at {bounds.Right:0} px in {holderWidth:0} px.");
            return;
        }
        Assert.Fail($"{where}: the holder is not an ancestor of the element.");
    }

    private static void AssertWholeText(TextBlock block, FrameworkElement holder, double holderWidth, string where)
    {
        var width = TextWidth(block);
        Assert.True(block.ActualWidth - block.Padding.Left - block.Padding.Right + 0.5 >= width,
            $"{where}: '{block.Text}' needs {width:0} px and has {block.ActualWidth:0} px.");
        AssertUnclipped(block, new Rect(block.Padding.Left, 0, width, block.ActualHeight), holder, holderWidth, $"{where}, '{block.Text}'");
    }

    private static void AssertWholeHeader(DataGrid grid, DataGridColumn column, string where)
    {
        var header = Descendants(grid).OfType<DataGridColumnHeader>().Single(h => ReferenceEquals(h.Column, column));
        var text = Descendants(header).OfType<TextBlock>().First(t => t.Text == (string)column.Header);
        AssertWholeText(text, header, Math.Min(header.ActualWidth, column.ActualWidth), $"{where}, header {column.Header}");
    }

    private static void AssertWholeCells(DataGrid grid, string header, string value, string where)
    {
        var column = grid.Columns.Single(c => c.Header as string == header);
        grid.ScrollIntoView(grid.Items[0], column);
        grid.UpdateLayout();
        AssertWholeHeader(grid, column, where);
        var cells = Descendants(grid).OfType<DataGridCell>().Where(c => ReferenceEquals(c.Column, column) && c.IsVisible).ToList();
        Assert.NotEmpty(cells);
        foreach (var cell in cells)
        {
            var text = Descendants(cell).OfType<TextBlock>().Single();
            Assert.Equal(value, text.Text);
            AssertWholeText(text, cell, Math.Min(cell.ActualWidth, column.ActualWidth), $"{where}, column {header}");
        }
    }

    private static Window WorkbenchHost(WorkbenchWindowViewModel viewModel, double width, double height, out WorkbenchWindow window)
    {
        window = (WorkbenchWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkbenchWindow));
        typeof(WorkbenchWindow).BaseType!.GetConstructor(Type.EmptyTypes)!.Invoke(window, null);
        window.InitializeComponent();
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = viewModel;
        return new Window
        {
            Width = width,
            Height = height,
            WindowStyle = WindowStyle.None,
            Content = content,
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush")
        };
    }

    private static void AssertWholeButton(WorkbenchWindow window, string name, FrameworkElement section, string where)
    {
        var button = Assert.IsAssignableFrom<ButtonBase>(window.FindName(name));
        Assert.True(button.IsVisible, $"{where}: {name} is not visible.");
        Assert.True(button.ActualWidth + 0.5 >= button.MinWidth, $"{where}: {name} is {button.ActualWidth:0} px wide.");
        AssertUnclipped(button, new Rect(0, 0, button.ActualWidth, button.ActualHeight), section, section.ActualWidth, $"{where}, {name}");
        var caption = Descendants(button).OfType<TextBlock>().Single(t => t.Text == button.Content as string);
        AssertWholeText(caption, section, section.ActualWidth, $"{where}, {name}");
    }

    /// <summary>The Return codes box shows its whole placeholder, and no box of its rows is cut.</summary>
    private static void AssertWholeReturnCodeRows(WorkbenchWindow window, FrameworkElement section, string where)
    {
        var codes = (Wpf.Ui.Controls.TextBox)window.FindName("txtReturnCodes");
        var needed = TextWidth(new TextBlock
        {
            Text = codes.PlaceholderText, FontFamily = codes.FontFamily, FontStyle = codes.FontStyle,
            FontWeight = codes.FontWeight, FontStretch = codes.FontStretch, FontSize = codes.FontSize
        }) + codes.Padding.Left + codes.Padding.Right + codes.BorderThickness.Left + codes.BorderThickness.Right;
        Assert.True(codes.ActualWidth + 0.5 >= needed, $"{where}: Return codes is {codes.ActualWidth:0} px wide; its placeholder needs {needed:0} px.");
        foreach (var (name, least) in new[] { ("txtEffectiveCommand", 200.0), ("txtReturnCodes", needed), ("cboRebootPolicy", 150.0), ("txtWorkingDirectory", 150.0) })
        {
            var box = (FrameworkElement)window.FindName(name);
            Assert.True(box.ActualWidth + 0.5 >= least, $"{where}: {name} is {box.ActualWidth:0} px wide; the least is {least:0} px.");
            AssertUnclipped(box, new Rect(0, 0, box.ActualWidth, box.ActualHeight), section, section.ActualWidth, $"{where}, {name}");
        }
    }

    // A width of zero selects the narrowest window that docks the side panel; the editor is narrowest at that width.
    [Theory]
    [InlineData(ApplicationTheme.Light, 1278, 820)]
    [InlineData(ApplicationTheme.Dark, 1278, 820)]
    [InlineData(ApplicationTheme.Light, 1024, 700)]
    [InlineData(ApplicationTheme.Dark, 1024, 700)]
    [InlineData(ApplicationTheme.Light, 0, 700)]
    [InlineData(ApplicationTheme.Dark, 0, 700)]
    public void EveryWorkbenchRowShowsItsButtonAndItsInheritedText(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            var viewModel = new WorkbenchWindowViewModel(null!, null!, null!, null!)
            {
                IdentityText = "Application id catalog:package-review; output folders keep using the packager identity.",
                ProvenanceText = @"Definitions\review.json",
                DisplayNameSource = Inherited,
                PublisherSource = Inherited,
                DescriptionSource = Inherited,
                TitleModeSource = Inherited,
                InstallModeSource = Inherited,
                CommandSource = Inherited,
                DetectionSource = Inherited,
                DetectionInheritedText = "Inherited rule: a registry value under HKLM that holds the staged version.",
                VariantSource = Inherited,
                InstallerTypeSource = Inherited,
                ExecContextSource = "Inherited: System"
            };
            var host = WorkbenchHost(viewModel, width == 0 ? WorkbenchWindow.SidePanelDockWidth : width, height, out var window);
            Show(host);
            try
            {
                Settle(host);
                if (width == 0)
                {
                    var body = (FrameworkElement)window.FindName("gridBody");
                    host.Width += WorkbenchWindow.SidePanelDockWidth - body.ActualWidth;
                    Settle(host);
                    Assert.True(((FrameworkElement)window.FindName("pnlSidePanel")).IsVisible, "The side panel is not docked.");
                }

                var sections = new (int Index, string Panel, string[] Buttons, string[] Texts)[]
                {
                    (0, "pnlApplication", ["btnDisplayNameReset", "btnPublisherReset", "btnDescriptionReset", "btnTitleModeReset"],
                        ["lblDisplayNameSrc", "lblPublisherSrc", "lblDescriptionSrc", "lblTitleModeSrc"]),
                    (1, "pnlInstall", ["btnInstallReset"], ["lblInstallModeSrc"]),
                    (2, "pnlDetection", ["btnDetectionReset"], ["lblDetectionSrc"]),
                    (3, "pnlRequirements", ["btnReqAdd", "btnReqReplace", "btnReqRemove", "btnInstallForReset", "btnInstallerTypeReset"],
                        ["lblVariantSrc", "lblInstallerTypeSrc"]),
                    (4, "pnlTiming", ["btnTimingReset"], ["lblExecContextSrc"])
                };
                foreach (var (index, panelName, buttons, texts) in sections)
                {
                    viewModel.SelectedSectionIndex = index;
                    Settle(host);
                    Settle(host);
                    var section = (FrameworkElement)window.FindName(panelName);
                    var where = $"Workbench {theme} {host.Width:0}x{height}, section {index}";
                    foreach (var name in buttons) AssertWholeButton(window, name, section, where);
                    foreach (var name in texts)
                        AssertWholeText((TextBlock)window.FindName(name), section, section.ActualWidth, $"{where}, {name}");
                    if (index == 1) AssertWholeReturnCodeRows(window, section, where);
                }
            }
            finally { host.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1278, 820)]
    [InlineData(ApplicationTheme.Dark, 1024, 700)]
    public void TheSourceRevisionsShowTheWholeTimeOfEachRevision(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            const string Added = "2026-11-27 12:58";
            var viewModel = new WorkbenchWindowViewModel(null!, null!, null!, null!) { IsByo = true };
            viewModel.ByoSources.Add(new ByoSourceRow(1, "contoso.msi", "1.0.1", "msi", "x64", new string('a', 64), Added, ""));
            var host = WorkbenchHost(viewModel, width, height, out var window);
            Show(host);
            try
            {
                Settle(host);
                Settle(host);
                AssertWholeCells((DataGrid)window.FindName("gridByoSources"), "Added", Added, $"Source revisions {theme} {width}x{height}");
            }
            finally { host.Close(); }
        });
    }

    private sealed class NoRunner : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct) => Task.FromResult(false);
    }

    private static OneClickPlanViewModel Plan(int applications, string folder)
    {
        var keys = Enumerable.Range(1, applications).Select(i => $"package-{i}").ToList();
        return new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = keys.Select((k, i) => (new OneClickApp(k, ".NET 8 Desktop Runtime (x86+x64) " + i, "Contoso", "8.0.34"),
                (PackagerInfo?)new PackagerInfo { Uid = k, Application = ".NET 8 Desktop Runtime (x86+x64) " + i })).ToList(),
            Settings = () => new OneClickPlanSettings
            {
                Tracked = keys,
                DefaultDestinations = new DestinationSet(true, true, true),
                Readiness = OneClickPlanner.Readiness(new PublishReadiness(new SystemsInUse { ConfigMgr = true }, true, "MCM", @"\\cm01\share", "", "", false, "")),
                Scope = new OneClickScopeSettings()
            },
            Action = OneClickActionNames.StageAndPackage,
            ReportFolder = folder
        }, new NoRunner());
    }

    // A width of zero keeps the size that the window opens with.
    [Theory]
    [InlineData(ApplicationTheme.Light, 0, 0, 3)]
    [InlineData(ApplicationTheme.Dark, 0, 0, 30)]
    [InlineData(ApplicationTheme.Light, 1100, 480, 30)]
    [InlineData(ApplicationTheme.Dark, 1100, 480, 3)]
    public void TheOneClickPlanShowsEveryWholeHeader(ApplicationTheme theme, int width, int height, int applications)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-clipped-" + Guid.NewGuid().ToString("N"));
        WithApplication(theme, () =>
        {
            var window = new OneClickPlanWindow(Plan(applications, folder), folder);
            if (width > 0)
            {
                window.Width = width;
                window.Height = height;
            }
            Show(window);
            try
            {
                Settle(window);
                Settle(window);
                var grid = (DataGrid)window.FindName("gridPlan");
                var where = $"One Click plan {theme} {window.Width:0}x{window.Height:0}, {applications} rows";
                var shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();
                Assert.Contains(shown, c => Equals(c.Header, "Last published"));
                Assert.Contains(shown, c => Equals(c.Header, "Destinations from"));
                foreach (var column in shown) AssertWholeHeader(grid, column, where);
                var reason = shown.Single(c => Equals(c.Header, "Reason"));
                Assert.True(reason.ActualWidth >= 170 - 0.5, $"{where}: Reason is {reason.ActualWidth:0} px wide.");
                // At the size that the window opens with, no header needs a sideways scroll.
                if (width == 0) AssertNoSidewaysScroll(grid, where);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1280, 680)]
    [InlineData(ApplicationTheme.Dark, 1280, 680)]
    [InlineData(ApplicationTheme.Light, 980, 420)]
    [InlineData(ApplicationTheme.Dark, 980, 420)]
    public void TheCveWindowShowsTheWholePublishedAndModifiedDates(ApplicationTheme theme, int width, int height)
    {
        WithApplication(theme, () =>
        {
            const string Published = "2026-02-24", Modified = "2026-07-18";
            var viewModel = new CveWindowViewModel(new PackagerInfo { Uid = "a", Application = "Contoso", Vendor = "Contoso" }, null);
            for (var i = 0; i < 30; i++)
                viewModel.Rows.Add(new CveWindowRow($"CVE-2026-{16000 + i}", "10.0 CRITICAL", "0.8%", "", "140.8.0", Published, Modified,
                    "A flaw in the graphics component lets a page leave the sandbox.", []));
            var packages = (PackagesViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
            var window = new CveWindow(viewModel, packages) { Width = width, Height = height };
            Show(window);
            try
            {
                Settle(window);
                Settle(window);
                var grid = (DataGrid)window.FindName("CveGrid");
                var where = $"CVE window {theme} {width}x{height}";
                AssertWholeCells(grid, "Published", Published, where);
                AssertWholeCells(grid, "Modified", Modified, where);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1000, 760)]
    [InlineData(ApplicationTheme.Dark, 760, 560)]
    public void TheImportWindowShowsTheWholeTimeOfEachFeed(ApplicationTheme theme, int width, int height)
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-clipped-" + Guid.NewGuid().ToString("N"));
        try
        {
            WithApplication(theme, () =>
            {
                const string Fetched = "2026-11-27 12:58";
                var service = new ApplicationPackagerUI.Services.VendorImportService(
                    new ApplicationPackagerEngine.Services.VendorImport.ImportedDefinitionStore(root), () => new HashSet<string>(), () => new HashSet<string>(),
                    new ApplicationPackagerEngine.Services.VendorImport.FeedFetcher(() => new HttpClientHandler(), null, (_, _) => Task.CompletedTask));
                var viewModel = new VendorImportViewModel(service, (_, _) => Task.FromResult(true));
                viewModel.Feeds.Add(new VendorFeedRow("contoso-software", "Contoso Software", 1, "Not verified", Fetched, "https://vendor.example/feed.json"));
                var window = new VendorImportWindow(viewModel) { Width = width, Height = height };
                Show(window);
                try
                {
                    Settle(window);
                    Settle(window);
                    AssertWholeCells((DataGrid)window.FindName("gridFeeds"), "Imported", Fetched, $"Import window {theme} {width}x{height}");
                }
                finally { window.Close(); }
            });
        }
        finally { TestCleanup.DeleteDirectory(root); }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1180)]
    [InlineData(ApplicationTheme.Dark, 900)]
    public void TheOperationHistoryShowsTheWholeTimes(ApplicationTheme theme, int width)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apd-clipped-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            WithApplication(theme, () =>
            {
                var inventory = new ApplicationInventoryService(Path.Combine(folder, "history.db"));
                var window = new OperationHistoryWindow(new PackagerInfo { Uid = "app1", Application = "Contoso Application" }, inventory) { Width = width, Height = 720 };
                // A time with no offset change: the text of the cell is the same in each time zone.
                var at = new DateTimeOffset(new DateTime(2026, 11, 27, 12, 58, 48, DateTimeKind.Local));
                const string Shown = "2026-11-27 12:58:48";
                window.Operations.Add(new InventoryOperationState("op1", "Stage", "app1", null, null, "Contoso", at, at, InventoryOutcome.Succeeded, "Detail"));
                window.Events.Add(new InventoryEventRecord(1, "ev1", "Published", "app1", "Contoso", null, null, "ConfigMgr", "MCM", "Publish", "1.0", null,
                    InventoryOutcome.Succeeded, "obj1", null, null, null, at, "op1", "Detail"));
                Show(window);
                try
                {
                    Settle(window);
                    Settle(window);
                    var where = $"Operation history {theme} {width}";
                    var operations = (DataGrid)window.FindName("OperationsGrid");
                    AssertWholeCells(operations, "Started", Shown, where);
                    AssertWholeCells(operations, "Finished", Shown, where);
                    AssertWholeCells((DataGrid)window.FindName("EventsGrid"), "Time", Shown, where);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            TestCleanup.DeleteDirectory(folder);
        }
    }
}
