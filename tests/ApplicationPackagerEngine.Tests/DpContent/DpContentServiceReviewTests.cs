using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.DpContent.DpContentHarness;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The real DP content script through the stub site: removal read-back, Undo, refusals, run results, chunk records, truncated counts and the deployment read.</summary>
public sealed class DpContentServiceReviewTests
{
    private const string Cm01 = StubDpContentSite.Nal;

    /// <summary>Puts the status row of a pair back on each read-back, as a summarizer that has not caught up with a delete.</summary>
    private static void KeepStatusRowAfterTheWrite(StubDpContentSite site, Action? onReadBack = null, bool keepInstance = false)
    {
        var applied = false;
        site.BeforeRun = (p, s) =>
        {
            var operation = (string?)p["Operation"];
            if (operation == "Apply")
            {
                applied = true;
                return;
            }
            if (!applied || operation != "State") return;
            if (s.Summary("MCM000A6", Cm01) is null) s.List("Summaries").Add(StubDpContentSite.Sum("MCM000A6", Cm01, 0, 0));
            if (keepInstance && !s.List("Instances").OfType<JsonObject>().Any(r => (string?)r["PackageID"] == "MCM000A6" && (string?)r["ServerNALPath"] == Cm01))
                s.List("Instances").Add(StubDpContentSite.Inst("MCM000A6", Cm01, 0, "2026-10-23T23:29:07"));
            onReadBack?.Invoke();
        };
    }

