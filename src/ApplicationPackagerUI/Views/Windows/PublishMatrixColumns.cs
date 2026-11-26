using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The system columns of the review and results matrices: a glyph and the text in each cell, and a cell name that a screen reader reads
/// as "Notepad++, Intune, Failed at content upload".
/// </summary>
public static class PublishMatrixColumns
{
    public static DataGridTemplateColumn For(string system, int index, double minWidth)
    {
        var template = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
                <DockPanel Margin="4,2">
                    <ui:SymbolIcon DockPanel.Dock="Left" Symbol="{Binding Glyphs[__I__]}" FontSize="16" Margin="0,0,4,0" VerticalAlignment="Top"
                                   Foreground="{Binding Foreground, RelativeSource={RelativeSource AncestorType=DataGridCell}}" />
                    <TextBlock Text="{Binding Cells[__I__]}" TextWrapping="Wrap" VerticalAlignment="Center" />
                </DockPanel>
            </DataTemplate>
            """.Replace("__I__", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var style = new Style(typeof(DataGridCell), Application.Current?.TryFindResource(typeof(DataGridCell)) as Style);
        style.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty, new Binding($"Names[{index}]")));
        return new DataGridTemplateColumn
        {
            Header = system,
            CellTemplate = template,
            CellStyle = style,
            SortMemberPath = $"Cells[{index}]",
            ClipboardContentBinding = new Binding($"Cells[{index}]"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = minWidth
        };
    }
}
