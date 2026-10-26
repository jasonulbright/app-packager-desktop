using System.Windows.Input;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace ApplicationPackagerUI.Views.Pages;

public partial class DetectionTesterPage : INavigationAware
{
    private readonly DetectionTesterViewModel _viewModel;

    public DetectionTesterPage(DetectionTesterViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The modifier keys held during a key press; replaceable so a test can press Ctrl without a keyboard.</summary>
    public Func<ModifierKeys> ModifierSource { get; set; } = () => Keyboard.Modifiers;

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    /// <summary>
    /// The focus move for a key press in the script editor, or null when the editor keeps the key.
    /// Ctrl+Tab otherwise reaches the TabControl and switches to the other tab.
    /// </summary>
    public static FocusNavigationDirection? EditorExit(Key key, ModifierKeys modifiers)
    {
        if (key != Key.Tab || (modifiers & ModifierKeys.Control) == 0) return null;
        return (modifiers & ModifierKeys.Shift) != 0 ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
    }

    private void OnScriptEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorExit(e.Key, ModifierSource()) is not { } direction) return;
        e.Handled = true;
        ((System.Windows.UIElement)sender).MoveFocus(new TraversalRequest(direction));
    }
}
