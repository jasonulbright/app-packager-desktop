using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The bulk selection of the Packages grid follows the PowerShell app: the header cycles none, every visible row,
/// the rows with an update, none, and each step writes one log line.
/// </summary>
public sealed class PackagesSelectionCycleTests
{
    private static List<PackagerInfo> Rows() =>
    [
        new() { Uid = "a", Application = "Alpha", Status = "Update available" },
        new() { Uid = "b", Application = "Beta", Status = "Up to date" },
        new() { Uid = "c", Application = "Gamma", Status = "Update available (3 CVEs, 1 in KEV)" },
        new() { Uid = "d", Application = "Delta", Status = "Not checked" },
    ];

    private static string Picked(IEnumerable<PackagerInfo> rows) => string.Concat(rows.Select(r => r.IsSelected ? r.Uid : "-"));

    [Fact]
    public void TheHeaderClickCyclesNoneAllUpdatesNoneAndLogsEachStep()
    {
        var rows = Rows();
        var cycle = new SelectionCycle();
        var log = new List<string>();
        Assert.False(cycle.HeaderCheck);

        cycle.Advance(rows, log.Add);
        Assert.True(cycle.HeaderCheck);
        Assert.Equal("abcd", Picked(rows));

        cycle.Advance(rows, log.Add);
        Assert.Null(cycle.HeaderCheck);
        Assert.Equal("a-c-", Picked(rows));

        cycle.Advance(rows, log.Add);
        Assert.False(cycle.HeaderCheck);
        Assert.Equal("----", Picked(rows));

        cycle.Advance(rows, log.Add);
        Assert.True(cycle.HeaderCheck);

        Assert.Equal(["Selected all visible rows.", "Selected rows with 'Update available' status.", "Deselected the visible rows.", "Selected all visible rows."], log);
    }

    [Fact]
    public void ABulkStepChangesTheVisibleRowsAndKeepsACheckThatTheFilterHides()
    {
        var rows = Rows();
        rows[3].IsSelected = true; // hidden by the filter
        var visible = rows.Take(3).ToList();
        var cycle = new SelectionCycle();

        cycle.Advance(visible, _ => { });
        Assert.Equal("abcd", Picked(rows));

        cycle.Advance(visible, _ => { });
        Assert.Equal("a-cd", Picked(rows));

        cycle.Advance(visible, _ => { });
        Assert.Equal("---d", Picked(rows));
    }

    [Fact]
    public void CtrlAClearsOnlyWhenEveryVisibleRowIsCheckedAndNeverUnchecksAHiddenRow()
    {
        var rows = Rows();
        var vm = NewViewModel(rows);
        rows[3].IsSelected = true;
        vm.FilteredPackagers = new ObservableCollection<PackagerInfo>(rows.Take(2));

        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal("ab-d", Picked(rows));
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.Equal("---d", Picked(rows));
    }

    [Fact]
    public void AReloadKeepsTheChecksByUidAndNamesTheCheckedRowsThatAreGone()
    {
        var before = Rows();
        before[0].IsSelected = before[3].IsSelected = true;
        var after = Rows().Take(3).Reverse().ToList();

        var gone = SelectionKeeper.Restore(before, after);

        Assert.Equal(["Delta"], gone);
        Assert.Equal("--a", Picked(after));
        Assert.Equal("1 checked application is no longer in the grid, so its check was dropped: Delta.", SelectionKeeper.GoneLine(gone));
        Assert.StartsWith("2 checked applications are no longer in the grid, so their checks were dropped: ", SelectionKeeper.GoneLine(["A", "B"]));
    }

    [Fact]
    public void RowClicksSortingAndFilteringDoNotMoveTheHeaderOrLog()
    {
        var rows = Rows();
        var cycle = new SelectionCycle();
        var log = new List<string>();
        cycle.Advance(rows, log.Add);
        rows[1].IsSelected = false; // a row click
        rows.Reverse();             // a sort
        Assert.True(cycle.HeaderCheck);
        Assert.Single(log);
        Assert.True(rows.Single(r => r.Uid == "a").IsSelected); // a row the filter hides keeps its selection
    }

