using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;

namespace ApplicationPackagerEngine.Tests;

internal static class SidebarGroupFixtures
{
    public static readonly SidebarGroup Main = new(Id: "main", Caption: "MAIN", AutomationName: "Main group");
    public static readonly SidebarGroup More = new(Id: "more", Caption: "MORE", AutomationName: "More group");

    public static SidebarRegistry Registry()
    {
        var registry = new SidebarRegistry();
        registry.RegisterGroup(Main);
        registry.RegisterGroup(More);
        registry.Register(new SidebarItemDefinition(Id: "home", Title: "Home", Symbol: "Home24", Kind: SidebarItemKind.Page) { PageType = typeof(PackagesPage), IsPinned = true, Group = Main.Id });
        registry.Register(new SidebarItemDefinition(Id: "a", Title: "Alpha", Symbol: "Apps24", Kind: SidebarItemKind.Page) { PageType = typeof(InstallerAnalysisPage), Group = Main.Id });
        registry.Register(new SidebarItemDefinition(Id: "b", Title: "Bravo", Symbol: "Add24", Kind: SidebarItemKind.Page) { PageType = typeof(AddAppPage), Group = Main.Id });
        registry.Register(new SidebarItemDefinition(Id: "w", Title: "Window", Symbol: "Wrench24", Kind: SidebarItemKind.Window) { Activate = _ => { }, Group = More.Id });
        registry.Register(new SidebarItemDefinition(Id: "t", Title: "Tool", Symbol: "Toolbox24", Kind: SidebarItemKind.Tool) { Activate = _ => { }, Group = More.Id });
        registry.Register(new SidebarItemDefinition(Id: "x", Title: "Xray", Symbol: "Beaker24", Kind: SidebarItemKind.Page) { PageType = typeof(DetectionTesterPage), Group = More.Id });
        return registry;
    }

    public static SidebarService Service(SidebarLayout? layout = null) => new(Registry(), new InMemorySidebarStore(layout));
}

public sealed class SidebarGroupRegistryTests
{
    private static readonly string[] ToolIds =
    [
        SidebarIds.InstallerAnalysis, SidebarIds.DetectionTester, SidebarIds.DeploymentHelper, SidebarIds.PivotsScripts,
        SidebarIds.CollectionCompliance, SidebarIds.MaintenanceWindows, SidebarIds.DpContentManager, SidebarIds.SiteHygiene
    ];

