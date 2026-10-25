using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using DataGrid = System.Windows.Controls.DataGrid;
using TabControl = System.Windows.Controls.TabControl;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The compiled Detection Tester page in a real window, over fake services.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionTesterPageTests
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
    /// as App.xaml merges them, and removes the application object afterwards.
    /// </summary>
    private static void WithPage(ApplicationTheme theme, Action<Window, DetectionTesterPage, DetectionTesterHarness> test,
        double width = 1200, double height = 900, Action<DetectionTesterHarness>? setup = null)
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
                var harness = new DetectionTesterHarness();
                harness.Applications.Applications.Add(new InstalledApplication("7-Zip", "Igor Pavlov", "24.09", "20261001", "HKLM 64-bit",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip", "u.exe", "u.exe /S", @"C:\Program Files\7-Zip"));
                harness.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "SOFTWARE" }));
                setup?.Invoke(harness);
                var viewModel = harness.Create();
                var page = new DetectionTesterPage(viewModel);
                var window = new Window
                {
                    Width = width, Height = height, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
                    WindowStyle = WindowStyle.None, Content = page
                };
                window.Show();
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                    var opened = page.OnNavigatedToAsync();
                    StaTestHelper.PumpUntil(() => opened.IsCompleted);
                    window.UpdateLayout();
                    StaTestHelper.Flush();
                    test(window, page, harness);
                    Assert.Empty(errors.Lines);
                }
                finally { window.Close(); }
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

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void BothTabsRenderInTheTheme(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var tabs = ById<TabControl>(page, "tabDetectionTester");
            Assert.Equal(["Detection", "Installed Applications"], tabs.Items.OfType<TabItem>().Select(t => (string)t.Header));

            Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.Text == "Detection Tester");
            var types = ById<System.Windows.Controls.ComboBox>(page, "cboClauseType");
            Assert.Equal(4, types.Items.Count);
            Assert.True(ById<FrameworkElement>(page, "pnlRegistryValue").IsVisible);
            var history = ById<DataGrid>(page, "dgHistory");
            Assert.Equal(["Time", "Kind", "Description", "Negative", "Result"], history.Columns.Select(c => (string)c.Header));
            Assert.Single(history.Items);
            Assert.True(ById<System.Windows.Controls.Button>(page, "btnRun").IsEnabled);

            var viewModel = (ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel)page.DataContext;
            viewModel.SelectedClauseType = "Script";
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "pnlScript").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "pnlRegistryValue").IsVisible);
            Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.IsVisible && t.Text == "This script runs on this computer as the current user.");
            Assert.True(ById<System.Windows.Controls.TextBox>(page, "txtScript").FontSize >= 14);

            tabs.SelectedIndex = 1;
            StaTestHelper.PumpUntil(() => Descendants(page).OfType<DataGrid>()
                .SingleOrDefault(g => AutomationProperties.GetAutomationId(g) == "dgApplications")?.Items.Count == 1);
            var grid = ById<DataGrid>(page, "dgApplications");
            Assert.Equal(["Name", "Publisher", "Version", "Install date", "Source"], grid.Columns.Select(c => (string)c.Header));
            Assert.True(ById<System.Windows.Controls.TextBox>(page, "txtApplicationFilter").IsVisible);
            Assert.False(ById<FrameworkElement>(page, "pnlApplicationDetails").IsVisible);
            viewModel.SelectedApplication = viewModel.Applications[0];
            StaTestHelper.Flush();
            Assert.True(ById<FrameworkElement>(page, "pnlApplicationDetails").IsVisible);
            Assert.Contains(Descendants(ById<FrameworkElement>(page, "pnlApplicationDetails")).OfType<System.Windows.Controls.TextBox>(),
                t => t.Text == @"C:\Program Files\7-Zip");
            Assert.Equal(1, harness.Applications.Calls);
        });
    }

    [Fact]
    public void AResultShowsItsStateInTheResultPanel()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var viewModel = (ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel)page.DataContext;
            viewModel.SelectedClauseType = "RegistryKey";
            viewModel.KeyPath = "SOFTWARE";
            ById<System.Windows.Controls.Button>(page, "btnRun").Command.Execute(null);
            StaTestHelper.PumpUntil(() => viewModel.ResultState == ApplicationPackagerUI.ViewModels.Pages.DetectionTesterResultState.Passed);
            Assert.Equal("Passed", ById<TextBlock>(page, "txtResultHeadline").Text);
            Assert.Equal(2, ById<DataGrid>(page, "dgHistory").Items.Count);
            Assert.Same(Application.Current.FindResource("SystemFillColorSuccessBrush"), ById<TextBlock>(page, "txtResultHeadline").Foreground);
        });
    }

    private static DetectionTesterViewModel ViewModelOf(DetectionTesterPage page) => (DetectionTesterViewModel)page.DataContext;

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheTitleUsesTheThemeTextColour(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var title = Descendants(page).OfType<TextBlock>().Single(t => t.Text == "Detection Tester");
            var color = Assert.IsType<SolidColorBrush>(title.Foreground).Color;
            Assert.Same(Application.Current.FindResource("TextFillColorPrimaryBrush"), title.Foreground);
            if (theme == ApplicationTheme.Dark) Assert.True(color.R > 200 && color.G > 200 && color.B > 200, $"Dark theme text is {color}.");
            else Assert.True(color.R < 80 && color.G < 80 && color.B < 80, $"Light theme text is {color}.");
        });
    }

    [Fact]
    public void TheCompoundTreeRendersEveryClauseThroughItsTemplate()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var viewModel = ViewModelOf(page);
            var import = viewModel.ImportManifestCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => import.IsCompleted);
            Assert.True(ById<FrameworkElement>(page, "pnlCompound").IsVisible);

            var tree = ById<System.Windows.Controls.TreeView>(page, "treeCompound");
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var item in Descendants(tree).OfType<System.Windows.Controls.TreeViewItem>().ToList()) item.IsExpanded = true;
                window.UpdateLayout();
                StaTestHelper.Flush();
            }
            var texts = Descendants(tree).OfType<System.Windows.Controls.TreeViewItem>()
                .Select(i => Descendants(i).OfType<TextBlock>().FirstOrDefault(t => t.IsVisible && t.Text == ((DetectionClauseNode)i.DataContext).Text)?.Text ?? "<not rendered>").ToList();
            Assert.Equal(6, texts.Count);
            Assert.Equal("Any of (Or)", texts[0]);
            Assert.Equal(2, texts.Count(t => t == "All of (And)"));
            Assert.Contains(texts, t => t.StartsWith(@"%ProgramFiles%\7-Zip\7z.exe", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.StartsWith(@"HKCU\SOFTWARE\7-Zip", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, t => t.Contains(nameof(DetectionClauseNode), StringComparison.Ordinal));
        }, setup: h => h.Prompts.ManifestPath = DetectionTesterHarness.FixturePath);
    }

    [Fact]
    public void AScriptResultShowsTheOutputPanelAndTheSystemNote()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var viewModel = ViewModelOf(page);
            viewModel.SelectedClauseType = "Script";
            viewModel.ScriptText = "'installed'";
            var run = viewModel.RunCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => run.IsCompleted);
            window.UpdateLayout();

            var stdout = ById<System.Windows.Controls.TextBox>(page, "txtStdout");
            Assert.True(stdout.IsVisible);
            Assert.Equal("installed", stdout.Text);
            Assert.Equal("warning text", ById<System.Windows.Controls.TextBox>(page, "txtStderr").Text);
            Assert.Contains(Descendants(page).OfType<TextBlock>(), t => t.IsVisible && t.Text == DetectionTesterViewModel.ScriptSystemNote);
            var exit = Descendants(page).OfType<TextBlock>().Single(t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "Exit code:"));
            Assert.Equal("3", exit.Inlines.OfType<System.Windows.Documents.Run>().Last().Text);
        }, setup: h => h.Tests.Answer = _ => FakeDetectionTestService.Result(true, true, "Detected.", "installed", "warning text", 3));
    }

    [Fact]
    public void InvalidAndCancelledUseTheCautionColour()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var viewModel = ViewModelOf(page);
            var headline = ById<TextBlock>(page, "txtResultHeadline");
            var caution = Application.Current.FindResource("SystemFillColorCautionBrush");

            viewModel.SelectedClauseType = "RegistryKey";
            viewModel.KeyPath = "";
            var invalid = viewModel.RunCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => invalid.IsCompleted);
            Assert.Equal("Invalid clause", headline.Text);
            Assert.True(headline.IsVisible);
            Assert.Same(caution, headline.Foreground);

            harness.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.KeyPath = "SOFTWARE";
            var run = viewModel.RunCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => viewModel.IsRunning);
            Assert.True(ById<FrameworkElement>(page, "btnCancel").IsVisible);
            ById<System.Windows.Controls.Button>(page, "btnCancel").Command.Execute(null);
            StaTestHelper.PumpUntil(() => run.IsCompleted);
            Assert.Equal("Cancelled", headline.Text);
            Assert.Same(caution, headline.Foreground);
            Assert.False(ById<FrameworkElement>(page, "btnCancel").IsVisible);
        });
    }

    [Fact]
    public void ALongHistoryRendersAndTheDescriptionCellCarriesTheFullText()
    {
        var longText = "HKLM\\SOFTWARE\\" + new string('k', 400) + " equals 1.0 (64-bit view)";
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var grid = ById<DataGrid>(page, "dgHistory");
            Assert.Equal(DetectionTesterViewModel.HistoryLimit, grid.Items.Count);
            grid.ScrollIntoView(grid.Items[0]);
            window.UpdateLayout();
            StaTestHelper.Flush();
            var cell = Descendants(grid).OfType<TextBlock>().First(t => t.Text == longText);
            Assert.Equal(longText, cell.ToolTip);
            Assert.Equal(TextTrimming.CharacterEllipsis, cell.TextTrimming);
            Assert.True(cell.ActualWidth < grid.ActualWidth);
        }, setup: h =>
        {
            h.History.Entries.Clear();
            for (var i = 0; i < 600; i++)
                h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now.AddMinutes(-i), new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" },
                    description: i == 0 ? longText : $"entry {i}"));
        });
    }

    [Fact]
    public void ANarrowWindowKeepsTheControlsOnScreen()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            foreach (var id in new[] { "btnRun", "btnImportManifest", "dgHistory", "txtHistoryNote", "txtKeyPath", "btnClearHistory" })
            {
                var element = ById<FrameworkElement>(page, id);
                Assert.True(element.IsVisible, $"{id} is not visible.");
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{id} has no size.");
            }
            ById<TabControl>(page, "tabDetectionTester").SelectedIndex = 1;
            StaTestHelper.PumpUntil(() => ViewModelOf(page).Applications.Count == 1);
            window.UpdateLayout();
            foreach (var id in new[] { "chkShowSystemComponents", "txtApplicationFilter", "dgApplications", "btnRefreshApplications" })
            {
                var element = ById<FrameworkElement>(page, id);
                Assert.True(element.IsVisible, $"{id} is not visible.");
                Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{id} has no size.");
            }
        }, width: 760, height: 640);
    }

    [Fact]
    public void TheNotesShowAndTheEditorCarriesTheKeyHint()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            Assert.Equal(DetectionTesterViewModel.HistoryNote, ById<TextBlock>(page, "txtHistoryNote").Text);
            Assert.True(ById<TextBlock>(page, "txtHistoryNote").IsVisible);
            ViewModelOf(page).SelectedClauseType = "Script";
            StaTestHelper.Flush();
            Assert.True(ById<TextBlock>(page, "txtEditorKeyHint").IsVisible);
            Assert.Equal("Press Ctrl+Tab to leave the editor.", ById<TextBlock>(page, "txtEditorKeyHint").Text);
            var editor = ById<System.Windows.Controls.TextBox>(page, "txtScript");
            Assert.Equal("Press Ctrl+Tab to leave the editor.", editor.ToolTip);
            Assert.True(editor.AcceptsTab);
        });
    }

    [Theory]
    [InlineData(System.Windows.Input.ModifierKeys.Control, true)]
    [InlineData(System.Windows.Input.ModifierKeys.None, false)]
    public void CtrlTabInTheEditorIsTakenByTheEditorAndKeepsTheTab(System.Windows.Input.ModifierKeys modifiers, bool handled)
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            ViewModelOf(page).SelectedClauseType = "Script";
            StaTestHelper.Flush();
            page.ModifierSource = () => modifiers;
            var editor = ById<System.Windows.Controls.TextBox>(page, "txtScript");
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, System.Windows.Input.Key.Tab)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent
            };
            editor.RaiseEvent(args);
            Assert.Equal(handled, args.Handled);
            Assert.Equal(0, ById<TabControl>(page, "tabDetectionTester").SelectedIndex);
        });
    }

    [Theory]
    [InlineData(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Control, System.Windows.Input.FocusNavigationDirection.Next)]
    [InlineData(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, System.Windows.Input.FocusNavigationDirection.Previous)]
    [InlineData(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.None, null)]
    [InlineData(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.Shift, null)]
    [InlineData(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control, null)]
    public void EditorExitMovesFocusOnlyForCtrlTab(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, System.Windows.Input.FocusNavigationDirection? expected) =>
        Assert.Equal(expected, DetectionTesterPage.EditorExit(key, modifiers));

    [Fact]
    public void AKeyPathPrefixChangesTheHiveSelectorAndTheStatusLineShowsIt()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var viewModel = ViewModelOf(page);
            viewModel.SelectedClauseType = "RegistryKey";
            var hive = ById<System.Windows.Controls.ComboBox>(page, "cboHive");
            hive.SelectedItem = "HKLM";
            ById<System.Windows.Controls.TextBox>(page, "txtKeyPath").Text = @"HKCU\SOFTWARE\Contoso";
            var run = viewModel.RunCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => run.IsCompleted);
            window.UpdateLayout();
            Assert.Equal("HKCU", hive.SelectedItem);
            Assert.Equal(@"SOFTWARE\Contoso", ById<System.Windows.Controls.TextBox>(page, "txtKeyPath").Text);
            var status = ById<TextBlock>(page, "txtDetectionStatus");
            Assert.Equal("The hive was set to HKCU from the key path.", status.Text);
            Assert.True(status.IsVisible && status.ActualHeight > 0);
        });
    }

    private static DataGridColumn Column(DataGrid grid, string header) => grid.Columns.Single(c => (string)c.Header == header);

    private static (DataGrid Grid, FrameworkElement Details) OpenInstalledApplications(Window window, DetectionTesterPage page)
    {
        ById<TabControl>(page, "tabDetectionTester").SelectedIndex = 1;
        StaTestHelper.PumpUntil(() => ViewModelOf(page).Applications.Count == 1);
        ViewModelOf(page).SelectedApplication = ViewModelOf(page).Applications[0];
        for (var pass = 0; pass < 3; pass++)
        {
            window.UpdateLayout();
            StaTestHelper.Flush();
        }
        return (ById<DataGrid>(page, "dgApplications"), ById<FrameworkElement>(page, "pnlApplicationSide"));
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(804, 700)]
    public void AtA1024PixelScreenTheNameAndPublisherColumnsStayReadable(double width, double height)
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var (grid, details) = OpenInstalledApplications(window, page);
            Assert.True(Column(grid, "Name").ActualWidth >= 120, $"Name is {Column(grid, "Name").ActualWidth} px.");
            Assert.True(Column(grid, "Publisher").ActualWidth >= 120, $"Publisher is {Column(grid, "Publisher").ActualWidth} px.");
            foreach (var header in new[] { "Version", "Install date", "Source" })
                Assert.True(Column(grid, header).Width.IsAbsolute, $"{header} is not a fixed width.");
            Assert.Equal(System.Windows.Controls.ScrollBarVisibility.Auto, grid.HorizontalScrollBarVisibility);
            Assert.True(details.IsVisible && details.ActualWidth >= 300 && details.ActualHeight >= 120,
                $"The detail panel is {details.ActualWidth} x {details.ActualHeight} px.");
            Assert.True(ById<FrameworkElement>(page, "btnUseForDetection").IsVisible);
        }, width: width, height: height);
    }

    [Fact]
    public void AtANarrowWidthTheDetailPanelMovesUnderTheGrid()
    {
        WithPage(ApplicationTheme.Light, (window, page, harness) =>
        {
            var (grid, details) = OpenInstalledApplications(window, page);
            Assert.Equal(2, Grid.GetRow(details));
            Assert.Equal(0, Grid.GetColumn(details));
            var gridBottom = grid.TranslatePoint(new System.Windows.Point(0, grid.ActualHeight), page).Y;
            Assert.True(details.TranslatePoint(new System.Windows.Point(0, 0), page).Y >= gridBottom);
            Assert.True(grid.ActualHeight >= 120, $"The grid is {grid.ActualHeight} px high.");
        }, width: 804, height: 700);
    }

    [Fact]
    public void AtA1440PixelScreenTheDetailPanelStaysBesideTheGrid()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            var (grid, details) = OpenInstalledApplications(window, page);
            Assert.Equal(1, Grid.GetRow(details));
            Assert.Equal(2, Grid.GetColumn(details));
            Assert.Equal(340, details.ActualWidth, 1);
            var gridRight = grid.TranslatePoint(new System.Windows.Point(grid.ActualWidth, 0), page).X;
            Assert.True(details.TranslatePoint(new System.Windows.Point(0, 0), page).X > gridRight);
            Assert.Equal(grid.TranslatePoint(new System.Windows.Point(0, 0), page).Y, details.TranslatePoint(new System.Windows.Point(0, 0), page).Y, 1);
            Assert.True(Column(grid, "Name").ActualWidth > Column(grid, "Publisher").ActualWidth);
            Assert.True(Column(grid, "Publisher").ActualWidth >= 120);
        }, width: 1440, height: 900);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSelectedTabHeaderUsesDifferentBrushesFromTheOtherHeader(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, harness) =>
        {
            var tabs = ById<TabControl>(page, "tabDetectionTester");
            void Check(int selected)
            {
                tabs.SelectedIndex = selected;
                window.UpdateLayout();
                StaTestHelper.Flush();
                var items = tabs.Items.OfType<TabItem>().ToList();
                var on = Header(items[selected]);
                var off = Header(items[1 - selected]);
                Assert.Same(Application.Current.FindResource("TextFillColorPrimaryBrush"), on.Text.Foreground);
                Assert.Same(Application.Current.FindResource("TextFillColorSecondaryBrush"), off.Text.Foreground);
                Assert.Same(Application.Current.FindResource("AccentFillColorDefaultBrush"), on.Underline.Background);
                Assert.NotEqual(((SolidColorBrush)on.Text.Foreground).Color, ((SolidColorBrush)off.Text.Foreground).Color);
                var accent = Assert.IsType<SolidColorBrush>(on.Underline.Background).Color;
                Assert.True(accent.A > 0, "The selected underline is transparent.");
                Assert.True(off.Underline.Background is null || ((SolidColorBrush)off.Underline.Background).Color.A == 0,
                    "The unselected underline is drawn.");
                Assert.True(on.Underline.IsVisible && on.Underline.ActualHeight >= 2);
            }
            Check(0);
            Check(1);
        });
    }

    private static (TextBlock Text, Border Underline) Header(TabItem item)
    {
        var parts = Descendants(item).ToList();
        return (parts.OfType<TextBlock>().Single(t => t.Name == "TabHeaderText"), parts.OfType<Border>().Single(b => b.Name == "TabHeaderUnderline"));
    }

    [Fact]
    public void TheSystemComponentsCheckBoxReloadsTheList()
    {
        WithPage(ApplicationTheme.Dark, (window, page, harness) =>
        {
            ById<TabControl>(page, "tabDetectionTester").SelectedIndex = 1;
            StaTestHelper.PumpUntil(() => ViewModelOf(page).Applications.Count == 1);
            var check = ById<System.Windows.Controls.CheckBox>(page, "chkShowSystemComponents");
            Assert.False(check.IsChecked);
            check.IsChecked = true;
            StaTestHelper.PumpUntil(() => harness.Applications.Calls == 2 && !ViewModelOf(page).IsLoadingApplications);
            Assert.Equal([false, true], harness.Applications.Includes);
            Assert.True(ViewModelOf(page).ShowSystemComponents);
        });
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionTesterSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-dt-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public DetectionTesterSettingsTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, json);
    }

    [Fact]
    public void ASettingsFileFromBeforeThePageLoadsWithoutPageChoices()
    {
        WriteSettings("{ \"FirstRunCompleted\": true, \"SiteCode\": \"LAB\", \"SiteCodeSaved\": true }");
        var settings = new SettingsService().Load();
        Assert.Null(settings.DetectionTester);
        Assert.Equal("LAB", settings.SiteCode);
        Assert.Null(DetectionTesterPreferences.Load(new SettingsService()));
    }

    [Fact]
    public void ThePageChoicesSaveIntoTheSettingsFileAndKeepTheOtherValues()
    {
        WriteSettings("{ \"FirstRunCompleted\": true, \"SiteCode\": \"LAB\", \"SiteCodeSaved\": true, \"Theme\": \"Light\" }");
        var service = new SettingsService();
        DetectionTesterPreferences.Save(service, new DetectionTesterSettings { ClauseType = "Script", Hive = "HKCU", View = "32-bit" });
        var loaded = new SettingsService().Load();
        Assert.Equal("Script", loaded.DetectionTester!.ClauseType);
        Assert.Equal("HKCU", loaded.DetectionTester.Hive);
        Assert.Equal("32-bit", loaded.DetectionTester.View);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal("LAB", loaded.SiteCode);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsNotOverwritten()
    {
        WriteSettings("{ this is not json");
        DetectionTesterPreferences.Save(new SettingsService(), new DetectionTesterSettings { ClauseType = "Script" });
        Assert.Equal("{ this is not json", File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public void TheApplicationServicesBuildThePageViewModel()
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddDetectionTesterPage();
        using var provider = services.BuildServiceProvider();
        var viewModel = provider.GetRequiredService<ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel>();
        Assert.Same(viewModel, provider.GetRequiredService<ApplicationPackagerUI.ViewModels.Pages.DetectionTesterViewModel>());
        Assert.IsType<DetectionTesterPrompts>(provider.GetRequiredService<IDetectionTesterPrompts>());
    }
}
