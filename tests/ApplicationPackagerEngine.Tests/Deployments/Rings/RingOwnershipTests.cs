using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>Reconcile adoption and Undo run never take a deployment of another run or of a single deployment; Undo run works from a new service instance.</summary>
public sealed class RingOwnershipTests
{
    private static DeployableObject Package => DeploymentHarness.Package;

    private static RingPlan Plan => RingHarness.ThreeRings(hold: true) with { Rings = [.. RingHarness.ThreeRings().Rings.Select(r => r with { SuccessThresholdPercent = null })] };

    /// <summary>A run whose ring 1 was saved Creating at <paramref name="startUtc"/> and whose process ended before the create.</summary>
    private static RingRun CrashedRun(RingHarness h, DateTime startUtc) =>
        h.Runs.Create(new RingRun(Guid.NewGuid().ToString(), "", Plan.Name, true, "LocalTime", DeploymentObjectType.Package, Package.Id, Package.Name, Package.Program, RingHarness.Account,
            startUtc, [.. RingPlanRules.Expand(Plan, RingHarness.Start, DeploymentHarness.Local).Select(r => new RingRunRing(r.Index, r.Name, r.CollectionId, "", r.Purpose, r.AvailableTime, r.Deadline,
                r.Index == 1 ? RingStatuses.Creating : RingStatuses.Held, r.Index == 1 ? TimeZoneInfo.ConvertTimeFromUtc(startUtc, DeploymentHarness.Local) : null, null, 0, "", null)
            { Definition = r.Definition, CreatingAtUtc = r.Index == 1 ? startUtc : null, CreatingBy = r.Index == 1 ? RingHarness.Account : null })], false), Package.Id);

    private static void SetRing(RingHarness h, RingRun run, string status, string? id)
    {
        var path = Path.Combine(h.RunsFolder, run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["Rings"]![0]!["Status"] = status;
        json["Rings"]![0]!["DeploymentID"] = id;
        json["Rings"]![0]!["CreatingAtUtc"] = null;
        File.WriteAllText(path, json.ToJsonString());
    }

    [Fact]
    public async Task ReconcileDoesNotAdoptTheDeploymentOfAnotherRunAndUndoRunDoesNotRemoveIt()
    {
        using var h = new RingHarness();
        var a = CrashedRun(h, h.UtcNow);
        var b = await h.CreateRunAsync(Plan, Package);
        Assert.True(b.Succeeded, b.Message);
        var bId = h.Run(b.Run!.RunId).Rings[0].DeploymentId!;

        var ring = (await h.Service.ReconcileAsync(a.RunId, CancellationToken.None)).Rings[0];
        Assert.Equal(RingStatuses.Creating, ring.Status);
        h.UtcNow = h.UtcNow.AddMinutes(6);
        Assert.Equal(RingStatuses.Held, (await h.Service.ReconcileAsync(a.RunId, CancellationToken.None)).Rings[0].Status);

        SetRing(h, a, RingStatuses.Created, bId);
        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, a.RunId));
        Assert.Equal([1], undo.KeptRings);
        Assert.Empty(undo.RingChanges);
        Assert.Contains(undo.Rows.Single(r => r.Index == 1).Checks, c => c.Message.Contains("cannot be proven to belong to this run", StringComparison.Ordinal));
        await h.ApplyAsync(undo);
        Assert.Contains(h.Site.List("Adverts").OfType<JsonObject>(), x => (string?)x["AdvertisementID"] == bId);
    }

    [Fact]
    public async Task ReconcileDoesNotAdoptASingleDeploymentMadeOnTheDeployTab()
    {
        using var h = new RingHarness();
        var a = CrashedRun(h, h.UtcNow);
        var options = RingPlanRules.Options(DeploymentObjectType.Package, Plan.Rings[0], RingHarness.Start, null, false);
        var single = await h.Deployments.PrepareAsync(DeploymentHarness.Create(Package, options), CancellationToken.None);
        Assert.True((await h.Deployments.ApplyAsync([single], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single().Succeeded);

        var ring = (await h.Service.ReconcileAsync(a.RunId, CancellationToken.None)).Rings[0];
        Assert.Equal(RingStatuses.Creating, ring.Status);
        Assert.Null(ring.DeploymentId);
    }

    [Fact]
    public async Task ReconcileDoesNotAdoptADeploymentMadeLaterThanTheAdoptionWindow()
    {
        using var h = new RingHarness();
        var start = h.UtcNow.AddMinutes(-10);
        var a = CrashedRun(h, start);
        var options = RingPlanRules.Options(DeploymentObjectType.Package, Plan.Rings[0], RingHarness.Start, null, false);
        var late = (await h.Deployments.PrepareAsync(DeploymentHarness.Create(Package, options), CancellationToken.None)) with { Ring = new DeploymentRingContext(Plan.Name, 1, "QA", a.RunId) };
        Assert.True((await h.Deployments.ApplyAsync([late], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single().Succeeded);

        var ring = (await h.Service.ReconcileAsync(a.RunId, CancellationToken.None)).Rings[0];
        Assert.Equal(RingStatuses.Held, ring.Status);
    }

    [Fact]
    public async Task ADeploymentThatAppearsAfterTheRingReturnedToHeldBlocksItsPromote()
    {
        using var h = new RingHarness();
        var a = CrashedRun(h, h.UtcNow.AddMinutes(-6));
        Assert.Equal(RingStatuses.Held, (await h.Service.ReconcileAsync(a.RunId, CancellationToken.None)).Rings[0].Status);
        var options = RingPlanRules.Options(DeploymentObjectType.Package, Plan.Rings[0], RingHarness.Start, null, false);
        var late = await h.Deployments.PrepareAsync(DeploymentHarness.Create(Package, options), CancellationToken.None);
        Assert.True((await h.Deployments.ApplyAsync([late], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single().Succeeded);

        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, a.RunId, 1));
        Assert.Contains(promote.Checks, c => c.Result == "Fail" && c.Message.Contains("already deployed", StringComparison.Ordinal));
        Assert.StartsWith(DeploymentService.SkippedPrefix, (await h.ApplyAsync(promote)).Message, StringComparison.Ordinal);
        Assert.Single(h.Site.List("Adverts"));
    }

    [Fact]
    public async Task UndoRunFromANewServiceInstanceRemovesTheRunsOwnDeployments()
    {
        using var h = new RingHarness();
        var created = await h.CreateRunAsync(RingHarness.ThreeRings(), DeploymentHarness.App);
        Assert.True(created.Succeeded, created.Message);
        var deployments = new DeploymentService(h.Site, new DeploymentHarness.FakeCollections(), h.Audit, utcNow: () => h.UtcNow, local: DeploymentHarness.Local);
        var runs = new RingRunStore(() => h.RunsFolder, utcNow: () => h.UtcNow, local: DeploymentHarness.Local, user: () => RingHarness.Account);
        var fresh = new RingService(deployments, deployments, h.Audit, h.Plans, runs, utcNow: () => h.UtcNow, local: DeploymentHarness.Local);

        var undo = await fresh.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, created.Run!.RunId), CancellationToken.None);
        Assert.False(undo.Blocked, string.Join(" | ", undo.Checks.Select(c => c.Message)));
        Assert.Empty(undo.KeptRings);
        var outcome = (await fresh.ApplyAsync([undo], new Progress<RingOutcome>(), CancellationToken.None)).Single();
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(h.Site.List("AppAssignments"));
    }
}
