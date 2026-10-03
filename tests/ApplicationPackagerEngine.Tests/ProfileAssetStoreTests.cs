using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class ProfileAssetStoreTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Add_CopiesIntoManagedStorageWithAContentIdProvenanceAndSize()
    {
        var id = "catalog:package-assets";
        var profile = _t.NewProfile(id);
        var source = _t.NewFile(Path.Combine(_t.NewFolder(), "settings.json"), """{"a":1}""");

        var asset = _t.Store.AddAsset(id, profile.ProfileId, source);

        Assert.Matches("^[0-9a-f]{16}$", asset.AssetId);
        Assert.Equal(asset.Sha256[..16], asset.AssetId);
        Assert.Equal(7, asset.Size);
        Assert.Equal(Path.GetFullPath(source), asset.Provenance);
        Assert.False(asset.Linked);
        Assert.True(File.Exists(asset.Path));
        Assert.NotEqual(asset.Provenance, asset.Path);
        Assert.Equal(asset.Path, _t.Store.ResolveAssetPath(id, profile.ProfileId, asset.AssetId));
    }

    [Fact]
    public void Remove_ReturnsTrueThenFalse()
    {
        var id = "catalog:package-assets-remove";
        var profile = _t.NewProfile(id);
        var asset = _t.Store.AddAsset(id, profile.ProfileId, _t.NewFile(Path.Combine(_t.NewFolder(), "a.txt"), "x"));

        Assert.True(_t.Store.RemoveAsset(id, profile.ProfileId, asset.AssetId));
        Assert.False(_t.Store.RemoveAsset(id, profile.ProfileId, asset.AssetId));
        Assert.Throws<ArgumentException>(() => _t.Store.RemoveAsset(id, profile.ProfileId, @"..\x"));
    }

    [Fact]
    public void Resolve_FailsALinkedAssetWhoseFileIsGone()
    {
        var id = "catalog:package-linked";
        var profile = _t.NewProfile(id);
        var source = _t.NewFile(Path.Combine(_t.NewFolder(), "linked.txt"), "x");
        var asset = _t.Store.AddAsset(id, profile.ProfileId, source, linked: true);
        Assert.Equal(Path.GetFullPath(source), _t.Store.ResolveAssetPath(id, profile.ProfileId, asset));

        File.Delete(source);

        var ex = Assert.Throws<FileNotFoundException>(() => _t.Store.ResolveAssetPath(id, profile.ProfileId, asset));
        Assert.Equal($"Linked source file '{Path.GetFullPath(source)}' is missing; the build cannot snapshot it.", ex.Message);
    }

    [Fact]
    public void Resolve_FailsAManagedAssetMissingFromStorage()
    {
        var profile = _t.NewProfile("catalog:package-managed-gone");
        var ex = Assert.Throws<FileNotFoundException>(() =>
            _t.Store.ResolveAssetPath(profile.ApplicationId, profile.ProfileId, "0123456789abcdef"));
        Assert.Equal($"Managed asset '0123456789abcdef' is missing from profile '{profile.ProfileId}'.", ex.Message);
    }

    [Fact]
    public void Add_RefusesTheDefaultProfile()
    {
        var source = _t.NewFile(Path.Combine(_t.NewFolder(), "b.txt"), "x");
        var ex = Assert.Throws<InvalidOperationException>(() => _t.Store.AddAsset("catalog:package-x", "default", source));
        Assert.Contains("default profile", ex.Message);
    }

    [Fact]
    public void Import_CopiesPathOnlyIconAndSourceFilesIntoStorage()
    {
        var id = "catalog:package-import";
        var profile = _t.NewProfile(id);
        var folder = _t.NewFolder();
        var icon = _t.NewFile(Path.Combine(folder, "icon.png"), "png");
        var config = _t.NewFile(Path.Combine(folder, "config.ini"), "[a]");
        var linked = _t.NewFile(Path.Combine(folder, "linked.ini"), "[b]");
        profile.Set("Application.Icon", new JsonObject { ["Path"] = icon });
        profile.Data["SourceFiles"] = new JsonArray(
            new JsonObject { ["Provenance"] = config, ["Destination"] = "config.ini" },
            new JsonObject { ["Provenance"] = linked, ["Destination"] = "linked.ini", ["Linked"] = true });

        _t.Store.ImportProfileAssets(profile);

        var iconAsset = profile.Get("Application.Icon.Asset").Text!;
        Assert.Matches("^[0-9a-f]{16}$", iconAsset);
        Assert.StartsWith(_t.Store.DataRoot, profile.Get("Application.Icon.Path").Text);
        var entries = profile.Data["SourceFiles"]!.AsArray();
        Assert.Matches("^[0-9a-f]{16}$", entries[0]!["Asset"]!.GetValue<string>());
        Assert.Equal(3L, entries[0]!["Size"]!.GetValue<long>());
        Assert.Null(entries[1]!["Asset"]);
        Assert.Equal(
            [iconAsset, entries[0]!["Asset"]!.GetValue<string>()],
            ApplicationPackagerEngine.Services.Workbench.WorkbenchStore.GetAssetReferences(profile));
    }
}
