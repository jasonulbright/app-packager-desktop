using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerUI.Helpers;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Checks the status columns of a real grid: the text color follows the outcome, and the text reads on the row fill, also when the row is selected.</summary>
internal static class StatusColumnKit
{
    private static string BrushOf(OutcomeKind kind) => kind switch
    {
        OutcomeKind.Success => "SystemFillColorSuccessBrush",
        OutcomeKind.Caution => "SystemFillColorCautionBrush",
        OutcomeKind.Critical => "SystemFillColorCriticalBrush",
        _ => "TextFillColorPrimaryBrush"
    };

    private static TextBlock CellText(DataGrid grid, DataGridRow row, DataGridColumn column)
    {
        var cell = Descendants(row).OfType<DataGridCell>().Single(c => ReferenceEquals(c.Column, column));
        return Descendants(cell).OfType<TextBlock>().First();
    }

    /// <summary>Runs the checks on the first rows of the grid and returns the outcomes seen, so a test can require that its data had each kind.</summary>
    public static HashSet<OutcomeKind> Assert(Window window, DataGrid grid, string name, params string[] headers)
    {
        var seen = new HashSet<OutcomeKind>();
        Xunit.Assert.True(grid.Items.Count >= 2, $"{name} needs two rows.");
        foreach (var header in headers)
        {
            var column = grid.Columns.Single(c => Equals(c.Header, header));
            var checkedCells = 0;
            for (var i = 0; i < Math.Min(grid.Items.Count, 12); i++)
            {
                // Another row is selected, so the row under test shows its plain fill.
                grid.SelectedIndex = i == 0 ? 1 : 0;
                var row = Row(grid, i);
                Settle(window);
                var block = CellText(grid, row, column);
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                var where = $"{name} {header} row {i} '{block.Text}'";
                block.BringIntoView();
                Settle(window);
                var kind = OutcomeKindConverter.Classify(block.Text);
                seen.Add(kind);
                Xunit.Assert.Equal(PageChromeKit.ColorOf(System.Windows.Application.Current.FindResource(BrushOf(kind))), PageChromeKit.ColorOf(block.Foreground));
                var plain = Pixels(window, block);
                Xunit.Assert.True(TextContrast(plain) >= PageChromeKit.TextContrastMinimum, $"{where}: contrast {TextContrast(plain):0.00} on {MostCommon(plain)}.");

                grid.SelectedIndex = i;
                Settle(window);
                Xunit.Assert.Equal(PageChromeKit.ColorOf(block.Foreground), PageChromeKit.ColorOf(Descendants(row).OfType<DataGridCell>().First(c => ReferenceEquals(c.Column, column)).Foreground));
                var selected = Pixels(window, block);
                Xunit.Assert.True(TextContrast(selected) >= PageChromeKit.TextContrastMinimum, $"{where} selected: contrast {TextContrast(selected):0.00} on {MostCommon(selected)}.");
                checkedCells++;
            }
            Xunit.Assert.True(checkedCells > 0, $"{name} {header} has no text to check.");
        }
        return seen;
    }
}
