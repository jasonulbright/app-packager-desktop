using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Views.Windows;

public partial class RingCollectionPickerWindow
{
    private readonly RingCollectionPickerViewModel _viewModel;

    public RingCollectionPickerWindow(RingCollectionPickerViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.ShownOrder = () => dgPickerCollections.Items.OfType<RingCollectionChoice>().ToList();
        viewModel.CloseRequested += (_, _) => Close();
        Loaded += (_, _) => txtFilter.Focus();
    }

    /// <summary>
    /// True when a double-click on <paramref name="source"/> picks: only in the single picker, and only on a
    /// row. A double-click on a check box, a header, the scroll bar or the empty grid area does nothing.
    /// </summary>
    public static bool PicksOnDoubleClick(bool multiSelect, DependencyObject? source)
    {
        if (multiSelect) return false;
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            switch (node)
            {
                case CheckBox or DataGridColumnHeader or ScrollBar:
                    return false;
                case DataGridRow:
                    return true;
                case System.Windows.Controls.DataGrid:
                    return false;
            }
        }
        return false;
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!PicksOnDoubleClick(_viewModel.MultiSelect, e.OriginalSource as DependencyObject)) return;
        if (_viewModel.PickCommand.CanExecute(null)) _viewModel.PickCommand.Execute(null);
    }
}
