using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Views.Pages;

public partial class DeploymentHelperPage : INavigationAware
{
    /// <summary>Below this width the object and collection lists stack instead of standing side by side.</summary>
    public const double SideBySideWidth = 1000;

    /// <summary>
    /// Below this width the schedule card and the content card stack. Each half must hold the columns of the distribution point group
    /// list without a sideways scroll.
    /// </summary>
    public const double CardsSideBySideWidth = 1480;

    private readonly DeploymentHelperViewModel _viewModel;

    public DeploymentHelperPage(DeploymentHelperViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        btnValidate.LostKeyboardFocus += OnValidateLostFocus;
        btnValidate.IsEnabledChanged += OnValidateEnabledChanged;
        // A software update group has no content card; the schedule card then takes the whole width.
        pnlDistribution.IsVisibleChanged += (_, _) => ArrangeCards(pnlDeploy.ActualWidth);
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

    /// <summary>True when the schedule card and the content card stand side by side.</summary>
    public bool CardsSideBySide => Grid.GetColumn(pnlDistribution) == 2;

    private void OnDeploySizeChanged(object sender, SizeChangedEventArgs e)
    {
        Place(pnlObjects, pnlCollections, e.NewSize.Width >= SideBySideWidth);
        ArrangeCards(e.NewSize.Width);
    }

    private void ArrangeCards(double width) =>
        Place(pnlSettings, pnlDistribution, width >= CardsSideBySideWidth && pnlDistribution.Visibility == Visibility.Visible);

    // The grid of a pair has the columns *, 16, * and three rows: side by side uses the first row, stacked uses the first and the third.
    private static void Place(FrameworkElement first, FrameworkElement second, bool sideBySide)
    {
        Grid.SetColumn(first, 0);
        Grid.SetColumnSpan(first, sideBySide ? 1 : 3);
        Grid.SetRow(second, sideBySide ? 0 : 2);
        Grid.SetColumn(second, sideBySide ? 2 : 0);
        Grid.SetColumnSpan(second, sideBySide ? 1 : 3);
        second.Margin = new Thickness(0, sideBySide ? 0 : 12, 0, 0);
    }

    // The checks sit at the end of the tab; the button brings them into view and puts the keyboard on the list when it has rows.
    private void OnViewChecksClick(object sender, RoutedEventArgs e)
    {
        pnlValidation.BringIntoView();
        pnlValidation.UpdateLayout();
        FirstCell(dgChecks)?.Focus();
    }

    private static DataGridCell? FirstCell(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is DataGridCell cell) return cell;
            if (FirstCell(child) is { } found) return found;
        }
        return null;
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

/// <summary>
/// The "Deployment to review" line of the Deploy tab: the object, the collection, the purpose and the member count of the form. While a
/// choice is missing or refused, the line says which one. The values are the object, the program, the collection, Required and the
/// values of the last prepare. Without a prepare, the names come from the lists and the line says that the count is from the last read.
/// With a prepare, the count is the count of the last check. Out-of-date values show no count; the line asks for Validate.
/// </summary>
public sealed class DeploymentReviewSummaryConverter : IMultiValueConverter
{
    public const string LastRead = " at the last read";
    public const string LastCheck = " at the last check";
    public const string OutOfDate = "changed, select Validate again";

    public static string Summary(DeploymentObjectRow? obj, string? program, DeploymentCollectionRow? collection, bool required, DeploymentReadValues? read = null)
    {
        if (obj is null) return DeploymentFormViewModel.PickObject;
        var type = obj.Object.Type;
        if (type == DeploymentObjectType.Package && string.IsNullOrEmpty(program)) return DeploymentFormViewModel.PickProgram;
        if (collection is null) return DeploymentFormViewModel.PickCollection;
        if (collection.IsBuiltIn) return DeploymentFormViewModel.BuiltInRefused;

        // Many application names already hold the version; a software update group has a count of updates in place of a version.
        var current = read?.Object ?? obj.Object;
        var name = current.Name;
        var version = type == DeploymentObjectType.SoftwareUpdateGroup ? "" : current.Version;
        if (version.Length > 0 && !name.Contains(version, StringComparison.OrdinalIgnoreCase)) name += " " + version;
        if (type == DeploymentObjectType.Package) name += $" (program {program})";
        var purpose = required ? DeploymentText.Required : DeploymentText.Available;
        var members = read is { OutOfDate: true }
            ? OutOfDate
            : DeploymentText.Count(read?.Collection.MemberCount ?? collection.MemberCount, "member") + (read is null ? LastRead : LastCheck);
        return $"{name} \u2192 {read?.Collection.Name ?? collection.Name} \u00b7 {purpose} \u00b7 {members}";
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        Summary(values[0] as DeploymentObjectRow, values[1] as string, values[2] as DeploymentCollectionRow, values[3] is true, values[4] as DeploymentReadValues);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
