using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.VendorImport;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The origin Imported in the three loaders (the catalog loader, the Workbench discovery and the command-line build resolution): where an
/// imported definition sits between the shipped and the user definitions, and that the label comes from the feed folder.
/// </summary>
public sealed class ImportedOriginLoaderTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly string _shipped;
    private readonly string _user;
    private readonly string _imported;

    public ImportedOriginLoaderTests()
    {
        _shipped = _t.NewFolder();
        _user = _t.NewFolder();
        _imported = Path.Combine(_t.Root, "imported");
    }

    public void Dispose() => _t.Dispose();

    private static string Json(string id, string name) => $$"""{ "Id": "{{id}}", "Application": "{{name}}", "Vendor": "V" }""";

    private void Feed(string feedId, string vendor, DateTimeOffset at, string[] replaced, params (string Id, string Name)[] definitions)
    {
        var validated = definitions
            .Select(d => new ValidatedDefinition(d.Id, new AppDefinition { Id = d.Id, Application = d.Name }, Json(d.Id, d.Name), [], [], []))
            .ToList();
        new ImportedDefinitionStore(_imported).Write(
            new FeedSource($"https://example.test/{feedId}.json", vendor, feedId, at, "00", 1, FeedTrust.NotVerified, null, null, null, [], replaced, []),
            validated);
    }

    private static readonly DateTimeOffset Early = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Late = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private WorkbenchDiscoveryRoots Roots() => new()
    {
        CatalogDefinitionsRoot = _shipped,
        CatalogOverridesRoot = _user,
        CustomDefinitionsRoot = _user,
        ImportedDefinitionsRoot = _imported
    };

    [Fact]
    public void AnImportedDefinitionGetsTheImportedOriginAndNeverTheCatalogOne()
    {
        Feed("contoso", "Contoso", Early, [], ("contoso-agent", "Contoso Agent"));

        var apps = _t.Store.DiscoverApplications(Roots());
        var app = Assert.Single(apps, a => a.ApplicationId == "imported:contoso-agent");

        Assert.Equal(ApplicationOrigin.Imported, app.Origin);
        Assert.Equal("Contoso Agent", app.DisplayName);
        Assert.DoesNotContain(apps, a => a.ApplicationId == "catalog:contoso-agent");
    }

    [Fact]
    public void AShippedIdIsNotReplacedUnlessTheImportWasConfirmedForIt()
    {
        _t.NewFile(Path.Combine(_shipped, "a.json"), Json("shared-1", "Shipped One"));
        Feed("contoso", "Contoso", Early, [], ("shared-1", "Imported One"));

        var apps = _t.Store.DiscoverApplications(Roots());

        Assert.Single(apps, a => a.ApplicationId == "catalog:shared-1" && a.DisplayName == "Shipped One");
        Assert.DoesNotContain(apps, a => a.ApplicationId == "imported:shared-1");
    }

    [Fact]
    public void AConfirmedReplacementTurnsTheShippedApplicationIntoAnImportedOne()
    {
        _t.NewFile(Path.Combine(_shipped, "a.json"), Json("shared-1", "Shipped One"));
        Feed("contoso", "Contoso", Early, ["shared-1"], ("shared-1", "Imported One"));

        var apps = _t.Store.DiscoverApplications(Roots());

        Assert.Single(apps, a => a.ApplicationId == "imported:shared-1" && a.DisplayName == "Imported One");
        Assert.DoesNotContain(apps, a => a.ApplicationId == "catalog:shared-1");
    }

    [Fact]
    public void AUserDefinitionWinsOverAnImportedOneEvenForAConfirmedReplacement()
    {
        _t.NewFile(Path.Combine(_shipped, "a.json"), Json("shared-1", "Shipped One"));
        _t.NewFile(Path.Combine(_user, "u.json"), Json("shared-1", "User One"));
        Feed("contoso", "Contoso", Early, ["shared-1"], ("shared-1", "Imported One"), ("other-1", "Other"));
        _t.NewFile(Path.Combine(_user, "v.json"), Json("other-1", "User Other"));

        var apps = _t.Store.DiscoverApplications(Roots());

        Assert.DoesNotContain(apps, a => a.Origin == ApplicationOrigin.Imported);
        Assert.Single(apps, a => a.ApplicationId == "catalog:shared-1");
        Assert.Single(apps, a => a.ApplicationId == "custom:other-1");
    }

    [Fact]
    public void WhenTwoFeedsHoldTheSameIdTheEarlierImportKeepsIt()
    {
        Feed("zeta-feed", "Zeta", Early, [], ("same-1", "From Zeta"));
        Feed("alpha-feed", "Alpha", Late, [], ("same-1", "From Alpha"));

        var apps = _t.Store.DiscoverApplications(Roots());

        Assert.Single(apps, a => a.ApplicationId == "imported:same-1" && a.DisplayName == "From Zeta");
    }

    [Fact]
    public void TheCatalogLoaderLabelsAnImportedDefinitionFromTheFeedFolderAndTheSourceRecord()
    {
        _t.NewFile(Path.Combine(_shipped, "a.json"), Json("shared-1", "Shipped One"));
        Feed("contoso", "Contoso", Early, ["shared-1"], ("shared-1", "Imported One"), ("contoso-agent", "Contoso Agent"));

        var loaded = PackagerDiscoveryService.LoadEffectiveDefinitions(_shipped, _user, default, _imported);

        var replaced = Assert.Single(loaded, d => d.Id == "shared-1");
        Assert.Equal(ApplicationOrigin.Imported, replaced.SourceOrigin);
        Assert.Equal("Imported One", replaced.Application);
        Assert.Equal("Imported: Contoso (Definition document NOT verified)", replaced.SourceLabel);
        Assert.Equal(ApplicationOrigin.Imported, Assert.Single(loaded, d => d.Id == "contoso-agent").SourceOrigin);
    }

    [Fact]
    public void TheCatalogLoaderKeepsTheShippedAndUserRulesWhenAnImportedDefinitionCollides()
    {
        _t.NewFile(Path.Combine(_shipped, "a.json"), Json("shared-1", "Shipped One"));
        _t.NewFile(Path.Combine(_user, "u.json"), Json("mine-1", "User Mine"));
        Feed("contoso", "Contoso", Early, [], ("shared-1", "Imported One"), ("mine-1", "Imported Mine"));

        var loaded = PackagerDiscoveryService.LoadEffectiveDefinitions(_shipped, _user, default, _imported);

        Assert.Equal("Shipped One", Assert.Single(loaded, d => d.Id == "shared-1").Application);
        Assert.Equal("User Mine", Assert.Single(loaded, d => d.Id == "mine-1").Application);
        Assert.DoesNotContain(loaded, d => d.SourceOrigin == ApplicationOrigin.Imported);
    }

    [Fact]
    public void TheLabelDoesNotComeFromTheFile()
    {
        // A file that names its own label is not a way to claim one: the property is not read from the JSON.
        var path = _t.NewFile(Path.Combine(_user, "u.json"), """{ "Id": "mine-1", "Application": "Mine", "SourceLabel": "Imported: Fake" }""");

        var loaded = PackagerDiscoveryService.LoadEffectiveDefinitions(_shipped, _user, default, _imported);

        Assert.Equal("", Assert.Single(loaded).SourceLabel);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TheCommandLineResolvesAnImportedApplicationByNameAndByPrefix()
    {
        Feed("contoso", "Contoso", Early, [], ("contoso-agent", "Contoso Agent"));
        var apps = _t.Store.DiscoverApplications(Roots());

        Assert.Equal("imported:contoso-agent", ApplicationResolver.Resolve("contoso-agent", apps));
        Assert.Equal("imported:contoso-agent", ApplicationResolver.Resolve("IMPORTED:contoso-agent", apps));
    }

    [Fact]
    public void AFileWhoseIdInsideDiffersFromItsNameIsDroppedByEveryLoader()
    {
        _t.NewFile(Path.Combine(_shipped, "a.json"), Json("shared-1", "Shipped One"));
        Feed("contoso", "Contoso", Early, ["shared-1"], ("agent", "Agent"));
        // The record lists "agent"; the file now claims the Id of a shipped application.
        File.WriteAllText(Path.Combine(_imported, "contoso", "agent.json"), Json("shared-1", "Not the agent"));

        var apps = _t.Store.DiscoverApplications(Roots());
        var loaded = PackagerDiscoveryService.LoadEffectiveDefinitions(_shipped, _user, default, _imported);

        Assert.DoesNotContain(apps, a => a.Origin == ApplicationOrigin.Imported);
        Assert.Single(apps, a => a.ApplicationId == "catalog:shared-1");
        Assert.DoesNotContain(loaded, d => d.SourceOrigin == ApplicationOrigin.Imported);
    }

    [Fact]
    public void ImportedIdsWithDotsStayTwoApplications()
    {
        Feed("contoso", "Contoso", Early, [], ("Contoso.Agent", "Agent"), ("Contoso.Tools", "Tools"));

        var apps = _t.Store.DiscoverApplications(Roots());

        Assert.Single(apps, a => a.ApplicationId == "imported:Contoso.Agent");
        Assert.Single(apps, a => a.ApplicationId == "imported:Contoso.Tools");
    }

    [Fact]
    public void AnImportedDefinitionRunsUnderItsOwnIdentityAndTheOthersKeepTheCatalogOne()
    {
        Assert.Equal(ApplicationOrigin.Imported, ApplicationIds.RunOrigin(ApplicationOrigin.Imported));
        Assert.Equal(ApplicationOrigin.Catalog, ApplicationIds.RunOrigin(ApplicationOrigin.Catalog));
        Assert.Equal(ApplicationOrigin.Catalog, ApplicationIds.RunOrigin(ApplicationOrigin.Custom));
        Assert.Equal("imported:x1", ApplicationPackagerEngine.Services.StagingService.CatalogApplicationId(
            new AppDefinition { Id = "x1", SourceOrigin = ApplicationOrigin.Imported }));
        Assert.Equal("catalog:x1", ApplicationPackagerEngine.Services.StagingService.CatalogApplicationId(
            new AppDefinition { Id = "x1", SourceOrigin = ApplicationOrigin.Custom }));
    }

    [Fact]
    public void RecoverKeepsGoingAfterAFolderItCannotRepair()
    {
        Directory.CreateDirectory(Path.Combine(_imported, ".staging-a"));
        Directory.CreateDirectory(Path.Combine(_imported, "contoso.old-1"));
        File.WriteAllText(Path.Combine(_imported, "contoso.old-1", "source.json"), "{}");
        // The staging folder is locked open, so its delete fails; the old copy of the other feed must still come back.
        using var locked = new FileStream(Path.Combine(_imported, ".staging-a", "held.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        new ImportedDefinitionStore(_imported).Recover();

        Assert.True(Directory.Exists(Path.Combine(_imported, "contoso")));
    }

    [Fact]
    public void TheImportedIdIsAValidApplicationIdWithItsOwnStorageKey()
    {
        var id = ApplicationIds.New(ApplicationOrigin.Imported, name: "contoso-agent");

        Assert.Equal("imported:contoso-agent", id);
        Assert.True(ApplicationIds.IsValid(id));
        Assert.Equal("imported_contoso-agent", ApplicationIds.ToKey(id));
        Assert.Equal("imported:contoso-agent", ApplicationIds.FromKey("imported_contoso-agent"));
        Assert.Equal("imported", ApplicationIds.OriginOf(id));
    }

    [Fact]
    public void ADocumentCannotSetItsOwnLabel()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Json("a-1", "A"))!.AsObject();
        node["SourceLabel"] = "Imported: Fake";
        var doc = new System.Text.Json.Nodes.JsonObject
        {
            ["FeedFormat"] = 1, ["Vendor"] = "V", ["FeedId"] = "v-feed", ["Definitions"] = new System.Text.Json.Nodes.JsonArray(node)
        }.ToJsonString();

        var result = FeedValidator.Validate(System.Text.Encoding.UTF8.GetBytes(doc));

        Assert.True(result.Refused || result.Definitions.Single().Blocked);
    }
}