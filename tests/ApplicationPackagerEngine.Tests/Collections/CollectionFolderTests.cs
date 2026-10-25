using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// The folder of each collection through the real read script on the stub site: from ObjectPath
/// when the provider fills it, otherwise from the folder tree.
/// </summary>
public sealed class CollectionFolderTests
{
    private static readonly object[] Folders =
    [
        new { ContainerNodeID = 7, Name = "Patching", ParentContainerNodeID = 0 },
        new { ContainerNodeID = 8, Name = "Servers", ParentContainerNodeID = 7 }
    ];

    private static readonly object[] FolderItems =
    [
        new { InstanceKey = "MCM00020", ContainerNodeID = 8 },
        new { InstanceKey = "MCM00021", ContainerNodeID = 7 }
    ];

    private static StubSitePowerShell Site(string objectPath, bool noObjectPath = false)
    {
        object options = noObjectPath
            ? new { Reject = "xx", Folders, FolderItems, NoObjectPath = true }
            : new { Reject = "xx", Folders, FolderItems };
        return new StubSitePowerShell(State(options,
            new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", ObjectPath = objectPath, Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00020", Name = "Patch servers", LimitToCollectionID = "SMS00001", ObjectPath = objectPath, Rules = Array.Empty<object>() },
            new { CollectionID = "MCM00021", Name = "Patch pilot", LimitToCollectionID = "SMS00001", ObjectPath = objectPath, Rules = Array.Empty<object>() }));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("", true)]
    public async Task AnEmptyOrMissingObjectPathTakesTheFolderFromTheFolderTree(string objectPath, bool noObjectPath)
    {
        if (!WindowsPowerShellPresent) return;
        var site = Site(objectPath, noObjectPath);
        var service = new CollectionService(site);

        var rows = await service.ListCollectionsAsync(CancellationToken.None);

        Assert.Equal(@"Patching\Servers", rows.Single(r => r.CollectionId == "MCM00020").FolderPath);
        Assert.Equal("Patching", rows.Single(r => r.CollectionId == "MCM00021").FolderPath);
        Assert.Equal("", rows.Single(r => r.CollectionId == "SMS00001").FolderPath);
        Assert.Equal(1, site.Log.Count(l => l == "Folders"));
    }

    [Fact]
    public async Task OpenByIdAndTheDetailShowTheFolderFromTheFolderTree()
    {
        if (!WindowsPowerShellPresent) return;
        var service = new CollectionService(Site(""));

        Assert.Equal(@"Patching\Servers", (await service.GetCollectionAsync("MCM00020", CancellationToken.None))!.FolderPath);
        Assert.Equal(@"Patching\Servers", (await service.GetDetailAsync("MCM00020", 0, CancellationToken.None)).Row.FolderPath);
    }

    [Fact]
    public async Task AFilledObjectPathIsUsedWithoutTheFolderQueries()
    {
        if (!WindowsPowerShellPresent) return;
        var site = Site("/Elsewhere");
        var rows = await new CollectionService(site).ListCollectionsAsync(CancellationToken.None);

        Assert.All(rows, r => Assert.Equal("Elsewhere", r.FolderPath));
        Assert.DoesNotContain("Folders", site.Log);
    }
}
