using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TreeView = System.Windows.Controls.TreeView;
using TextBlock = System.Windows.Controls.TextBlock;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Point = System.Windows.Point;
using TabControl = System.Windows.Controls.TabControl;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>The compiled Collection and Compliance page and its dialogs in real windows, over fake services.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CollectionCompliancePageTests
{
    /// <summary>The main window gives this width to the navigation pane and its border; the page gets the rest.</summary>
    private const double NavigationPaneWidth = 230;

    private const double ReadableColumnWidth = 60;

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

    /// <summary>Runs the test with the theme and control dictionaries merged as App.xaml merges them; a binding error fails the test.</summary>
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
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ApplicationPackagerEngine.Tests.Compliance.RenderKit.ToolTipStyles, UriKind.Absolute) });
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

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        StaTestHelper.Flush();
    }

    private static void Run(Task task) => StaTestHelper.PumpUntil(() => task.IsCompleted);

    private static void WithPage(ApplicationTheme theme, Action<Window, CollectionCompliancePage, CollectionComplianceHarness> test, double width = 1440 - NavigationPaneWidth,
        Action<CollectionComplianceHarness>? arrange = null, double height = 900)
    {
        WithApplication(theme, () =>
        {
            var harness = new CollectionComplianceHarness();
            arrange?.Invoke(harness);
            var page = new CollectionCompliancePage(harness.Create(), new ApplicationPackagerEngine.Tests.Compliance.ComplianceHarness().Create());
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                Settle(window);
                test(window, page, harness);
            }
            finally { window.Close(); }
        });
    }

    private static CollectionComplianceViewModel Vm(CollectionCompliancePage page) => (CollectionComplianceViewModel)page.DataContext;

    private static void SelectCollection(Window window, CollectionCompliancePage page, string id)
    {
        var viewModel = Vm(page);
        var grid = ById<DataGrid>(page, "dgCollections");
        grid.SelectedItem = viewModel.Rows.Single(r => r.CollectionId == id);
        Run(viewModel.DetailLoad);
        Settle(window);
    }

    private static void ShowTab(Window window, CollectionCompliancePage page, int index)
    {
        ById<TabControl>(page, "tabCollectionCompliance").SelectedIndex = index;
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheFourTabsRenderInTheTheme(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            var tabs = ById<TabControl>(page, "tabCollectionCompliance");
            Assert.Equal(["Collections", "WQL Editor", "Templates", "Site Queries"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));
            var title = Descendants(page).OfType<TextBlock>().Single(t => t.Text == "Collection and Compliance");
            Assert.Same(Application.Current.FindResource("TextFillColorPrimaryBrush"), title.Foreground);
            Assert.DoesNotContain(Descendants(page).OfType<FrameworkElement>(), e => AutomationProperties.GetAutomationId(e) == "btnComplianceBaselines");
            Assert.True(ById<System.Windows.Controls.RadioButton>(page, "rbSectionCollections").IsChecked);
            Assert.False(ById<FrameworkElement>(page, "viewCompliance").IsVisible);

            var grid = ById<DataGrid>(page, "dgCollections");
            Assert.Equal(["Name", "ID", "Members", "Limiting collection", "Refresh", "Type", "Rules", "Folder"], grid.Columns.Select(c => (string)c.Header));
            Assert.Equal(4, grid.Items.Count);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(grid));
            Assert.True(grid.EnableRowVirtualization);
            Assert.False(ById<FrameworkElement>(page, "tabCollectionDetails").IsVisible);
            Assert.Equal(0, harness.Service.DetailCalls);

            SelectCollection(window, page, "LAB00010");
            Assert.True(ById<FrameworkElement>(page, "tabCollectionDetails").IsVisible);
            Assert.True(ById<Button>(page, "btnRemoveCollection").IsEnabled);
            viewModel.DetailTabIndex = 1;
            Settle(window);
            Assert.Equal(2, ById<DataGrid>(page, "dgMembers").Items.Count);
            Assert.Equal("Members 1 to 2 of 3", ById<TextBlock>(page, "txtMemberPage").Text);
            viewModel.DetailTabIndex = 2;
            Settle(window);
            Assert.Single(ById<ListBox>(page, "lstQueryRules").Items);

            SelectCollection(window, page, "SMS00001");
            Assert.False(ById<Button>(page, "btnRemoveCollection").IsEnabled);
            Assert.False(ById<Button>(page, "btnEditCollection").IsEnabled);
            Assert.Equal(CollectionComplianceViewModel.BuiltInRefused, ById<Button>(page, "btnRemoveCollection").ToolTip);

            ShowTab(window, page, CollectionComplianceViewModel.WqlTab);
            var tree = ById<TreeView>(page, "treeWqlCollections");
            Assert.Equal(3, tree.Items.Count);
            var editor = ById<System.Windows.Controls.TextBox>(page, "txtWql");
            Assert.Contains("Consolas", editor.FontFamily.Source, StringComparison.Ordinal);
            Assert.True(editor.AcceptsReturn);

            ShowTab(window, page, CollectionComplianceViewModel.TemplatesTab);
            var templates = ById<DataGrid>(page, "dgTemplates");
            Assert.Equal(3, templates.Items.Count);
            viewModel.SelectedTemplate = viewModel.TemplateRows.Single(t => t.Name == "Software installed by name");
            Settle(window);
            Assert.Single(ById<ItemsControl>(page, "lstTemplateParameters").Items);
            Assert.Equal(WqlParameters.Required, ById<TextBlock>(page, "txtTemplateMessage").Text);
            // A read-only text box of the theme justifies wrapped text; a query reads left-aligned.
            Assert.Equal(TextAlignment.Left, ById<System.Windows.Controls.TextBox>(page, "txtTemplateWql").TextAlignment);
            Assert.False(ById<FrameworkElement>(page, "pnlTemplatesMessage").IsVisible);

            ShowTab(window, page, CollectionComplianceViewModel.SiteQueriesTab);
            Run(viewModel.QueriesLoad);
            Settle(window);
            Assert.Equal(2, ById<DataGrid>(page, "dgSavedQueries").Items.Count);
        });
    }

    [Fact]
    public void ThePreviewGridShowsTheColumnsOfTheQueryWithTheirFullNames()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            ShowTab(window, page, CollectionComplianceViewModel.WqlTab);
            viewModel.WqlText = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
            Run(viewModel.PreviewWqlCommand.ExecuteAsync(null));
            Settle(window);
            var grid = ById<DataGrid>(page, "dgPreview");
            Assert.Equal(["SMS_R_System.ResourceId", "SMS_R_System.Name"], grid.Columns.Select(c => (string)c.Header));
            var headers = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Where(h => h.Column is not null)
                .Select(h => Descendants(h).OfType<TextBlock>().First().Text).ToList();
            Assert.Equal(["SMS_R_System.ResourceId", "SMS_R_System.Name"], headers);
            Assert.Equal(2, grid.Items.Count);
            var cells = Descendants(grid).OfType<DataGridCell>().SelectMany(c => Descendants(c).OfType<TextBlock>()).Select(t => t.Text).ToList();
            Assert.Contains("PC01", cells);
            Assert.Equal(CollectionComplianceViewModel.PreviewText(2, 2), ById<TextBlock>(page, "txtWqlMessage").Text);
        });
    }

    [Fact]
    public void ScreenReadersHearPlainNamesForRowsTreeItemsAndMembers()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgCollections");
            var names = UIElementAutomationPeer.CreatePeerForElement(grid).GetChildren().OfType<DataGridItemAutomationPeer>().Select(p => p.GetName()).ToList();
            Assert.Equal(["All Systems (SMS00001)", "Pilot (LAB00012)", "Servers (LAB00010)", "Workstations (LAB00011)"], names);

            SelectCollection(window, page, "LAB00010");
            Vm(page).DetailTabIndex = 1;
            Settle(window);
            var members = UIElementAutomationPeer.CreatePeerForElement(ById<DataGrid>(page, "dgMembers")).GetChildren().OfType<DataGridItemAutomationPeer>()
                .Select(p => p.GetName()).ToList();
            Assert.Equal("Servers-PC01 (16777001), Direct", members[0]);

            ShowTab(window, page, CollectionComplianceViewModel.WqlTab);
            var tree = ById<TreeView>(page, "treeWqlCollections");
            var items = UIElementAutomationPeer.CreatePeerForElement(tree).GetChildren().Select(p => p.GetName()).ToList();
            Assert.Equal(["Patching", "All Systems (SMS00001)", "Pilot (LAB00012)"], items);
            Assert.All(names.Concat(items), n => Assert.DoesNotContain("{", n));
        });
    }

    [Fact]
    public void SelectingACollectionInTheTreeLoadsItsQueryRules()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            ShowTab(window, page, CollectionComplianceViewModel.WqlTab);
            var tree = ById<TreeView>(page, "treeWqlCollections");
            var folder = viewModel.WqlTree[0];
            folder.IsExpanded = true;
            Settle(window);
            folder.Children.Single(c => c.Text == "Servers").IsExpanded = true;
            Settle(window);
            folder.Children.Single(c => c.Text == "Servers").Children[0].IsSelected = true;
            Settle(window);
            Assert.Equal("LAB00010", viewModel.WqlCollection?.CollectionId);
            Run(viewModel.WqlRulesLoad);
            Settle(window);
            Assert.Single(ById<ComboBox>(page, "cboWqlRule").Items);
            Assert.Equal("Servers (LAB00010)", ById<TextBlock>(page, "txtWqlCollection").Text);
        });
    }

    private static double TextWidth(TextBlock style, string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(style.FontFamily, style.FontStyle, style.FontWeight, style.FontStretch), style.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(style).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static System.Windows.Controls.Primitives.DataGridColumnHeader Header(DataGrid grid, string header) =>
        Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => h.Column is not null && Equals(h.Column.Header, header));

    private static System.Windows.Controls.ScrollViewer GridScroller(DataGrid grid) => Descendants(grid).OfType<System.Windows.Controls.ScrollViewer>().First();

    private static void DragRightEdge(DataGrid grid, string header, double change)
    {
        var h = Header(grid, header);
        var gripper = (System.Windows.Controls.Primitives.Thumb)h.Template.FindName("PART_RightHeaderGripper", h);
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(change, 0));
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(change, 0, false));
        grid.UpdateLayout();
        StaTestHelper.Flush();
    }

    /// <summary>
    /// Every column can be resized, has a minimum that holds its header text and is at least that
    /// wide; every text column trims with an ellipsis and shows the full text as a tooltip.
    /// </summary>
    private static void AssertResizableReadableColumns(DataGrid grid)
    {
        Assert.True(grid.CanUserResizeColumns);
        foreach (var column in grid.Columns)
        {
            var name = column.Header as string;
            Assert.True(column.CanUserResize, $"{name} cannot be resized.");
            Assert.True(column.ActualWidth >= column.MinWidth - 0.5, $"{name} is {column.ActualWidth} px wide below its minimum {column.MinWidth} px.");
            if (string.IsNullOrEmpty(name))
            {
                Assert.True(column.MinWidth >= 40, $"The unnamed column has a minimum of {column.MinWidth} px.");
                continue;
            }
            Assert.True(column.MinWidth >= ReadableColumnWidth, $"{name} has a minimum of {column.MinWidth} px.");
            var header = Header(grid, name);
            var label = Descendants(header).OfType<TextBlock>().First(t => t.Text == name);
            var needed = TextWidth(label, name) + header.Padding.Left + header.Padding.Right;
            Assert.True(column.MinWidth >= needed, $"{name} has a minimum of {column.MinWidth} px; its header needs {needed:0} px.");
            if (column is DataGridTextColumn text)
            {
                var setters = text.ElementStyle.Setters.OfType<Setter>().ToList();
                Assert.Contains(setters, s => s.Property == TextBlock.TextTrimmingProperty && Equals(s.Value, TextTrimming.CharacterEllipsis));
                Assert.Contains(setters, s => s.Property == FrameworkElement.ToolTipProperty);
            }
        }
    }

    private static void AssertNoSidewaysScroll(DataGrid grid, string name)
    {
        var scroller = GridScroller(grid);
        Assert.True(scroller.ScrollableWidth < 1, $"{name}: the columns need {scroller.ScrollableWidth:0} px more than the {scroller.ViewportWidth:0} px grid.");
    }

    private static void AssertTextFits(DataGrid grid, string header, IEnumerable<string> texts)
    {
        var column = grid.Columns.Single(c => Equals(c.Header, header));
        var cell = Descendants(grid).OfType<DataGridCell>().First(c => c.Column == column);
        var block = Descendants(cell).OfType<TextBlock>().First();
        var chrome = 2 * block.TranslatePoint(new Point(0, 0), cell).X;
        foreach (var text in texts)
        {
            var needed = TextWidth(block, text) + chrome;
            Assert.True(column.ActualWidth >= needed, $"{header} is {column.ActualWidth} px wide; '{text}' needs {needed:0} px.");
        }
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth)]
    [InlineData(ApplicationTheme.Light, 1440 - 48)]
    public void EveryGridShowsAllItsColumnsAtACommonWidth(ApplicationTheme theme, double width)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            SelectCollection(window, page, "LAB00010");
            var collections = ById<DataGrid>(page, "dgCollections");
            Assert.Equal(2, Grid.GetRow(ById<FrameworkElement>(page, "pnlCollectionDetails")));
            AssertResizableReadableColumns(collections);
            AssertNoSidewaysScroll(collections, "Collections");
            AssertTextFits(collections, "ID", ["LAB0000F"]);
            AssertTextFits(collections, "Refresh", ["Continuous", "Periodic"]);
            AssertTextFits(collections, "Type", ["Built-in", "Custom"]);
            foreach (var name in new[] { "Name", "Limiting collection", "Folder" })
                Assert.True(collections.Columns.Single(c => Equals(c.Header, name)).ActualWidth >= 90, $"{name} is narrower than 90 px.");

            viewModel.DetailTabIndex = 1;
            Settle(window);
            var members = ById<DataGrid>(page, "dgMembers");
            AssertResizableReadableColumns(members);
            AssertNoSidewaysScroll(members, "Members");
            AssertTextFits(members, "Membership", ["From a rule", "Direct"]);

            ShowTab(window, page, CollectionComplianceViewModel.TemplatesTab);
            viewModel.SelectedTemplate = viewModel.TemplateRows[0];
            Settle(window);
            var templates = ById<DataGrid>(page, "dgTemplates");
            AssertResizableReadableColumns(templates);
            AssertNoSidewaysScroll(templates, "Templates");
            AssertTextFits(templates, "Source", ["Shipped", "User"]);

            ShowTab(window, page, CollectionComplianceViewModel.SiteQueriesTab);
            Run(viewModel.QueriesLoad);
            Settle(window);
            var queries = ById<DataGrid>(page, "dgSavedQueries");
            AssertResizableReadableColumns(queries);
            AssertNoSidewaysScroll(queries, "Site queries");

            ShowTab(window, page, CollectionComplianceViewModel.WqlTab);
            viewModel.WqlText = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
            Run(viewModel.PreviewWqlCommand.ExecuteAsync(null));
            Settle(window);
            var preview = ById<DataGrid>(page, "dgPreview");
            AssertResizableReadableColumns(preview);
            AssertNoSidewaysScroll(preview, "Preview");

            ShowTab(window, page, CollectionComplianceViewModel.CollectionsTab);
            DragRightEdge(collections, "Refresh", 40);
            AssertResizableReadableColumns(collections);
            DragRightEdge(collections, "Refresh", -400);
            AssertResizableReadableColumns(collections);
        }, width);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1440 - NavigationPaneWidth)]
    public void TheDetailsSitUnderTheGridWithTheirTabsOnOneLine(double width)
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            SelectCollection(window, page, "LAB00010");
            Assert.Equal(2, Grid.GetRow(ById<FrameworkElement>(page, "pnlCollectionDetails")));
            Assert.True(ById<FrameworkElement>(page, "splitCollectionDetails").IsVisible);
            var tabs = ById<TabControl>(page, "tabCollectionDetails");
            Assert.Equal(["Properties", "Direct Members", "Query Rules", "Include Rules", "Exclude Rules"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));
            var tops = tabs.Items.OfType<TabItem>().Select(t => Math.Round(t.TranslatePoint(new Point(0, 0), tabs).Y)).Distinct().ToList();
            Assert.Single(tops);
            Assert.Equal("2 matches.", CollectionComplianceViewModel.PreviewText(2, 2));
        }, width: width);
    }

    /// <summary>The rows of the Collections grid that fit in its height.</summary>
    private static double RowCapacity(DataGrid grid)
    {
        var header = Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>().First().ActualHeight;
        return (grid.ActualHeight - header) / Row(grid, 0).ActualHeight;
    }

    /// <summary>A main window of 1440x900 gives the page its height less the title bar.</summary>
    private const double PageHeightAt900 = 900 - 48;

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AtNineHundredPixelsTheGridShowsSixRowsWithTheDetailsAndMoreWithoutThem(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            SelectCollection(window, page, "LAB00010");
            var grid = ById<DataGrid>(page, "dgCollections");
            Assert.True(ById<FrameworkElement>(page, "pnlCollectionDetails").IsVisible);
            var withDetails = RowCapacity(grid);
            Assert.True(withDetails >= 6, $"The grid holds {withDetails:0.0} rows with the details open.");

            viewModel.ShowDetails = false;
            Settle(window);
            Assert.False(ById<FrameworkElement>(page, "pnlCollectionDetails").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "splitCollectionDetails").IsVisible);
            Assert.True(RowCapacity(grid) >= withDetails + 6, $"Without the details the grid holds {RowCapacity(grid):0.0} rows.");
            Assert.True(ById<System.Windows.Controls.CheckBox>(page, "chkShowDetails").IsVisible);

            viewModel.ShowDetails = true;
            Settle(window);
            Assert.True(ById<FrameworkElement>(page, "pnlCollectionDetails").IsVisible);
            Assert.Equal(withDetails, RowCapacity(grid), 1);
            Pixels(window, grid, $"Collections-900-{theme}");
        }, height: PageHeightAt900);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AtNineHundredBySixHundredTheDetailsScrollIntoViewWhole(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            SelectCollection(window, page, "LAB00010");
            var scroller = ById<System.Windows.Controls.ScrollViewer>(page, "scrollCollectionsTab");
            var details = ById<FrameworkElement>(page, "pnlCollectionDetails");
            scroller.ScrollToEnd();
            Settle(window);
            var bounds = details.TransformToAncestor(scroller).TransformBounds(new Rect(details.RenderSize));
            Assert.True(bounds.Top >= -0.5 && bounds.Bottom <= scroller.ViewportHeight + 0.5,
                $"The details span {bounds.Top:0} to {bounds.Bottom:0} px in a {scroller.ViewportHeight:0} px view.");
            var tabs = ById<TabControl>(page, "tabCollectionDetails");
            Assert.True(tabs.IsVisible && tabs.ActualHeight > 0);
            Assert.True(RowCapacity(ById<DataGrid>(page, "dgCollections")) >= 3);
        }, width: 900 - NavigationPaneWidth, height: 600 - 48);
    }

    [Fact]
    public async Task TheDetailsChoiceIsKept()
    {
        var harness = new CollectionComplianceHarness();
        var viewModel = await harness.OpenAsync();
        viewModel.ShowDetails = false;
        await viewModel.OnNavigatedFromAsync();
        Assert.True(harness.Saved!.DetailsHidden);

        var again = await harness.OpenAsync();
        Assert.False(again.ShowDetails);
    }

    [Fact]
    public void AConnectionProblemShowsItsMessageAboveTheTabs()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            Assert.True(ById<FrameworkElement>(page, "pnlConnection").IsVisible);
            Assert.Equal(CollectionComplianceViewModel.SiteCodeMessage, ById<TextBlock>(page, "txtConnectionMessage").Text);
        }, arrange: h => h.Settings.SiteCode = "");
    }

    [Fact]
    public void ASkippedUserTemplateFileShowsTheMessageOnTheTemplatesTab()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            ShowTab(window, page, CollectionComplianceViewModel.TemplatesTab);
            Assert.True(ById<FrameworkElement>(page, "pnlTemplatesMessage").IsVisible);
            Assert.Equal("1 user template file was skipped: broken.json.", ById<TextBlock>(page, "txtTemplatesMessage").Text);
        }, arrange: h => h.Store.Message = "1 user template file was skipped: broken.json.");
    }

    [Fact]
    public void TheResultsPanelAndUndoShowAfterAWrite()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            Assert.False(ById<FrameworkElement>(page, "pnlResults").IsVisible);
            SelectCollection(window, page, "LAB00012");
            Run(viewModel.RemoveCollectionCommand.ExecuteAsync(null));
            Settle(window);
            Assert.True(ById<FrameworkElement>(page, "pnlResults").IsVisible);
            Assert.True(ById<FrameworkElement>(page, "btnUndoLast").IsVisible);
            Assert.Single(ById<DataGrid>(page, "dgResults").Items);
        });
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The pixels of the element's bounds, each composed over the theme's application background.</summary>
    private static List<Color> Pixels(Window window, FrameworkElement element, string? saveAs = null)
    {
        Settle(window);
        var root = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle((Brush)Application.Current.FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(root);
        var directory = Environment.GetEnvironmentVariable("APD_RENDER_DIR");
        if (saveAs is not null && !string.IsNullOrEmpty(directory))
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, saveAs + ".png"));
            encoder.Save(file);
        }
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        var x = Math.Max(0, (int)Math.Ceiling(bounds.X));
        var y = Math.Max(0, (int)Math.Ceiling(bounds.Y));
        var w = Math.Min(width - x, (int)Math.Floor(bounds.Width));
        var h = Math.Min(height - y, (int)Math.Floor(bounds.Height));
        Assert.True(w > 0 && h > 0, $"The element at {bounds} is outside the rendered area.");
        var data = new byte[w * h * 4];
        bitmap.CopyPixels(new Int32Rect(x, y, w, h), data, w * 4, 0);
        var pixels = new List<Color>(w * h);
        for (var i = 0; i < data.Length; i += 4) pixels.Add(Color.FromRgb(data[i + 2], data[i + 1], data[i]));
        return pixels;
    }

    private static Color MostCommon(List<Color> pixels) => pixels.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;

    private static double TextContrast(List<Color> pixels)
    {
        var background = MostCommon(pixels);
        return pixels.Max(p => Contrast(p, background));
    }

    private static DataGridRow Row(DataGrid grid, int index)
    {
        grid.ScrollIntoView(grid.Items[index]);
        grid.UpdateLayout();
        return (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index);
    }

    private static TextBlock RowText(DataGridRow row) =>
        Descendants(row).OfType<DataGridCell>().Where(c => c.Column is DataGridTextColumn).SelectMany(c => Descendants(c).OfType<TextBlock>())
            .First(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.ActualWidth > 20);

    /// <summary>A row's text is readable on its background; a selected row differs visibly and a focused cell has the theme's focus rectangle.</summary>
    private static void AssertReadableRowsAndVisibleSelection(Window window, DataGrid grid, string name, Action<int> select, bool themedSelection = true)
    {
        Assert.True(grid.Items.Count >= 2, $"{name} needs two rows.");
        select(0);
        Settle(window);
        var plain = Pixels(window, RowText(Row(grid, 1)), name);
        Assert.True(TextContrast(plain) >= 4.5, $"{name}: the text of an unselected row has a contrast of {TextContrast(plain):0.00} on {MostCommon(plain)}.");
        var selected = Pixels(window, RowText(Row(grid, 0)));
        Assert.True(TextContrast(selected) >= 4.5, $"{name}: the text of a selected row has a contrast of {TextContrast(selected):0.00} on {MostCommon(selected)}.");
        if (!themedSelection) return;
        Assert.Same(Application.Current.FindResource("DefaultControlFocusVisualStyle"), Descendants(Row(grid, 0)).OfType<DataGridCell>().First().FocusVisualStyle);
        var mark = Contrast(MostCommon(selected), MostCommon(plain));
        Assert.True(mark >= 1.15, $"{name}: a selected row ({MostCommon(selected)}) is not visibly different from an unselected row ({MostCommon(plain)}): {mark:0.00}.");
    }

    private static void AssertReadableListAndVisibleSelection(Window window, ListBox list, string name)
    {
        list.SelectedIndex = 0;
        Settle(window);
        TextBlock ItemText(int i) => Descendants((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).OfType<TextBlock>()
            .First(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text));
        var plain = Pixels(window, ItemText(1), name);
        Assert.True(TextContrast(plain) >= 4.5, $"{name}: an unselected item has a contrast of {TextContrast(plain):0.00}.");
        var selected = Pixels(window, ItemText(0));
        Assert.True(TextContrast(selected) >= 4.5, $"{name}: the selected item has a contrast of {TextContrast(selected):0.00} on {MostCommon(selected)}.");
        var mark = Contrast(MostCommon(selected), MostCommon(plain));
        Assert.True(mark >= 1.15, $"{name}: the selected item ({MostCommon(selected)}) is not visibly different from another ({MostCommon(plain)}): {mark:0.00}.");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePageGridsShowReadableRowsAndAVisibleSelectionInTheTheme(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = Vm(page);
            var collections = ById<DataGrid>(page, "dgCollections");
            AssertReadableRowsAndVisibleSelection(window, collections, $"Collections-{theme}", i => collections.SelectedIndex = i);

            SelectCollection(window, page, "LAB00010");
            viewModel.DetailTabIndex = 1;
            Settle(window);
            var members = ById<DataGrid>(page, "dgMembers");
            AssertReadableRowsAndVisibleSelection(window, members, $"Members-{theme}", i => members.SelectedIndex = i);

            ShowTab(window, page, CollectionComplianceViewModel.TemplatesTab);
            var templates = ById<DataGrid>(page, "dgTemplates");
            AssertReadableRowsAndVisibleSelection(window, templates, $"Templates-{theme}", i => templates.SelectedIndex = i);

            ShowTab(window, page, CollectionComplianceViewModel.SiteQueriesTab);
            Run(viewModel.QueriesLoad);
            Settle(window);
            var queries = ById<DataGrid>(page, "dgSavedQueries");
            AssertReadableRowsAndVisibleSelection(window, queries, $"SiteQueries-{theme}", i => queries.SelectedIndex = i);

            ShowTab(window, page, CollectionComplianceViewModel.WqlTab);
            viewModel.WqlText = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System";
            Run(viewModel.PreviewWqlCommand.ExecuteAsync(null));
            Settle(window);
            var preview = ById<DataGrid>(page, "dgPreview");
            AssertReadableRowsAndVisibleSelection(window, preview, $"Preview-{theme}", i => preview.SelectedIndex = i);

            ShowTab(window, page, CollectionComplianceViewModel.CollectionsTab);
            viewModel.SetSelectedRows(viewModel.Rows.Where(r => !r.IsBuiltIn));
            Run(viewModel.EvaluateCommand.ExecuteAsync(null));
            Settle(window);
            var results = ById<DataGrid>(page, "dgResults");
            AssertReadableRowsAndVisibleSelection(window, results, $"Results-{theme}", i => results.SelectedIndex = i);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Dark, ApplicationTheme.Light)]
    public void TheCollectionsGridFollowsALiveThemeSwitch(ApplicationTheme start, ApplicationTheme next)
    {
        WithPage(start, (window, page, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgCollections");
            AssertReadableRowsAndVisibleSelection(window, grid, $"Collections-{start}", i => grid.SelectedIndex = i);
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            dictionaries[dictionaries.IndexOf(dictionaries.OfType<ThemesDictionary>().Single())] = new ThemesDictionary { Theme = next };
            StaTestHelper.Flush();
            AssertReadableRowsAndVisibleSelection(window, grid, $"Collections-{start}-to-{next}", i => grid.SelectedIndex = i);
        });
    }

    public static TheoryData<CollectionEditorMode, ApplicationTheme> ModesAndThemes()
    {
        var data = new TheoryData<CollectionEditorMode, ApplicationTheme>();
        foreach (var mode in Enum.GetValues<CollectionEditorMode>())
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
                data.Add(mode, theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(ModesAndThemes))]
    public void TheCollectionEditorRendersEachMode(CollectionEditorMode mode, ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new CollectionComplianceHarness();
            var editor = new CollectionEditorViewModel(mode, harness.Service.Collections, _ => Task.FromResult(true), mode == CollectionEditorMode.New ? null : harness.Servers);
            var window = new CollectionEditorWindow(editor);
            Show(window);
            try
            {
                Assert.Equal(editor.Title, window.Title);
                Assert.Equal(mode == CollectionEditorMode.Copy, ById<FrameworkElement>(window, "txtCopySource").IsVisible);
                Assert.Equal(mode != CollectionEditorMode.Copy, ById<FrameworkElement>(window, "lstLimiting").IsVisible);
                Assert.Equal(mode != CollectionEditorMode.Copy, ById<FrameworkElement>(window, "cboRefreshType").IsVisible);
                Assert.Equal(mode != CollectionEditorMode.Copy, ById<FrameworkElement>(window, "txtRefreshCount").IsVisible);
                Assert.Equal(mode != CollectionEditorMode.Copy, ById<FrameworkElement>(window, "cboRefreshUnit").IsVisible);
                if (mode != CollectionEditorMode.Copy)
                {
                    editor.RefreshType = "Manual";
                    Settle(window);
                    Assert.False(ById<FrameworkElement>(window, "txtRefreshCount").IsVisible);
                    editor.RefreshType = "Periodic";
                    Settle(window);
                }
                Assert.Equal(mode == CollectionEditorMode.Copy ? "Copy of Servers" : mode == CollectionEditorMode.Edit ? "Servers" : "",
                    ById<System.Windows.Controls.TextBox>(window, "txtCollectionName").Text);
                Assert.True(ById<Button>(window, "btnCollectionSave").IsDefault);
                if (mode == CollectionEditorMode.Copy) return;
                var list = ById<ListBox>(window, "lstLimiting");
                Assert.Equal(editor.LimitingChoices.Count, list.Items.Count);
                Assert.Equal("Selected: All Systems (SMS00001)", ById<TextBlock>(window, "txtSelectedLimiting").Text);
                AssertReadableListAndVisibleSelection(window, list, $"Editor-{mode}-{theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AtItsMinimumSizeThePeriodicEditorShowsEveryControlAboveTheFooter(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new CollectionComplianceHarness();
            var editor = new CollectionEditorViewModel(CollectionEditorMode.Edit, harness.Service.Collections, _ => Task.FromResult(true), harness.Servers);
            var window = new CollectionEditorWindow(editor);
            Show(window);
            try
            {
                Assert.Equal((CollectionEditorViewModel.MinimumWidth, CollectionEditorViewModel.MinimumHeight), (window.MinWidth, window.MinHeight));
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                editor.RefreshType = "Periodic";
                editor.ShowMessage(CollectionEditorViewModel.RefreshIntervalInvalid);
                Settle(window);
                var footer = ById<FrameworkElement>(window, "pnlEditorFooter");
                var footerTop = footer.TranslatePoint(new Point(0, 0), window).Y;
                foreach (var id in new[] { "txtCollectionName", "txtCollectionComment", "cboRefreshType", "txtRefreshCount", "cboRefreshUnit", "txtLimitingFilter", "lstLimiting", "txtSelectedLimiting", "btnCollectionSave" })
                {
                    var element = ById<FrameworkElement>(window, id);
                    Assert.True(element.IsVisible, $"{id} is not visible.");
                    var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Right <= window.ActualWidth + 0.5, $"{id} ends at {bounds.Right:0} px in a {window.ActualWidth:0} px window.");
                    if (id != "btnCollectionSave") Assert.True(bounds.Bottom <= footerTop + 0.5, $"{id} ends at {bounds.Bottom:0} px under the footer at {footerTop:0} px.");
                }
                Assert.True(ById<FrameworkElement>(window, "lstLimiting").ActualHeight >= 96, "The limiting collection list is too short to show its items.");
                Pixels(window, footer, $"Editor-minimum-{theme}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ARememberedEditorSizeBelowTheMinimumOpensAtTheMinimum()
    {
        var harness = new CollectionComplianceHarness { Saved = new ApplicationPackagerUI.Models.CollectionComplianceSettings { EditorWidth = 480, EditorHeight = 420 } };
        var viewModel = await harness.OpenAsync();
        (double Width, double Height) opened = default;
        harness.Prompts.Editor = e => opened = (e.Width, e.Height);
        await viewModel.NewCollectionCommand.ExecuteAsync(null);
        Assert.Equal((CollectionEditorViewModel.MinimumWidth, CollectionEditorViewModel.MinimumHeight), opened);
    }

    [Fact]
    public void AFilterThatHidesTheSelectedLimitingCollectionKeepsIt()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var harness = new CollectionComplianceHarness();
            var editor = new CollectionEditorViewModel(CollectionEditorMode.New, harness.Service.Collections, _ => Task.FromResult(true));
            var window = new CollectionEditorWindow(editor);
            Show(window);
            try
            {
                editor.LimitingFilter = "serv";
                Settle(window);
                Assert.Single(ById<ListBox>(window, "lstLimiting").Items);
                Assert.Equal("SMS00001", editor.SelectedLimiting?.Row.CollectionId);
                Assert.Equal("Selected: All Systems (SMS00001)", ById<TextBlock>(window, "txtSelectedLimiting").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void KeyboardFocusOnALimitingCollectionSelectsIt()
    {
        WithApplication(ApplicationTheme.Dark, () =>
        {
            var harness = new CollectionComplianceHarness();
            var editor = new CollectionEditorViewModel(CollectionEditorMode.New, harness.Service.Collections, _ => Task.FromResult(true));
            var window = new CollectionEditorWindow(editor);
            Show(window);
            try
            {
                var list = ById<ListBox>(window, "lstLimiting");
                var index = editor.LimitingChoices.ToList().FindIndex(c => c.Row.CollectionId == "LAB00011");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
                item.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, null, item)
                    { RoutedEvent = System.Windows.Input.Keyboard.GotKeyboardFocusEvent });
                Assert.Equal("LAB00011", editor.SelectedLimiting?.Row.CollectionId);
                Assert.True(item.IsSelected);
                Assert.All(new[] { "txtCollectionName", "txtCollectionComment", "cboRefreshType", "txtLimitingFilter", "lstLimiting", "btnCollectionSave" },
                    id => Assert.True(System.Windows.Input.KeyboardNavigation.GetIsTabStop(ById<Control>(window, id)) || ById<Control>(window, id).Focusable, id));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePickerAndThePromptRenderInTheTheme(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new CollectionComplianceHarness();
            var picker = new CollectionPickerViewModel("Apply template All workstations", "Check the collections.", "Show plan",
                harness.Service.Collections.Where(c => !c.IsBuiltIn), _ => Task.FromResult(true));
            var pickerWindow = new CollectionPickerWindow(picker);
            Show(pickerWindow);
            try
            {
                var grid = ById<DataGrid>(pickerWindow, "dgPickerTargets");
                Assert.Equal(3, grid.Items.Count);
                AssertResizableReadableColumns(grid);
                AssertReadableRowsAndVisibleSelection(pickerWindow, grid, $"Picker-{theme}", i => grid.SelectedIndex = i);
                picker.Items[0].IsChecked = true;
                Settle(pickerWindow);
                Assert.Equal("1 checked", ById<TextBlock>(pickerWindow, "txtPickerCount").Text.Trim());
                var names = UIElementAutomationPeer.CreatePeerForElement(grid).GetChildren().OfType<DataGridItemAutomationPeer>().Select(p => p.GetName()).ToList();
                Assert.Equal(["Pilot (LAB00012)", "Servers (LAB00010)", "Workstations (LAB00011)"], names);
            }
            finally { pickerWindow.Close(); }

            var prompt = new TextPromptViewModel("Add direct members", "Device names", "One name per line.", "Look up and show plan", true, _ => null, _ => Task.FromResult(true));
            var promptWindow = new TextPromptWindow(prompt);
            Show(promptWindow);
            try
            {
                Assert.True(ById<System.Windows.Controls.TextBox>(promptWindow, "txtPromptValue").AcceptsReturn);
                Assert.True(ById<FrameworkElement>(promptWindow, "txtPromptNote").IsVisible);
                prompt.ShowMessage(TextPromptViewModel.Required);
                Settle(promptWindow);
                Assert.Equal(TextPromptViewModel.Required, ById<TextBlock>(promptWindow, "txtPromptMessage").Text);
            }
            finally { promptWindow.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AtItsDefaultSizeThePlanWindowShowsTheWholeDetailOfARemove(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var detail = "Removes collection CCM-Test-10190741-5 (MCM00066). Members now: 0. Deployments to the collection: 1. ConfigMgr removes them with the collection. "
                + "Maintenance windows on the collection: 1. ConfigMgr removes them with the collection. Collections that include or exclude it: 0. "
                + "Collections that use it as limiting collection: 0. Rules captured for Undo: 0. If these counts or the properties change before the remove starts, "
                + $"the collection is not removed. {CollectionComplianceViewModel.RemoveUndoNote} {CollectionWriter.RecreatedNote}";
            var plan = new ChangePlan("Remove collection", "Remove collection", [new ChangePlanItem("Remove", "CCM-Test-10190741-5 (MCM00066)", detail)], hasUndo: true, isDestructive: true);
            var viewModel = new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult([])), (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                var grid = ById<DataGrid>(window, "gridChangePlan");
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(grid));
                var cell = Descendants(Row(grid, 0)).OfType<DataGridCell>().Single(c => Equals(c.Column.Header, "Detail"));
                var text = Descendants(cell).OfType<TextBlock>().First();
                Assert.Equal(detail, text.ToolTip);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                var bottom = text.TransformToAncestor(grid).TransformBounds(new Rect(text.RenderSize)).Bottom;
                Assert.True(bottom <= grid.ActualHeight + 0.5, $"The detail ends at {bottom:0} px in a {grid.ActualHeight:0} px list.");
                Pixels(window, grid, $"RemovePlan-{theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AtItsDefaultSizeThePlanWindowShowsALongObjectNameAndIdUncut(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            const string target = "CCM-Test-10190923-5 Patch Ring Workstations (MCM00012)";
            const string detail = "Removes collection CCM-Test-10190923-5 Patch Ring Workstations (MCM00012). Members now: 0. Deployments to the collection: 1.";
            var plan = new ChangePlan("Remove collection", "Remove collection", [new ChangePlanItem("Remove", target, detail)], isDestructive: true);
            var viewModel = new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                var grid = ById<DataGrid>(window, "gridChangePlan");
                var cells = Descendants(Row(grid, 0)).OfType<DataGridCell>().ToList();
                var objectCell = cells.Single(c => Equals(c.Column.Header, "Object"));
                var text = Descendants(objectCell).OfType<TextBlock>().First();
                Assert.Equal(target, text.ToolTip);
                Assert.True(text.ActualWidth <= objectCell.ActualWidth + 0.5, $"The object text is {text.ActualWidth:0} px in a {objectCell.ActualWidth:0} px cell.");
                var detailCell = cells.Single(c => Equals(c.Column.Header, "Detail"));
                Assert.True(detailCell.ActualWidth >= 300, $"The Detail column is {detailCell.ActualWidth:0} px wide.");
                Pixels(window, grid, $"LongObjectPlan-{theme}");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePlanWindowShowsALongTemplatePlan(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var items = Enumerable.Range(1, 150).Select(i => i % 10 == 0
                ? new ChangePlanItem("Skip", $"Collection {i} (LAB{i:X5})", CollectionComplianceViewModel.RuleNameExists("All workstations"))
                : new ChangePlanItem("Add", $"Collection {i} (LAB{i:X5})", "Query rule All workstations: select SMS_R_System.ResourceId from SMS_R_System where SMS_R_System.OperatingSystemNameandVersion like '%Workstation%'. Undo removes it."));
            var plan = new ChangePlan("Apply template All workstations", "Add 135 rules", items, hasUndo: true);
            var viewModel = new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult(
                c.Plan.Items.Select(i => i.Action == "Skip" ? ChangePlanItemResult.Skip(i, "Not changed.") : new ChangePlanItemResult(i, true, "Added.")).ToList())),
                (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                var grid = ById<DataGrid>(window, "gridChangePlan");
                Assert.Equal(150, grid.Items.Count);
                AssertReadableRowsAndVisibleSelection(window, grid, $"Plan-{theme}", i => grid.SelectedIndex = i, themedSelection: false);
                Run(viewModel.ConfirmCommand.ExecuteAsync(null));
                Settle(window);
                Assert.Equal("135 changed, 0 failed, 15 skipped.", viewModel.ResultText);
            }
            finally { window.Close(); }
        });
    }
}