    [Fact]
    public void TheDefaultRegistryHasOneCaptionedGroupTools()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarGroupIds.Tools], registry.Groups.Select(g => g.Id));
        Assert.Equal(["TOOLS"], registry.Groups.Select(g => g.Caption));
        Assert.Equal(["Tools group"], registry.Groups.Select(g => g.AutomationName));
        Assert.All(registry.Groups, g => Assert.Equal(g.Caption.ToUpperInvariant(), g.Caption));
    }

    [Fact]
    public void TheFirstGroupHasNoCaptionAndHoldsAppPackagerTheAddEntriesAndTheWorkbenchInThatOrder()
    {
        var registry = SidebarRegistry.CreateDefault();
        var first = registry.Items.Where(i => i.Group is null).ToList();
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench], SidebarFixtures.Ids(first));
        Assert.Equal(["App Packager", "Add from web", "Add from file", "Workbench"], first.Select(i => i.Title));
        Assert.Equal(first, registry.Items.Take(4));
        Assert.True(first[0].IsPinned);
        Assert.Equal(typeof(PackagesPage), first[0].PageType);
        Assert.Equal(typeof(AddAppPage), first[1].PageType);
        Assert.Equal(typeof(AddFilePage), first[2].PageType);
        Assert.Equal("addfile", first[2].Id);
        Assert.Equal(SidebarItemKind.Window, first[3].Kind);
    }

    [Fact]
    public void TheToolsGroupHoldsTheEightToolsInTheirOrder()
    {
        var registry = SidebarRegistry.CreateDefault();
        var tools = registry.Items.Where(i => i.Group == SidebarGroupIds.Tools).ToList();
        Assert.Equal(ToolIds, SidebarFixtures.Ids(tools));
        Assert.Equal(
        [
            "Installer Analysis", "Detection Tester", "Deployment Helper", "Pivots and Scripts",
            "Collection and Compliance", "Maintenance Windows", "DP Content Manager", "Site Hygiene"
        ], tools.Select(i => i.Title));
        Assert.Equal(tools, registry.Items.Skip(4));
    }

    [Fact]
    public void TheApplicationsEntryIsNotInTheSidebar()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Null(registry.Find(SidebarIds.Applications));
        Assert.Null(typeof(PackagesPage).Assembly.GetType("ApplicationPackagerUI.Views.Pages.ApplicationsPage"));
        Assert.DoesNotContain(registry.Items, i => i.Title is "Applications" or "Add App" or "Catalog" or "Packages" or "Application Workbench");
    }

    [Fact]
    public void RegistrationRefusesAnUnknownGroupAGroupSplitByAnotherGroupAndARepeatedGroup()
    {
        var registry = SidebarGroupFixtures.Registry();
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new SidebarItemDefinition(Id: "u", Title: "Unknown", Symbol: "Apps24", Kind: SidebarItemKind.Tool) { Activate = _ => { }, Group = "nope" }));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new SidebarItemDefinition(Id: "late", Title: "Late", Symbol: "Apps24", Kind: SidebarItemKind.Tool) { Activate = _ => { }, Group = "MAIN" }));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterGroup(new SidebarGroup(Id: "More", Caption: "AGAIN", AutomationName: "Again group")));
        registry.Register(new SidebarItemDefinition(Id: "y", Title: "Yankee", Symbol: "Apps24", Kind: SidebarItemKind.Tool) { Activate = _ => { }, Group = " More " });
        Assert.Equal("y", registry.Items[^1].Id);
        var service = new SidebarService(registry, new InMemorySidebarStore(new SidebarLayout { Order = ["y", "w"], Hidden = [] }));
        Assert.Equal(["home", "a", "b", "y", "w", "t", "x"], SidebarFixtures.Ids(service.All));
        Assert.False(service.Move("y", 1));
    }
}

public sealed class SidebarGroupServiceTests
{
    [Fact]
    public void MoveKeepsTheEntryInsideItsGroup()
    {
        var service = SidebarGroupFixtures.Service();
        Assert.True(service.Move("x", 1));
        Assert.Equal(["home", "a", "b", "x", "w", "t"], SidebarFixtures.Ids(service.All));
        Assert.True(service.Move("a", 99));
        Assert.Equal(["home", "b", "a", "x", "w", "t"], SidebarFixtures.Ids(service.All));
        Assert.False(service.Move("a", 5));
        Assert.False(service.Move("x", 0));
    }

    [Fact]
    public void MoveVisibleKeepsTheEntryInsideItsGroup()
    {
        var service = SidebarGroupFixtures.Service();
        Assert.True(service.MoveVisible("t", 1));
        Assert.Equal(["home", "a", "b", "t", "w", "x"], SidebarFixtures.Ids(service.Items));
        Assert.True(service.MoveVisible("a", 99));
        Assert.Equal(["home", "b", "a", "t", "w", "x"], SidebarFixtures.Ids(service.Items));
        Assert.False(service.MoveVisible("t", 2));
        Assert.False(service.MoveVisible("a", 3));
    }

