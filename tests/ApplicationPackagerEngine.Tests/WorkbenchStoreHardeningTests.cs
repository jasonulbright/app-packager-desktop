using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchStoreHardeningTests : IDisposable
{
    private const string App = "catalog:7zip";
    private readonly WorkbenchTestRoot _root = new();
    private WorkbenchStore Store => _root.Store;

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("profile.")]
    [InlineData("profile ")]
    [InlineData("a/b")]
    [InlineData("")]
    public void AProfileIdThatIsNotAPlainFolderName_IsRejected(string profileId)
    {
        Assert.Throws<ArgumentException>(() => ApplicationIds.AssertProfileId(profileId));
        Assert.ThrowsAny<ArgumentException>(() => Store.GetProfileFolder(App, profileId));
        Assert.ThrowsAny<ArgumentException>(() => Store.GetDraftPath(App, profileId));
    }

    [Theory]
    [InlineData("custom:..")]
    [InlineData("custom:.")]
    [InlineData("catalog:...")]
    [InlineData("catalog:7zip.")]
    [InlineData("byo:")]
    public void AnApplicationIdWhoseNameIsNotAPlainFolderName_IsRejected(string applicationId)
    {
        Assert.False(ApplicationIds.IsValid(applicationId));
        Assert.Throws<ArgumentException>(() => ApplicationIds.ToKey(applicationId));
    }

    [Theory]
    [InlineData("catalog:7zip")]
    [InlineData("custom:my.app-1_x")]
    [InlineData("custom:.hidden")]
    public void PlainNames_StayValid(string applicationId) => Assert.True(ApplicationIds.IsValid(applicationId));

    [Fact]
    public void TheProfileDownloadRoot_RejectsATraversingProfileId()
    {
        var root = _root.NewFolder("downloads");
        Assert.ThrowsAny<ArgumentException>(() => WorkbenchPaths.GetProfileDownloadRoot(root, ".."));
        Assert.Equal(Path.Combine(root, "profiles", "p1"), WorkbenchPaths.GetProfileDownloadRoot(root, "p1"));
    }

    [Fact]
    public void CombineUnder_RefusesAPathOutsideItsParent()
    {
        var parent = _root.NewFolder("parent");
        Assert.Throws<InvalidOperationException>(() => WorkbenchPaths.CombineUnder(parent, ".."));
        Assert.Throws<InvalidOperationException>(() => WorkbenchPaths.CombineUnder(parent, @"C:\Windows"));
        Assert.Equal(Path.Combine(parent, "a", "b"), WorkbenchPaths.CombineUnder(parent, "a", "b"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"..\..\..")]
    [InlineData("0123456789abcdef0")]
    [InlineData("not-a-hash")]
    public void AnAssetIdThatIsNotAContentHash_IsRejected(string assetId)
    {
        var profile = _root.NewProfile(App);
        Assert.Throws<ArgumentException>(() => Store.ResolveAssetPath(App, profile.ProfileId, assetId));
    }

    [Fact]
    public void AManagedAssetWhoseContentChanged_IsRefused()
    {
        var profile = _root.NewProfile(App);
        var asset = Store.AddAsset(App, profile.ProfileId, _root.NewFile(Path.Combine(_root.Root, "src", "icon.png"), "original"));
        Assert.Equal(asset.Path, Store.ResolveAssetPath(App, profile.ProfileId, asset.AssetId));

        File.WriteAllText(asset.Path, "replaced");
        var ex = Assert.Throws<InvalidOperationException>(() => Store.ResolveAssetPath(App, profile.ProfileId, asset.AssetId));
        Assert.Contains(asset.AssetId, ex.Message);
    }

    [Fact]
    public void ASnapshotWithAnExistingBuildId_IsRefused()
    {
        var request = new RunSnapshotRequest { ApplicationId = App, BuildId = "20260101-000000-abcdef01" };
        var first = Store.CreateRunSnapshot(request);
        var before = File.ReadAllText(first.Path!);

        var ex = Assert.Throws<InvalidOperationException>(() => Store.CreateRunSnapshot(request));
        Assert.Contains("20260101-000000-abcdef01", ex.Message);
        Assert.Equal(before, File.ReadAllText(first.Path!));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("build.")]
    public void ABuildIdThatIsNotAPlainFileName_IsRejected(string buildId) =>
        Assert.Throws<ArgumentException>(() => Store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = App, BuildId = buildId }));

    [Fact]
    public void TwoSavesFromTheSameRevision_OneSucceedsAndOneIsRefused()
    {
        var saved = _root.NewProfile(App);
        for (var round = 0; round < 20; round++)
        {
            var loaded = Store.GetProfile(App, saved.ProfileId);
            using var start = new Barrier(2);
            var outcomes = new Exception?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                var copy = new WorkbenchProfile(WorkbenchJson.CloneObject(loaded.Data));
                copy.Data["Name"] = $"writer {i}";
                start.SignalAndWait();
                try { Store.SaveProfile(copy); }
                catch (Exception ex) { outcomes[i] = ex; }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Single(outcomes, o => o is null);
            var refused = Assert.Single(outcomes, o => o is not null);
            Assert.IsType<InvalidOperationException>(refused);
            Assert.Contains("changed on disk", refused!.Message);
            Assert.Equal(loaded.Revision + 1, Store.GetProfile(App, saved.ProfileId).Revision);
        }
    }

    [Fact]
    public void AReplaceThatFailsAfterMovingTheOriginal_LeavesTheOriginalInPlace()
    {
        var path = _root.NewFile(Path.Combine(_root.Root, "w5", "profile.json"), "original");
        Assert.ThrowsAny<IOException>(() => WorkbenchJson.WriteText(path, "updated", (temp, target, backup) =>
        {
            File.Move(target, backup);
            throw new IOException("Unable to move the replacement file to the file to be replaced.");
        }));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(["profile.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void AReplaceThatFailsBeforeMovingTheOriginal_LeavesNoSideFiles()
    {
        var path = _root.NewFile(Path.Combine(_root.Root, "w5b", "profile.json"), "original");
        Assert.ThrowsAny<IOException>(() => WorkbenchJson.WriteText(path, "updated", (_, _, _) => throw new IOException("locked")));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(["profile.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }
}
