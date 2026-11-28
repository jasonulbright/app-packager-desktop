using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerUI.Controls.Mock;

/// <summary>True for the row of the last item of a table. The values are the item, the item count and the table.</summary>
public sealed class LastRowConverter : IMultiValueConverter
{
    public static LastRowConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [var item, int count, DataGrid grid] && count > 0 && count == grid.Items.Count && ReferenceEquals(grid.Items[count - 1], item);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