    [Fact]
    public void AMoveDownToTheEndOfAGroupStaysBeforeAHiddenEntryOfTheNextGroup()
    {
        var service = SidebarGroupFixtures.Service(new SidebarLayout { Order = [], Hidden = ["w"] });
        Assert.True(service.MoveVisible("a", 2));
        Assert.Equal(["home", "b", "a", "w", "t", "x"], SidebarFixtures.Ids(service.All));
        Assert.True(service.Show("w"));
        Assert.Equal(["home", "b", "a", "w", "t", "x"], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void AFlatSavedOrderLoadsWithEachGroupInItsSavedRelativeOrder()
    {
        var service = SidebarGroupFixtures.Service(new SidebarLayout { Order = ["t", "x", "gone", "b", "w", "home", "a", "t"], Hidden = ["x", "gone"] });
        Assert.Equal(["home", "b", "a", "t", "x", "w"], SidebarFixtures.Ids(service.All));
        Assert.Equal(["x"], SidebarFixtures.Ids(service.Hidden));
    }

    [Fact]
    public void AnIdMissingFromTheSavedOrderIsAppendedToTheEndOfItsGroup()
    {
        var service = SidebarGroupFixtures.Service(new SidebarLayout { Order = ["x", "b"], Hidden = [] });
        Assert.Equal(["home", "b", "a", "x", "w", "t"], SidebarFixtures.Ids(service.All));
    }

    [Fact]
    public void HideAndShowKeepTheEntryInItsGroup()
    {
        var service = SidebarGroupFixtures.Service();
        Assert.True(service.Hide("w"));
        Assert.True(service.MoveVisible("x", 3));
        Assert.Equal(["home", "a", "b", "x", "t"], SidebarFixtures.Ids(service.Items));
        Assert.True(service.Show("w"));
        Assert.Equal(["home", "a", "b", "w", "x", "t"], SidebarFixtures.Ids(service.Items));
    }

    [Fact]
    public void ResetRestoresTheGroupOrderAndShowsEveryEntry()
    {
        var service = SidebarGroupFixtures.Service(new SidebarLayout { Order = ["x", "t", "w", "b", "a"], Hidden = ["a", "t"] });
        service.Reset();
        Assert.Equal(["home", "a", "b", "w", "t", "x"], SidebarFixtures.Ids(service.Items));
        Assert.Empty(service.Hidden);
    }

    [Fact]
    public void ThePinnedEntryStaysFirstShownAndUnmovable()
    {
        var service = SidebarGroupFixtures.Service(new SidebarLayout { Order = ["a", "home"], Hidden = ["home"] });
        Assert.Equal("home", service.All[0].Id);
        Assert.False(service.Hide("home"));
        Assert.False(service.Move("home", 2));
        Assert.False(service.MoveVisible("home", 2));
        Assert.True(service.Move("b", 0));
        Assert.Equal(["home", "b", "a"], SidebarFixtures.Ids(service.All.Take(3)));
    }

    [Fact]
    public void TheServiceGivesTheGroupOfEachEntry()
    {
        var service = SidebarGroupFixtures.Service();
        Assert.Equal([SidebarGroupFixtures.Main, SidebarGroupFixtures.More], service.Groups);
        Assert.Equal(SidebarGroupFixtures.Main, service.GroupOf(service.All[0]));
        Assert.Equal(SidebarGroupFixtures.More, service.GroupOf(service.All[^1]));
        Assert.Null(new SidebarService(SidebarFixtures.Registry(), new InMemorySidebarStore()).GroupOf(SidebarFixtures.Registry().Items[1]));
    }

    [Fact]
    public void ADefaultSidebarMoveOfAToolIntoTheFirstGroupStopsAtTheFirstTool()
    {
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore());
        Assert.True(service.Move(SidebarIds.SiteHygiene, 1));
        Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.SiteHygiene], SidebarFixtures.Ids(service.All.Take(5)));
        Assert.False(service.Move(SidebarIds.Workbench, 5));
    }
}

public sealed class SidebarGroupDropMathTests
{
    [Theory]
    [InlineData(500, 1, 2, 2)]
    [InlineData(-5, 1, 2, 1)]
    [InlineData(61, 2, 3, 2)]
    [InlineData(99, 0, 3, 2)]
    public void TheInsertionGapStaysBetweenTheMinimumAndTheMaximum(double y, int minimum, int maximum, int expected)
    {
        Assert.Equal(expected, SidebarDropMath.InsertionIndex([20, 60, 100], y, minimum, maximum));
    }
}

public sealed class SidebarGroupSettingsEditorTests
{
    private static (SidebarSettingsEditor Editor, SidebarService Service) Create(SidebarLayout? layout = null)
    {
        var service = SidebarGroupFixtures.Service(layout);
        return (new SidebarSettingsEditor(service), service);
    }

    [Fact]
    public void TheFirstRowOfEachGroupCarriesTheGroupCaption()
    {
        var (editor, _) = Create();
        Assert.Equal(["home", "a", "b", "w", "t", "x"], editor.Rows.Select(r => r.Id));
        Assert.Equal([true, false, false, true, false, false], editor.Rows.Select(r => r.IsGroupStart));
        Assert.Equal(["MAIN", "MAIN", "MAIN", "MORE", "MORE", "MORE"], editor.Rows.Select(r => r.GroupCaption));
        Assert.Equal("More group", editor.Rows[3].GroupAutomationName);
    }

