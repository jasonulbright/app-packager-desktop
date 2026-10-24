using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerUI.Views.Pages;

public partial class PivotsScriptsPage : INavigationAware
{
    private readonly PivotsScriptsViewModel _viewModel;

    public PivotsScriptsPage(PivotsScriptsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        foreach (var (editor, words) in new[] { (txtQuery, PivotsScriptsViewModel.PivotWords), (txtDraft, PivotsScriptsViewModel.ScriptWords) })
        {
            editor.SelectionChanged += (_, _) => ShowCaret(editor);
            editor.GotKeyboardFocus += (_, _) => ShowCaret(editor);
            editor.LostKeyboardFocus += (_, _) =>
            {
                _leaveArmed = null;
                _pendingExit = null;
            };
            editor.PreviewKeyDown += (_, e) => OnEditorKey(editor, words, e);
            editor.PreviewKeyUp += (_, _) => OnEditorKeyUp(editor);
        }
    }

    public const string EditorKeyHint = "Ctrl+Space shows the starter list. Tab types a tab. To leave the editor, press Ctrl+Tab, or press Esc and then Tab.";

    /// <summary>The modifier keys held during a key press; replaceable so a test can press Ctrl without a keyboard.</summary>
    public Func<ModifierKeys> ModifierSource { get; set; } = () => Keyboard.Modifiers;

    /// <summary>The editor in which Esc was the last key: its next Tab or Shift+Tab leaves it.</summary>
    private TextBox? _leaveArmed;

    /// <summary>
    /// The editor and the direction of a Ctrl+Tab whose focus move waits for the release of Ctrl.
    /// While Ctrl is down, MoveFocus follows the Ctrl+Tab navigation rules, and these move the focus
    /// out of the page to the main window.
    /// </summary>
    private (TextBox Editor, FocusNavigationDirection Direction)? _pendingExit;

    /// <summary>
    /// The focus move for a key press in an editor, or null when the editor keeps the key. Tab types a
    /// tab; Ctrl+Tab, or Tab right after Esc, leaves the editor. Ctrl+Tab otherwise reaches the
    /// TabControl and switches to the next tab.
    /// </summary>
    public static FocusNavigationDirection? EditorExit(Key key, ModifierKeys modifiers, bool afterEscape)
    {
        if (key != Key.Tab || ((modifiers & ModifierKeys.Control) == 0 && !afterEscape)) return null;
        return (modifiers & ModifierKeys.Shift) != 0 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
    }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    private void ShowCaret(TextBox editor)
    {
        var index = editor.CaretIndex;
        var line = editor.GetLineIndexFromCharacterIndex(index);
        var start = line < 0 ? 0 : editor.GetCharacterIndexFromLineIndex(line);
        _viewModel.SetCaret(Math.Max(line, 0) + 1, index - Math.Max(start, 0) + 1);
    }

    /// <summary>Ctrl+Space opens the starter list at the caret; a choice inserts its text at the caret. Ctrl+Tab, or Esc and then Tab, leaves the editor.</summary>
    private void OnEditorKey(TextBox editor, IReadOnlyList<string> words, KeyEventArgs e)
    {
        var modifiers = ModifierSource();
        var afterEscape = ReferenceEquals(_leaveArmed, editor);
        if (e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl))
        {
            _leaveArmed = null;
            if (e.Key != Key.Tab) _pendingExit = null;
        }
        if (e.Key == Key.Escape && modifiers == ModifierKeys.None)
        {
            _leaveArmed = editor;
            return;
        }
        if (EditorExit(e.Key, modifiers, afterEscape) is { } direction)
        {
            e.Handled = true;
            if ((modifiers & ModifierKeys.Control) != 0) _pendingExit = (editor, direction);
            else editor.MoveFocus(new TraversalRequest(direction));
            return;
        }
        if (e.Key != Key.Space || (modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = editor, Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint };
        var caret = editor.GetRectFromCharacterIndex(editor.CaretIndex);
        if (!caret.IsEmpty)
        {
            menu.HorizontalOffset = caret.Left;
            menu.VerticalOffset = caret.Bottom;
        }
        foreach (var word in words)
        {
            var item = new MenuItem { Header = word.Replace("_", "__", StringComparison.Ordinal) };
            item.Click += (_, _) =>
            {
                var at = editor.SelectionStart;
                editor.SelectedText = word;
                editor.Select(at + word.Length, 0);
                editor.Focus();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first) first.Focus();
    }

    /// <summary>The release of Ctrl after Ctrl+Tab moves the focus by the Tab rules of the page.</summary>
    private void OnEditorKeyUp(TextBox editor)
    {
        if (_pendingExit is not { } pending || !ReferenceEquals(pending.Editor, editor) || (ModifierSource() & ModifierKeys.Control) != 0) return;
        _pendingExit = null;
        editor.MoveFocus(new TraversalRequest(pending.Direction));
    }

    private void OnQueryLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.UseQueryItemCommand.CanExecute(null)) _viewModel.UseQueryItemCommand.Execute(null);
    }

    private void OnScriptLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.UseScriptItemCommand.CanExecute(null)) _viewModel.UseScriptItemCommand.Execute(null);
    }
}
