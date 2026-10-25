using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>Abandon Creating ring and the audit records of Close run, cancelled rings and Abandon, through the real ring service and the stub site.</summary>
public sealed class RingAbandonAndAuditTests
{
    private static DeployableObject Package => DeploymentHarness.Package;

    private static RingPlan Plan => RingHarness.ThreeRings(hold: true) with { Rings = [.. RingHarness.ThreeRings().Rings.Select(r => r with { SuccessThresholdPercent = null })] };

    /// <summary>A package run whose ring 1 is Creating, with a deployment that holds its values but whose status message 30006 has not arrived.</summary>
    private static async Task<RingRun> UnprovenRunAsync(RingHarness h)
    {
        h.Site.SetOption("NoStatus", true);
        var run = h.Runs.Create(new RingRun(Guid.NewGuid().ToString(), "", Plan.Name, true, "LocalTime", DeploymentObjectType.Package, Package.Id, Package.Name, Package.Program, RingHarness.Account,
            h.UtcNow, [.. RingPlanRules.Expand(Plan, RingHarness.Start, DeploymentHarness.Local).Select(r => new RingRunRing(r.Index, r.Name, r.CollectionId, "", r.Purpose, r.AvailableTime, r.Deadline,
                r.Index == 1 ? RingStatuses.Creating : RingStatuses.Held, r.Index == 1 ? DeploymentHarness.LocalNow : null, null, 0, "", null)
            { Definition = r.Definition, CreatingAtUtc = r.Index == 1 ? h.UtcNow : null, CreatingBy = r.Index == 1 ? RingHarness.Account : null })], false), Package.Id);
        var options = RingPlanRules.Options(DeploymentObjectType.Package, Plan.Rings[0], RingHarness.Start, null, false);
        var made = (await h.Deployments.ApplyAsync([(await h.Deployments.PrepareAsync(DeploymentHarness.Create(Package, options), CancellationToken.None)) with { Ring = new DeploymentRingContext(Plan.Name, 1, "QA", run.RunId) }],
            new Progress<DeploymentOutcome>(), CancellationToken.None)).Single();
        Assert.True(made.Succeeded, made.Message);
        return run;
    }

    [Fact]
    public async Task AnUnprovenCreatingRingOffersAbandonAfterTheLongTimeoutAndAbandonMarksItHeldWithAnAuditRecord()
    {
        using var h = new RingHarness();
        var run = await UnprovenRunAsync(h);
        Assert.DoesNotContain("Abandon", (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0].Note, StringComparison.Ordinal);
        h.UtcNow = h.UtcNow.Add(RingService.AbandonAfter).AddMinutes(1);
        var ring = (await h.Service.ReconcileAsync(run.RunId, CancellationToken.None)).Rings[0];
        Assert.Equal(RingStatuses.Creating, ring.Status);
        Assert.Contains("Abandon Creating ring", ring.Note);

        var close = await h.PrepareAsync(RingHarness.Request(RingChangeKind.CloseRun, run.RunId));
        Assert.Contains(close.Checks, c => c.Result == "Fail" && c.Message.Contains("Abandon", StringComparison.Ordinal));

        var abandon = await h.PrepareAsync(RingHarness.Request(RingChangeKind.AbandonRing, run.RunId, 1));
        Assert.False(abandon.Blocked, string.Join(" | ", abandon.Checks.Select(c => c.Message)));
        Assert.Contains(abandon.Impact, l => l.Contains("may exist", StringComparison.Ordinal));
        var outcome = await h.ApplyAsync(abandon);
        Assert.True(outcome.Succeeded, outcome.Message);
        var after = h.Run(run.RunId);
        Assert.Equal((RingStatuses.Held, (DateTime?)null, (string?)null), (after.Rings[0].Status, after.Rings[0].CreatingAtUtc, after.Rings[0].CreatingBy));
        var record = Assert.Single(h.Records, r => r.Event == AuditEvents.Abandon);
        Assert.Equal((Plan.Name, run.RunId, 1), (record.PlanName, record.RunId, record.RingIndex));
        Assert.Single(h.Site.List("Adverts"));
    }

    [Fact]
    public async Task AbandonIsRefusedForARingThatIsNotCreating()
    {
        using var h = new RingHarness();
        var created = await h.CreateRunAsync(Plan, Package);
        var abandon = await h.PrepareAsync(RingHarness.Request(RingChangeKind.AbandonRing, created.Run!.RunId, 2));
        Assert.True(abandon.Blocked);
        Assert.StartsWith(DeploymentService.SkippedPrefix, (await h.ApplyAsync(abandon)).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Records, r => r.Event == AuditEvents.Abandon);
    }

    [Fact]
    public async Task CloseRunWritesACloseRecordAndOneCancelRecordPerHeldRing()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(Plan, Package)).Run!.RunId);
        var closed = await h.ApplyAsync(await h.PrepareAsync(RingHarness.Request(RingChangeKind.CloseRun, run.RunId)));
        Assert.True(closed.Succeeded, closed.Message);
        var cancels = h.Records.Where(r => r.Event == AuditEvents.CancelRing).ToList();
        Assert.Equal([2, 3], cancels.Select(r => r.RingIndex!.Value));
        Assert.All(cancels, r => Assert.Equal((Plan.Name, run.RunId), (r.PlanName, r.RunId)));
        var close = Assert.Single(h.Records, r => r.Event == AuditEvents.Close);
        Assert.Equal((Plan.Name, run.RunId), (close.PlanName, close.RunId));
        Assert.All(h.Records, r => Assert.True(r.Verified, r.Event));
    }

    [Fact]
    public async Task UndoRunWritesACancelRecordPerHeldRingAndACloseRecord()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(Plan, Package)).Run!.RunId);
        var undone = await h.ApplyAsync(await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId)));
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal([2, 3], h.Records.Where(r => r.Event == AuditEvents.CancelRing).Select(r => r.RingIndex!.Value));
        Assert.Single(h.Records, r => r.Event == AuditEvents.Close && r.RunId == run.RunId);
    }

    [Fact]
    public async Task CloseRunIsRefusedWhenTheAuditLogCannotBeWritten()
    {
        using var h = new RingHarness();
        var run = h.Run((await h.CreateRunAsync(Plan, Package)).Run!.RunId);
        var close = await h.PrepareAsync(RingHarness.Request(RingChangeKind.CloseRun, run.RunId));
        File.SetAttributes(h.Audit.Path, FileAttributes.ReadOnly);
        try
        {
            var refused = await h.ApplyAsync(close);
            Assert.StartsWith(DeploymentService.SkippedPrefix, refused.Message, StringComparison.Ordinal);
            Assert.False(h.Run(run.RunId).IsClosed);
        }
        finally
        {
            File.SetAttributes(h.Audit.Path, FileAttributes.Normal);
        }
    }
}
