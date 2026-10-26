using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Tests;

public sealed class AppLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aplog-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AnEntryGoesToTheTailAndToOneSessionFileWithLevelAndArea()
    {
        var log = new AppLog(_folder);
        log.Write(AppLogLevel.Info, "ConfigMgr", "Connected to site ABC.");
        log.Write(AppLogLevel.Error, "History", "Write failed.");

        Assert.Equal([AppLogLevel.Info, AppLogLevel.Error], log.Tail().Select(e => e.Level));
        Assert.Equal(["ConfigMgr", "History"], log.Tail().Select(e => e.Area));
        var file = Assert.Single(Directory.GetFiles(_folder, "app-*.log"));
        Assert.Equal(file, log.SessionFile);
        var lines = File.ReadAllLines(file).Where(l => l.Length > 0).ToList();
        Assert.Contains(lines, l => l.Contains("INFO") && l.Contains("ConfigMgr") && l.EndsWith("Connected to site ABC.", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("ERROR") && l.Contains("History") && l.EndsWith("Write failed.", StringComparison.Ordinal));
    }

    [Fact]
    public void NoFileIsCreatedBeforeTheFirstEntry()
    {
        _ = new AppLog(_folder);
        Assert.False(Directory.Exists(_folder) && Directory.GetFiles(_folder).Length > 0);
    }

    [Fact]
    public void TheTailKeepsOnlyTheNewestEntries()
    {
        var log = new AppLog(_folder, tailSize: 3);
        for (var i = 1; i <= 5; i++) log.Write(AppLogLevel.Info, "A", "m" + i);
        Assert.Equal(["m3", "m4", "m5"], log.Tail().Select(e => e.Message));
    }

    [Fact]
    public void ADebugEntryBelowTheMinimumLevelIsDropped()
    {
        var log = new AppLog(_folder) { MinimumLevel = AppLogLevel.Info };
        log.Write(AppLogLevel.Debug, "A", "noise");
        log.Write(AppLogLevel.Warning, "A", "kept");
        Assert.Equal(["kept"], log.Tail().Select(e => e.Message));
    }

    [Fact]
    public void TheSameRetentionAsThePackagesLogKeepsThirtySessionFilesAndLeavesOtherLogsAlone()
    {
        Directory.CreateDirectory(_folder);
        for (var i = 0; i < 35; i++)
            File.WriteAllText(Path.Combine(_folder, $"app-20200101-0000{i:00}-1.log"), "old");
        File.WriteAllText(Path.Combine(_folder, "packr-20200101-000000.log"), "packages");

        var log = new AppLog(_folder);
        log.Write(AppLogLevel.Info, "A", "new");

        var sessions = Directory.GetFiles(_folder, "app-*.log");
        Assert.Equal(AppLog.KeepSessionFiles, sessions.Length);
        Assert.Contains(log.SessionFile, sessions);
        Assert.DoesNotContain(Path.Combine(_folder, "app-20200101-000000-1.log"), sessions);
        Assert.True(File.Exists(Path.Combine(_folder, "packr-20200101-000000.log")));
    }

    [Theory]
    [InlineData("password=Hunter2 next", "Hunter2")]
    [InlineData("ClientSecret: abc123def", "abc123def")]
    [InlineData("token=eyJhbGciOi rest", "eyJhbGciOi")]
    [InlineData("-Password 'P@ss word'", "P@ss word")]
    [InlineData("apikey = k-123", "k-123")]
    public void ASecretAfterAKnownKeyIsMasked(string message, string secret)
    {
        var log = new AppLog(_folder);
        log.Write(AppLogLevel.Info, "A", message);
        Assert.DoesNotContain(secret, log.Tail().Single().Message);
        Assert.Contains(SecretMask.Mask, log.Tail().Single().Message);
        Assert.DoesNotContain(secret, File.ReadAllText(log.SessionFile!));
    }

    [Fact]
    public void ARegisteredSecretValueIsMaskedWhereverItAppears()
    {
        var log = new AppLog(_folder);
        log.RegisterSecret("s3cr3t-value");
        log.Write(AppLogLevel.Error, "A", "The command failed: bad value s3cr3t-value in request");
        Assert.Equal("The command failed: bad value *** in request", log.Tail().Single().Message);
    }

    [Fact]
    public void AMultiLineMessageStaysOneLine()
    {
        var log = new AppLog(_folder);
        log.Write(AppLogLevel.Info, "A", "first\r\nsecond\nthird");
        Assert.Equal("first | second | third", log.Tail().Single().Message);
    }

    [Fact]
    public void ConcurrentWritersLoseNoEntry()
    {
        var log = new AppLog(_folder, tailSize: 10_000);
        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 200; i++) log.Write(AppLogLevel.Info, "T" + t, "entry " + i);
        });
        Assert.Equal(1600, log.Tail().Count);
        Assert.Equal(1600, File.ReadAllLines(log.SessionFile!).Count(l => l.Contains("entry ")));
    }

    [Fact]
    public void AnUnwritableFolderStillKeepsTheTail()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "aplog-block-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            var log = new AppLog(blocker);
            log.Write(AppLogLevel.Warning, "A", "kept in memory");
            Assert.Equal("kept in memory", log.Tail().Single().Message);
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public void AThrowingWrittenSubscriberNeverReachesWriteAndTheOtherSubscribersStillRun()
    {
        var log = new AppLog(_folder);
        var seen = new List<string>();
        log.Written += (_, _) => throw new InvalidOperationException("subscriber failed");
        log.Written += (_, e) => seen.Add(e.Message);

        log.Write(AppLogLevel.Info, "A", "first");
        log.Write(AppLogLevel.Info, "A", "second");

        Assert.Equal(["first", "second"], seen);
        Assert.Equal(["first", "second"], log.Tail().Select(e => e.Message));
        Assert.Contains("second", File.ReadAllText(log.SessionFile!));
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void AFolderThatCannotBeCreatedIsTriedAgainAfterTheRetryInterval()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, "Logs");
        File.WriteAllText(target, "a file where the folder should be");
        var log = new AppLog(target, time: time);

        log.Write(AppLogLevel.Info, "A", "while blocked");
        Assert.Null(log.SessionFile);
        File.Delete(target);

        time.Now += AppLog.FileRetryInterval - TimeSpan.FromSeconds(1);
        log.Write(AppLogLevel.Info, "A", "before the interval");
        Assert.False(Directory.Exists(target));
        Assert.Null(log.SessionFile);

        time.Now += TimeSpan.FromSeconds(2);
        log.Write(AppLogLevel.Info, "A", "after the interval");
        Assert.NotNull(log.SessionFile);
        var text = File.ReadAllText(log.SessionFile!);
        Assert.Contains("after the interval", text);
        Assert.DoesNotContain("before the interval", text);
        Assert.Equal(3, log.Tail().Count);
    }

    [Fact]
    public void ASessionFileThatWasBusyIsWrittenAgainAfterTheRetryInterval()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        var log = new AppLog(_folder, time: time);
        log.Write(AppLogLevel.Info, "A", "one");
        var file = log.SessionFile!;

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            log.Write(AppLogLevel.Info, "A", "two");
            Assert.Null(log.SessionFile);
        }

        time.Now += TimeSpan.FromSeconds(5);
        log.Write(AppLogLevel.Info, "A", "three");
        time.Now += AppLog.FileRetryInterval;
        log.Write(AppLogLevel.Info, "A", "four");

        Assert.Equal(file, log.SessionFile);
        var lines = File.ReadAllLines(file);
        Assert.Contains(lines, l => l.EndsWith("one", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("four", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.EndsWith("three", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(_folder, "app-*.log"));
    }

    [Fact]
    public void WrittenIsRaisedWithTheMaskedEntry()
    {
        var log = new AppLog(_folder);
        AppLogEntry? seen = null;
        log.Written += (_, e) => seen = e;
        log.Write(AppLogLevel.Info, "A", "password=abc");
        Assert.NotNull(seen);
        Assert.DoesNotContain("abc", seen!.Message);
    }
}
