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
            editor.PreviewKeyDown += (_, e) => OnEditorKey(editor, words, e);
        }
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

    /// <summary>Ctrl+Space opens the starter list at the caret; a choice inserts its text at the caret.</summary>
    private static void OnEditorKey(TextBox editor, IReadOnlyList<string> words, KeyEventArgs e)
    {
        if (e.Key != Key.Space || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
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

    private void OnQueryLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.UseQueryItemCommand.CanExecute(null)) _viewModel.UseQueryItemCommand.Execute(null);
    }

    private void OnScriptLibraryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.UseScriptItemCommand.CanExecute(null)) _viewModel.UseScriptItemCommand.Execute(null);
    }
}
