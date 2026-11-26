using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ApplicationPackagerUI.Helpers;
using ComboBox = System.Windows.Controls.ComboBox;
using NumberBox = Wpf.Ui.Controls.NumberBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>
/// The help text of the fields, and the saved layout of the Application Settings grid. The order, the widths and the sort are saved at
/// once, as the choice of columns is; together they are one layout that survives a restart.
/// </summary>
public partial class SettingsPage
{
    private readonly HashSet<string> _sizedApplicationColumns = new(StringComparer.Ordinal);
    private bool _applyingApplicationLayout;

    /// <summary>
    /// A screen reader reads the explanation of a field when the field gets the keyboard focus. The explanation is the tooltip of the
    /// field, which a keyboard user does not see.
    /// </summary>
    private void SetHelpFromToolTips(params DependencyObject[] pages)
    {
        foreach (var page in pages)
        foreach (var child in LogicalTreeHelper.GetChildren(page).OfType<DependencyObject>())
        {
            if (child is TextBox or ComboBox or System.Windows.Controls.CheckBox or NumberBox && child is FrameworkElement { ToolTip: string tip }
                && AutomationProperties.GetHelpText(child).Length == 0)
                AutomationProperties.SetHelpText(child, tip);
            SetHelpFromToolTips(child);
        }
    }

    private static string ColumnKey(DataGridColumn column) => column.Header as string ?? "";

    private void TrackApplicationLayout()
    {
        dgApplications.ColumnReordered += (_, _) => SaveApplicationLayout();
        dgApplications.Sorting += (_, _) => Dispatcher.BeginInvoke(SaveApplicationLayout);
        dgApplications.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (ColumnGrips.SizedColumn(dgApplications, e.OriginalSource) is not { } column) return;
            _sizedApplicationColumns.Add(ColumnKey(column));
            Dispatcher.BeginInvoke(SaveApplicationLayout);
        }), true);
        dgApplications.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ClickCount != 2 || ColumnGrips.SizedColumn(dgApplications, e.OriginalSource) is not { } column) return;
            ColumnGrips.AfterFit(dgApplications, column, () =>
            {
                _sizedApplicationColumns.Add(ColumnKey(column));
                SaveApplicationLayout();
            });
        }), true);
    }

    /// <summary>Applies the saved order, widths and sort to the columns that the grid has now. The first column stays first.</summary>
    private void ApplyApplicationLayout()
    {
        var layout = _viewModel.ApplicationGridLayout;
        if (layout is null || dgApplications.Columns.Count < 2) return;
        _applyingApplicationLayout = true;
        try
        {
            var movable = dgApplications.Columns.Skip(1).Where(c => ColumnKey(c).Length > 0).ToList();
            var byId = movable.ToDictionary(ColumnKey, StringComparer.Ordinal);
            var order = PackagesColumnLayout.Order(movable.Select(ColumnKey).ToList(), layout);
            for (var i = 0; i < order.Count; i++) byId[order[i]].DisplayIndex = 1 + i;
            _sizedApplicationColumns.Clear();
            foreach (var column in movable)
            {
                if (PackagesColumnLayout.WidthOf(layout, ColumnKey(column)) is not { } width) continue;
                _sizedApplicationColumns.Add(ColumnKey(column));
                column.MinWidth = Math.Max(width, NarrowestColumn);
                column.Width = new DataGridLength(column.MinWidth);
            }
            var sorted = movable.FirstOrDefault(c => ColumnKey(c) == layout.SortId && !string.IsNullOrEmpty(c.SortMemberPath));
            if (sorted is null) return;
            var direction = layout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            dgApplications.Items.SortDescriptions.Clear();
            dgApplications.Items.SortDescriptions.Add(new SortDescription(sorted.SortMemberPath, direction));
            sorted.SortDirection = direction;
        }
        finally { _applyingApplicationLayout = false; }
    }

    private void SaveApplicationLayout()
    {
        if (_applyingApplicationLayout) return;
        var movable = dgApplications.Columns.Skip(1).Where(c => ColumnKey(c).Length > 0).OrderBy(c => c.DisplayIndex).ToList();
        var shown = movable.Select(ColumnKey).ToHashSet(StringComparer.Ordinal);
        var sorted = movable.FirstOrDefault(c => c.SortDirection is not null);
        // A hidden column keeps what the operator set for it.
        var kept = (_viewModel.ApplicationGridLayout?.Columns ?? []).Where(c => c?.Id is not null && !shown.Contains(c.Id));
        _viewModel.SaveApplicationGridLayout(new PackagesColumnLayout
        {
            Columns = movable.Select(c => new PackagesColumnState
            {
                Id = ColumnKey(c),
                Width = _sizedApplicationColumns.Contains(ColumnKey(c)) && c.ActualWidth > 0 ? c.ActualWidth : 0
            }).Concat(kept).ToList(),
            SortId = sorted is null ? "" : ColumnKey(sorted),
            SortDescending = sorted?.SortDirection == ListSortDirection.Descending
        });
    }
}
