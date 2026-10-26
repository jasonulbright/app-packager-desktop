using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Views.Pages;

public partial class DeploymentHelperPage : INavigationAware
{
    /// <summary>Below this width the object and collection lists stack instead of standing side by side.</summary>
    public const double SideBySideWidth = 1000;

    private readonly DeploymentHelperViewModel _viewModel;

    public DeploymentHelperPage(DeploymentHelperViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        btnValidate.LostKeyboardFocus += OnValidateLostFocus;
        btnValidate.IsEnabledChanged += OnValidateEnabledChanged;
    }

    private bool _validateLostFocus;
    private IInputElement? _focusWhileValidating;

    // A command button is disabled while its command runs, and a disabled button loses the keyboard
    // focus to the first stop of the page; the focus goes back when the operator did not move it.
    private void OnValidateLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (btnValidate.IsEnabled) return;
        _validateLostFocus = true;
        _focusWhileValidating = e.NewFocus;
    }

    private void OnValidateEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !_validateLostFocus) return;
        _validateLostFocus = false;
        var now = Keyboard.FocusedElement;
        if (now is null || ReferenceEquals(now, _focusWhileValidating)) btnValidate.Focus();
    }

    public Task OnNavigatedToAsync() => _viewModel.OnNavigatedToAsync();

    public Task OnNavigatedFromAsync() => _viewModel.OnNavigatedFromAsync();

    /// <summary>True when the object and collection lists stand side by side.</summary>
    public bool PickersSideBySide => Grid.GetColumn(pnlCollections) == 2;

    private void OnDeploySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var sideBySide = e.NewSize.Width >= SideBySideWidth;
        if (sideBySide == PickersSideBySide) return;
        Grid.SetColumn(pnlObjects, 0);
        Grid.SetColumnSpan(pnlObjects, sideBySide ? 1 : 3);
        Grid.SetRow(pnlCollections, sideBySide ? 0 : 2);
        Grid.SetColumn(pnlCollections, sideBySide ? 2 : 0);
        Grid.SetColumnSpan(pnlCollections, sideBySide ? 1 : 3);
        pnlCollections.Margin = new Thickness(0, sideBySide ? 0 : 12, 0, 0);
    }

    // A right-click does not select a row by itself, so an action would run on the row selected before.
    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, grid);
             element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is not DataGridRow row) continue;
            if (!row.IsSelected) grid.SelectedItem = row.Item;
            row.Focus();
            return;
        }
    }
}
