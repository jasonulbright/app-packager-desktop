using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>Plans of 1, 2, 4 and 13 rings through the real Deployments script and the stub site.</summary>
public sealed class RingCountScriptTests
{
    private static DeployableObject Package => DeploymentHarness.Package;

    private static int Queries(RingHarness h) => h.Site.Log.Count(l => l == "Query");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(13)]
    public async Task CreateAllLiveCountsReconcileAndUndoWorkForAnyRingCount(int count)
    {
        using var h = new RingHarness(waves: count);
        var plan = RingHarness.WavePlan(count);
        var rows = await h.ExpandAsync(plan, Package);
        Assert.Equal(count, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain(r.Checks, c => c.Result is "Fail" or "Unknown"));
        Assert.Equal(Enumerable.Range(0, count).Select(i => RingHarness.Start.AddDays(i)), rows.Select(r => r.AvailableTime));

        var runsBefore = h.Site.Runs.Count;
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, Package, rows));
        Assert.False(prepared.Blocked, string.Join(" | ", prepared.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
        Assert.Equal(count, h.Site.Runs.Count - runsBefore);
        Assert.Equal(count, prepared.RingChanges.Count);
        var outcome = await h.ApplyAsync(prepared);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(count, h.Site.List("Adverts").Count);
        var run = h.Run(outcome.Run!.RunId);
        Assert.All(run.Rings, r => Assert.Equal(RingStatuses.Created, r.Status));
        Assert.Equal(count, h.Records.Count(r => r.Event == AuditEvents.Create && r.RunId == run.RunId));

        runsBefore = h.Site.Runs.Count;
        var queries = Queries(h);
        var live = await h.Service.LiveCountsAsync(run, CancellationToken.None);
        Assert.Equal(1, h.Site.Runs.Count - runsBefore);
        Assert.Equal(2, Queries(h) - queries);
        Assert.All(live, l => Assert.Equal((RingLiveStates.Summarized, 0), (l.State, l.Targeted)));
        foreach (var ring in run.Rings) h.Summarize(ring.DeploymentId!, 2, 2);
        Assert.All(await h.Service.LiveCountsAsync(run, CancellationToken.None), l => Assert.Equal((RingLiveStates.Summarized, 2, 2), (l.State, l.Targeted, l.Success)));

        runsBefore = h.Site.Runs.Count;
        var reconciled = await h.Service.ReconcileAsync(run.RunId, CancellationToken.None);
        Assert.Equal(1, h.Site.Runs.Count - runsBefore);
        Assert.True(reconciled.IsClosed);

        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId));
        Assert.False(undo.Blocked, string.Join(" | ", undo.Checks.Select(c => c.Message)));
        Assert.Empty(undo.KeptRings);
        var undone = await h.ApplyAsync(undo);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Empty(h.Site.List("Adverts"));
        Assert.All(h.Runs.List(true, out _).Single().Rings, r => Assert.Equal(RingStatuses.Removed, r.Status));
    }

    [Fact]
    public async Task ARequiredApplicationRunScansTheApplicationNamesOnceAtPrepareAndBeforeEachCreate()
    {
        using var h = new RingHarness(waves: 4);
        var plan = RingHarness.WavePlan(4, purpose: "Required");
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, DeploymentHarness.App, await h.ExpandAsync(plan, DeploymentHarness.App), required: true));
        Assert.False(prepared.Blocked, string.Join(" | ", prepared.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
        Assert.Equal(1, h.Site.Log.Count(l => l == "NameScan"));
        var outcome = await h.ApplyAsync(prepared);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(5, h.Site.Log.Count(l => l == "NameScan"));
    }

    [Fact]
    public async Task LiveCountsOfOneHundredFiftyOneDeploymentsReadChunksOfFiftyIdsThroughTheScript()
    {
        using var h = new RingHarness();
        var adverts = h.Site.List("Adverts");
        var summaries = h.Site.List("Summaries");
        var ids = Enumerable.Range(1, 150).Select(i => "MCM3" + i.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        foreach (var id in ids)
        {
            adverts.Add(new JsonObject { ["AdvertisementID"] = id, ["PackageID"] = "MCM0001B", ["ProgramName"] = "CMCO-CANARY-Run", ["CollectionID"] = RingHarness.C1 });
            summaries.Add(new JsonObject { ["DeploymentID"] = id, ["AssignmentID"] = 0, ["CollectionID"] = RingHarness.C1, ["NumberTargeted"] = 3, ["NumberSuccess"] = 2, ["NumberErrors"] = 1,
                ["NumberInProgress"] = 0, ["NumberUnknown"] = 0, ["NumberOther"] = 0, ["SummarizationTime"] = "2026-10-20T08:30:00" });
        }
        var queries = Queries(h);
        var runs = h.Site.Runs.Count;

        var live = await h.Deployments.LiveAsync(DeploymentObjectType.Package, [.. ids, "MCM3FFFF"], CancellationToken.None);

        Assert.Equal(1, h.Site.Runs.Count - runs);
        // 151 IDs: four object queries and four summary queries of at most 50 terms each.
        Assert.Equal(8, Queries(h) - queries);
        Assert.All(live.Take(150), r => Assert.Equal((true, (int?)3, 2, 1), (r.Exists, r.Targeted, r.Success, r.Errors)));
        Assert.Equal((false, (int?)null), (live[150].Exists, live[150].Targeted));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(13)]
    public async Task HoldAndPromoteThroughEveryRing(int count)
    {
        using var h = new RingHarness(waves: count);
        var outcome = await h.CreateRunAsync(RingHarness.WavePlan(count, hold: true, purpose: "Required"), Package, required: true);
        Assert.True(outcome.Succeeded, outcome.Message);
        var runId = outcome.Run!.RunId;
        for (var index = 2; index <= count; index++)
        {
            var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, runId, index, required: true));
            Assert.False(promote.Blocked, $"ring {index}: " + string.Join(" | ", promote.Checks.Where(c => c.Result != "Pass").Select(c => c.Message)));
            Assert.Contains(promote.Impact, l => l.Contains("Required", StringComparison.Ordinal));
            var promoted = await h.ApplyAsync(promote);
            Assert.True(promoted.Succeeded, $"ring {index}: {promoted.Message}");
        }
        var run = h.Run(runId);
        Assert.All(run.Rings, r => Assert.Equal(RingStatuses.Created, r.Status));
        var adverts = h.Site.List("Adverts").OfType<JsonObject>().OrderBy(a => (string?)a["CollectionID"]).ToList();
        Assert.Equal(count, adverts.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(i => RingHarness.Start.AddDays(i).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
            adverts.Select(a => (string?)a["PresentTime"]));
        Assert.True((await h.ApplyAsync(await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, runId)))).Succeeded);
        Assert.Empty(h.Site.List("Adverts"));
    }
}
