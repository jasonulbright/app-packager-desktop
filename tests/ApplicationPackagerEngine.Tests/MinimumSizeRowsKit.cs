using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The whole data rows that a page grid shows in the minimum audit window of 1024x720.</summary>
internal sealed class MinimumSizeRows
{
    /// <summary>The window that the layout audits use as the smallest supported size.</summary>
    public const int Width = 1024;

    public const int Height = 720;

    /// <summary>The least number of whole data rows that a main grid shows at the minimum size.</summary>
    public const int Least = 3;

    private readonly List<(string Name, int Items, int Rows)> _measured = [];

    public void Add(string name, DataGrid grid) => _measured.Add((name, grid.Items.Count, AppPackagerFitTests.WholeRows(grid)));

    public void AssertEnough(string where)
    {
        var report = string.Join("; ", _measured.Select(m => $"{m.Name} {m.Rows} of {m.Items} rows"));
        Assert.All(_measured, m => Assert.True(m.Items >= Least, $"{where}: {m.Name} has {m.Items} rows, too few to measure. {report}."));
        Assert.All(_measured, m => Assert.True(m.Rows >= Least, $"{where}: {m.Name} shows {m.Rows} whole rows; the least is {Least}. {report}."));
    }
}
