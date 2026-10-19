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

    /// <summary>
    /// Below this width of the Installed Applications area, the detail panel moves under the grid.
    /// Beside the grid, the panel and its gap take 356 px and the five columns need 630 px at their minimum widths.
    /// </summary>
    public const double DetailsBesideMinWidth = 1000;

    public static bool DetailsBelow(double layoutWidth) => layoutWidth < DetailsBesideMinWidth;

    private void OnApplicationsLayoutSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var below = DetailsBelow(e.NewSize.Width);
        if (below == (System.Windows.Controls.Grid.GetRow(pnlApplicationSide) == 2)) return;
        var layout = gridApplicationsLayout;
        layout.ColumnDefinitions[1].Width = new System.Windows.GridLength(below ? 0 : 16);
        layout.ColumnDefinitions[2].Width = new System.Windows.GridLength(below ? 0 : 340);
        layout.RowDefinitions[2].Height = below ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) : System.Windows.GridLength.Auto;
        System.Windows.Controls.Grid.SetRow(pnlApplicationSide, below ? 2 : 1);
        System.Windows.Controls.Grid.SetColumn(pnlApplicationSide, below ? 0 : 2);
        System.Windows.Controls.Grid.SetColumnSpan(pnlApplicationSide, below ? 3 : 1);
        pnlApplicationSide.Margin = new System.Windows.Thickness(0, below ? 12 : 0, 0, 0);
    }

    private void OnScriptEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorExit(e.Key, ModifierSource()) is not { } direction) return;
        e.Handled = true;
        ((System.Windows.UIElement)sender).MoveFocus(new TraversalRequest(direction));
    }
}
