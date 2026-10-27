using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A layout store kept as JSON, so every load returns a new object as the settings file does.</summary>
internal sealed class InMemorySidebarStore : ISidebarLayoutStore
{
    private string? _json;
    public int Saves { get; private set; }
    public bool Refuse { get; set; }

    public InMemorySidebarStore(SidebarLayout? initial = null) =>
        _json = initial is null ? null : JsonSerializer.Serialize(initial);

    public SidebarLayout? Stored => _json is null ? null : JsonSerializer.Deserialize<SidebarLayout>(_json);

    public SidebarLayout? Load() => Stored;

    public bool Save(SidebarLayout layout)
    {
        if (Refuse) return false;
        _json = JsonSerializer.Serialize(layout);
        Saves++;
        return true;
    }
}

internal static class SidebarFixtures
{
    public static SidebarRegistry Registry()
    {
        var registry = new SidebarRegistry();
        registry.Register(new SidebarItemDefinition("home", "Home", "Home24", SidebarItemKind.Page) { PageType = typeof(PackagesPage), IsPinned = true });
        registry.Register(new SidebarItemDefinition("a", "Alpha", "Apps24", SidebarItemKind.Page) { PageType = typeof(DetectionTesterPage) });
        registry.Register(new SidebarItemDefinition("b", "Bravo", "Add24", SidebarItemKind.Page) { PageType = typeof(AddAppPage) });
        registry.Register(new SidebarItemDefinition("w", "Window", "Wrench24", SidebarItemKind.Window) { Activate = _ => { } });
        registry.Register(new SidebarItemDefinition("t", "Tool", "Toolbox24", SidebarItemKind.Tool) { Activate = _ => { } });
        return registry;
    }

    public static string[] Ids(IEnumerable<SidebarItemDefinition> items) => items.Select(i => i.Id).ToArray();
}

public sealed class SidebarRegistryTests
{
    [Fact]
    public void TheDefaultRegistryStartsWithAppPackagerPinnedFirstThenTheAddEntriesAndTheWorkbench()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench], SidebarFixtures.Ids(registry.Items.Take(4)));
        Assert.Equal(["App Packager", "Add from web", "Add from file", "Workbench"], registry.Items.Take(4).Select(i => i.Title));
        Assert.True(registry.Items[0].IsPinned);
        Assert.Equal(typeof(PackagesPage), registry.Items[0].PageType);
        Assert.All(registry.Items.Skip(1), i => Assert.False(i.IsPinned));
        Assert.DoesNotContain(registry.Items, i => i.Title == "Settings");
    }

    [Fact]
    public void TheWorkbenchEntryIsAWindowWithAClickAction()
    {
        var workbench = SidebarRegistry.CreateDefault().Find(SidebarIds.Workbench)!;
        Assert.Equal(SidebarItemKind.Window, workbench.Kind);
        Assert.NotNull(workbench.Activate);
        Assert.Null(workbench.PageType);
    }

    [Fact]
    public void EverySymbolNamesAnExistingIcon()
    {
        foreach (var item in SidebarRegistry.CreateDefault().Items)
            Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(item.Symbol, out _), item.Symbol);
    }

    [Fact]
    public void AToolRegisteredLaterIsAppendedAndFoundWithoutRegardToCase()
    {
        var registry = SidebarRegistry.CreateDefault();
        registry.Register(new SidebarItemDefinition("future-tool", "Future Tool", "Star24", SidebarItemKind.Tool) { Activate = _ => { }, Group = SidebarGroupIds.Tools });
        Assert.Equal("future-tool", registry.Items[^1].Id);
        Assert.Same(registry.Items[^1], registry.Find("Future-Tool"));
        Assert.Null(registry.Find("unknown"));
    }

    [Fact]
    public void RegistrationRefusesARepeatedIdAPageWithoutTypeAndAWindowOrToolWithoutAction()
    {
        var registry = SidebarFixtures.Registry();
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SidebarItemDefinition("A", "Again", "Apps24", SidebarItemKind.Page) { PageType = typeof(AddAppPage) }));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SidebarItemDefinition("p", "Page", "Apps24", SidebarItemKind.Page)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SidebarItemDefinition("x", "Win", "Apps24", SidebarItemKind.Window)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SidebarItemDefinition("y", "Tool", "Apps24", SidebarItemKind.Tool)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SidebarItemDefinition(" ", "Blank", "Apps24", SidebarItemKind.Tool) { Activate = _ => { } }));
    }

    [Fact]
    public void APinnedEntryCannotBeRegisteredAfterAnEntryThatCanMove()
    {
        var registry = SidebarFixtures.Registry();
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new SidebarItemDefinition("late", "Late", "Apps24", SidebarItemKind.Tool) { Activate = _ => { }, IsPinned = true }));
    }
}

