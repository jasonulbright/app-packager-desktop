using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class ApplicationDefinitionTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void New_BuildsCatalogAndCustomIdsFromAScriptPathOrName()
    {
        Assert.Equal("catalog:package-7zip", ApplicationIds.New(ApplicationOrigin.Catalog, path: @"C:\x\package-7zip.ps1"));
        Assert.Equal("custom:package-mine", ApplicationIds.New(ApplicationOrigin.Custom, name: "package-mine"));
        Assert.Equal("catalog:fa505d1415214f14", ApplicationIds.New(ApplicationOrigin.Catalog, name: "fa505d1415214f14"));
    }

    [Fact]
    public void New_BuildsAUniqueByoId()
    {
        var first = ApplicationIds.New(ApplicationOrigin.Byo);
        var second = ApplicationIds.New(ApplicationOrigin.Byo);
        Assert.Matches("^byo:[0-9a-f]{32}$", first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Key_RoundTrips()
    {
        var key = ApplicationIds.ToKey("catalog:package-7zip");
        Assert.Equal("catalog_package-7zip", key);
        Assert.Equal("catalog:package-7zip", ApplicationIds.FromKey(key));
    }

    [Theory]
    [InlineData(@"catalog:..\..\evil")]
    [InlineData("catalog:a/b")]
    [InlineData("other:name")]
    [InlineData("catalog:")]
    public void Key_RejectsAnIdWithPathCharactersOrAnUnknownOrigin(string applicationId)
    {
        var ex = Assert.Throws<ArgumentException>(() => ApplicationIds.ToKey(applicationId));
        Assert.Contains("Invalid ApplicationId", ex.Message);
    }

    [Fact]
    public void Discover_FindsCatalogDefinitionsScriptsCustomDefinitionsAndPersistedByoApps()
    {
        var catalogDefinitions = _t.NewFolder();
        var packagers = _t.NewFolder();
        _t.NewFile(Path.Combine(catalogDefinitions, "alpha.json"), """{ "Id": "aaaa000011112222", "Application": "Alpha" }""");
        _t.NewFile(Path.Combine(packagers, "package-alpha.ps1"), "# packager");
        _t.NewFile(Path.Combine(packagers, "package-retired.notps1"), "# packager");
        _t.NewFile(Path.Combine(_t.Store.CustomDefinitionsRoot, "mine.json"), """{ "Id": "mine01", "Application": "Mine" }""");
        var byoId = ApplicationIds.New(ApplicationOrigin.Byo);
        _t.Store.SaveApplicationDefinition(new WorkbenchApplicationDefinition { ApplicationId = byoId, DisplayName = "Dropped App", Origin = "byo" });

        var apps = _t.Store.DiscoverApplications(new WorkbenchDiscoveryRoots
        {
            CatalogDefinitionsRoot = catalogDefinitions,
            PackagersRoot = packagers
        });

        Assert.Single(apps, a => a.ApplicationId == "catalog:aaaa000011112222" && a.DisplayName == "Alpha");
        Assert.Single(apps, a => a.ApplicationId == "catalog:package-alpha" && a.Runnable);
        Assert.Single(apps, a => a.ApplicationId == "catalog:package-retired" && !a.Runnable);
        Assert.Single(apps, a => a.ApplicationId == "custom:mine01" && a.Origin == ApplicationOrigin.Custom);
        Assert.Single(apps, a => a.ApplicationId == byoId && a.DisplayName == "Dropped App" && a.UpdatePolicy == "Manual");
    }

    [Fact]
    public void Discover_UsesUserFilesAsCatalogOverridesAndCustomDefinitionsWithoutDuplicatingCatalogIds()
    {
        var catalog = _t.NewFolder();
        var user = _t.NewFolder();
        var catalogId = "catalogoverride01";
        var customId = "newcustom01";
        _t.NewFile(Path.Combine(catalog, "shipped.json"), $$"""{ "Id": "{{catalogId}}", "Application": "Shipped Name" }""");
        var userOverride = _t.NewFile(Path.Combine(user, "my-shipped.json"), $$"""{ "Id": "{{catalogId}}", "Application": "User Name" }""");
        _t.NewFile(Path.Combine(user, "custom.json"), $$"""{ "Id": "{{customId}}", "Application": "Custom App" }""");

        var applications = _t.Store.DiscoverApplications(new WorkbenchDiscoveryRoots
        {
            CatalogDefinitionsRoot = catalog,
            CatalogOverridesRoot = user,
            CustomDefinitionsRoot = user
        });

        var overridden = Assert.Single(applications, a => a.ApplicationId == $"catalog:{catalogId}");
        Assert.Equal(Path.GetFullPath(userOverride), overridden.SourcePath);
        Assert.Equal("User Name", overridden.DisplayName);
        Assert.DoesNotContain(applications, a => a.ApplicationId == $"custom:{catalogId}");
        Assert.Single(applications, a => a.ApplicationId == $"custom:{customId}" && a.SourcePath == Path.Combine(user, "custom.json"));
    }

    [Fact]
    public void Get_ReturnsAnUnsavedDefaultDefinitionForAnUnknownApplication()
    {
        var definition = _t.Store.GetApplicationDefinition("catalog:package-not-saved");
        Assert.False(definition.Persisted);
        Assert.Equal("default", definition.ActiveProfileId);
        Assert.Equal("catalog", definition.Origin);
        Assert.Equal("Discovered", definition.UpdatePolicy);
        Assert.Equal("Manual", _t.Store.GetApplicationDefinition(ApplicationIds.New(ApplicationOrigin.Byo)).UpdatePolicy);
    }

    [Fact]
    public void Save_WritesAtomicallyAndRoundTripsUnknownFields()
    {
        var id = "catalog:package-roundtrip";
        var folder = _t.Store.GetApplicationFolder(id, create: true);
        File.WriteAllText(Path.Combine(folder, "application.json"),
            """{ "SchemaVersion": 1, "ApplicationId": "catalog:package-roundtrip", "DisplayName": "Old", "FutureField": { "A": 1 } }""");

        var definition = _t.Store.GetApplicationDefinition(id);
        Assert.True(definition.Persisted);
        definition.DisplayName = "New";
        var saved = _t.Store.SaveApplicationDefinition(definition);

        Assert.Equal("New", saved.DisplayName);
        Assert.True(saved.Persisted);
        Assert.Contains("FutureField", File.ReadAllText(Path.Combine(folder, "application.json")));
        Assert.Equal(["application.json"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_RefusesASchemaNewerThanThisBuild()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _t.Store.SaveApplicationDefinition(
            new WorkbenchApplicationDefinition { ApplicationId = "catalog:package-future", SchemaVersion = 9 }));
        Assert.Contains("newer than this build supports", ex.Message);
    }

    [Fact]
    public void AtomicWrite_ReplacesAnExistingFileAndLeavesNoSideFiles()
    {
        var folder = _t.NewFolder();
        var path = Path.Combine(folder, "doc.json");
        WorkbenchJson.WriteText(path, "one");
        WorkbenchJson.WriteText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(folder));
    }
}

[Collection(WorkbenchEnvironmentCollection.Name)]
public sealed class WorkbenchStorageRootTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly string? _saved = Environment.GetEnvironmentVariable(WorkbenchPaths.DataRootVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, _saved);
        _t.Dispose();
    }

    [Fact]
    public void DataRoot_PrefersTheEnvironmentVariableOverThePreference()
    {
        var fromEnvironment = Path.Combine(_t.Root, "env-root");
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, fromEnvironment);
        Assert.Equal(AppDataPaths.ScopeRootToCurrentUser(fromEnvironment), WorkbenchPaths.ResolveDataRoot(@"C:\ignored", create: false));
    }

    [Fact]
    public void DataRoot_UsesThePreferenceWhenNoEnvironmentVariableIsSet()
    {
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, null);
        var wanted = Path.Combine(_t.Root, "pref-root");
        var scoped = AppDataPaths.ScopeRootToCurrentUser(wanted);
        Assert.Equal(scoped, WorkbenchPaths.ResolveDataRoot(wanted));
        Assert.True(Directory.Exists(scoped));
    }

    [Fact]
    public void DataRoot_DefaultsBesideTheDesktopSettingsFile()
    {
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, null);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager", "Workbench");
        Assert.Equal(expected, WorkbenchPaths.ResolveDataRoot(create: false));
    }
}
