using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DetectionHistoryStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dthistory-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestCleanup.DeleteDirectory(_folder);
    }

    private string HistoryFile => Path.Combine(_folder, DetectionHistoryStore.FileName);

    private static DetectionHistoryEntry Entry(int n, DateTimeOffset? time = null) => new(
        Guid.NewGuid(),
        time ?? DateTimeOffset.UnixEpoch.AddMinutes(n),
        "RegistryKey",
        $"Clause {n}",
        Negative: n % 2 == 0,
        Passed: n % 3 == 0,
        $"Summary {n}",
        new StageDetection { Type = "RegistryKey", RegistryKeyRelative = $@"SOFTWARE\Test{n}", Hive = "CurrentUser", Is64Bit = false });

    [Fact]
    public void EntriesSurviveANewStoreNewestFirstWithTheirDetection()
    {
        var store = new DetectionHistoryStore(_folder);
        store.Add(Entry(1));
        store.Add(Entry(2));

        var reloaded = new DetectionHistoryStore(_folder).All();

        Assert.Equal(["Clause 2", "Clause 1"], reloaded.Select(e => e.Description));
        var second = reloaded[0];
        Assert.True(second.Negative);
        Assert.Equal(@"SOFTWARE\Test2", second.Detection.RegistryKeyRelative);
        Assert.Equal("CurrentUser", second.Detection.Hive);
        Assert.False(second.Detection.Is64Bit);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(2), second.Time);
    }

    [Fact]
    public void AHistoryFolderThatIsAJunctionIsNeitherReadNorWritten()
    {
        var target = Path.Combine(_folder + "-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, DetectionHistoryStore.FileName), """{"Version":1,"Entries":[]}""");
        Junctions.Create(_folder, target);
        try
        {
            var store = new DetectionHistoryStore(_folder);

            Assert.Equal(DetectionFolderRefusedException.HistoryMessage,
                Assert.Throws<DetectionFolderRefusedException>(() => store.All()).Message);
            Assert.Throws<DetectionFolderRefusedException>(() => store.Add(Entry(1)));
            Assert.Throws<DetectionFolderRefusedException>(store.Clear);
            Assert.Equal("""{"Version":1,"Entries":[]}""", File.ReadAllText(Path.Combine(target, DetectionHistoryStore.FileName)));
            Assert.Single(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(_folder);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultHistoryFolderIsRefusedWhenItIsAJunction()
    {
        var saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);
        var target = _folder + "-target";
        string? link = null;
        Directory.CreateDirectory(target);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _folder);
            Directory.CreateDirectory(AppDataPaths.Root);
            link = DetectionHistoryStore.DefaultFolder();
            Junctions.Create(link, target);

            var store = new DetectionHistoryStore();

            Assert.Throws<DetectionFolderRefusedException>(() => store.Add(Entry(1)));
            Assert.Empty(Directory.GetFiles(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, saved);
            if (link is not null) Junctions.Remove(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void NoFileIsWrittenBeforeTheFirstEntry()
    {
        Assert.Empty(new DetectionHistoryStore(_folder).All());
        Assert.False(File.Exists(HistoryFile));
    }

    [Fact]
    public void OnlyTheNewestEntriesAreKept()
    {
        var store = new DetectionHistoryStore(_folder, capacity: 5);
        for (var i = 1; i <= 8; i++) store.Add(Entry(i));

        Assert.Equal(["Clause 8", "Clause 7", "Clause 6", "Clause 5", "Clause 4"], store.All().Select(e => e.Description));
        Assert.Equal(5, new DetectionHistoryStore(_folder, capacity: 5).All().Count);
    }

    [Fact]
    public void TheDefaultBoundIsFiveHundred() =>
        Assert.Equal(500, DetectionHistoryStore.DefaultCapacity);

    [Fact]
    public void AFileHoldingMoreThanTheBoundIsCutOnLoad()
    {
        var store = new DetectionHistoryStore(_folder, capacity: 10);
        for (var i = 1; i <= 10; i++) store.Add(Entry(i));

        Assert.Equal(3, new DetectionHistoryStore(_folder, capacity: 3).All().Count);
    }

    [Fact]
    public void ClearEmptiesTheStoreAndTheFile()
    {
        var store = new DetectionHistoryStore(_folder);
        store.Add(Entry(1));
        store.Clear();

        Assert.Empty(store.All());
        Assert.Empty(new DetectionHistoryStore(_folder).All());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void ACorruptFileIsKeptAsideAndTheStoreStartsEmpty(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(HistoryFile, content);

        var store = new DetectionHistoryStore(_folder);
        Assert.Empty(store.All());
        store.Add(Entry(1));

        var aside = Assert.Single(Directory.GetFiles(_folder, DetectionHistoryStore.FileName + ".unreadable-*"));
        Assert.Equal(content, File.ReadAllText(aside));
        Assert.Equal(["Clause 1"], new DetectionHistoryStore(_folder).All().Select(e => e.Description));
    }

    [Fact]
    public void ACorruptFileIsLeftInPlaceUntilTheStoreWrites()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(HistoryFile, "{ not json");

        Assert.Empty(new DetectionHistoryStore(_folder).All());

        Assert.Equal("{ not json", File.ReadAllText(HistoryFile));
        Assert.Empty(Directory.GetFiles(_folder, DetectionHistoryStore.FileName + ".unreadable-*"));
    }

    private FileStream LockHistory() =>
        new(HistoryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public async Task ABrieflyLockedFileIsReadOnARetry()
    {
        new DetectionHistoryStore(_folder).Add(Entry(1));
        var held = LockHistory();
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            held.Dispose();
        });

        var entries = new DetectionHistoryStore(_folder).All();
        await release;

        Assert.Equal(["Clause 1"], entries.Select(e => e.Description));
    }

    [Fact]
    public void ALockedFileIsNotTreatedAsCorruptAndItsHistoryIsKept()
    {
        var first = new DetectionHistoryStore(_folder);
        first.Add(Entry(1));
        first.Add(Entry(2));
        var store = new DetectionHistoryStore(_folder);

        using (LockHistory())
        {
            Assert.Empty(store.All());
            store.Add(Entry(3));
            Assert.Equal(["Clause 3"], store.All().Select(e => e.Description));
        }
        store.Add(Entry(4));

        Assert.Equal(["Clause 4", "Clause 3", "Clause 2", "Clause 1"], store.All().Select(e => e.Description));
        Assert.Equal(["Clause 4", "Clause 3", "Clause 2", "Clause 1"],
            new DetectionHistoryStore(_folder).All().Select(e => e.Description));
        Assert.Empty(Directory.GetFiles(_folder, DetectionHistoryStore.FileName + ".unreadable-*"));
    }

    [Fact]
    public void AnEntryWithoutADetectionIsDroppedAndTheRestLoad()
    {
        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid();
        File.WriteAllText(HistoryFile, $$"""
            { "Version": 1, "Entries": [
              { "Id": "{{id}}", "Time": "2026-10-18T10:00:00+00:00", "Kind": "File", "Description": "Good", "Negative": false, "Passed": true, "Summary": "s",
                "Detection": { "Type": "File", "FilePath": "C:\\x", "FileName": "a.exe" } },
              { "Id": "{{Guid.NewGuid()}}", "Time": "2026-10-18T09:00:00+00:00", "Kind": "File", "Description": "Bad", "Negative": false, "Passed": true, "Summary": "s" },
              null
            ] }
            """);

        var entries = new DetectionHistoryStore(_folder).All();

        var entry = Assert.Single(entries);
        Assert.Equal(id, entry.Id);
        Assert.Equal("a.exe", entry.Detection.FileName);
    }

    [Fact]
    public void AnUnwritableFolderKeepsTheEntryInMemoryWithoutThrowing()
    {
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(HistoryFile);

        var store = new DetectionHistoryStore(_folder);
        store.Add(Entry(1));

        Assert.Equal(["Clause 1"], store.All().Select(e => e.Description));
    }

    [Fact]
    public async Task ConcurrentAddsLoseNothing()
    {
        var store = new DetectionHistoryStore(_folder);

        await Task.WhenAll(Enumerable.Range(1, 40).Select(i => Task.Run(() => store.Add(Entry(i)))));

        Assert.Equal(40, store.All().Count);
        Assert.Equal(40, new DetectionHistoryStore(_folder).All().Count);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void TheDefaultFolderIsUnderTheUserDataRoot() =>
        Assert.Equal(Path.Combine(AppDataPaths.Root, "DetectionTester"), DetectionHistoryStore.DefaultFolder());
}
