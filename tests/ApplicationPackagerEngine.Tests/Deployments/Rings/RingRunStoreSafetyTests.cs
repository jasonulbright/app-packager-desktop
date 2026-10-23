using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>The lock heartbeat and token check, the lock taken before a new run file is visible, pinned folders, the ring cap and unreadable ring values.</summary>
public sealed class RingRunStoreSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ringsafe-" + Guid.NewGuid().ToString("N"));
    private DateTime _utc = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Lab = TimeZoneInfo.CreateCustomTimeZone("Lab", TimeSpan.FromHours(-4), "Lab", "Lab");

    private string Folder => Path.Combine(_root, "runs");
    private RingRunStore Store(Func<string?>? folder = null, Func<string>? user = null) =>
        new(folder ?? (() => Folder), utcNow: () => _utc, local: Lab, user: user ?? (() => @"CONTOSO\Admin"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void AHeldLockIsTouchedAndASaveRefusesWhenTheLockFileIsNoLongerThisSessions()
    {
        var store = Store();
        var run = store.Create(RingRunStoreTests.NewRun(), "MCM0001B");
        var lockPath = Path.Combine(Folder, run.FileName + ".lock");
        using var held = store.Lock(run.FileName);
        File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddHours(-3));
        held.Touch();
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromMinutes(5));

        File.Delete(lockPath);
        File.WriteAllText(lockPath, @"CONTOSO\Other on PC2 at 2026-10-20 08:00:00, process 9, lock 0123456789abcdef");
        var before = File.ReadAllText(Path.Combine(Folder, run.FileName));
        var ex = Assert.Throws<DeploymentServiceException>(() => store.Save(run with { Rings = [.. run.Rings.Select(r => r with { Status = RingStatuses.Cancelled })] }, held));
        Assert.Contains("not held by this session", ex.Message);
        Assert.Equal(before, File.ReadAllText(Path.Combine(Folder, run.FileName)));
    }

    [Fact]
    public void ASaveRefusesALockOfAnotherRunFile()
    {
        var store = Store();
        var first = store.Create(RingRunStoreTests.NewRun(), "MCM0001B");
        var second = store.Create(RingRunStoreTests.NewRun(), "MCM0001B");
        using var held = store.Lock(first.FileName);
        Assert.Throws<DeploymentServiceException>(() => store.Save(second, held));
    }

    [Fact]
    public void TheStaleLockTextSaysToCheckBeforeTheLockIsRemoved()
    {
        var text = RingRunStore.LockedMessage(new RingLockInfo(@"C:\runs\a.json.lock", @"CONTOSO\Admin on PC1", TimeSpan.FromHours(2), true));
        Assert.Contains("Another session may still be running: check before you remove the lock.", text);
        Assert.DoesNotContain("probably ended", text);
    }

    [Fact]
    public void AFailedOwnerWriteLeavesNoLockFile()
    {
        var store = Store();
        var run = store.Create(RingRunStoreTests.NewRun(), "MCM0001B");
        var failing = Store(user: () => throw new InvalidOperationException("no identity"));
        Assert.ThrowsAny<Exception>(() => failing.Lock(run.FileName));
        Assert.False(File.Exists(Path.Combine(Folder, run.FileName + ".lock")));
        using (store.Lock(run.FileName)) { }
    }

    [Fact]
    public void ANewRunFileIsLockedBeforeItIsVisible()
    {
        var store = Store();
        Directory.CreateDirectory(Folder);
        // A lock file left for the first free name shows that the lock is taken first: that name is skipped.
        File.WriteAllText(Path.Combine(Folder, "Office_Rings_MCM0001B_20261020-080000.json.lock"), "left by a crash");
        var (run, held) = store.CreateLocked(RingRunStoreTests.NewRun(), "MCM0001B");
        using (held)
        {
            Assert.Equal("Office_Rings_MCM0001B_20261020-080000-2.json", run.FileName);
            Assert.True(File.Exists(Path.Combine(Folder, run.FileName + ".lock")));
            Assert.Throws<DeploymentServiceException>(() => Store().Lock(run.FileName));
            Assert.Equal(RingStatuses.Created, store.Save(run with { Rings = [.. run.Rings.Select(r => r.Index == 1 ? r with { Status = RingStatuses.Created, DeploymentId = "MCM20040" } : r)] }, held).Rings[0].Status);
        }
        Assert.False(File.Exists(Path.Combine(Folder, run.FileName + ".lock")));
    }

    [Fact]
    public void APinnedStoreKeepsItsFolderWhenTheSettingChanges()
    {
        var current = Folder;
        var store = Store(() => current);
        var pinned = store.Pinned();
        current = Path.Combine(_root, "other");
        Assert.Equal(Folder, pinned.Folder);
        Assert.Equal(current, store.Folder);
    }

    private string WriteRun(Action<JsonObject> edit, int rings = 3)
    {
        var store = Store();
        var run = store.Create(RingRunStoreTests.NewRun(), "MCM0001B");
        var path = Path.Combine(Folder, run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (rings > 3)
        {
            var list = json["Rings"]!.AsArray();
            for (var i = 4; i <= rings; i++)
            {
                var copy = list[0]!.DeepClone().AsObject();
                copy["Index"] = i;
                copy["CollectionID"] = "MCMX" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
                list.Add(copy);
            }
        }
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
        return run.RunId;
    }

    [Fact]
    public void ARunFileWithMoreThanOneHundredRingsIsFlaggedAndANewOneIsRefused()
    {
        var id = WriteRun(_ => { }, rings: 101);
        var run = Assert.Single(Store().List(false, out _));
        Assert.Equal(id, run.RunId);
        Assert.Contains(run.Problems, p => p.Contains("at most 100 rings", StringComparison.Ordinal));
        var template = RingRunStoreTests.NewRun();
        var big = template with { Rings = [.. Enumerable.Range(1, 101).Select(i => template.Rings[0] with { Index = i, CollectionId = "MCMX" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) })] };
        var ex = Assert.Throws<DeploymentServiceException>(() => Store().Create(big, "MCM0001B"));
        Assert.Contains("100", ex.Message);
    }

    [Theory]
    [InlineData("SuccessThresholdPercent", "99.5")]
    [InlineData("SuccessThresholdPercent", "150")]
    [InlineData("SuccessThresholdPercent", "high")]
    [InlineData("OverrideServiceWindow", "maybe")]
    [InlineData("ShiftedMinutes", "1.5")]
    [InlineData("Status", "Paused")]
    public void AnUnreadableRingValueFlagsTheRingAndNeverFailsOpen(string field, string value)
    {
        WriteRun(json => json["Rings"]![0]![field] = value);
        var run = Assert.Single(Store().List(false, out _));
        Assert.NotEqual("", run.Rings[0].Problem);
        Assert.StartsWith("Unreadable ring 1", run.Rings[0].Problem, StringComparison.Ordinal);
        Assert.Contains(run.Rings[0].Problem, run.Problems);
    }
}