public sealed class SidebarServiceTests
{
    private static (SidebarService Service, InMemorySidebarStore Store) Create(SidebarLayout? layout = null)
    {
        var store = new InMemorySidebarStore(layout);
        return (new SidebarService(SidebarFixtures.Registry(), store), store);
    }

    [Fact]
    public void WithoutASavedLayoutTheOrderIsTheRegistryOrderAndNothingIsHidden()
    {
        var (service, store) = Create();
        Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(service.Items));
        Assert.Empty(service.Hidden);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void ALayoutWithNullListsLoadsAsTheDefault()
    {
        var (service, _) = Create(new SidebarLayout { Order = null, Hidden = null });
        Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(service.Items));
        Assert.Empty(service.Hidden);
    }

    [Fact]
    public void TheSavedOrderAndHiddenListApplyAndUnknownIdsAreIgnored()
    {
        var (service, store) = Create(new SidebarLayout { Order = ["t", "gone", "B", "a", "w", "t"], Hidden = ["W", "gone"] });
        Assert.Equal(["home", "t", "b", "a"], SidebarFixtures.Ids(service.Items));
        Assert.Equal(["w"], SidebarFixtures.Ids(service.Hidden));
        Assert.Equal(["home", "t", "b", "a", "w"], SidebarFixtures.Ids(service.All));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void AnIdMissingFromTheSavedOrderIsAppendedAtTheEndAndShown()
    {
        var (service, _) = Create(new SidebarLayout { Order = ["b", "a"], Hidden = [] });
        Assert.Equal(["home", "b", "a", "w", "t"], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void ThePinnedEntryStaysFirstAndShownWhateverTheSavedLayout()
    {
        var (service, _) = Create(new SidebarLayout { Order = ["a", "b", "home"], Hidden = ["home"] });
        Assert.Equal("home", service.Items[0].Id);
        Assert.Empty(service.Hidden);
    }

    [Fact]
    public void MoveReordersPersistsAndRaisesChanged()
    {
        var (service, store) = Create();
        var events = new List<bool>();
        service.Changed += (_, e) => events.Add(e.Saved);
        Assert.True(service.Move("t", 1));
        Assert.Equal(["home", "t", "a", "b", "w"], SidebarFixtures.Ids(service.Items));
        Assert.Equal(["home", "t", "a", "b", "w"], store.Stored!.Order!);
        Assert.Equal([true], events);
    }

    [Fact]
    public void MoveClampsBelowThePinnedEntryAndPastTheEnd()
    {
        var (service, _) = Create();
        Assert.True(service.Move("b", 0));
        Assert.Equal(["home", "b", "a", "w", "t"], SidebarFixtures.Ids(service.Items));
        Assert.True(service.Move("b", 99));
        Assert.Equal(["home", "a", "w", "t", "b"], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void MoveOfThePinnedEntryAnUnknownIdOrToTheSamePlaceDoesNothing()
    {
        var (service, store) = Create();
        var changed = 0;
        service.Changed += (_, _) => changed++;
        Assert.False(service.Move("home", 3));
        Assert.False(service.Move("nope", 1));
        Assert.False(service.Move("a", 1));
        Assert.False(service.Move("a", 0));
        Assert.Equal(0, changed);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void MoveVisibleCountsOnlyShownEntriesAndKeepsHiddenOnesInTheOrder()
    {
        var (service, _) = Create(new SidebarLayout { Order = ["a", "b", "w", "t"], Hidden = ["b"] });
        Assert.Equal(["home", "a", "w", "t"], SidebarFixtures.Ids(service.Items));
        Assert.True(service.MoveVisible("t", 1));
        Assert.Equal(["home", "t", "a", "w"], SidebarFixtures.Ids(service.Items));
        Assert.True(service.MoveVisible("a", 3));
        Assert.Equal(["home", "t", "w", "a"], SidebarFixtures.Ids(service.Items));
        Assert.Contains("b", SidebarFixtures.Ids(service.All));
        Assert.False(service.MoveVisible("b", 1));
        Assert.False(service.MoveVisible("home", 2));
        Assert.True(service.MoveVisible("w", 0));
        Assert.Equal(["home", "w", "t", "a"], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void HideRemovesTheEntryAndShowReturnsItToItsSavedPlace()
    {
        var (service, store) = Create();
        Assert.True(service.Hide("b"));
        Assert.Equal(["home", "a", "w", "t"], SidebarFixtures.Ids(service.Items));
        Assert.Equal(["b"], store.Stored!.Hidden!);
        Assert.True(service.IsHidden("B"));

        Assert.True(service.Move("t", 1));
        Assert.True(service.Show("b"));
        Assert.Equal(["home", "t", "a", "b", "w"], SidebarFixtures.Ids(service.Items));
        Assert.Empty(store.Stored!.Hidden!);
    }

    [Fact]
    public void HideOfThePinnedEntryAnUnknownIdOrAHiddenEntryReturnsFalse()
    {
        var (service, store) = Create();
        var changed = 0;
        service.Changed += (_, _) => changed++;
        Assert.False(service.Hide("home"));
        Assert.False(service.Hide("nope"));
        Assert.True(service.Hide("a"));
        Assert.False(service.Hide("a"));
        Assert.Equal(1, changed);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void ShowOfAShownOrUnknownEntryReturnsFalse()
    {
        var (service, _) = Create();
        Assert.False(service.Show("a"));
        Assert.False(service.Show("nope"));
        Assert.False(service.Show("home"));
    }

    [Fact]
    public void ResetRestoresTheRegistryOrderShowsEverythingAndSaves()
    {
        var (service, store) = Create(new SidebarLayout { Order = ["t", "w", "b", "a"], Hidden = ["a", "w"] });
        var changed = 0;
        service.Changed += (_, _) => changed++;
        service.Reset();
        Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(service.Items));
        Assert.Empty(service.Hidden);
        Assert.Equal(["home", "a", "b", "w", "t"], store.Stored!.Order!);
        Assert.Empty(store.Stored!.Hidden!);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ARefusedSaveKeepsTheChangeForTheSessionAndReportsItUnsaved()
    {
        var (service, store) = Create();
        store.Refuse = true;
        var events = new List<bool>();
        service.Changed += (_, e) => events.Add(e.Saved);
        Assert.True(service.Hide("a"));
        Assert.True(service.IsHidden("a"));
        Assert.Equal([false], events);
        Assert.Null(store.Stored);
    }

    [Fact]
    public void ANewServiceOnTheSameStoreLoadsTheSavedLayout()
    {
        var (service, store) = Create();
        service.Move("w", 1);
        service.Hide("b");
        var reloaded = new SidebarService(SidebarFixtures.Registry(), store);
        Assert.Equal(["home", "w", "a", "t"], SidebarFixtures.Ids(reloaded.Items));
        Assert.Equal(["b"], SidebarFixtures.Ids(reloaded.Hidden));
    }

    [Fact]
    public void AToolAddedToTheRegistryAfterASaveAppearsAtTheEndShown()
    {
        var (service, store) = Create();
        service.Move("t", 1);
        var registry = SidebarFixtures.Registry();
        registry.Register(new SidebarItemDefinition("new", "New tool", "Beaker24", SidebarItemKind.Tool) { Activate = _ => { } });
        var reloaded = new SidebarService(registry, store);
        Assert.Equal(["home", "t", "a", "b", "w", "new"], SidebarFixtures.Ids(reloaded.Items));
    }
}

public sealed class SidebarDropMathTests
{
    [Theory]
    [InlineData(-5, 1)]
    [InlineData(10, 1)]
    [InlineData(25, 1)]
    [InlineData(61, 2)]
    [InlineData(99, 2)]
    [InlineData(101, 3)]
    [InlineData(500, 3)]
    public void TheInsertionGapFollowsTheItemMidpointsAndNeverPrecedesThePinnedEntries(double y, int expected)
    {
        Assert.Equal(expected, SidebarDropMath.InsertionIndex([20, 60, 100], y, minimum: 1));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 3, 2)]
    [InlineData(3, 1, 1)]
    public void TheFinalIndexAllowsForTheDraggedEntryLeavingItsPlace(int source, int insertion, int expected)
    {
        Assert.Equal(expected, SidebarDropMath.FinalIndex(source, insertion));
    }

    [Theory]
    [InlineData(3, 3, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    [InlineData(-3.9, 3.9, false)]
    public void ADragStartsOnlyPastTheSystemThreshold(double dx, double dy, bool expected)
    {
        Assert.Equal(expected, SidebarDropMath.PastThreshold(dx, dy, 4, 4));
    }
}

public sealed class SidebarSettingsEditorTests
{
    private static (SidebarSettingsEditor Editor, SidebarService Service) Create(SidebarLayout? layout = null)
    {
        var service = new SidebarService(SidebarFixtures.Registry(), new InMemorySidebarStore(layout));
        return (new SidebarSettingsEditor(service), service);
    }

    [Fact]
    public void EveryEntryHasARowInTheSidebarOrderWithItsShownState()
    {
        var (editor, _) = Create(new SidebarLayout { Order = ["b", "a", "w", "t"], Hidden = ["a"] });
        Assert.Equal(["home", "b", "a", "w", "t"], editor.Rows.Select(r => r.Id));
        Assert.Equal([true, true, false, true, true], editor.Rows.Select(r => r.Shown));
        Assert.Equal("Bravo", editor.Rows[1].Title);
    }

    [Fact]
    public void ThePinnedRowCannotBeHiddenOrMovedAndTheRowBelowItCannotMoveUp()
    {
        var (editor, _) = Create();
        var home = editor.Rows[0];
        Assert.False(home.CanHide);
        Assert.False(home.CanMoveUp);
        Assert.False(home.CanMoveDown);
        Assert.False(editor.Rows[1].CanMoveUp);
        Assert.True(editor.Rows[1].CanMoveDown);
        Assert.False(editor.Rows[^1].CanMoveDown);
        Assert.True(editor.Rows[^1].CanHide);
    }

    [Fact]
    public void ClearingAndSettingShownHidesAndShowsTheEntryInTheService()
    {
        var (editor, service) = Create();
        editor.Rows[2].Shown = false;
        Assert.True(service.IsHidden("b"));
        editor.Rows[2].Shown = true;
        Assert.False(service.IsHidden("b"));
    }

    [Fact]
    public void ClearingShownOnThePinnedRowLeavesItShown()
    {
        var (editor, service) = Create();
        editor.Rows[0].Shown = false;
        Assert.Empty(service.Hidden);
        Assert.True(editor.Rows[0].Shown);
    }

    [Fact]
    public void UpAndDownMoveTheEntryOnePlaceInTheService()
    {
        var (editor, service) = Create();
        var bravo = editor.Rows[2];
        bravo.MoveUpCommand.Execute(null);
        Assert.Equal(["home", "b", "a", "w", "t"], SidebarFixtures.Ids(service.All));
        Assert.Same(bravo, editor.Rows[1]);
        bravo.MoveDownCommand.Execute(null);
        bravo.MoveDownCommand.Execute(null);
        Assert.Equal(["home", "a", "w", "b", "t"], SidebarFixtures.Ids(service.All));
        Assert.Equal(["home", "a", "w", "b", "t"], editor.Rows.Select(r => r.Id));
    }

    [Fact]
    public void UpAndDownOnAShownRowStepOverTheHiddenRowsBetween()
    {
        var (editor, service) = Create(new SidebarLayout { Order = ["a", "b", "w", "t"], Hidden = ["b"] });
        var window = editor.Rows.Single(r => r.Id == "w");
        window.MoveUpCommand.Execute(null);
        Assert.Equal(["home", "w", "a", "b", "t"], SidebarFixtures.Ids(service.All));
        Assert.Equal(["home", "w", "a", "t"], SidebarFixtures.Ids(service.Items));

        var alpha = editor.Rows.Single(r => r.Id == "a");
        alpha.MoveDownCommand.Execute(null);
        Assert.Equal(["home", "w", "b", "t", "a"], SidebarFixtures.Ids(service.All));
        Assert.Equal(["home", "w", "t", "a"], SidebarFixtures.Ids(service.Items));
        Assert.Equal(["home", "w", "b", "t", "a"], editor.Rows.Select(r => r.Id));
    }

    [Fact]
    public void UpOnAShownRowWithOnlyHiddenRowsAboveMovesItBelowThePinnedRow()
    {
        var (editor, service) = Create(new SidebarLayout { Order = ["a", "b", "w", "t"], Hidden = ["a", "b"] });
        editor.Rows.Single(r => r.Id == "w").MoveUpCommand.Execute(null);
        Assert.Equal(["home", "w", "a", "b", "t"], SidebarFixtures.Ids(service.All));
        editor.Rows.Single(r => r.Id == "t").MoveDownCommand.Execute(null);
        Assert.Equal(["home", "w", "a", "b", "t"], SidebarFixtures.Ids(service.All));
    }

    [Fact]
    public void DownOnAShownRowWithOnlyHiddenRowsBelowMovesItToTheEnd()
    {
        var (editor, service) = Create(new SidebarLayout { Order = ["a", "b", "w", "t"], Hidden = ["w", "t"] });
        editor.Rows.Single(r => r.Id == "b").MoveDownCommand.Execute(null);
        Assert.Equal(["home", "a", "w", "t", "b"], SidebarFixtures.Ids(service.All));
    }

    [Fact]
    public void UpAndDownOnAHiddenRowMoveItOnePlace()
    {
        var (editor, service) = Create(new SidebarLayout { Order = ["a", "b", "w", "t"], Hidden = ["w"] });
        var window = editor.Rows.Single(r => r.Id == "w");
        window.MoveUpCommand.Execute(null);
        Assert.Equal(["home", "a", "w", "b", "t"], SidebarFixtures.Ids(service.All));
        window.MoveDownCommand.Execute(null);
        window.MoveDownCommand.Execute(null);
        Assert.Equal(["home", "a", "b", "t", "w"], SidebarFixtures.Ids(service.All));
        Assert.True(service.IsHidden("w"));
    }

    [Fact]
    public void AChangeFromTheSidebarUpdatesTheRowsInPlace()
    {
        var (editor, service) = Create();
        var rows = editor.Rows.ToList();
        service.Hide("w");
        service.Move("t", 1);
        Assert.Equal(["home", "t", "a", "b", "w"], editor.Rows.Select(r => r.Id));
        Assert.False(editor.Rows.Single(r => r.Id == "w").Shown);
        Assert.All(editor.Rows, r => Assert.Contains(r, rows));
    }

    [Fact]
    public void ResetRestoresTheDefaultOrderAndShowsEveryRow()
    {
        var (editor, service) = Create(new SidebarLayout { Order = ["t", "w"], Hidden = ["a"] });
        editor.ResetCommand.Execute(null);
        Assert.Equal(["home", "a", "b", "w", "t"], editor.Rows.Select(r => r.Id));
        Assert.All(editor.Rows, r => Assert.True(r.Shown));
        Assert.Empty(service.Hidden);
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SidebarPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-sidebar-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public SidebarPersistenceTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, json);
    }

    [Fact]
    public void TheLayoutSurvivesASaveAndANewProcess()
    {
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        Assert.True(service.Move(SidebarIds.SiteHygiene, 4));
        Assert.True(service.Hide(SidebarIds.DetectionTester));

        var reloaded = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.SiteHygiene], SidebarFixtures.Ids(reloaded.Items.Take(5)));
        Assert.DoesNotContain(reloaded.Items, i => i.Id == SidebarIds.DetectionTester);
        Assert.Equal([SidebarIds.DetectionTester], SidebarFixtures.Ids(reloaded.Hidden));
        var stored = JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!["Sidebar"]!;
        Assert.Equal(SidebarIds.DetectionTester, (string?)stored["Hidden"]![0]);
    }

    [Fact]
    public void AFileFromTheCurrentReleaseLoadsWithoutALayoutAndGivesTheDefaultSidebar()
    {
        WriteSettings("""{ "SiteCode": "PS1", "CompanyName": "Contoso", "PaneOpen": false }""");
        var settings = new SettingsService();
        Assert.Null(settings.Load().Sidebar);
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(settings));
        Assert.Equal(SidebarFixtures.Ids(SidebarRegistry.CreateDefault().Items), SidebarFixtures.Ids(service.Items));
        Assert.Equal("""{ "SiteCode": "PS1", "CompanyName": "Contoso", "PaneOpen": false }""", File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public void ANullLayoutOrNullListsLoad()
    {
        WriteSettings("""{ "Sidebar": null }""");
        Assert.Null(new SettingsService().Load().Sidebar);
        WriteSettings("""{ "Sidebar": { "Order": null, "Hidden": null } }""");
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        Assert.Equal(SidebarRegistry.CreateDefault().Items.Count, service.Items.Count);
    }

    [Fact]
    public void ASidebarSaveKeepsEveryOtherStoredValue()
    {
        WriteSettings("""{ "SiteCode": "PS1", "SiteCodeSaved": true, "CompanyName": "Contoso", "HiddenApplications": ["git"] }""");
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        service.Hide(SidebarIds.Workbench);
        var loaded = new SettingsService().Load();
        Assert.Equal("PS1", loaded.SiteCode);
        Assert.Equal("Contoso", loaded.CompanyName);
        Assert.Equal(["git"], loaded.HiddenApplications);
        Assert.Equal([SidebarIds.Workbench], loaded.Sidebar!.Hidden!);
    }

    [Fact]
    public void ASidebarSaveDoesNotStoreTheLauncherSiteCode()
    {
        WriteSettings("""{ "CompanyName": "Contoso" }""");
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "LCH");
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        service.Hide(SidebarIds.Workbench);
        var stored = JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!.AsObject();
        Assert.NotEqual("LCH", (string?)stored["SiteCode"]);
        Assert.False(stored.ContainsKey("SiteCodeSaved") && (bool?)stored["SiteCodeSaved"] == true);
    }

    [Fact]
    public void AnUnreadableFileIsNotReplacedAndTheChangeIsReportedUnsaved()
    {
        WriteSettings("{ not json");
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        var saved = new List<bool>();
        service.Changed += (_, e) => saved.Add(e.Saved);
        Assert.True(service.Hide(SidebarIds.Workbench));
        Assert.Equal([false], saved);
        Assert.Equal("{ not json", File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public void ASaveOfAnotherSettingAfterASidebarChangeKeepsTheLayout()
    {
        var settings = new SettingsService();
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(settings));
        service.Move(SidebarIds.SiteHygiene, 4);
        var s = settings.Load();
        s.CompanyName = "Fabrikam";
        settings.SaveOperatorEdit(s);
        var reloaded = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        Assert.Equal(SidebarIds.SiteHygiene, reloaded.Items[4].Id);
    }
}
