using System.Windows;
using System.Windows.Controls;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Windows.Data;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.Views.Pages;

public partial class SettingsPage
{
    private readonly SettingsViewModel _viewModel = null!;

    /// <summary>The page the tree showed last, so a return to Settings in the same run opens where the user was.</summary>
    private static string s_lastPage = "general";

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        App.Services.GetRequiredService<Services.UpdateCoordinator>().ShowAndCheck(Window.GetWindow(this));

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SelectPage(s_lastPage);
        BuildApplicationColumns();
        TrackApplicationColumnGrips();
        TrackApplicationLayout();
        SetHelpFromToolTips(page_configmgr, page_supersedence);
        TrackInput();
        TrackDiscoveryStatus();
        CollectionViewSource.GetDefaultView(_viewModel.SupersedenceRows).Filter = o => o is Models.SupersedenceRow r && _viewModel.MatchesFilter(r);
        _viewModel.FilterChanged += () => CollectionViewSource.GetDefaultView(_viewModel.SupersedenceRows).Refresh();
        // The view model is built before the application list finishes loading, so the
        // One Click grid is filled when the page is shown and refilled when that list
        // changed size; an unchanged list keeps unsaved edits.
        Loaded += (_, _) =>
        {
            if (_viewModel.OneClickRows.Count != _viewModel.PackagerCount)
                _viewModel.RefreshOneClickRows();
            if (_viewModel.SupersedenceRows.Count == 0 || _viewModel.SupersedenceRows.Count > _viewModel.PackagerCount)
                _viewModel.RefreshSupersedenceRows();
        };
    }

    private void ShowSetupChecklist_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not Windows.MainWindow main) return;
        main.GetNavigation().Navigate(typeof(PackagesPage));
        var packages = App.Services.GetRequiredService<PackagesPage>();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, packages.ShowChecklist);
    }

    private void OpenSystemsInUse_Click(object sender, RoutedEventArgs e) => SelectPage("systems");

    /// <summary>Opens the ConfigMgr page with the connection options of Discover (Test connection, encryption and certificate).</summary>
    private void ShowDiscoveryOptions_Click(object sender, RoutedEventArgs e)
    {
        SelectPage("configmgr");
        RevealDiscoveryOptions();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => pnlDiscoveryOptions.BringIntoView());
    }

    private void RevealDiscoveryOptions() => pnlDiscoveryOptions.Tag = "shown";

    /// <summary>A run of Discover or a connection test shows its result, and with it the options that a failed connection needs.</summary>
    private void TrackDiscoveryStatus()
    {
        if (_viewModel.DiscoverStatus is { Length: > 0 }) RevealDiscoveryOptions();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.DiscoverStatus) && _viewModel.DiscoverStatus is { Length: > 0 })
                Dispatcher.BeginInvoke(RevealDiscoveryOptions);
        };
    }

    /// <summary>Shows one page of the Options tree and hides the others. An unknown id leaves the page as it is.</summary>
    public void SelectPage(string id)
    {
        if (FindName("page_" + id) is not FrameworkElement) return;
        s_lastPage = id;
        foreach (var node in Leaves(OptionsTree.Items))
            if ((string?)node.Tag == id) node.IsSelected = true;
        ShowOnly(id);
    }

    private void ShowOnly(string id)
    {
        foreach (var node in Leaves(OptionsTree.Items))
            if (node.Tag is string tag && FindName("page_" + tag) is FrameworkElement page)
                page.Visibility = tag == id ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEnumerable<TreeViewItem> Leaves(ItemCollection items)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is string) yield return item;
            foreach (var child in Leaves(item.Items)) yield return child;
        }
    }

    private void OptionsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item) return;
        // A group has no page of its own: choosing it opens its first page.
        if (item.Tag is not string id)
        {
            if (item.Items.OfType<TreeViewItem>().FirstOrDefault() is { } first) first.IsSelected = true;
            return;
        }
        s_lastPage = id;
        ShowOnly(id);
    }

    /// <summary>Builds the columns of the application grid after the fixed ones, one for each registered setting that the person chose to show.</summary>
    private void BuildApplicationColumns()
    {
        while (dgApplications.Columns.Count > 3) dgApplications.Columns.RemoveAt(dgApplications.Columns.Count - 1);
        foreach (var column in ApplicationColumns.All.Where(c => _viewModel.VisibleColumnIds.Contains(c.Id)))
        {
            var cell = column.Choices is null
                ? new FrameworkElementFactory(typeof(System.Windows.Controls.TextBox))
                : new FrameworkElementFactory(typeof(ComboBox));
            cell.SetResourceReference(FrameworkElement.StyleProperty, column.Choices is null ? "Mk.Settings.CellTextBox" : "Mk.Settings.CellComboBox");
            cell.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, column.AutomationName);
            cell.SetBinding(UIElement.IsEnabledProperty, new Binding($"Editable[{column.Id}]"));
            cell.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"Tips[{column.Id}]"));
            // A fixed field is disabled. Its reason still shows on hover, and a screen reader reads it as the help of the field and of its cell.
            cell.SetValue(ToolTipService.ShowOnDisabledProperty, true);
            cell.SetBinding(System.Windows.Automation.AutomationProperties.HelpTextProperty, new Binding($"Tips[{column.Id}]"));
            if (column.Choices is null)
                cell.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding($"Cells[{column.Id}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            else
            {
                cell.SetBinding(ItemsControl.ItemsSourceProperty, new Binding($"Lists[{column.Id}]") { Mode = BindingMode.OneWay });
                cell.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
                    new Binding($"Cells[{column.Id}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            }
            dgApplications.Columns.Add(new DataGridTemplateColumn
            {
                Header = column.Header,
                Width = column.Width,
                // The grid has a star column, so without a minimum it narrows the other columns instead of scrolling sideways.
                MinWidth = column.Width,
                SortMemberPath = $"Cells[{column.Id}]",
                CellStyle = CellStyleOf(column),
                CellTemplate = new DataTemplate { VisualTree = cell }
            });
        }
        ApplyApplicationLayout();
    }

    // Each column has its width as MinWidth, so the grid scrolls sideways and does not narrow it. A grip of the operator lowers that minimum
    // down to this floor. The floor of the Check column keeps its box whole.
    private const double NarrowestColumn = 40;
    private const double NarrowestCheckColumn = 56;

    private double FloorOf(DataGridColumn column) => ReferenceEquals(column, dgApplications.Columns[0]) ? NarrowestCheckColumn : NarrowestColumn;

    private (DataGridColumn Column, double Width, double MinWidth)? _pressedColumn;
    private (DataGridColumn Column, DataGridLength Width)? _pinnedStar;

    private void TrackApplicationColumnGrips()
    {
        dgApplications.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler(OnColumnGripPressed), true);
        dgApplications.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler(OnColumnGripReleased), true);
        dgApplications.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new System.Windows.Input.MouseButtonEventHandler(OnColumnGripMouseDown), true);
    }

    private void OnColumnGripPressed(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (Helpers.ColumnGrips.SizedColumn(dgApplications, e.OriginalSource) is not { } column) return;
        // While a grid has a star column, a column grows only by the width that other columns can give. Here none can give any, so the star
        // column is a pixel column until the release.
        if (dgApplications.Columns.FirstOrDefault(c => c.Width.IsStar) is { } star)
        {
            _pinnedStar = (star, star.Width);
            star.Width = new DataGridLength(star.ActualWidth);
        }
        _pressedColumn = (column, column.ActualWidth, column.MinWidth);
        column.MinWidth = FloorOf(column);
    }

    private void OnColumnGripReleased(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_pressedColumn is not { } pressed) return;
        _pressedColumn = null;
        // A click on a grip that moved nothing sets no width.
        var moved = Math.Abs(pressed.Column.ActualWidth - pressed.Width) >= 0.5;
        pressed.Column.MinWidth = moved ? pressed.Column.ActualWidth : pressed.MinWidth;
        if (moved && _tracksApplicationLayout) NoteSizedApplicationColumn(pressed.Column);
        if (_pinnedStar is not { } star) return;
        _pinnedStar = null;
        star.Column.Width = star.Width;
    }

    /// <summary>A double click on a grip fits the column to its content. The second press starts no drag, so the MinWidth is lowered here.</summary>
    private void OnColumnGripMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || Helpers.ColumnGrips.SizedColumn(dgApplications, e.OriginalSource) is not { } column) return;
        column.MinWidth = FloorOf(column);
        Helpers.ColumnGrips.AfterFit(dgApplications, column, () => column.MinWidth = column.Width.Value);
    }

    /// <summary>The style of a grid cell with the help of its field. The cell keeps the focus when its control is disabled.</summary>
    private Style? CellStyleOf(ApplicationColumn column)
    {
        // The cell style of the theme, which the other cells of the grid have. A lookup by the DataGridCell type finds the style of the system:
        // its selected cell is light in each theme.
        if ((dgApplications.CellStyle ?? TryFindResource("DefaultDataGridCellStyle") as Style) is not { } basis) return null;
        var style = new Style(typeof(System.Windows.Controls.DataGridCell), basis);
        style.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.HelpTextProperty, new Binding($"Tips[{column.Id}]")));
        return style;
    }

    /// <summary>The line under the grid says what the focused field does, or why it is unavailable, so the keyboard reaches what the tooltip says.</summary>
    private void Applications_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        for (var element = e.NewFocus as DependencyObject; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is not System.Windows.Controls.DataGridCell cell) continue;
            if (cell.DataContext is Models.SupersedenceRow row && ApplicationColumns.All.FirstOrDefault(c => Equals(c.Header, cell.Column?.Header)) is { } column)
                txtFieldHelp.Text = ApplicationColumns.FieldHelp(row, column);
            return;
        }
    }

    /// <summary>The smallest height of the application grid; a shorter window scrolls the page.</summary>
    private const double MinimumGridHeight = 240;

    /// <summary>The application grid takes the height that the rest of its page leaves, so the grid fills the page and keeps its own scrolling.</summary>
    private void Supersedence_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The grid has a scroll viewer of its own; only the changes of the page count.
        if (!ReferenceEquals(e.OriginalSource, page_supersedence) || page_supersedence.ViewportHeight <= 0) return;
        var rest = pnlApplicationSettings.ActualHeight + pnlApplicationSettings.Margin.Top + pnlApplicationSettings.Margin.Bottom - dgApplications.ActualHeight;
        var height = Math.Max(MinimumGridHeight, Math.Floor(page_supersedence.ViewportHeight - rest));
        if (Math.Abs(dgApplications.Height - height) >= 1) dgApplications.Height = height;
    }

    /// <summary>The column chooser: one check item for each setting of the registry.</summary>
    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var column in ApplicationColumns.All)
        {
            var item = new System.Windows.Controls.MenuItem { Header = column.Header, IsCheckable = true, IsChecked = _viewModel.VisibleColumnIds.Contains(column.Id), StaysOpenOnClick = true, ToolTip = column.Tooltip };
            item.Click += (_, _) => { _viewModel.SetColumnVisible(column.Id, item.IsChecked); BuildApplicationColumns(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
