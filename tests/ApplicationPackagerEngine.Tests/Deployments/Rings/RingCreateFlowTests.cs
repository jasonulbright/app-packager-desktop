using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>The create flow of a run through the real Deployments script and the stub site: partial rings, run file failures, foreign edits and the create-now choice.</summary>
public sealed class RingCreateFlowTests
{
    private static DeployableObject PendingPackage => new(DeploymentObjectType.Package, "MCM0001C", "DH Pending", "1", "Run", false);

    /// <summary>The lab site with package MCM0001C, whose content is on no distribution point yet.</summary>
    private static JsonObject PendingPackageLab()
    {
        var lab = StubDeploymentSite.Lab();
        lab["Packages"]!.AsArray().Add(JsonNode.Parse("""{ "PackageID": "MCM0001C", "Name": "DH Pending", "Version": "1", "Programs": [ "Run" ], "Content": { "Targeted": 0, "Success": 0 } }"""));
        return lab;
    }

    private static string Detail(RingChange change) => string.Join(" | ", change.Checks.Where(c => c.Result != "Pass").Select(c => c.Message));

    [Fact]
    public async Task APartialRingStopsCreateAllAndTheRunIsNotReportedAsASuccess()
    {
        using var h = new RingHarness();
        var basePlan = RingHarness.ThreeRings();
        // CM01 ignores RunFromDistributionPoint on a task sequence deployment, so the read-back reports other stored values.
        var plan = basePlan with { Rings = [basePlan.Rings[0], basePlan.Rings[1] with { TaskSequenceDeploymentOption = "RunFromDistributionPoint" }, basePlan.Rings[2]] };
        var ts = DeploymentHarness.TaskSequence;
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, ts, await h.ExpandAsync(plan, ts)));
        Assert.False(prepared.Blocked, Detail(prepared));

        var outcome = await h.ApplyAsync(prepared);

        Assert.False(outcome.Succeeded, outcome.Message);
        Assert.Equal(2, h.CreateCalls);
        Assert.True(outcome.RingOutcomes[0].Succeeded);
        Assert.False(outcome.RingOutcomes[1].Succeeded);
        Assert.NotNull(outcome.RingOutcomes[1].UndoChange);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[2].Message, StringComparison.Ordinal);
        Assert.StartsWith("The run stopped", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Ring 2 'Pilot'", outcome.Message);
        Assert.NotNull(outcome.UndoChange);
        var run = h.Run(outcome.Run!.RunId);
        Assert.Equal([RingStatuses.Created, RingStatuses.Created, RingStatuses.Held], run.Rings.Select(r => r.Status));
        Assert.Equal(outcome.RingOutcomes[1].UndoChange!.CreatedId, run.Rings[1].DeploymentId);
    }

    [Fact]
    public async Task AFailedRunFileSaveBeforeALaterRingKeepsTheEarlierOutcomesAndTheUndo()
    {
        using var h = new RingHarness(beforeSave: r =>
        {
            if (r.Rings.Any(x => x.Index == 2 && x.Status == RingStatuses.Creating)) throw new IOException("disk full");
        });
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, DeploymentHarness.App, await h.ExpandAsync(plan, DeploymentHarness.App)));

        var outcome = await h.ApplyAsync(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, h.CreateCalls);
        Assert.Equal(3, outcome.RingOutcomes.Count);
        Assert.True(outcome.RingOutcomes[0].Succeeded);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[1].Message, StringComparison.Ordinal);
        Assert.Contains("run file", outcome.RingOutcomes[1].Message);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[2].Message, StringComparison.Ordinal);
        Assert.Contains("run file could not be", outcome.Message);
        Assert.DoesNotContain("disk full", outcome.Message, StringComparison.Ordinal);
        Assert.NotNull(outcome.UndoChange);
        Assert.Equal(outcome.Run!.RunId, outcome.UndoChange!.RunId);
        Assert.Equal([RingStatuses.Created, RingStatuses.Held, RingStatuses.Held], h.Run(outcome.Run.RunId).Rings.Select(r => r.Status));
    }

    [Fact]
    public async Task AFailedContentDistributionLeavesTheRingHeldBecauseNothingWasCreated()
    {
        using var h = new RingHarness(PendingPackageLab());
        var three = RingHarness.ThreeRings(dpGroup: "All DPs");
        var plan = three with { Rings = [three.Rings[0]] };
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, PendingPackage, await h.ExpandAsync(plan, PendingPackage)));
        Assert.False(prepared.Blocked, Detail(prepared));
        h.Site.Fail("distribute:All DPs");

        var outcome = await h.ApplyAsync(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, h.CreateCalls);
        Assert.True(outcome.RingOutcomes[0].NotCreated, outcome.RingOutcomes[0].Message);
        Assert.Null(outcome.UndoChange);
        Assert.All(h.Run(outcome.Run!.RunId).Rings, r => Assert.Equal(RingStatuses.Held, r.Status));
    }

    [Fact]
    public async Task ARingThatAnotherWriterChangedDuringTheRunIsNotOverwrittenOrCreated()
    {
        using var h = new RingHarness();
        var plan = RingHarness.ThreeRings();
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, DeploymentHarness.App, await h.ExpandAsync(plan, DeploymentHarness.App)));
        var edited = false;
        h.Site.AfterRun = p =>
        {
            if ((string?)p["Operation"] != "Create" || edited) return null;
            edited = true;
            var file = Directory.GetFiles(h.RunsFolder, "*.json").Single();
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["Rings"]![1]!["Status"] = RingStatuses.Created;
            json["Rings"]![1]!["DeploymentID"] = "{11111111-2222-3333-4444-555555555555}";
            File.WriteAllText(file, json.ToJsonString());
            return null;
        };

        var outcome = await h.ApplyAsync(prepared);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, h.CreateCalls);
        Assert.StartsWith(DeploymentService.SkippedPrefix, outcome.RingOutcomes[1].Message, StringComparison.Ordinal);
        Assert.Contains("not Held", outcome.RingOutcomes[1].Message);
        var run = h.Run(outcome.Run!.RunId);
        Assert.Equal((RingStatuses.Created, "{11111111-2222-3333-4444-555555555555}"), (run.Rings[1].Status, run.Rings[1].DeploymentId));
        Assert.Equal(RingStatuses.Created, run.Rings[0].Status);
    }

    [Fact]
    public async Task TheConfirmedCreateNowChoiceReachesTheRingCreate()
    {
        using var h = new RingHarness(PendingPackageLab());
        var package = PendingPackage;
        var basePlan = RingHarness.ThreeRings(dpGroup: "All DPs");
        var plan = basePlan with { Rings = [basePlan.Rings[0]] };
        var prepared = await h.PrepareAsync(RingHarness.CreateRun(plan, package, await h.ExpandAsync(plan, package)));
        Assert.False(prepared.Blocked, Detail(prepared));
        Assert.Contains(prepared.Checks, c => c.Message.Contains("Content distribution in progress", StringComparison.Ordinal));

        var outcome = await h.ApplyAsync(prepared with { CreateNowContentDistributing = true });

        Assert.True(outcome.Succeeded, outcome.Message + " | " + string.Join(" | ", outcome.RingOutcomes.Select(o => o.Message)));
        Assert.Equal(1, h.CreateCalls);
        Assert.Equal(RingStatuses.Created, h.Run(outcome.Run!.RunId).Rings[0].Status);
    }
}
