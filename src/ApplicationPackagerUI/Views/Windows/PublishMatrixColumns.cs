using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The system columns of the review and results matrices, with a cell name that a screen reader reads as
/// "Notepad++, Intune, Failed at content upload".
/// </summary>
public static class PublishMatrixColumns
{
    /// <summary>A system column of the review as the mock draws it: the text of the cell only; the glyph column stays on the results.</summary>
    public static DataGridTemplateColumn Text(string header, int index, double width, double minWidth, Style cell)
    {
        var template = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding Cells[__I__]}" TextWrapping="Wrap" VerticalAlignment="Center" Margin="0,2" />
            </DataTemplate>
            """.Replace("__I__", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = template,
            CellStyle = Named(cell, $"Names[{index}]"),
            SortMemberPath = $"Cells[{index}]",
            ClipboardContentBinding = new System.Windows.Data.Binding($"Cells[{index}]"),
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            MinWidth = minWidth
        };
    }

    /// <summary>The cell style of the grid, plus the name of the cell.</summary>
    public static Style Named(Style cell, string namePath)
    {
        var style = new Style(typeof(DataGridCell), cell);
        style.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new System.Windows.Data.Binding(namePath)));
        return style;
    }
}
