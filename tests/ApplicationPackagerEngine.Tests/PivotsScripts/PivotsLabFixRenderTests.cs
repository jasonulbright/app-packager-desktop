using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The keyboard, screen reader and plan text checks of the lab run in real windows, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PivotsLabFixRenderTests
{
    private const double NavigationPaneWidth = 230;
    private const double CollapsedPaneWidth = 48;

    private static void WithPage(ApplicationTheme theme, Action<Window, PivotsScriptsPage, VM, PivotsScriptsHarness> test, double width = 1440 - CollapsedPaneWidth, double height = 900 - 48)
    {
        WithApplication(theme, () =>
        {
            var h = new PivotsScriptsHarness();
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

    private static string FocusedId()
    {
        var focused = Keyboard.FocusedElement as FrameworkElement;
        return focused is null ? "none" : AutomationProperties.GetAutomationId(focused) is { Length: > 0 } id ? id : focused.GetType().Name;
    }

    private static void Press(Window window, PivotsScriptsPage page, TextBox editor, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        page.ModifierSource = () => modifiers;
        var args = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        editor.RaiseEvent(args);
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TabTypesATabInTheQueryEditorAndCtrlTabOrEscThenTabLeavesItOnThePivotTab(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var editor = ById<TextBox>(page, "txtQuery");
            Assert.Equal(PivotsScriptsPage.EditorKeyHint, ById<TextBlock>(page, "txtQueryKeyHint").Text);
            Assert.Equal(PivotsScriptsPage.EditorKeyHint, editor.ToolTip);
            Assert.Equal(PivotsScriptsPage.EditorKeyHint, AutomationProperties.GetHelpText(editor));
            Assert.True(editor.AcceptsTab);

            Keyboard.Focus(editor);
            Settle(window);
            Press(window, page, editor, Key.Tab);
            Assert.Equal("txtQuery", FocusedId());

            Press(window, page, editor, Key.Tab, ModifierKeys.Control);
            Release(window, page, editor, Key.LeftCtrl);
            Assert.NotEqual("txtQuery", FocusedId());
            Assert.Equal(VM.PivotTab, viewModel.SelectedTabIndex);

            Keyboard.Focus(editor);
            Settle(window);
            Press(window, page, editor, Key.Escape);
            Press(window, page, editor, Key.Tab);
            Assert.NotEqual("txtQuery", FocusedId());
            Assert.Equal(VM.PivotTab, viewModel.SelectedTabIndex);

            Keyboard.Focus(editor);
            Settle(window);
            Press(window, page, editor, Key.Escape);
            Press(window, page, editor, Key.A);
            Press(window, page, editor, Key.Tab);
            Assert.Equal("txtQuery", FocusedId());
        });
    }

    private static void Release(Window window, PivotsScriptsPage page, TextBox editor, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        page.ModifierSource = () => modifiers;
        var args = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
        editor.RaiseEvent(args);
        Settle(window);
    }

    /// <summary>The element that Tab without a modifier reaches from the editor in the given direction.</summary>
    private static string PlainTabTarget(Window window, TextBox editor, FocusNavigationDirection direction)
    {
        Keyboard.Focus(editor);
        Settle(window);
        editor.MoveFocus(new TraversalRequest(direction));
        Settle(window);
        var id = FocusedId();
        Keyboard.Focus(editor);
        Settle(window);
        return id;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, "txtQuery")]
    [InlineData(ApplicationTheme.Dark, "txtQuery")]
    [InlineData(ApplicationTheme.Light, "txtDraft")]
    public void CtrlTabMovesTheFocusOnlyAfterCtrlIsReleasedAndReachesTheControlThatTabReaches(ApplicationTheme theme, string editorId)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            if (editorId == "txtDraft")
            {
                viewModel.SelectedTabIndex = VM.SiteScriptsTab;
                Run(viewModel.EnsureTabLoadedAsync());
                Settle(window);
            }
            var editor = ById<TextBox>(page, editorId);
            var next = PlainTabTarget(window, editor, FocusNavigationDirection.Next);
            var previous = PlainTabTarget(window, editor, FocusNavigationDirection.Previous);
            Assert.NotEqual(editorId, next);
            Assert.NotEqual(editorId, previous);

            // WPF moves the focus by the Ctrl+Tab rules while Ctrl is down, and those rules leave the page: the move waits for the release.
            Press(window, page, editor, Key.Tab, ModifierKeys.Control);
            Assert.Equal(editorId, FocusedId());
            Assert.DoesNotContain('\t', editor.Text);
            Release(window, page, editor, Key.Tab, ModifierKeys.Control);
            Assert.Equal(editorId, FocusedId());
            Release(window, page, editor, Key.LeftCtrl);
            Assert.Equal(next, FocusedId());
            Assert.True(Keyboard.FocusedElement is DependencyObject focused && page.IsAncestorOf(focused), "Ctrl+Tab leaves the page.");

            Keyboard.Focus(editor);
            Settle(window);
            Press(window, page, editor, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal(editorId, FocusedId());
            Release(window, page, editor, Key.LeftCtrl, ModifierKeys.Shift);
            Assert.Equal(previous, FocusedId());

            Keyboard.Focus(editor);
            Settle(window);
            Press(window, page, editor, Key.Escape);
            Press(window, page, editor, Key.Tab);
            Assert.Equal(next, FocusedId());

            Keyboard.Focus(editor);
            Settle(window);
            Press(window, page, editor, Key.Tab, ModifierKeys.Control);
            var other = editorId == "txtDraft" ? "btnRefreshSiteScripts" : "btnResolve";
            Keyboard.Focus(ById<FrameworkElement>(page, other));
            Settle(window);
            Release(window, page, editor, Key.LeftCtrl);
            Assert.Equal(other, FocusedId());
            Assert.Contains("Ctrl+Tab", PivotsScriptsPage.EditorKeyHint, StringComparison.Ordinal);
            Assert.Contains("Esc and then Tab", PivotsScriptsPage.EditorKeyHint, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheParametersDialogSaysNoDefaultForAParameterWithoutADefault(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var script = new SiteScript("G", "Label Count", "a", "b", SiteScriptStates.Approved, 1, "PowerShell", "H",
                [new ScriptParameter("Label", "System.String", true, null), new ScriptParameter("Count", "System.Int32", false, "3"), new ScriptParameter("Note", "System.String", false, "")], null);
            var window = new PivotsParametersWindow(new PivotsParametersViewModel(script, null));
            Show(window);
            try
            {
                Settle(window);
                var lines = Descendants(window).OfType<TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text).Where(t => t.StartsWith("Label", StringComparison.Ordinal) || t.StartsWith("Count", StringComparison.Ordinal) || t.StartsWith("Note", StringComparison.Ordinal)).ToList();
                Assert.Equal(3, lines.Count);
                Assert.All(lines, l => Assert.DoesNotContain("default: )", l, StringComparison.Ordinal));
                Assert.Equal("Label (System.String, required: Yes, no default)", lines[0]);
                Assert.Equal("Count (System.Int32, required: No, default: 3)", lines[1]);
                Assert.Equal("Note (System.String, required: No, no default)", lines[2]);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void AKeyboardOnlyQueryRunReachesTheEditorRightAfterTheTargetButtonsAndRunQuerySoonAfterIt(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            var resolve = ById<Wpf.Ui.Controls.Button>(page, "btnResolve");
            Keyboard.Focus(resolve);
            Settle(window);
            Assert.Equal("btnResolve", FocusedId());
            viewModel.TargetText = "LAB00012";
            harness.Service.ResolveGate = new TaskCompletionSource();
            var resolving = viewModel.ResolveCommand.ExecuteAsync(null);
            Settle(window);
            Assert.False(resolve.IsEnabled);
            harness.Service.ResolveGate.SetResult();
            Run(resolving);
            Settle(window);
            Assert.NotNull(viewModel.Resolved);
            Assert.Equal("btnResolve", FocusedId());
            var path = new List<string>();
            for (var i = 0; i < 10 && FocusedId() != "txtQuery"; i++)
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Settle(window);
                path.Add(FocusedId());
            }
            Assert.True(FocusedId() == "txtQuery", "Tab from Resolve: " + string.Join(" > ", path));
            Assert.True(path.Count <= 2, $"Tab from Resolve reaches the editor after {path.Count} presses: {string.Join(" > ", path)}");

            var editor = ById<TextBox>(page, "txtQuery");
            Press(window, page, editor, Key.Tab, ModifierKeys.Control);
            Release(window, page, editor, Key.LeftCtrl);
            path.Clear();
            path.Add(FocusedId());
            for (var i = 0; i < 6 && FocusedId() != "btnRunPivot"; i++)
            {
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Settle(window);
                path.Add(FocusedId());
            }
            Assert.True(FocusedId() == "btnRunPivot", "Tab from the editor: " + string.Join(" > ", path));
            Assert.True(path.Count <= 3, $"The editor reaches Run query after {path.Count} presses: {string.Join(" > ", path)}");
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheLibraryItemsCarryTheirNameForAScreenReaderAndNotTheRecordText(ApplicationTheme theme)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            foreach (var (tab, id) in new[] { (VM.PivotTab, "lstQueryLibrary"), (VM.SiteScriptsTab, "lstScriptLibrary") })
            {
                viewModel.SelectedTabIndex = tab;
                Run(viewModel.EnsureTabLoadedAsync());
                Settle(window);
                var list = ById<ListBox>(page, id);
                var items = Descendants(list).OfType<ListBoxItem>().ToList();
                Assert.NotEmpty(items);
                foreach (var item in items)
                {
                    var library = Assert.IsType<PivotsLibraryItem>(item.DataContext);
                    var name = UIElementAutomationPeer.CreatePeerForElement(item).GetName();
                    Assert.Equal(library.Name, name);
                    Assert.DoesNotContain(library.Hash, name, StringComparison.Ordinal);
                }
            }
        });
    }

    private const string LabScript = "param(\r\n    [string]$Label = 'psl'\r\n)\r\n$build = (Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion').CurrentBuild\r\n"
        + "[pscustomobject]@{ Label = $Label; Build = $build; Time = (Get-Date).ToString('s') } | ConvertTo-Json -Compress";

    [Theory]
    [InlineData(ApplicationTheme.Light, 820)]
    [InlineData(ApplicationTheme.Dark, 620)]
    public void ThePlanShowsTheScriptTextWithItsOwnSpacing(ApplicationTheme theme, double width)
    {
        WithApplication(theme, () =>
        {
            var plan = new ChangePlan("Run a script on devices", "Run on 1 device", [new ChangePlanItem("Run script", "CLIENT01 (ResourceID 16777221)", "Online state: Online.")], isDestructive: true, hasUndo: false)
            {
                DestructiveConfirmation = PivotsScriptsText.ScriptRunBox,
                Body = LabScript,
                BodyName = "Script text"
            };
            var dialog = new ChangePlanViewModel(plan, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
            var window = new ChangePlanWindow(dialog) { Width = width };
            Show(window);
            try
            {
                Settle(window);
                var body = (TextBox)window.FindName("txtBody");
                Assert.Equal(LabScript, body.Text);
                var line = LabScript.IndexOf("$build = (", StringComparison.Ordinal);
                body.ScrollToLine(body.GetLineIndexFromCharacterIndex(line));
                Settle(window);
                double X(int i) => body.GetRectFromCharacterIndex(i).Left;
                var cell = X(line + 1) - X(line);
                Assert.True(cell > 0, "The script text is not laid out.");
                foreach (var offset in new[] { 7, 9, 10 })
                {
                    var expected = cell * offset;
                    var actual = X(line + offset) - X(line);
                    Assert.True(Math.Abs(actual - expected) < 1, $"Character {offset} of \"$build = (\" is {actual:0.0} px from its start at {width} px; the text has {expected:0.0} px.");
                }
                Assert.True(body.ExtentWidth <= body.ViewportWidth + 0.5 || body.HorizontalScrollBarVisibility != System.Windows.Controls.ScrollBarVisibility.Disabled,
                    "A long script line is cut without a way to scroll to it.");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - CollapsedPaneWidth, 900 - 48)]
    [InlineData(ApplicationTheme.Dark, 1440 - NavigationPaneWidth, 900 - 48)]
    [InlineData(ApplicationTheme.Light, 900 - NavigationPaneWidth, 600 - 48)]
    [InlineData(ApplicationTheme.Dark, 900 - CollapsedPaneWidth, 600 - 48)]
    public void TheScriptTabShowsBothHashesAndTheParameterDefinitionsInsideItsWidth(ApplicationTheme theme, double width, double height)
    {
        WithPage(theme, (window, page, viewModel, harness) =>
        {
            viewModel.SelectedTabIndex = VM.ScriptTab;
            Run(viewModel.EnsureTabLoadedAsync());
            viewModel.SelectedApproved = viewModel.ApprovedScripts.Single(r => r.Name == "Read key");
            Run(viewModel.ScriptDetailTask);
            Settle(window);
            var scroller = ById<ScrollViewer>(page, "scrollScriptTab");
            Assert.True(scroller.ScrollableWidth < 1, $"The Script tab scrolls sideways by {scroller.ScrollableWidth:0} px at {width}x{height}.");
            foreach (var id in new[] { "txtScriptDetail", "txtParametersText", "btnParameters", "btnRunScript" })
            {
                var element = ById<FrameworkElement>(page, id);
                element.BringIntoView();
                Settle(window);
                var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(element.RenderSize));
                Assert.True(bounds.Left >= -0.5 && bounds.Right <= scroller.ViewportWidth + 0.5, $"{id} spans {bounds.Left:0} to {bounds.Right:0} px in a {scroller.ViewportWidth:0} px tab at {width}x{height}.");
            }
            var detail = ById<TextBlock>(page, "txtScriptDetail").Text;
            Assert.Contains($"{PivotsScriptsText.TextHashLabel} {harness.ParamScript.Hash}", detail);
            Assert.Contains($"{PivotsScriptsText.SiteHashLabel} {harness.ParamScript.SiteHash}", detail);
            Assert.Contains("Depth = 1 (default of the site)", ById<TextBlock>(page, "txtParametersText").Text);
            Assert.True(ById<FrameworkElement>(page, "btnParameters").IsEnabled);
        }, width, height);
    }
}
