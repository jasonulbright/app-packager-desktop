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
using DataGridCell = System.Windows.Controls.DataGridCell;
using Point = System.Windows.Point;
using TabControl = System.Windows.Controls.TabControl;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

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

    private static void WithPage(ApplicationTheme theme, Action<Window, MaintenanceWindowsPage, MaintenanceWindowsHarness> test, double width = 1400, double height = 900)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var viewModel = harness.Create();
            var page = new MaintenanceWindowsPage(viewModel);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
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

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AWindowWhoseScheduleThePageCannotHoldDisablesEditAndRemoveWithATooltip(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            const string text = "Occurs every 1 hours effective 10/1/2026 12:00 AM";
            harness.Service.Windows.Add(new MaintenanceWindowInfo(harness.Kiosks.CollectionId, harness.Kiosks.Name, "Hourly", "{900}", "Any", true,
                new MaintenanceSchedule(MaintenanceScheduleKind.OneTime, DateTime.MinValue, TimeSpan.Zero, false, 1, null, null, null, 0, null), text, null, text));
            var viewModel = harness.Create();
            var page = new MaintenanceWindowsPage(viewModel);
            var window = new Window { Width = 1400, Height = 900, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                var opened = page.OnNavigatedToAsync();
                StaTestHelper.PumpUntil(() => opened.IsCompleted);
                var grid = ById<DataGrid>(page, "dgWindows");
                grid.SelectedItem = viewModel.Rows.Single(r => r.WindowName == "Hourly");
                StaTestHelper.Flush();
                var edit = ById<System.Windows.Controls.Button>(page, "btnEditWindow");
                var remove = ById<System.Windows.Controls.Button>(page, "btnRemoveWindow");
                Assert.False(edit.IsEnabled);
                Assert.False(remove.IsEnabled);
                Assert.True(ById<System.Windows.Controls.Button>(page, "btnToggleEnabled").IsEnabled);
                Assert.Equal(MaintenanceWindowsViewModel.ScheduleNotHeld, edit.ToolTip);
                Assert.Equal(MaintenanceWindowsViewModel.ScheduleNotHeld, remove.ToolTip);
                Assert.True(ToolTipService.GetShowOnDisabled(edit));
                viewModel.DetailTabIndex = 1;
                StaTestHelper.Flush();
                Assert.Equal([MaintenanceWindowsViewModel.NoOccurrencesShown], ById<ItemsControl>(page, "lstNextOccurrences").Items.Cast<string>());
            }
            finally { window.Close(); }
        });
    }

    private const string OwnDescription = "Patch the file servers first.";

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDetailPanelShowsTheOwnDescriptionOnly(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var own = harness.Service.AddWindow(harness.Kiosks, "Own text");
            harness.Service.Windows[^1] = own with { Description = OwnDescription };
            var configMgr = harness.Service.AddWindow(harness.Kiosks, "ConfigMgr text");
            harness.Service.Windows[^1] = configMgr with { Description = harness.Projector.Describe(configMgr.Schedule) };
            var viewModel = harness.Create();
            var page = new MaintenanceWindowsPage(viewModel);
            var window = new Window { Width = 1400, Height = 900, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                var opened = page.OnNavigatedToAsync();
                StaTestHelper.PumpUntil(() => opened.IsCompleted);
                var grid = ById<DataGrid>(page, "dgWindows");
                grid.SelectedItem = viewModel.Rows.Single(r => r.WindowName == "Own text");
                StaTestHelper.Flush();
                var properties = ById<ItemsControl>(page, "lstProperties");
                var box = Descendants(properties).OfType<System.Windows.Controls.TextBox>().Single(t => AutomationProperties.GetName(t) == "Description");
                Assert.Equal(OwnDescription, box.Text);
                Assert.True(box.IsReadOnly);
                Assert.Contains(Descendants(properties).OfType<TextBlock>(), t => t.Text == "Description");

                foreach (var name in new[] { "ConfigMgr text", "Patch Tuesday" })
                {
                    grid.SelectedItem = viewModel.Rows.Single(r => r.WindowName == name);
                    StaTestHelper.Flush();
                    Assert.DoesNotContain(Descendants(properties).OfType<TextBlock>(), t => t.Text == "Description");
                    Assert.DoesNotContain(Descendants(properties).OfType<System.Windows.Controls.TextBox>(), t => AutomationProperties.GetName(t) == "Description");
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheEditorShowsTheOwnDescriptionReadOnlyWithANote(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var plain = harness.Service.Windows[0];
            foreach (var (source, own) in new[] { (plain with { Description = OwnDescription }, true), (plain with { Description = harness.Projector.Describe(plain.Schedule) }, false), (plain, false) })
            {
                var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.EditWindow, harness.Projector, _ => Task.FromResult(true),
                    () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows);
                editor.Load(source);
                var window = new MaintenanceWindowEditorWindow(editor);
                Show(window);
                try
                {
                    Assert.Equal(own, ById<FrameworkElement>(window, "pnlWindowDescription").IsVisible);
                    Assert.False(ById<FrameworkElement>(window, "txtTemplateDescription").IsVisible);
                    if (!own) continue;
                    var box = ById<System.Windows.Controls.TextBox>(window, "txtWindowDescription");
                    Assert.Equal(OwnDescription, box.Text);
                    Assert.True(box.IsReadOnly);
                    Assert.Equal(MaintenanceWindowEditorViewModel.DescriptionKeptNote, ById<TextBlock>(window, "txtDescriptionKept").Text);
                    Assert.True(ById<TextBlock>(window, "txtDescriptionKept").IsVisible);
                }
                finally { window.Close(); }
            }
        });
    }

    /// <summary>The main window gives this width to the navigation pane and its border; the page gets the rest.</summary>
    private const double NavigationPaneWidth = 230;

    private const double ReadableColumnWidth = 60;

    private static double TextWidth(TextBlock style, string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(style.FontFamily, style.FontStyle, style.FontWeight, style.FontStretch), style.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(style).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static System.Windows.Controls.Primitives.DataGridColumnHeader Header(DataGrid grid, string header) =>
        Descendants(grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => h.Column is not null && Equals(h.Column.Header, header));

    private static System.Windows.Controls.Primitives.Thumb RightGripper(DataGrid grid, string header)
    {
        var h = Header(grid, header);
        return (System.Windows.Controls.Primitives.Thumb)h.Template.FindName("PART_RightHeaderGripper", h);
    }

    private static void DragRightEdge(DataGrid grid, string header, double change)
    {
        var gripper = RightGripper(grid, header);
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(change, 0));
        gripper.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(change, 0, false));
        grid.UpdateLayout();
        StaTestHelper.Flush();
    }

    private static void DoubleClickRightEdge(DataGrid grid, string header)
    {
        RightGripper(grid, header).RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        grid.UpdateLayout();
        StaTestHelper.Flush();
    }

    private static void AssertDragWidens(DataGrid grid, string header, double change)
    {
        var column = grid.Columns.Single(c => Equals(c.Header, header));
        var before = column.ActualWidth;
        DragRightEdge(grid, header, change);
        Assert.True(column.ActualWidth >= before + change - 1, $"Dragging the {header} edge {change} px moved it from {before} px to {column.ActualWidth} px.");
    }

    private static System.Windows.Controls.ScrollViewer GridScroller(DataGrid grid) =>
        Descendants(grid).OfType<System.Windows.Controls.ScrollViewer>().First();

    /// <summary>
    /// Every column can be resized, has a minimum that holds its header text, and is at least that
    /// minimum wide; every text column trims with an ellipsis and shows the full text as a tooltip.
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
                var style = text.ElementStyle;
                Assert.NotNull(style);
                var setters = style.Setters.OfType<Setter>().ToList();
                Assert.Contains(setters, s => s.Property == TextBlock.TextTrimmingProperty && Equals(s.Value, TextTrimming.CharacterEllipsis));
                Assert.Contains(setters, s => s.Property == FrameworkElement.ToolTipProperty);
            }
        }
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
    [InlineData(1440)]
    [InlineData(1024)]
    public void TheWindowsGridKeepsReadableResizableColumnsAtACommonWidth(double width)
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgWindows");
            grid.SelectedIndex = 0;
            window.UpdateLayout();
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "tabWindowDetails").IsVisible);
            AssertResizableReadableColumns(grid);
            var scroller = GridScroller(grid);
            if (width >= 1440)
            {
                Assert.Equal(0, Grid.GetRow(ById<FrameworkElement>(page, "pnlWindowDetails")));
                Assert.True(ById<FrameworkElement>(page, "splitWindowDetails").IsVisible);
                Assert.True(scroller.ScrollableWidth < 1, $"The columns need {scroller.ScrollableWidth:0} px more than the {scroller.ViewportWidth:0} px grid.");
                AssertTextFits(grid, "Status", ["Enabled", "Disabled"]);
                AssertTextFits(grid, "Type", MaintenanceWindowsText.WindowTypes.Select(t => t.Text));
                AssertTextFits(grid, "Recurrence", MaintenanceWindowsText.Kinds.Select(k => k.Text).Append(MaintenanceWindowsText.OtherSchedule));
                AssertTextFits(grid, "Duration", ["24h 0m"]);
                AssertTextFits(grid, "Next occurrence", [MaintenanceWindowsText.Time(new DateTime(2026, 12, 28, 22, 0, 0))]);
                foreach (var name in new[] { "Collection", "Window", "Schedule" })
                    Assert.True(grid.Columns.Single(c => Equals(c.Header, name)).ActualWidth >= 90, $"{name} is narrower than 90 px.");
            }
            else
            {
                Assert.True(scroller.ScrollableWidth > 0, "A narrow grid does not scroll sideways.");
                Assert.Equal(1, Grid.GetRow(ById<FrameworkElement>(page, "pnlWindowDetails")));
                Assert.False(ById<FrameworkElement>(page, "splitWindowDetails").IsVisible);
            }

            AssertDragWidens(grid, "Type", 40);
            AssertResizableReadableColumns(grid);
            DragRightEdge(grid, "Type", -400);
            AssertResizableReadableColumns(grid);
        }, width - NavigationPaneWidth);
    }

    [Fact]
    public void AutoSizingATemplateColumnKeepsTheOtherColumnsReadable()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            ById<TabControl>(page, "tabMaintenanceWindows").SelectedIndex = 2;
            window.UpdateLayout();
            StaTestHelper.Flush();
            var grid = ById<DataGrid>(page, "dgTemplates");
            grid.SelectedIndex = 0;
            window.UpdateLayout();
            StaTestHelper.Flush();
            Assert.True(GridScroller(grid).ScrollableWidth < 1, "A selected template row scrolls the grid sideways.");
            AssertResizableReadableColumns(grid);
            AssertTextFits(grid, "Recurrence", MaintenanceWindowsText.Kinds.Select(k => k.Text));
            AssertTextFits(grid, "Type", MaintenanceWindowsText.WindowTypes.Select(t => t.Text));
            AssertTextFits(grid, "Duration", ["24h 0m"]);
            AssertTextFits(grid, "Source", ["Shipped", "User"]);
            foreach (var header in new[] { "Recurrence", "Type", "Name" })
            {
                DoubleClickRightEdge(grid, header);
                AssertResizableReadableColumns(grid);
            }
            AssertDragWidens(grid, "Name", 600);
            AssertResizableReadableColumns(grid);
        }, 1440 - NavigationPaneWidth);
    }

    [Fact]
    public void TheCoverageGridKeepsReadableResizableColumns()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            ById<TabControl>(page, "tabMaintenanceWindows").SelectedIndex = 1;
            window.UpdateLayout();
            StaTestHelper.Flush();
            var grid = ById<DataGrid>(page, "dgCoverage");
            AssertResizableReadableColumns(grid);
            DoubleClickRightEdge(grid, "Coverage");
            AssertResizableReadableColumns(grid);
            AssertDragWidens(grid, "Collection", 600);
            AssertResizableReadableColumns(grid);
        }, 1024 - NavigationPaneWidth);
    }

    [Fact]
    public void TheApplyTemplateListKeepsReadableResizableColumns()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var dialog = new MaintenanceApplyTemplateViewModel(harness.Store.Templates, harness.Service.Collections,
                new Dictionary<string, int>(), _ => Task.FromResult(true));
            var window = new MaintenanceApplyTemplateWindow(dialog);
            Show(window);
            try
            {
                var grid = ById<DataGrid>(window, "dgApplyTargets");
                AssertResizableReadableColumns(grid);
                DoubleClickRightEdge(grid, "Collection");
                AssertResizableReadableColumns(grid);
                AssertDragWidens(grid, "ID", 600);
                AssertResizableReadableColumns(grid);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ScreenReadersHearPlainNamesForRowsAndItems()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var viewModel = (MaintenanceWindowsViewModel)page.DataContext;
            var grid = ById<DataGrid>(page, "dgWindows");
            var names = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(grid).GetChildren()
                .OfType<System.Windows.Automation.Peers.DataGridItemAutomationPeer>().Select(p => p.GetName()).ToList();
            Assert.Equal(3, names.Count);
            Assert.Contains("Nightly, Workstations, Weekly at 02:00", names);
            Assert.All(names, n => Assert.DoesNotContain("{", n));

            var collections = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, harness.Projector, _ => Task.FromResult(true),
                () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows);
            var editor = new MaintenanceWindowEditorWindow(collections);
            Show(editor);
            try
            {
                var list = ById<System.Windows.Controls.ListBox>(editor, "lstCollections");
                var items = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(list).GetChildren().Select(p => p.GetName()).ToList();
                Assert.Equal(["Kiosks (LAB00012)", "Servers (LAB00010)", "Workstations (LAB00011)"], items);
            }
            finally { editor.Close(); }
        });
    }

    [Fact]
    public void KeyboardFocusOnACollectionSelectsIt()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, harness.Projector, _ => Task.FromResult(true),
                () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows);
            var window = new MaintenanceWindowEditorWindow(editor);
            Show(window);
            try
            {
                editor.CollectionFilter = "Serv";
                StaTestHelper.Flush();
                var list = ById<System.Windows.Controls.ListBox>(window, "lstCollections");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Null(editor.SelectedCollection);
                item.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, null, item)
                    { RoutedEvent = System.Windows.Input.Keyboard.GotKeyboardFocusEvent });
                Assert.Equal("Servers", editor.SelectedCollection?.Collection.Name);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheEditorShowsTheOwnCollectionOfTheWindow()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            for (var i = 0; i < 20; i++) harness.Service.AddCollection($"LAB001{i:00}", $"Area {i:00}");
            var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.EditWindow, harness.Projector, _ => Task.FromResult(true),
                () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows);
            editor.Load(harness.Service.Windows.Single(w => w.WindowName == "Nightly"));
            var window = new MaintenanceWindowEditorWindow(editor);
            Show(window);
            try
            {
                var list = ById<System.Windows.Controls.ListBox>(window, "lstCollections");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(editor.SelectedCollection);
                Assert.NotNull(item);
                Assert.True(item.IsSelected);
                var top = item.TranslatePoint(new Point(0, 0), list).Y;
                Assert.True(top >= 0 && top + item.ActualHeight <= list.ActualHeight, $"The item is at {top} in a list of {list.ActualHeight} px.");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheApplyTemplateCheckBoxFitsItsColumn()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var dialog = new MaintenanceApplyTemplateViewModel(harness.Store.Templates, harness.Service.Collections,
                new Dictionary<string, int>(), _ => Task.FromResult(true));
            var window = new MaintenanceApplyTemplateWindow(dialog);
            Show(window);
            try
            {
                var grid = ById<DataGrid>(window, "dgApplyTargets");
                var cell = Descendants(grid).OfType<DataGridCell>().First(c => c.Column.DisplayIndex == 0);
                var box = Descendants(cell).OfType<System.Windows.Controls.CheckBox>().Single();
                var bullet = Descendants(box).OfType<System.Windows.Controls.Primitives.BulletDecorator>().First();
                var glyph = Descendants(bullet).OfType<Border>().First();
                Assert.True(bullet.ActualWidth >= glyph.ActualWidth, $"The glyph is {glyph.ActualWidth} px wide in a {bullet.ActualWidth} px space.");
                var left = glyph.TranslatePoint(new Point(0, 0), cell).X;
                Assert.True(left >= 0, $"The glyph starts at {left} px.");
                Assert.True(left + glyph.ActualWidth <= cell.ActualWidth, $"The glyph ends at {left + glyph.ActualWidth} px in a {cell.ActualWidth} px cell.");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ThePlanDetailWrapsAndHasATooltip()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var detail = "Old: " + string.Join(" ", Enumerable.Repeat("Occurs the Second Tuesday of every 1 months effective 10/14/2026 2:00 AM", 4)) + ".";
            var plan = new ChangePlan("Remove maintenance window", "Remove window", [new ChangePlanItem("Remove", "Kiosks: Nightly", detail)]);
            var viewModel = new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(viewModel);
            Show(window);
            try
            {
                var text = Descendants(ById<DataGrid>(window, "gridChangePlan")).OfType<TextBlock>().Single(t => t.Text == detail);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.Equal(detail, text.ToolTip);
            }
            finally { window.Close(); }
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
                Assert.Equal(editor.ShowWeekdayOffset, ById<FrameworkElement>(window, "cboWeekdayOffset").IsVisible);
                Assert.Equal(editor.IsRecurring, ById<TextBlock>(window, "txtNoEndDate").IsVisible);
                Assert.Equal(MaintenanceWindowEditorViewModel.NoEndDateNote, ById<TextBlock>(window, "txtNoEndDate").Text);
                Assert.DoesNotContain(Descendants(window), e => AutomationProperties.GetAutomationId(e) is "dpEndDate" or "chkHasEnd" or "txtComment");
                Assert.Equal(25, ById<System.Windows.Controls.ComboBox>(window, "cboDurationHours").Items.Count);
                var picker = ById<DatePicker>(window, "dpStartDate");
                Assert.Equal((editor.FirstStartDate, editor.LastStartDate), (picker.DisplayDateStart, picker.DisplayDateEnd));
                Assert.Equal(editor.StartDate!.Value.ToString("D", System.Globalization.CultureInfo.CurrentCulture), picker.ToolTip);
                if (kind == MaintenanceScheduleKind.MonthlyByDate)
                {
                    editor.DayOfMonth = 31;
                    StaTestHelper.Flush();
                    Assert.Equal(editor.DayOfMonthNote, ById<TextBlock>(window, "txtDayOfMonthNote").Text);
                    Assert.True(ById<TextBlock>(window, "txtDayOfMonthNote").IsVisible);
                }
                Assert.Equal(kind == MaintenanceScheduleKind.OneTime ? 1 : 5, ById<ItemsControl>(window, "lstPreview").Items.Count);
                Assert.Equal(editor.ScheduleText, ById<TextBlock>(window, "txtScheduleText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheEditorClipsALongCollectionNameWithATooltipAndFitsTheWorkArea()
    {
        WithApplication(ApplicationTheme.Dark, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var name = "Servers in the " + string.Join(" ", Enumerable.Repeat("very long", 30)) + " folder";
            harness.Service.AddCollection("LAB00099", name, @"Patching\" + new string('x', 300));
            var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, harness.Projector, _ => Task.FromResult(true),
                () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows) { Width = 100_000, Height = 100_000 };
            var window = new MaintenanceWindowEditorWindow(editor);
            Show(window);
            try
            {
                Assert.True(window.Width <= SystemParameters.WorkArea.Width);
                Assert.True(window.Height <= SystemParameters.WorkArea.Height);
                var list = ById<System.Windows.Controls.ListBox>(window, "lstCollections");
                Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(list));
                var texts = Descendants(list).OfType<TextBlock>().Where(t => t.Text.Contains("very long", StringComparison.Ordinal)
                    || t.Text.Contains("xxxx", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, texts.Count);
                Assert.All(texts, t =>
                {
                    Assert.Equal(TextTrimming.CharacterEllipsis, t.TextTrimming);
                    Assert.Equal(t.Text, t.ToolTip);
                    Assert.True(t.ActualWidth <= list.ActualWidth);
                });
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

    /// <summary>
    /// Renders the window and returns the pixels of the element's bounds, each composed over the
    /// theme's application background, as a window backdrop shows through a transparent area.
    /// </summary>
    private static List<Color> Pixels(Window window, FrameworkElement element, string? saveAs = null)
    {
        window.UpdateLayout();
        StaTestHelper.Flush();
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
            using var file = System.IO.File.Create(System.IO.Path.Combine(directory, saveAs + ".png"));
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

    private static Color MostCommon(List<Color> pixels) =>
        pixels.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;

    /// <summary>The contrast of the strongest text pixel against the most common pixel of a cell's text.</summary>
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

    /// <summary>
    /// A data row's text is readable on its background. With the page's row style, a selected row
    /// also differs visibly from an unselected row and a focused cell shows the theme's focus rectangle.
    /// </summary>
    private static void AssertReadableRowsAndVisibleSelection(Window window, DataGrid grid, string name, Action<int> select, bool themedSelection = true)
    {
        Assert.True(grid.Items.Count >= 2, $"{name} needs two rows.");
        select(0);
        window.UpdateLayout();
        StaTestHelper.Flush();
        var plain = Pixels(window, RowText(Row(grid, 1)), name);
        var plainContrast = TextContrast(plain);
        Assert.True(plainContrast >= 4.5, $"{name}: the text of an unselected row has a contrast of {plainContrast:0.00} on {MostCommon(plain)}.");
        var selected = Pixels(window, RowText(Row(grid, 0)));
        var selectedContrast = TextContrast(selected);
        Assert.True(selectedContrast >= 4.5, $"{name}: the text of a selected row has a contrast of {selectedContrast:0.00} on {MostCommon(selected)}.");
        if (!themedSelection) return;
        Assert.Same(Application.Current.FindResource("DefaultControlFocusVisualStyle"), Descendants(Row(grid, 0)).OfType<DataGridCell>().First().FocusVisualStyle);
        var mark = Contrast(MostCommon(selected), MostCommon(plain));
        Assert.True(mark >= 1.15, $"{name}: a selected row ({MostCommon(selected)}) is not visibly different from an unselected row ({MostCommon(plain)}): {mark:0.00}.");
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePageGridsShowReadableRowsAndAVisibleSelectionInTheTheme(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = (MaintenanceWindowsViewModel)page.DataContext;
            var tabs = ById<TabControl>(page, "tabMaintenanceWindows");
            var windows = ById<DataGrid>(page, "dgWindows");
            AssertReadableRowsAndVisibleSelection(window, windows, $"Windows-{theme}", i => windows.SelectedIndex = i);

            tabs.SelectedIndex = 1;
            StaTestHelper.Flush();
            var coverage = ById<DataGrid>(page, "dgCoverage");
            AssertReadableRowsAndVisibleSelection(window, coverage, $"Coverage-{theme}", i => coverage.SelectedIndex = i);

            tabs.SelectedIndex = 2;
            StaTestHelper.Flush();
            var templates = ById<DataGrid>(page, "dgTemplates");
            AssertReadableRowsAndVisibleSelection(window, templates, $"Templates-{theme}", i => templates.SelectedIndex = i);

            tabs.SelectedIndex = 0;
            StaTestHelper.Flush();
            windows.SelectAll();
            StaTestHelper.Flush();
            var toggle = viewModel.ToggleEnabledCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => toggle.IsCompleted);
            StaTestHelper.Flush();
            var results = ById<DataGrid>(page, "dgResults");
            AssertReadableRowsAndVisibleSelection(window, results, $"Results-{theme}", i => results.SelectedIndex = i);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Dark, ApplicationTheme.Light)]
    public void TheWindowsGridFollowsALiveThemeSwitch(ApplicationTheme start, ApplicationTheme next)
    {
        WithPage(start, (window, page, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgWindows");
            AssertReadableRowsAndVisibleSelection(window, grid, $"Windows-{start}", i => grid.SelectedIndex = i);
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            dictionaries[dictionaries.IndexOf(dictionaries.OfType<ThemesDictionary>().Single())] = new ThemesDictionary { Theme = next };
            StaTestHelper.Flush();
            AssertReadableRowsAndVisibleSelection(window, grid, $"Windows-{start}-to-{next}", i => grid.SelectedIndex = i);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheDialogListsShowReadableRowsAndAVisibleSelectionInTheTheme(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var harness = new MaintenanceWindowsHarness();
            var dialog = new MaintenanceApplyTemplateViewModel(harness.Store.Templates, harness.Service.Collections,
                new Dictionary<string, int>(), _ => Task.FromResult(true));
            var applyWindow = new MaintenanceApplyTemplateWindow(dialog);
            Show(applyWindow);
            try
            {
                var grid = ById<DataGrid>(applyWindow, "dgApplyTargets");
                AssertReadableRowsAndVisibleSelection(applyWindow, grid, $"ApplyTemplate-{theme}", i => grid.SelectedIndex = i);
            }
            finally { applyWindow.Close(); }

            var plan = new ChangePlan("Toggle", "Toggle 2 windows",
                [new ChangePlanItem("Disable", "Servers: Patch Tuesday", "Enabled to disabled."), new ChangePlanItem("Disable", "Kiosks: Nightly", "Enabled to disabled.")]);
            var planWindow = new ChangePlanWindow(new ChangePlanViewModel(plan, (c, p, ct) => Task.FromResult(new ChangePlanResult([]))));
            Show(planWindow);
            try
            {
                var grid = ById<DataGrid>(planWindow, "gridChangePlan");
                AssertReadableRowsAndVisibleSelection(planWindow, grid, $"Plan-{theme}", i => grid.SelectedIndex = i, themedSelection: false);
            }
            finally { planWindow.Close(); }

            var editor = new MaintenanceWindowEditorViewModel(MaintenanceEditorMode.NewWindow, harness.Projector, _ => Task.FromResult(true),
                () => MaintenanceWindowsHarness.Now, harness.Service.Collections, harness.Service.Windows);
            var editorWindow = new MaintenanceWindowEditorWindow(editor);
            Show(editorWindow);
            try
            {
                var list = ById<System.Windows.Controls.ListBox>(editorWindow, "lstCollections");
                list.SelectedIndex = 0;
                editorWindow.UpdateLayout();
                StaTestHelper.Flush();
                TextBlock ItemText(int i) => Descendants((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i)).OfType<TextBlock>()
                    .First(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text));
                var plain = Pixels(editorWindow, ItemText(1), $"Editor-{theme}");
                Assert.True(TextContrast(plain) >= 4.5, $"Editor: an unselected collection has a contrast of {TextContrast(plain):0.00}.");
                var selected = Pixels(editorWindow, ItemText(0));
                Assert.True(TextContrast(selected) >= 4.5, $"Editor: the selected collection has a contrast of {TextContrast(selected):0.00} on {MostCommon(selected)}.");
                var mark = Contrast(MostCommon(selected), MostCommon(plain));
                Assert.True(mark >= 1.15, $"Editor: the selected collection ({MostCommon(selected)}) is not visibly different from another ({MostCommon(plain)}): {mark:0.00}.");
                var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var folder = Descendants(item).OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).ElementAt(1);
                Assert.True(TextContrast(Pixels(editorWindow, folder)) >= 4.5, $"Editor: the folder of the selected collection has a contrast of {TextContrast(Pixels(editorWindow, folder)):0.00}.");
            }
            finally { editorWindow.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryMainGridShowsAtLeastThreeWholeRowsAtTheMinimumSize(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var viewModel = (MaintenanceWindowsViewModel)page.DataContext;
            while (viewModel.Rows.Count < 6) viewModel.Rows.Add(viewModel.Rows[viewModel.Rows.Count % 3]);
            while (viewModel.Coverage.Count < 6) viewModel.Coverage.Add(viewModel.Coverage[viewModel.Coverage.Count % 4]);
            while (viewModel.Templates.Count < 6) viewModel.Templates.Add(viewModel.Templates[viewModel.Templates.Count % 2]);
            var rows = new MinimumSizeRows();
            var tabs = ById<TabControl>(page, "tabMaintenanceWindows");
            foreach (var (index, id) in new[] { (0, "dgWindows"), (1, "dgCoverage"), (2, "dgTemplates") })
            {
                tabs.SelectedIndex = index;
                window.UpdateLayout();
                StaTestHelper.Flush();
                rows.Add(id, ById<DataGrid>(page, id));
            }
            rows.AssertEnough($"Maintenance Windows {theme}");
        }, MinimumSizeRows.Width - NavigationPaneWidth, MinimumSizeRows.Height - 48);
    }
}
