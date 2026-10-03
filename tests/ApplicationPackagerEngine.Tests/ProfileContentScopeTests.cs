using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class ProfileContentScopeTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void SharedCache_ResolvesAProfileDownloadSubrootBackToTheSharedCache()
    {
        var root = Path.Combine(_t.Root, "dl");
        var shared = WorkbenchPaths.GetSharedDownloadCacheRoot(root, create: false);
        Assert.Equal(Path.Combine(root, "_cache"), shared);
        Assert.Equal(shared, WorkbenchPaths.GetSharedDownloadCacheRoot(Path.Combine(root, "profiles", "abc123"), create: false));
        Assert.Equal(shared, WorkbenchPaths.GetSharedDownloadCacheRoot(root + "\\", create: false));
    }

    [Fact]
    public void ProfileDownloadRoot_IsolatesANonDefaultProfileAndKeepsTheDefaultRoot()
    {
        var root = Path.Combine(_t.Root, "dl");
        Assert.Equal(Path.Combine(root, "profiles", "abc123"), WorkbenchPaths.GetProfileDownloadRoot(root, "abc123"));
        Assert.Equal(root, WorkbenchPaths.GetProfileDownloadRoot(root, "default"));
        Assert.Equal(root, WorkbenchPaths.GetProfileDownloadRoot(root, null));
        Assert.Equal("", WorkbenchPaths.GetProfileDownloadRoot("", "abc123"));
    }

    [Theory]
    [InlineData("Lab Build", "LabBuild-4f1c2d3e")]
    [InlineData("v2.", "v2-4f1c2d3e")]
    [InlineData("Ünïcode only ✓", "ncodeonly-4f1c2d3e")]
    [InlineData("✓✓", "4f1c2d3e")]
    [InlineData("", "4f1c2d3e")]
    public void Scope_DerivesTheSuffixFromTheProfileNameAndId(string name, string suffix)
    {
        var scope = ProfileContentScope.FromValues("4f1c2d3e5a6b7c8d", name);
        Assert.Equal(suffix, scope.Suffix);
        Assert.Equal($"1.2.3-{suffix}", scope.ScopedVersionFolder("1.2.3"));
    }

    [Fact]
    public void Scope_TwoProfilesWhoseNamesReduceToOneTextGetDifferentFolders()
    {
        var spaced = ProfileContentScope.FromValues("aaaa1111bbbb2222cccc3333dddd4444", "Pilot A").ScopedVersionFolder("1.0");
        var joined = ProfileContentScope.FromValues("eeee5555ffff6666aaaa7777bbbb8888", "PilotA").ScopedVersionFolder("1.0");

        Assert.Equal("1.0-PilotA-aaaa1111", spaced);
        Assert.Equal("1.0-PilotA-eeee5555", joined);
    }

    [Fact]
    public void Scope_KeepsTheVersionFolderForTheDefaultProfileOrNoSnapshot()
    {
        Assert.Equal("1.2.3", ProfileContentScope.FromValues("default", "Packager default").ScopedVersionFolder("1.2.3"));
        Assert.Equal(ProfileContentScope.Default, ProfileContentScope.FromSnapshot((string?)null));
        Assert.Equal(ProfileContentScope.Default, ProfileContentScope.FromSnapshot(Path.Combine(_t.Root, "missing.json")));
        var broken = _t.NewFile(Path.Combine(_t.Root, "broken.json"), "{ not json");
        Assert.Equal(ProfileContentScope.Default, ProfileContentScope.FromSnapshot(broken));
    }

    [Fact]
    public void Scope_GivesTwoProfilesOfOneApplicationDisjointVersionFolders()
    {
        var id = "catalog:package-scope";
        var alpha = _t.NewProfile(id, "Alpha");
        var beta = _t.NewProfile(id, "Beta");
        var alphaSnapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = id, ProfileId = alpha.ProfileId });
        var betaSnapshot = _t.Store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = id, ProfileId = beta.ProfileId });

        var alphaFolder = ProfileContentScope.FromSnapshot(alphaSnapshot.Path).ScopedVersionFolder("1.0.0");
        var betaFolder = ProfileContentScope.FromSnapshot(betaSnapshot).ScopedVersionFolder("1.0.0");

        Assert.Equal($"1.0.0-Alpha-{alpha.ProfileId[..8]}", alphaFolder);
        Assert.Equal($"1.0.0-Beta-{beta.ProfileId[..8]}", betaFolder);
    }
}
