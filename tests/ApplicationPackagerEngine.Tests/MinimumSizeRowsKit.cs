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

    private readonly HashSet<string> _complete = [];

    public void Add(string name, DataGrid grid) => _measured.Add((name, grid.Items.Count, AppPackagerFitTests.WholeRows(grid)));

    /// <summary>A grid with a fixed, short data set (such as a pipeline of two stages): every row must show.</summary>
    public void AddComplete(string name, DataGrid grid)
    {
        _complete.Add(name);
        Add(name, grid);
    }

    /// <summary>Repeats the last item of a grid with a growable source until the grid has <see cref="Least"/> + 3 items, then measures it.</summary>
    public void AddPadded(string name, DataGrid grid)
    {
        if (grid.ItemsSource is System.Collections.IList { IsFixedSize: false, IsReadOnly: false, Count: > 0 } list)
        {
            var last = list[list.Count - 1];
            while (list.Count < Least + 3) list.Add(last);
            grid.UpdateLayout();
        }
        Add(name, grid);
    }

    public void AssertEnough(string where)
    {
        var report = string.Join("; ", _measured.Select(m => $"{m.Name} {m.Rows} of {m.Items} rows"));
        Assert.All(_measured.Where(m => _complete.Contains(m.Name)), m => Assert.True(m.Items > 0 && m.Rows == m.Items, $"{where}: {m.Name} shows {m.Rows} of {m.Items} rows. {report}."));
        _measured.RemoveAll(m => _complete.Contains(m.Name));
        Assert.All(_measured, m => Assert.True(m.Items >= Least, $"{where}: {m.Name} has {m.Items} rows, too few to measure. {report}."));
        Assert.All(_measured, m => Assert.True(m.Rows >= Least, $"{where}: {m.Name} shows {m.Rows} whole rows; the least is {Least}. {report}."));
    }
}
