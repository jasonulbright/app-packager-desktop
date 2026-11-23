using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using TextBlock = System.Windows.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using CheckBox = System.Windows.Controls.CheckBox;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Import settings window and its view model (Q-112e): the checklist, the preview before the commit, the conflict choice, and the
/// footer menu rules. Real window in both themes at two sizes. Site codes are fictional (AB1 target, CD2 source); hosts are under example.com.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ImportSiteSettingsWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-importwin-" + Guid.NewGuid().ToString("N"));

    public ImportSiteSettingsWindowTests()
    {
        SiteCatalog.Create("AB1", "cm.ab1.example.com", "Target", _root);
        SiteCatalog.Create("CD2", "cm.cd2.example.com", "Source", _root);
        Directory.CreateDirectory(SiteDataPaths.GetSiteFolder("CD2", dataRoot: _root));
        File.WriteAllText(Path.Combine(SiteDataPaths.GetSiteFolder("CD2", dataRoot: _root), "settings.json"), """
        {
          "ProviderMachine": "cm.cd2.example.com", "FileShareRoot": "\\\\files.cd2.example.com\\content$",
          "ContentDistribution": { "DpGroupName": "CD2 DPs", "TestCollectionName": "CD2 Test" },
          "SiteHygiene": { "PauseDays": 3 }
        }
        """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(SiteDataPaths.GetSiteFolder("AB1", dataRoot: _root), "settings.json"),
            """{ "ContentDistribution": { "DpGroupName": "AB1 DPs" } }""", new UTF8Encoding(false));
    }

    public void Dispose() => TestCleanup.DeleteDirectory(_root);

    private string TargetFile => Path.Combine(SiteDataPaths.GetSiteFolder("AB1", dataRoot: _root), "settings.json");

    private ImportSiteSettingsViewModel NewViewModel() =>
        new(SiteCatalog.List(_root).Where(s => s.FolderName == "CD2").ToList(), "AB1", _root);

    public static TheoryData<ApplicationTheme, int, int> Cases()
    {
        var data = new TheoryData<ApplicationTheme, int, int>();
        foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            foreach (var (width, height) in new[] { (1440, 900), (1024, 720) })
                data.Add(theme, width, height);
        return data;
    }

    // ---- view model ----

    [Fact]
    public void TheChecklistStartsWithTheDefaultsTickedAndThePreviewShowsTheConflictWithKeepAsTheDefault()
    {
        var vm = NewViewModel();
        Assert.Equal("AB1 - Target (cm.ab1.example.com)", vm.TargetText);
        Assert.Equal("CD2", vm.SelectedSource!.FolderName);
        Assert.Equal(SiteSettingsImport.Items.Count, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal(r.Item.DefaultOn, r.Selected));
        var cd = vm.Rows.Single(r => r.Id == "apd.content-distribution");
        Assert.True(cd.IsConflict);
        Assert.Equal(0, cd.ChoiceIndex);
        Assert.Equal("Keeps the target value", cd.PlanText);
        Assert.Equal("Will copy", vm.Rows.Single(r => r.Id == "apd.hygiene-options").PlanText);
        Assert.Equal("Not ticked", vm.Rows.Single(r => r.Id == "apd.share-root").PlanText);
        Assert.True(vm.CanImport);
        Assert.Contains("The source site is not changed", vm.Summary);
    }

    [Fact]
    public void ThePreviewWritesNothingAndATickOrAChoiceChangesTheSummary()
    {
        var before = File.ReadAllText(TargetFile);
        var vm = NewViewModel();
        var summary = vm.Summary;
        vm.Rows.Single(r => r.Id == "apd.share-root").Selected = true;
        Assert.NotEqual(summary, vm.Summary);
        Assert.Equal("Will copy", vm.Rows.Single(r => r.Id == "apd.share-root").PlanText);
        vm.Rows.Single(r => r.Id == "apd.content-distribution").ChoiceIndex = 1;
        Assert.Equal("Will replace", vm.Rows.Single(r => r.Id == "apd.content-distribution").PlanText);
        Assert.Equal(before, File.ReadAllText(TargetFile));
        Assert.Null(vm.Result);
    }

    [Fact]
    public void NothingTickedOrNothingToCopyDisablesImport()
    {
        var vm = NewViewModel();
        foreach (var row in vm.Rows) row.Selected = false;
        Assert.False(vm.CanImport);
        Assert.Contains("Nothing is ticked", vm.Summary);
    }

    [Fact]
    public void ImportWritesTheTickedValuesKeepsTheConflictAndReportsIt()
    {
        var vm = NewViewModel();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.ImportCommand.Execute(null);

        Assert.True(closed);
        Assert.NotNull(vm.Result);
        var t = JsonNode.Parse(File.ReadAllText(TargetFile))!.AsObject();
        Assert.Equal("AB1 DPs", t["ContentDistribution"]!["DpGroupName"]!.GetValue<string>());
        Assert.Equal(3, t["SiteHygiene"]!["PauseDays"]!.GetValue<int>());
        Assert.Null(t["FileShareRoot"]);
        var message = ImportSiteSettingsViewModel.ResultMessage(vm.Result!, "AB1");
        Assert.Contains("Imported from site CD2 into site AB1", message);
        Assert.Contains("1 kept", message);
        Assert.Contains("The source site is unchanged", message);
    }

    [Fact]
    public void CancelWritesNothingAndLeavesNoResult()
    {
        var before = File.ReadAllText(TargetFile);
        var vm = NewViewModel();
        vm.CancelCommand.Execute(null);
        Assert.Null(vm.Result);
        Assert.Equal(before, File.ReadAllText(TargetFile));
        Assert.False(File.Exists(Path.Combine(SiteDataPaths.GetSiteFolder("AB1", dataRoot: _root), SiteSettingsImport.LogFileName)));
    }

    [Fact]
    public void ADamagedSourceShowsTheReasonAndDisablesImport()
    {
        File.WriteAllText(Path.Combine(SiteDataPaths.GetSiteFolder("CD2", dataRoot: _root), "settings.json"), "{ not json");
        var vm = NewViewModel();
        Assert.False(vm.CanImport);
        Assert.Contains("cannot be read", vm.ValidationMessage);
    }

    // ---- the footer menu rules ----

    [Fact]
    public void ImportIsRefusedWhileARunIsActiveWithoutAnActiveSiteOrWithNoOtherSite()
    {
        var running = false;
        using var scope = SiteContext.Scope("AB1", _root);
        var manager = new SiteManager(() => running ? "a check is running" : null, _root);
        Assert.Null(manager.ImportRefusal());
        Assert.Equal(new[] { "CD2" }, manager.ImportSources().Select(s => s.FolderName));
        running = true;
        Assert.Contains("a check is running", manager.ImportRefusal());
        running = false;
        using (SiteContext.Scope(null, _root))
            Assert.Contains("No site is active", new SiteManager(() => null, _root).ImportRefusal());
        Directory.Delete(SiteDataPaths.GetSiteFolder("CD2", dataRoot: _root), recursive: true);
        Assert.Contains("no other site", manager.ImportRefusal());
    }

    [Fact]
    public void TheManagerRaisesChangedWhenTheSettingsOfTheActiveSiteChange()
    {
        var manager = new SiteManager(() => null, _root);
        var raised = 0;
        manager.Changed += () => raised++;
        manager.NotifyActiveSiteSettingsChanged();
        Assert.Equal(1, raised);
    }

    // ---- the window ----

    private static Rect Bounds(FrameworkElement element, System.Windows.Media.Visual root) =>
        element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheWindowShowsEveryControlWholeWithATooltipAndTheChecklistRows(ApplicationTheme theme, int width, int height)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var vm = NewViewModel();
            var window = new ImportSiteSettingsWindow(vm) { Width = width - 200, Height = height - 120 };
            try
            {
                RenderKit.Show(window);
                var root = (FrameworkElement)window.Content;
                var where = $"{theme} {width}x{height}";

                var source = RenderKit.ById<ComboBox>(root, "cmbImportSource");
                var target = RenderKit.ById<TextBlock>(root, "txtImportTarget");
                var grid = RenderKit.ById<DataGrid>(root, "dgImportItems");
                var import = RenderKit.ById<Wpf.Ui.Controls.Button>(root, "btnImportSettings");
                var cancel = RenderKit.ById<Wpf.Ui.Controls.Button>(root, "btnImportCancel");
                var summary = RenderKit.ById<TextBlock>(root, "txtImportSummary");
                var never = RenderKit.ById<TextBlock>(root, "txtImportNever");

                foreach (FrameworkElement control in new FrameworkElement[] { source, target, grid, import, cancel, summary, never })
                {
                    var bounds = Bounds(control, root);
                    Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0, $"{where}: {control.Name} has no size.");
                    Assert.True(bounds.Left >= -0.5 && bounds.Right <= root.ActualWidth + 0.5 && bounds.Bottom <= root.ActualHeight + 0.5, $"{where}: {control.Name} is outside the window ({bounds}).");
                }
                Assert.False(string.IsNullOrWhiteSpace(source.ToolTip as string));
                Assert.False(string.IsNullOrWhiteSpace(import.ToolTip as string));
                Assert.False(string.IsNullOrWhiteSpace(cancel.ToolTip as string));
                Assert.Equal("AB1 - Target (cm.ab1.example.com)", target.Text);
                Assert.Equal(SiteSettingsImport.Items.Count, grid.Items.Count);
                Assert.True(Bounds(grid, root).Bottom <= Bounds(never, root).Top + 0.5, $"{where}: the grid overlaps the note.");
                Assert.True(Bounds(never, root).Bottom <= Bounds(import, root).Top + 0.5, $"{where}: the note overlaps the buttons.");
                Assert.True(Bounds(import, root).Right <= Bounds(cancel, root).Left + 0.5 || Bounds(cancel, root).Right <= Bounds(import, root).Left + 0.5, $"{where}: Import and Cancel overlap.");
                Assert.True(Bounds(summary, root).Right <= Bounds(import, root).Left + 0.5 || Bounds(summary, root).Bottom <= Bounds(import, root).Top + 0.5, $"{where}: the summary overlaps Import.");
                Assert.True(import.IsEnabled);

                // Every check box and every conflict box of a realized row has a tooltip, and only a conflict row can choose.
                var checks = RenderKit.Descendants(grid).OfType<CheckBox>().ToList();
                Assert.NotEmpty(checks);
                Assert.All(checks, c => Assert.False(string.IsNullOrWhiteSpace(c.ToolTip as string), $"{where}: a check box has no tooltip."));
                var combos = RenderKit.Descendants(grid).OfType<ComboBox>().ToList();
                Assert.NotEmpty(combos);
                Assert.All(combos, c => Assert.False(string.IsNullOrWhiteSpace(c.ToolTip as string), $"{where}: a conflict box has no tooltip."));
                Assert.Single(combos.Where(c => c.IsEnabled));

                // A tick refreshes the preview in the window.
                var box = checks.First(c => (string)System.Windows.Automation.AutomationProperties.GetName(c) == "File share root");
                box.IsChecked = true;
                RenderKit.Settle(window);
                Assert.Equal("Will copy", vm.Rows.Single(r => r.Id == "apd.share-root").PlanText);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void TheCommitButtonWritesAndClosesTheWindow()
    {
        RenderKit.WithApplication(ApplicationTheme.Light, () =>
        {
            var vm = NewViewModel();
            var window = new ImportSiteSettingsWindow(vm);
            RenderKit.Show(window);
            try
            {
                var import = RenderKit.ById<Wpf.Ui.Controls.Button>((FrameworkElement)window.Content, "btnImportSettings");
                import.Command!.Execute(null);
                Assert.NotNull(vm.Result);
                Assert.Equal(SiteImportReport.Done, vm.Result!.Outcome);
                Assert.True(File.ReadAllText(TargetFile).Contains("\"PauseDays\"", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        });
    }
}