    [Fact]
    public void TheGroupStartFollowsAMoveInsideTheGroup()
    {
        var (editor, service) = Create();
        var tool = editor.Rows.Single(r => r.Id == "t");
        tool.MoveUpCommand.Execute(null);
        Assert.Equal(["home", "a", "b", "t", "w", "x"], SidebarFixtures.Ids(service.All));
        Assert.True(tool.IsGroupStart);
        Assert.False(editor.Rows.Single(r => r.Id == "w").IsGroupStart);
    }

    [Fact]
    public void TheRowsAtTheEdgesOfAGroupCannotMoveOutOfIt()
    {
        var (editor, service) = Create();
        Assert.Equal([false, false, true, false, true, true], editor.Rows.Select(r => r.CanMoveUp));
        Assert.Equal([false, true, false, true, true, false], editor.Rows.Select(r => r.CanMoveDown));
        editor.Rows.Single(r => r.Id == "b").MoveDownCommand.Execute(null);
        editor.Rows.Single(r => r.Id == "w").MoveUpCommand.Execute(null);
        Assert.Equal(["home", "a", "b", "w", "t", "x"], SidebarFixtures.Ids(service.All));
    }

    [Fact]
    public void UpOnAShownRowWithOnlyHiddenRowsAboveInItsGroupMovesItToTheStartOfTheGroup()
    {
        var (editor, service) = Create(new SidebarLayout { Order = [], Hidden = ["w", "t"] });
        editor.Rows.Single(r => r.Id == "x").MoveUpCommand.Execute(null);
        Assert.Equal(["home", "a", "b", "x", "w", "t"], SidebarFixtures.Ids(service.All));
        editor.Rows.Single(r => r.Id == "a").MoveDownCommand.Execute(null);
        Assert.Equal(["home", "b", "a", "x", "w", "t"], SidebarFixtures.Ids(service.All));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SidebarGroupMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-sidebar-groups-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public SidebarGroupMigrationTests()
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
        TestCleanup.DeleteDirectory(_root);
    }

    private const string OldSettings = """
        { "SiteCode": "PS1", "FirstRunCompleted": true,
          "Sidebar": {
            "Order": ["site-hygiene", "detection-tester", "applications", "packages", "workbench", "installer-analysis", "suite-launcher", "add-app"],
            "Hidden": ["detection-tester", "suite-launcher", "packages", "applications", "add-app"] } }
        """;

    [Fact]
    public void ASettingsFileWithToolsOrderedBeforeTheFirstGroupAndRemovedEntriesLoadsGroupByGroup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, OldSettings);

        var service = new SidebarService(SidebarRegistry.CreateDefault(), new SettingsSidebarLayoutStore(new SettingsService()));
        Assert.Equal(
        [
            SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.AddApp, SidebarIds.AddFile,
            SidebarIds.SiteHygiene, SidebarIds.DetectionTester, SidebarIds.InstallerAnalysis,
            SidebarIds.DeploymentHelper, SidebarIds.PivotsScripts, SidebarIds.CollectionCompliance, SidebarIds.MaintenanceWindows, SidebarIds.DpContentManager
        ], SidebarFixtures.Ids(service.All));
        Assert.Equal([SidebarIds.AddApp, SidebarIds.DetectionTester], SidebarFixtures.Ids(service.Hidden));
        Assert.Equal(SidebarIds.Packages, service.Items[0].Id);
        Assert.Equal(OldSettings, File.ReadAllText(SettingsService.SettingsPath));

        Assert.True(service.Hide(SidebarIds.Workbench));
        var stored = JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!["Sidebar"]!;
        Assert.DoesNotContain(SidebarIds.Applications, stored["Order"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(SidebarFixtures.Ids(service.All), stored["Order"]!.AsArray().Select(n => (string)n!));
        Assert.Equal([SidebarIds.Workbench, SidebarIds.AddApp, SidebarIds.DetectionTester], stored["Hidden"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("PS1", (string?)JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!["SiteCode"]);
    }
}
