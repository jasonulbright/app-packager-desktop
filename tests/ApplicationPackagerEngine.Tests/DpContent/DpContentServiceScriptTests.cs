using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.DpContent.DpContentHarness;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The real DP content script through the stub site with CM01 shapes.</summary>
public sealed class DpContentServiceScriptTests
{
    private const string Cm01 = StubDpContentSite.Nal;
    private static readonly string Backup = StubDpContentSite.NalOf("CM01.contoso.com.backup");

    [Fact]
    public async Task TheLoadIsOneScriptRunAndNamesEveryRow()
    {
        using var h = new DpContentHarness();
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);

        Assert.Single(h.Site.Runs);
        Assert.False(snapshot.Truncated);
        Assert.Equal(7, snapshot.Pairs.Count);
        Assert.Equal(7, snapshot.TotalPairs);
        var cm01 = snapshot.Dps.Single(d => d.DpName == "CM01.CONTOSO.COM");
        Assert.Equal(6, cm01.Total);
        Assert.Equal(5, cm01.Installed);
        Assert.Equal(1, cm01.Failed);
        Assert.Equal(0, cm01.InProgress);
        Assert.True(cm01.CountsKnown);
        var orphan = snapshot.Content.Single(c => c.PackageId == "MCM00005");
        Assert.Equal("unknown content (MCM00005)", orphan.Name);
        Assert.Equal(DpContentTypes.Package, orphan.ContentType);
        var app = snapshot.Content.Single(c => c.PackageId == "MCM000A8");
        Assert.Equal(DpContentTypes.Application, app.ContentType);
        Assert.Equal("16780955", app.ObjectId);
        var failing = snapshot.Pairs.Single(p => p.PackageId == "MCM000A7");
        Assert.Equal(DpContentStates.ContentUpdating, failing.State);
        Assert.Equal(2306, failing.MessageId);
        Assert.True(failing.IsFailed);
        Assert.True(failing.IsIssue);
        Assert.Equal(DpContentTypes.TaskSequence, snapshot.Content.Single(c => c.PackageId == "MCM00008").ContentType);
        Assert.Equal(0, snapshot.Content.Single(c => c.PackageId == "MCM00008").DpsTargeted);
    }

    [Fact]
    public async Task ALoadOverTheCapIsTruncatedAndSaysSo()
    {
        var h0 = StubDpContentSite.Lab();
        using var h = new DpContentHarness(h0);
        var service = new DpContentService(h.Site, h.Audit) { MaxPairs = 3 };
        var snapshot = await service.LoadAsync(new Progress<string>(), CancellationToken.None);

        Assert.True(snapshot.Truncated);
        Assert.Equal(3, snapshot.Pairs.Count);
        Assert.Equal(7, snapshot.TotalPairs);
        Assert.Contains("showing 3 of 7", snapshot.Note);
    }

    [Fact]
    public async Task OverlappingDpNamesChangeOnlyTheExactPair()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0));
        var outcome = await h.ApplyAsync(change);

        Assert.Equal(DpContentResults.Confirmed, Assert.Single(outcome.Pairs).Result);
        Assert.Contains("Put:MCM000A6", h.Site.Log);
        Assert.Single(h.Site.Log, l => l == "Put:MCM000A6");
        var backup = h.Site.List("Instances").OfType<JsonObject>().Single(r => (string?)r["ServerNALPath"] == Backup);
        Assert.False((bool)backup["RefreshNow"]!);
        Assert.Equal(0, (int)backup["Status"]!);
    }

    [Fact]
    public async Task APairIsFoundWhenTheSiteHoldsItsNalPathInOtherLetterCase()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Validate, Pair("MCM000A8", Cm01, 0, DpContentTypes.Application));
        var outcome = await h.ApplyAsync(change);

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Confirmed, pair.Result);
        Assert.Contains("2384", pair.Message);
    }

    [Fact]
    public async Task AFailedCallIsReportedFailedNeverOk()
    {
        using var h = new DpContentHarness();
        h.Site.Fail("put:MCM000A6");
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0));
        var outcome = await h.ApplyAsync(change);

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Failed, pair.Result);
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("SECRET", pair.Message);
        Assert.DoesNotContain("SECRET", outcome.Message);
        Assert.Equal("ConfigMgr reported an error. The log has the kind of failure.", pair.Message);
    }

    [Fact]
    public async Task AnAccessDenialIsReportedWithAPlainReason()
    {
        using var h = new DpContentHarness();
        h.Site.Fail("access:MCM000A6");
        var change = await h.PrepareAsync(DpContentChangeKind.Validate, Pair("MCM000A6", Cm01, 0));
        var outcome = await h.ApplyAsync(change);

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Failed, pair.Result);
        Assert.StartsWith("ConfigMgr refused the change: the account has no right", pair.Message);
    }

    [Fact]
    public async Task RedistributeOfAFailingPairReportsTheNewError()
    {
        using var h = new DpContentHarness();
        h.Site.SetOption("RefreshError:MCM000A7", true);
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A7", Cm01, 7));
        var outcome = await h.ApplyAsync(change);

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Failed, pair.Result);
        Assert.Contains("status message 2306", pair.Message);
    }

    [Fact]
    public async Task ARequestTheSiteHasNotFinishedInTheWindowStaysRequested()
    {
        using var h = new DpContentHarness(window: TimeSpan.FromSeconds(4));
        h.Site.SetOption("Ticks", 50);
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0));
        var outcome = await h.ApplyAsync(change);

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Requested, pair.Result);
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public async Task ARequestWithNoSignOfWorkReadsUnchanged()
    {
        using var h = new DpContentHarness(window: TimeSpan.FromSeconds(10));
        h.Site.SetOption("NoChange:MCM000A6", true);
        var change = await h.PrepareAsync(DpContentChangeKind.Validate, Pair("MCM000A6", Cm01, 0));
        h.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] != "State") return;
            foreach (var i in site.List("Instances").OfType<JsonObject>().Where(r => (string?)r["PackageID"] == "MCM000A6")) i["Status"] = 0;
        };
        var outcome = await h.ApplyAsync(change);

        Assert.Equal(DpContentResults.Unchanged, Assert.Single(outcome.Pairs).Result);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task RemovalWithoutTheSecondConfirmationIsRefusedInTheService()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        Assert.True(change.NeedsRemovalConfirmation);
        var runs = h.Site.Runs.Count;
        var outcome = await h.ApplyAsync(change with { LastCopyAcknowledged = true });

        Assert.False(outcome.Succeeded);
        Assert.StartsWith(DpContentService.SkippedPrefix, outcome.Message);
        Assert.Contains("second confirmation", outcome.Message);
        Assert.Equal(runs, h.Site.Runs.Count);
        Assert.NotNull(h.Site.Summary("MCM000A6", Cm01));
        Assert.Empty(h.AuditLines());
    }

    [Fact]
    public async Task RemovingTheLastCopyIsRefusedWithoutTheBox()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM0001B", Cm01, 0));
        Assert.True(change.NeedsLastCopyAcknowledgement);
        Assert.Contains(change.Checks, c => c.Name == DpContentService.CheckNames.LastCopy && c.Result == DeploymentCheckResults.Warn);
        var impact = Assert.Single(change.ContentImpact);
        Assert.Equal(0, impact.DpsRemaining);
        Assert.Equal(3, impact.DeploymentCount);

        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });
        Assert.False(outcome.Succeeded);
        Assert.Contains("no distribution point with the content installed", outcome.Message);
        Assert.NotNull(h.Site.Summary("MCM0001B", Cm01));
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARemovalThatLeavesAnotherCopyNeedsNoLastCopyBoxAndCarriesItsUndo()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        Assert.False(change.NeedsLastCopyAcknowledgement);
        Assert.Equal(1, Assert.Single(change.ContentImpact).DpsRemaining);

        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });
        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Confirmed, pair.Result);
        Assert.Null(h.Site.Summary("MCM000A6", Cm01));
        Assert.NotNull(h.Site.Summary("MCM000A6", Backup));
        var undo = Assert.IsType<DpContentChange>(outcome.UndoChange);
        Assert.Equal(DpContentChangeKind.DistributeAgain, undo.Kind);
        Assert.Equal(Cm01, Assert.Single(undo.Pairs).NalPath);
        Assert.Null(undo.Fingerprint);
    }

    [Fact]
    public async Task UndoRestoresOnlyThePairsThatWereInstalled()
    {
        using var h = new DpContentHarness();
        h.Site.SetOption("RemovePending:MCM000A7", true);
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0), Pair("MCM000A7", Cm01, 7));
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true, LastCopyAcknowledged = true });

        Assert.All(outcome.Pairs, p => Assert.Contains(p.Result, new[] { DpContentResults.Confirmed, DpContentResults.Requested }));
        var undo = Assert.IsType<DpContentChange>(outcome.UndoChange);
        Assert.Equal("MCM000A6", Assert.Single(undo.Pairs).PackageId);
    }

    [Fact]
    public async Task DistributeAgainBringsTheContentBack()
    {
        using var h = new DpContentHarness();
        var removed = await h.ApplyAsync((await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A8", Cm01, 0, DpContentTypes.Application))) with { RemovalConfirmed = true, LastCopyAcknowledged = true });
        var undo = await h.Service.PrepareAsync(removed.UndoChange!, CancellationToken.None);
        Assert.False(undo.Blocked);
        var outcome = await h.ApplyAsync(undo);

        Assert.Equal(DpContentResults.Confirmed, Assert.Single(outcome.Pairs).Result);
        Assert.Contains(h.Site.Log, l => l.StartsWith("Start-CMContentDistribution:SMS_ContentPackage:MCM000A8:CM01.CONTOSO.COM", StringComparison.Ordinal));
        Assert.DoesNotContain("DependenciesIncluded", h.Site.Log);
        Assert.Equal(0, (int)h.Site.Summary("MCM000A8", Cm01)!["State"]!);
        Assert.Null(outcome.RetryChange);
    }

    [Fact]
    public async Task ADistributionTheSiteIgnoresReadsUnchangedAndOffersARetry()
    {
        using var h = new DpContentHarness(window: TimeSpan.FromSeconds(10));
        var removed = await h.ApplyAsync((await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0))) with { RemovalConfirmed = true });
        h.Site.SetOption("DistributeIgnored:MCM000A6", true);
        var outcome = await h.ApplyAsync(await h.Service.PrepareAsync(removed.UndoChange!, CancellationToken.None));

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Unchanged, pair.Result);
        Assert.Contains("run it again later", pair.Message);
        Assert.Equal("MCM000A6", Assert.Single(outcome.RetryChange!.Pairs).PackageId);
    }

    [Fact]
    public async Task APairThatChangedBeforePrepareIsLeftOutWithASkippedMessage()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 3), Pair("MCM0001B", Cm01, 0));

        Assert.Equal("MCM0001B", Assert.Single(change.Pairs).PackageId);
        var dropped = Assert.Single(change.Dropped);
        Assert.Equal(DpContentResults.Skipped, dropped.Result);
        Assert.StartsWith("Skipped: the state changed from Install failed to Installed", dropped.Message);
    }

    [Fact]
    public async Task APairThatChangedBetweenPrepareAndTheWriteIsSkipped()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0), Pair("MCM000A6", Backup, 0));
        h.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] != "Apply") return;
            site.Summary("MCM000A6", Backup)!["State"] = 3;
        };
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true, LastCopyAcknowledged = true });

        var stale = outcome.Pairs.Single(p => p.Pair.NalPath == Backup);
        Assert.Equal(DpContentResults.Skipped, stale.Result);
        Assert.StartsWith("Skipped: the pair changed before the write: it is Install failed now.", stale.Message);
        Assert.Equal(DpContentResults.Confirmed, outcome.Pairs.Single(p => p.Pair.NalPath == Cm01).Result);
        Assert.NotNull(h.Site.Summary("MCM000A6", Backup));
    }

    [Fact]
    public async Task APlanThatWasNotPreparedOrWasWidenedIsRefused()
    {
        using var h = new DpContentHarness();
        var outcome = await h.ApplyAsync(new DpContentChange(DpContentChangeKind.Redistribute, [Pair("MCM000A6", Cm01, 0)], false, false, [], [], null));
        Assert.StartsWith("Skipped: the plan was not prepared", outcome.Message);

        var prepared = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0));
        var widened = prepared with { Pairs = [.. prepared.Pairs, Pair("MCM0001B", Cm01, 0)] };
        var refused = await h.ApplyAsync(widened);
        Assert.StartsWith("Skipped: the plan holds a pair that the prepared plan does not hold", refused.Message);
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Put:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUntickedPairIsNotChanged()
    {
        using var h = new DpContentHarness();
        var prepared = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0), Pair("MCM000A6", Backup, 0));
        var outcome = await h.ApplyAsync(prepared with { Pairs = prepared.Pairs.Where(p => p.NalPath == Backup).ToList() });

        Assert.Equal(Backup, Assert.Single(outcome.Pairs).Pair.NalPath);
        Assert.Single(h.Site.Log, l => l == "Put:MCM000A6");
        var cm01 = h.Site.List("Instances").OfType<JsonObject>().Single(r => (string?)r["ServerNALPath"] == Cm01 && (string?)r["PackageID"] == "MCM000A6");
        Assert.False((bool)cm01["RefreshNow"]!);
    }

    [Fact]
    public async Task TheAuditLogMustBeWritableBeforeARemoval()
    {
        using var h0 = new DpContentHarness();
        Directory.CreateDirectory(Path.Combine(h0.Folder, "log-as-folder.jsonl"));
        using var h = new DpContentHarness(auditPath: Path.Combine(h0.Folder, "log-as-folder.jsonl"));
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        Assert.True(change.Blocked);
        Assert.Contains(change.Checks, c => c.Name == DpContentService.CheckNames.AuditLog && c.Result == DeploymentCheckResults.Fail);

        var forced = await h.ApplyAsync(change with { RemovalConfirmed = true, Checks = [] });
        Assert.False(forced.Succeeded);
        Assert.StartsWith("Skipped: the audit log cannot be written, so nothing was removed.", forced.Message);
        Assert.NotNull(h.Site.Summary("MCM000A6", Cm01));
    }

    [Fact]
    public async Task ARemovalWritesItsAuditRecordBeforeTheWrite()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        var linesAtWrite = -1;
        h.Site.BeforeRun = (p, _) => { if ((string?)p["Operation"] == "Apply") linesAtWrite = h.AuditLines().Count; };
        await h.ApplyAsync(change with { RemovalConfirmed = true });

        Assert.Equal(1, linesAtWrite);
        var records = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished", "ReadBack"], records.Select(r => r.Phase));
        Assert.All(records, r => Assert.True(r.Verified));
        Assert.Equal(DpContentResults.Confirmed, records[2].Pairs.Single().Result);
        Assert.Equal(0, records[0].Pairs.Single().StateBefore);
    }

    [Fact]
    public async Task ARedistributionWritesItsAuditRecordAfterTheChange()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0));
        var linesAtWrite = -1;
        h.Site.BeforeRun = (p, _) => { if ((string?)p["Operation"] == "Apply") linesAtWrite = h.AuditLines().Count; };
        await h.ApplyAsync(change);

        Assert.Equal(0, linesAtWrite);
        var record = Assert.Single(h.Audit.Read(10));
        Assert.Equal("Redistribute", record.Event);
        Assert.Equal("Finished", record.Phase);
        Assert.Equal("Succeeded", record.Result);
    }

    [Fact]
    public async Task ThePlanTellsWhichDeploymentsItCounts()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A8", Cm01, 0, DpContentTypes.Application), Pair("MCM00006", Cm01, 0, DpContentTypes.BootImage));

        Assert.Equal(1, change.ContentImpact.Single(i => i.PackageId == "MCM000A8").DeploymentCount);
        Assert.Equal(1, change.ContentImpact.Single(i => i.PackageId == "MCM00006").DeploymentCount);
        Assert.Contains(change.Impact, l => l.StartsWith("Counted: application deployments", StringComparison.Ordinal) && l.Contains("Not counted: software update deployments"));
        Assert.Contains(change.Impact, l => l.StartsWith("Undo distributes the 2 pairs that are installed now again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APlanOverTheCapIsRefused()
    {
        using var h = new DpContentHarness();
        var many = Enumerable.Range(0, DpContentService.MaxPairsPerRun + 1).Select(i => Pair($"MCM{i:X5}", Cm01, 0)).ToArray();
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, many);

        Assert.True(change.Blocked);
        Assert.Contains("A run takes at most 500 pairs", Assert.Single(change.Checks).Message);
        Assert.Empty(h.Site.Runs);
    }

    [Fact]
    public async Task ARunWorksInPartsAndCancelSkipsTheRest()
    {
        var state = StubDpContentSite.Lab();
        var nals = Enumerable.Range(0, 60).Select(i => StubDpContentSite.NalOf($"DP{i:D2}.contoso.com")).ToList();
        foreach (var nal in nals)
        {
            ((JsonArray)state["Dps"]!).Add(StubDpContentSite.Dp(nal, DpContentAggregator.ServerOf(nal).ToUpperInvariant()));
            ((JsonArray)state["Summaries"]!).Add(StubDpContentSite.Sum("MCM0001B", nal, 0, 0));
            ((JsonArray)state["Instances"]!).Add(StubDpContentSite.Inst("MCM0001B", nal, 0, "2026-10-20T09:13:44"));
        }
        using var h = new DpContentHarness(state);
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, nals.Select(n => Pair("MCM0001B", n, 0)).ToArray());
        using var cts = new CancellationTokenSource();
        var applies = 0;
        h.Site.BeforeRun = (p, _) => { if ((string?)p["Operation"] == "Apply" && ++applies == 2) cts.Cancel(); };
        var outcome = await h.ApplyAsync(change, cts.Token);

        Assert.Equal(2, applies);
        Assert.Equal(50, outcome.Pairs.Count(p => p.Result != DpContentResults.Skipped));
        Assert.Equal(10, outcome.Pairs.Count(p => p.Result == DpContentResults.Skipped));
        Assert.All(outcome.Pairs.Where(p => p.Result == DpContentResults.Skipped), p => Assert.Equal("Skipped: the run was cancelled before this part.", p.Message));
        Assert.Equal(50, h.Site.Log.Count(l => l == "Put:MCM0001B"));
        Assert.Equal(2, h.Reports.Count(r => !r.Final));
        Assert.Single(h.Reports, r => r.Final);
    }

    [Fact]
    public async Task ASessionFailureDuringAPartStopsTheRun()
    {
        using var h = new DpContentHarness(window: TimeSpan.FromSeconds(5));
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        h.Site.AfterRun = p => (string?)p["Operation"] == "Apply" ? new ApplicationPackagerEngine.Services.ConfigMgrSession.ConfigMgrSessionException(
            ApplicationPackagerEngine.Services.ConfigMgrSession.ConfigMgrFailure.ProcessDied, "secret detail") : null;
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Confirmed, pair.Result);
        Assert.DoesNotContain("secret", outcome.Message);
    }

    [Fact]
    public async Task APairAnotherProcessRemovedAfterThePlanIsSkippedAndTheMessageSaysSo()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        h.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] != "Apply") return;
            site.List("Summaries").Remove(site.Summary("MCM000A6", Cm01));
            var instance = site.List("Instances").OfType<JsonObject>().Single(r => (string?)r["PackageID"] == "MCM000A6" && (string?)r["ServerNALPath"] == Cm01);
            site.List("Instances").Remove(instance);
        };
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true, LastCopyAcknowledged = true });

        Assert.Equal("Skipped: no pair was changed. The site no longer lists the pair. Nothing was changed.", outcome.Message);
        Assert.Equal(DpContentResults.Skipped, Assert.Single(outcome.Pairs).Result);
        Assert.Null(outcome.UndoChange);
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAppLogHoldsCountsAndKindsButNoNamesOrIds()
    {
        var log = new RecordingLog();
        using var h = new DpContentHarness();
        var service = new DpContentService(h.Site, h.Audit, log, (_, _) => Task.CompletedTask);
        await service.LoadAsync(new Progress<string>(), CancellationToken.None);
        h.Site.Fail("put:MCM000A6");
        var change = await service.PrepareAsync(new DpContentChange(DpContentChangeKind.Redistribute, [Pair("MCM000A6", Cm01, 0), Pair("MCM0001B", Cm01, 0)], false, false, [], [], null), CancellationToken.None);
        await service.ApplyAsync([change], new Progress<DpContentOutcome>(), CancellationToken.None);
        var removal = await service.PrepareAsync(new DpContentChange(DpContentChangeKind.Remove, [Pair("MCM000A6", Backup, 0)], false, false, [], [], null), CancellationToken.None);
        await service.ApplyAsync([removal with { RemovalConfirmed = true }], new Progress<DpContentOutcome>(), CancellationToken.None);

        Assert.NotEmpty(log.Messages);
        Assert.All(log.Messages, m =>
        {
            Assert.Equal(DpContentService.LogArea, m.Area);
            foreach (var text in new[] { "MCM0", "CM01", "contoso", "DPCM", "CANARY", "SECRET", "Display=" }) Assert.DoesNotContain(text, m.Message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(log.Messages, m => m.Message.Contains("kind error", StringComparison.Ordinal));
    }

    private sealed class RecordingLog : ApplicationPackagerEngine.Services.Logging.IAppLog
    {
        public List<(string Area, string Message)> Messages { get; } = [];
        public string? SessionFile => null;
        public ApplicationPackagerEngine.Services.Logging.AppLogLevel MinimumLevel { get; set; } = ApplicationPackagerEngine.Services.Logging.AppLogLevel.Debug;
        public event EventHandler<ApplicationPackagerEngine.Services.Logging.AppLogEntry>? Written;
        public void RegisterSecret(string value) { }
        public void Write(ApplicationPackagerEngine.Services.Logging.AppLogLevel level, string area, string message)
        {
            lock (Messages) Messages.Add((area, message));
            Written?.Invoke(this, new(DateTimeOffset.UtcNow, level, area, message));
        }
        public IReadOnlyList<ApplicationPackagerEngine.Services.Logging.AppLogEntry> Tail() => [];
    }
}