    [Fact]
    public async Task ARemovalWhoseInstanceIsGoneWhileItsStatusRowStaysReadsRequestedAndKeepsItsUndo()
    {
        using var h = new DpContentHarness(window: TimeSpan.FromSeconds(5));
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        KeepStatusRowAfterTheWrite(h.Site);
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Requested, pair.Result);
        Assert.Equal(Cm01, Assert.Single(outcome.UndoChange!.Pairs).NalPath);
    }

    [Fact]
    public async Task ARemovalWhoseReadBackIsCancelledAfterOneReadKeepsItsUndoAndDidNotSucceed()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        using var cts = new CancellationTokenSource();
        KeepStatusRowAfterTheWrite(h.Site, cts.Cancel, keepInstance: true);
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true }, cts.Token);

        Assert.Equal(DpContentResults.Unchanged, Assert.Single(outcome.Pairs).Result);
        Assert.Contains("Delete:MCM000A6", h.Site.Log);
        Assert.Equal(Cm01, Assert.Single(outcome.UndoChange!.Pairs).NalPath);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task AFailedReadOfTheCurrentCopiesBeforeARemovalIsRefusedAsSkipped()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        h.Site.AfterRun = p => (string?)p["Operation"] == "State" ? new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, "SECRET-DETAIL") : null;
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });

        Assert.False(outcome.Succeeded);
        Assert.StartsWith("Skipped: the current copies of the content could not be read before the removal, so nothing was removed.", outcome.Message);
        Assert.DoesNotContain("SECRET", outcome.Message);
        Assert.Equal(DpContentResults.Skipped, Assert.Single(outcome.Pairs).Result);
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.Null(outcome.UndoChange);
    }

    [Fact]
    public async Task ACancelDuringTheReadOfTheCurrentCopiesIsRefusedAsSkipped()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A6", Cm01, 0));
        h.Site.AfterRun = p => (string?)p["Operation"] == "State" ? new OperationCanceledException() : null;
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });

        Assert.Equal("Skipped: the run was cancelled before this change. Nothing was removed.", outcome.Message);
        Assert.Equal(DpContentResults.Skipped, Assert.Single(outcome.Pairs).Result);
    }

    [Fact]
    public async Task ARunStoppedAfterASessionFailureIsNotSucceededAndItsPartRecordSaysUnknown()
    {
        var state = StubDpContentSite.Lab();
        var nals = Enumerable.Range(0, 30).Select(i => StubDpContentSite.NalOf($"DP{i:D2}.contoso.com")).ToList();
        foreach (var nal in nals)
        {
            ((JsonArray)state["Dps"]!).Add(StubDpContentSite.Dp(nal, DpContentAggregator.ServerOf(nal).ToUpperInvariant()));
            ((JsonArray)state["Summaries"]!).Add(StubDpContentSite.Sum("MCM0001B", nal, 0, 0));
            ((JsonArray)state["Instances"]!).Add(StubDpContentSite.Inst("MCM0001B", nal, 0, "2026-10-20T09:13:44"));
        }
        using var h = new DpContentHarness(state, window: TimeSpan.FromSeconds(5));
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, nals.Select(n => Pair("MCM0001B", n, 0)).ToArray());
        h.Site.AfterRun = p => (string?)p["Operation"] == "Apply" ? new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "secret detail") : null;
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });

        Assert.Equal(DpContentService.ChunkSize, outcome.Pairs.Count(p => p.Result != DpContentResults.Skipped));
        Assert.Equal(30 - DpContentService.ChunkSize, outcome.Pairs.Count(p => p.Result == DpContentResults.Skipped));
        Assert.All(outcome.Pairs.Where(p => p.Result != DpContentResults.Skipped), p => Assert.Equal(DpContentResults.Confirmed, p.Result));
        Assert.False(outcome.Succeeded);
        var finished = Assert.Single(h.Audit.Read(10), r => r.Phase == "Finished");
        Assert.Equal("Unknown", finished.Result);
        Assert.Contains("0 requested, 25 not known", finished.Message);
    }

    [Fact]
    public async Task ACancelledRunWithPartsLeftIsNotSucceeded()
    {
        var state = StubDpContentSite.Lab();
        var nals = Enumerable.Range(0, 30).Select(i => StubDpContentSite.NalOf($"DP{i:D2}.contoso.com")).ToList();
        foreach (var nal in nals)
        {
            ((JsonArray)state["Dps"]!).Add(StubDpContentSite.Dp(nal, DpContentAggregator.ServerOf(nal).ToUpperInvariant()));
            ((JsonArray)state["Summaries"]!).Add(StubDpContentSite.Sum("MCM0001B", nal, 0, 0));
            ((JsonArray)state["Instances"]!).Add(StubDpContentSite.Inst("MCM0001B", nal, 0, "2026-10-20T09:13:44"));
        }
        using var h = new DpContentHarness(state, window: TimeSpan.FromSeconds(4));
        h.Site.SetOption("Ticks", 50);
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, nals.Select(n => Pair("MCM0001B", n, 0)).ToArray());
        using var cts = new CancellationTokenSource();
        h.Site.AfterRun = p =>
        {
            if ((string?)p["Operation"] == "Apply") cts.Cancel();
            return null;
        };
        var outcome = await h.ApplyAsync(change, cts.Token);

        Assert.Equal(DpContentService.ChunkSize, outcome.Pairs.Count(p => p.Result == DpContentResults.Requested));
        Assert.Equal(30 - DpContentService.ChunkSize, outcome.Pairs.Count(p => p.Result == DpContentResults.Skipped));
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task ATruncatedLoadMarksTheCountsOfContentWithoutRowsAsNotKnown()
    {
        using var h = new DpContentHarness();
        var full = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);
        Assert.All(full.Content, c => Assert.True(c.CountsKnown));

        var service = new DpContentService(h.Site, h.Audit) { MaxPairs = 3 };
        var snapshot = await service.LoadAsync(new Progress<string>(), CancellationToken.None);
        Assert.True(snapshot.Truncated);
        Assert.True(snapshot.Content.Single(c => c.PackageId == "MCM000A6").CountsKnown);
        var app = snapshot.Content.Single(c => c.PackageId == "MCM000A8");
        Assert.False(app.CountsKnown);
        Assert.Equal(0, app.DpsTargeted);
    }

    [Fact]
    public async Task AFailedDeploymentQueryLeavesTheCountsUnknownAndTheRemovalCanStillBePrepared()
    {
        using var h = new DpContentHarness();
        h.Site.Fail("query:SMS_Advertisement");
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM0001B", Cm01, 0));

        Assert.Single(change.Pairs);
        Assert.NotNull(change.Fingerprint);
        var impact = Assert.Single(change.ContentImpact);
        Assert.Equal(-1, impact.DeploymentCount);
        Assert.Equal("The deployments that use the content could not be read.", impact.DeploymentNote);
        var check = Assert.Single(change.Checks, c => c.Name == DpContentService.CheckNames.Deployments);
        Assert.Equal(DeploymentCheckResults.Warn, check.Result);
        Assert.Contains(change.Impact, l => l.Contains("the deployments that use it are not known.", StringComparison.Ordinal));
        Assert.Equal(0, impact.DpsRemaining);
    }
}