    [Fact]
    public void SelectUpdateAvailableSetsTheHeaderToIndeterminateAndLogsTheSameLine()
    {
        var rows = Rows();
        var cycle = new SelectionCycle();
        var log = new List<string>();
        cycle.SelectUpdates(rows, log.Add);
        Assert.Null(cycle.HeaderCheck);
        Assert.Equal("a-c-", Picked(rows));
        Assert.Equal(["Selected rows with 'Update available' status."], log);

        // The next header click clears, as in the PowerShell app.
        cycle.Advance(rows, log.Add);
        Assert.False(cycle.HeaderCheck);
        Assert.Equal("----", Picked(rows));
        Assert.Equal("Deselected the visible rows.", log[^1]);
    }

    [Fact]
    public void AViewModelCommandWritesTheLogLinesAndRaisesTheHeaderChange()
    {
        var vm = NewViewModel(Rows());
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.CycleSelectionCommand.Execute(null);
        Assert.True(vm.HeaderSelection);
        vm.SelectUpdateAvailableCommand.Execute(null);
        Assert.Null(vm.HeaderSelection);
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.True(vm.HeaderSelection);
        vm.ToggleSelectAllCommand.Execute(null);
        Assert.False(vm.HeaderSelection);

        var lines = vm.LogText.Split(Environment.NewLine).Select(l => l[(l.IndexOf("  ", StringComparison.Ordinal) + 2)..]).ToList();
        Assert.Equal(["Selected all visible rows.", "Selected rows with 'Update available' status.", "Selected all visible rows.", "Deselected the visible rows."], lines);
        Assert.Equal(4, changed.Count(n => n == nameof(PackagesViewModel.HeaderSelection)));
    }

    [Theory]
    [InlineData(true, "Staging 1 of 4: Alpha")]
    [InlineData(false, "Stage finished.")]
    public void ARowStatusChangeOnATabLeavesTheStatusLine(bool running, string line)
    {
        var rows = Rows();
        var vm = NewViewModel(rows);
        vm.StatusTab = ApplicationPackagerUI.Helpers.PackagesTabChoice.Update;
        // The run state is set on the field: the property also tells the commands, which this view model does not have.
        typeof(PackagesViewModel).GetField("_isOperationRunning", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, running);
        vm.StatusText = line;

        rows[0].Status = "Up to date";
        vm.RefreshCounts();

        Assert.Equal(line, vm.StatusText);
        Assert.DoesNotContain(rows[0], vm.FilteredPackagers);
    }

    internal static PackagesViewModel NewViewModel(List<PackagerInfo> rows)
    {
        // A constructed view model reads the settings and the definitions folder; the selection needs only the rows.
        var vm = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        typeof(PackagesViewModel).GetField("_selection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, new SelectionCycle());
        vm.LogText = "";
        vm.Packagers = new ObservableCollection<PackagerInfo>(rows);
        vm.FilteredPackagers = new ObservableCollection<PackagerInfo>(rows);
        return vm;
    }
}

/// <summary>The header check box of the Packages grid in a real window, read through UI Automation, in both themes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesHeaderCheckBoxAutomationTests
{
    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheHeaderCheckBoxShowsOnIndeterminateAndOffThroughTheCycle(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            System.Windows.Application.Current.Resources["BooleanToVisibilityConverter"] = new System.Windows.Controls.BooleanToVisibilityConverter();
            var rows = new List<PackagerInfo>
            {
                new() { Uid = "a", Application = "Alpha", Status = "Update available" },
                new() { Uid = "b", Application = "Beta", Status = "Up to date" },
            };
            var vm = PackagesSelectionCycleTests.NewViewModel(rows);
            var page = new PackagesPage(vm);
            var window = new Window { Width = 1440 - 48, Height = 900 - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                var box = Descendants(page).OfType<System.Windows.Controls.CheckBox>()
                    .Single(e => AutomationProperties.GetAutomationId(e) == "chkSelectAllPackages");
                var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(box).GetPattern(PatternInterface.Toggle)!;
                Assert.Equal(ToggleState.Off, toggle.ToggleState);

                box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(ToggleState.On, toggle.ToggleState);
                Assert.All(rows, r => Assert.True(r.IsSelected));

                box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(ToggleState.Indeterminate, toggle.ToggleState);
                Assert.Equal([true, false], rows.Select(r => r.IsSelected));

                box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(ToggleState.Off, toggle.ToggleState);
                Assert.All(rows, r => Assert.False(r.IsSelected));

                // Select Update Available moves the header to the indeterminate state too.
                vm.SelectUpdateAvailableCommand.Execute(null);
                Assert.Equal(ToggleState.Indeterminate, toggle.ToggleState);
            }
            finally { window.Close(); }
        });
    }
}
