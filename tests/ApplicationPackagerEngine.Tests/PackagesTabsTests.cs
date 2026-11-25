using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using Application = System.Windows.Application;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using RadioButton = System.Windows.Controls.RadioButton;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Overhaul V1 phase 3 (Q-126): the status tabs of the Packages grid and the selection line. A tab, a search and a check change the shown list in place,
/// so the sort and the scroll position stay; the counts are over every application; the selection line says how many checked rows the view hides.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesTabsTests
{
    private static List<PackagerInfo> Rows() =>
    [
        new() { Uid = "a", Application = "Alpha", Status = "Ready" },
        new() { Uid = "b", Application = "Bravo", Status = CveRowState.UpdateAvailable, CurrentVersion = "1.0" },
        new() { Uid = "c", Application = "Charlie", Status = "Not found in ConfigMgr" },
        new() { Uid = "d", Application = "Delta", Status = "Up to date", CurrentVersion = "2.0" },
        new() { Uid = "e", Application = "Echo", Status = "Cached" }
    ];

    [Theory]
    [InlineData(PackagesTabChoice.All, new[] { "a", "b", "c", "d", "e" })]
    [InlineData(PackagesTabChoice.Update, new[] { "b" })]
    [InlineData(PackagesTabChoice.NotFound, new[] { "c" })]
    [InlineData(PackagesTabChoice.NotChecked, new[] { "a", "e" })]
    public void EachTabShowsItsRows(PackagesTabChoice tab, string[] expected) =>
        Assert.Equal(expected, Rows().Where(r => PackagesTab.Matches(tab, r)).Select(r => r.Uid));

    [Fact]
    public void TheSelectedTabShowsTheCheckedRows()
    {
        var rows = Rows();
        rows[1].IsSelected = true;
        rows[3].IsSelected = true;
        Assert.Equal(["b", "d"], rows.Where(r => PackagesTab.Matches(PackagesTabChoice.Selected, r)).Select(r => r.Uid));
    }

    [Fact]
    public void TheShownListChangesInPlaceAndKeepsTheRowsThatStay()
    {
        var rows = Rows();
        var shown = new ObservableCollection<PackagerInfo>(rows);
        var changes = new List<string>();
        shown.CollectionChanged += (_, e) => changes.Add(e.Action.ToString());

        ListSync.Sync(shown, [rows[1], rows[3]]);
        Assert.Equal(["b", "d"], shown.Select(r => r.Uid));
        Assert.All(changes, c => Assert.Equal("Remove", c));
        Assert.Equal(3, changes.Count);

        changes.Clear();
        ListSync.Sync(shown, [rows[0], rows[1], rows[3]]);
        Assert.Equal(["a", "b", "d"], shown.Select(r => r.Uid));
        Assert.Equal(["Add"], changes);
        Assert.Same(rows[1], shown[1]);

        ListSync.Sync(shown, [rows[3], rows[1], rows[0]]);
        Assert.Equal(["d", "b", "a"], shown.Select(r => r.Uid));
        ListSync.Sync(shown, []);
        Assert.Empty(shown);
    }

    // The view model without a window: the tab, the search and the selection line.

    private static PackagesViewModel ViewModel(List<PackagerInfo> rows)
    {
        var viewModel = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        viewModel.Packagers = new ObservableCollection<PackagerInfo>(rows);
        viewModel.FilteredPackagers = new ObservableCollection<PackagerInfo>(rows);
        return viewModel;
    }

    [Fact]
    public void AChangeOfTabFiltersTheListInPlace()
    {
        var rows = Rows();
        var viewModel = ViewModel(rows);
        var list = viewModel.FilteredPackagers;

        viewModel.StatusTab = PackagesTabChoice.Update;

        Assert.Same(list, viewModel.FilteredPackagers);
        Assert.Equal(["b"], viewModel.FilteredPackagers.Select(r => r.Uid));
        Assert.Equal(new PackagesTabCounts(5, 1, 1, 2), viewModel.TabCounts);
        Assert.Equal("Updates available 1", viewModel.TabUpdateText);
        Assert.Equal("Not found in ConfigMgr 1", viewModel.TabNotFoundText);
        Assert.Equal("All 5", viewModel.TabAllText);
    }

    [Fact]
    public void ASearchAndATabTogetherAreBothApplied()
    {
        var rows = Rows();
        var viewModel = ViewModel(rows);
        viewModel.StatusTab = PackagesTabChoice.NotChecked;
        viewModel.SearchText = "Echo";
        Assert.Equal(["e"], viewModel.FilteredPackagers.Select(r => r.Uid));
        // The counts stay over every application.
        Assert.Equal(5, viewModel.TabCounts.All);
    }

    [Fact]
    public void TheSelectionLineCountsTheCheckedRowsTheViewHides()
    {
        var rows = Rows();
        var viewModel = ViewModel(rows);
        rows[0].IsSelected = true;
        rows[1].IsSelected = true;
        viewModel.StatusTab = PackagesTabChoice.Update;

        Assert.Equal(2, viewModel.SelectedCount);
        Assert.Equal(1, viewModel.HiddenSelectedCount);
        Assert.Equal("2 selected, 1 outside this view", viewModel.SelectionText);
        Assert.True(viewModel.HasHiddenSelection);

        viewModel.SearchText = "Bravo";
        viewModel.ShowSelectedCommand.Execute(null);
        Assert.Equal("", viewModel.SearchText);
        Assert.Equal(PackagesTabChoice.Selected, viewModel.StatusTab);
        Assert.Equal(["a", "b"], viewModel.FilteredPackagers.Select(r => r.Uid));
        Assert.Equal("2 selected", viewModel.SelectionText);

        viewModel.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.Equal(PackagesTabChoice.All, viewModel.StatusTab);
        Assert.Equal(5, viewModel.FilteredPackagers.Count);
    }

    [Fact]
    public void ARowThatChangesItsStatusLeavesTheTabWithoutAReload()
    {
        var rows = Rows();
        var viewModel = ViewModel(rows);
        viewModel.StatusTab = PackagesTabChoice.Update;
        var list = viewModel.FilteredPackagers;
        Assert.Single(list);

        rows[1].Status = "Up to date";
        viewModel.RefreshCounts();

        Assert.Same(list, viewModel.FilteredPackagers);
        Assert.Empty(viewModel.FilteredPackagers);
        Assert.Equal(0, viewModel.TabCounts.Update);
    }

    [Fact]
    public void TheConverterChecksTheTabThatEqualsItsParameter()
    {
        var converter = new EnumEqualsConverter();
        Assert.Equal(true, converter.Convert(PackagesTabChoice.Update, typeof(bool), "Update", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(PackagesTabChoice.All, typeof(bool), "Update", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PackagesTabChoice.NotFound, converter.ConvertBack(true, typeof(PackagesTabChoice), "NotFound", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(System.Windows.Data.Binding.DoNothing, converter.ConvertBack(false, typeof(PackagesTabChoice), "NotFound", System.Globalization.CultureInfo.InvariantCulture));
    }

    // The page.

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ThePageShowsTheFourTabsWithTheirCountsAndTheSelectionLine(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            Application.Current.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
            var rows = Rows();
            rows[0].IsSelected = true;
            var viewModel = ViewModel(rows);
            var page = new PackagesPage(viewModel);
            var window = new Window { Width = 1200, Height = 700, WindowStyle = WindowStyle.None, Content = page };
            RenderKit.Show(window);
            try
            {
                window.UpdateLayout();
                string Tab(string id) => (string)RenderKit.ById<RadioButton>(page, id).Content;
                Assert.Equal("All 5", Tab("tabStatusAll"));
                Assert.Equal("Updates available 1", Tab("tabStatusUpdate"));
                Assert.Equal("Not found in ConfigMgr 1", Tab("tabStatusNotFound"));
                Assert.Equal("Not checked 2", Tab("tabStatusNotChecked"));
                Assert.True(RenderKit.ById<RadioButton>(page, "tabStatusAll").IsChecked);

                // A tab has its count in its name for a screen reader, and a click on a tab changes the list.
                var update = RenderKit.ById<RadioButton>(page, "tabStatusUpdate");
                Assert.Equal("Updates available 1", UIElementAutomationPeer.CreatePeerForElement(update).GetName());
                update.IsChecked = true;
                Assert.Equal(PackagesTabChoice.Update, viewModel.StatusTab);
                Assert.Equal(["b"], viewModel.FilteredPackagers.Select(r => r.Uid));

                var line = RenderKit.ById<TextBlock>(page, "txtSelectionCount");
                Assert.Equal("1 selected, 1 outside this view", line.Text);
                Assert.True(RenderKit.ById<ButtonBase>(page, "btnShowSelected").IsVisible);
                Assert.True(RenderKit.ById<ButtonBase>(page, "btnClearSelection").IsVisible);
            }
            finally { window.Close(); }
        });
    }
}
