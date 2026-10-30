using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CatalogWindowTests
{
    private sealed class Store
    {
        public AppSettings Settings { get; set; } = new();
        public int Refreshes { get; set; }
        public List<string> Shown { get; } = [];
        public List<string> Log { get; } = [];
    }

    private static IReadOnlyList<PackagerInfo> Packagers() =>
    [
        new PackagerInfo { Uid = "git", Vendor = "Git", Application = "Git", Script = "git.json", IsJsonDefined = true },
        new PackagerInfo { Uid = "grepwin", Vendor = "Stefan Kueng", Application = "grepWin", Script = "grepwin.json", IsJsonDefined = true },
        new PackagerInfo { Uid = "tortoisegit", Vendor = "Stefan Kueng", Application = "TortoiseGit", Script = "tortoisegit.json", IsJsonDefined = true },
        new PackagerInfo { Uid = "gsudo", Vendor = "Gerardo Grignoli", Application = "gsudo", Script = "package-gsudo.ps1" },
    ];

    private static (CatalogViewModel ViewModel, Store Store) Catalog(params string[] hidden)
    {
        var store = new Store { Settings = new AppSettings { HiddenApplications = hidden } };
        var viewModel = new CatalogViewModel(new CatalogServices(
            LoadSettings: () => store.Settings,
            SaveSettings: s => store.Settings = s,
            LoadPackagers: () => Task.FromResult<IReadOnlyList<PackagerInfo>?>(Packagers()),
            RefreshGrid: () => { store.Refreshes++; return Task.CompletedTask; },
            ShowSaved: store.Shown.Add,
            Log: store.Log.Add));
        return (viewModel, store);
    }

    private static AppVisibilityItem App(CatalogViewModel viewModel, string uid) => viewModel.VendorGroups.SelectMany(g => g.Apps).Single(a => a.Uid == uid);

    [Fact]
    public void TheCountsAndTheTextNameTheAppPackagerGrid()
    {
        var (viewModel, _) = Catalog("gsudo");
        Assert.Equal(4, viewModel.TotalCount);
        Assert.Equal(3, viewModel.VisibleCount);
        Assert.Equal("3 of 4 applications are visible in the App Packager grid.", viewModel.CountText);
        Assert.Equal(["Gerardo Grignoli", "Git", "Stefan Kueng"], viewModel.VendorGroups.Select(g => g.Vendor));
    }

    [Fact]
    public void TheSearchMatchesApplicationsAndVendorsAndHidesGroupsWithoutAMatch()
    {
        var (viewModel, _) = Catalog();
        viewModel.SearchText = "git";
        Assert.True(App(viewModel, "git").MatchesSearch);
        Assert.True(App(viewModel, "tortoisegit").MatchesSearch);
        Assert.False(App(viewModel, "grepwin").MatchesSearch);
        Assert.False(viewModel.VendorGroups.Single(g => g.Vendor == "Gerardo Grignoli").MatchesSearch);
        Assert.Equal(2, viewModel.MatchCount);
        Assert.Equal("4 of 4 applications are visible in the App Packager grid. 2 match the search.", viewModel.CountText);

        viewModel.SearchText = "KUENG";
        Assert.True(App(viewModel, "grepwin").MatchesSearch);
        Assert.True(App(viewModel, "tortoisegit").MatchesSearch);
        Assert.False(App(viewModel, "git").MatchesSearch);

        viewModel.SearchText = " ";
        Assert.All(viewModel.VendorGroups.SelectMany(g => g.Apps), a => Assert.True(a.MatchesSearch));
        Assert.Equal(4, viewModel.MatchCount);
    }

    [Fact]
    public void HideAllAndShowAllChangeOnlyTheMatchesAndEveryApplicationWithoutASearch()
    {
        var (viewModel, _) = Catalog();
        viewModel.SearchText = "git";
        viewModel.HideAllCommand.Execute(null);
        Assert.False(App(viewModel, "git").IsVisible);
        Assert.False(App(viewModel, "tortoisegit").IsVisible);
        Assert.True(App(viewModel, "grepwin").IsVisible);
        Assert.Null(viewModel.VendorGroups.Single(g => g.Vendor == "Stefan Kueng").IsAllVisible);
        Assert.Equal(2, viewModel.VisibleCount);

        viewModel.SearchText = "";
        viewModel.HideAllCommand.Execute(null);
        Assert.Equal(0, viewModel.VisibleCount);
        viewModel.ShowAllCommand.Execute(null);
        Assert.Equal(4, viewModel.VisibleCount);
    }

    [Fact]
    public async Task SaveWritesTheHiddenListReloadsTheGridAndReports()
    {
        var (viewModel, store) = Catalog();
        App(viewModel, "grepwin").IsVisible = false;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["grepwin"], store.Settings.HiddenApplications);
        Assert.Equal(1, store.Refreshes);
        Assert.Equal(["Saved. 3 of 4 visible."], store.Shown);
        Assert.Empty(store.Log);
    }

    [Fact]
    public async Task AChangeThatIsNotSaved_IsNotDescribedAsTheStateOfTheGrid()
    {
        var (viewModel, _) = Catalog();
        viewModel.HideAllCommand.Execute(null);

        Assert.Equal("0 of 4 applications are selected to show. The App Packager grid changes when you select Save.", viewModel.CountText);
        viewModel.SearchText = "git";
        Assert.Equal("0 of 4 applications are selected to show. The App Packager grid changes when you select Save. 2 match the search.", viewModel.CountText);

        viewModel.SearchText = "";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal("0 of 4 applications are visible in the App Packager grid.", viewModel.CountText);
    }

    [Fact]
    public void AChangeThatIsUndone_DescribesTheGridAgain()
    {
        var (viewModel, _) = Catalog("gsudo");
        App(viewModel, "git").IsVisible = false;
        App(viewModel, "git").IsVisible = true;

        Assert.Equal("3 of 4 applications are visible in the App Packager grid.", viewModel.CountText);
    }

    public static TheoryData<ApplicationTheme, bool> Windows() => new()
    {
        { ApplicationTheme.Light, false }, { ApplicationTheme.Dark, false }, { ApplicationTheme.Light, true }, { ApplicationTheme.Dark, true }
    };

    [Theory]
    [MemberData(nameof(Windows))]
    public void TheWindowIsTitledCatalogShowsEveryControlAndOpensOnTheSearch(ApplicationTheme theme, bool smallest)
    {
        WithApplication(theme, () =>
        {
            var (viewModel, _) = Catalog("gsudo");
            var window = new CatalogWindow(viewModel);
            if (smallest)
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
            }
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            window.Show();
            var where = $"Catalog {theme} {(smallest ? "smallest" : "default")}";
            try
            {
                Settle(window);
                Assert.Equal("Catalog", window.Title);
                Assert.Equal("Catalog", Assert.Single(Descendants(window).OfType<Wpf.Ui.Controls.TitleBar>()).Title);
                var title = ById<TextBlock>(window, "txtCatalogTitle");
                Assert.Equal("Catalog", title.Text);
                PageChromeKit.AssertTitle(window, title, where);
                Assert.Equal(viewModel.CountText, ById<TextBlock>(window, "txtPageDescription").Text);

                window.Activate();
                StaTestHelper.Flush();
                Assert.Equal("txtCatalogSearch", PageChromeKit.Focused());

                var footer = new[] { "btnCatalogShowAll", "btnCatalogHideAll", "btnCatalogSave", "btnCatalogClose" };
                foreach (var id in footer) AssertInside(window, ById<FrameworkElement>(window, id), window.ActualHeight, $"{where} {id}");
                PageChromeKit.AssertNothingClipped(window, where);
                PageChromeKit.AssertAutomationNames(window, where);

                var order = PageChromeKit.Tabs(40);
                var first = order.IndexOf("btnCatalogShowAll");
                Assert.True(first > 0, $"{where}: Tab does not reach Show all: {string.Join(", ", order)}.");
                Assert.Equal(footer, order.Skip(first).Take(footer.Length));
                Assert.Contains(order.Take(first), id => id.StartsWith("Show every application of", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheSearchBoxFiltersTheVendorGroupsInTheWindow()
    {
        WithApplication(ApplicationTheme.Dark, () =>
        {
            var (viewModel, _) = Catalog();
            var window = new CatalogWindow(viewModel);
            Show(window);
            try
            {
                viewModel.SearchText = "gsudo";
                Settle(window);
                var shown = Descendants(window).OfType<Expander>().Where(e => e.IsVisible).Select(e => AutomationPropertiesName(e)).ToList();
                Assert.Equal(["Gerardo Grignoli"], shown);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheVendorCheckBoxChangesOnlyTheApplicationsTheSearchShows()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var (viewModel, _) = Catalog();
            var window = new CatalogWindow(viewModel);
            Show(window);
            try
            {
                viewModel.SearchText = "grepwin";
                Settle(window);
                var box = Descendants(window).OfType<System.Windows.Controls.CheckBox>().Single(c => AutomationPropertiesName(c) == "Show every application of Stefan Kueng");
                box.IsChecked = false;
                box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                StaTestHelper.Flush();
                Assert.False(App(viewModel, "grepwin").IsVisible);
                Assert.True(App(viewModel, "tortoisegit").IsVisible);
                Assert.Equal(3, viewModel.VisibleCount);
            }
            finally { window.Close(); }
        });
    }

    private static string AutomationPropertiesName(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);

    [Fact]
    public void EscapeClosesTheWindow()
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var window = new CatalogWindow(Catalog().ViewModel);
            Show(window);
            var close = ById<Wpf.Ui.Controls.Button>(window, "btnCatalogClose");
            Assert.True(close.IsCancel);
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            StaTestHelper.Flush();
            Assert.False(window.IsVisible);
        });
    }
}
