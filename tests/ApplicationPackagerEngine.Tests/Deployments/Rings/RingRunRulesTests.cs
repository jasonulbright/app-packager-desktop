using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>The unrounded threshold comparison and the whole-run rules Promote checks.</summary>
public sealed class RingRunRulesTests
{
    private static RingLiveCounts Counts(int targeted, int success) => new(1, "", RingLiveStates.Summarized, targeted, success, 0, 0, 0, true, "");

    [Theory]
    [InlineData(2001, 2000, 100, false)]
    [InlineData(2000, 2000, 100, true)]
    [InlineData(2001, 1000, 50, false)]
    [InlineData(1000, 990, 99, true)]
    [InlineData(3, 2, 66, true)]
    [InlineData(3, 2, 67, false)]
    [InlineData(5, 0, 0, true)]
    public void TheThresholdComparesTheUnroundedRate(int targeted, int success, int threshold, bool met)
    {
        var result = RingPlanRules.Threshold(Counts(targeted, success), threshold);
        Assert.Equal(met, result.Met);
        if (!met) Assert.Contains($"{threshold}% threshold", result.Reason);
    }

    [Fact]
    public void ARateThatWouldRoundUpToTheThresholdIsShownBelowIt()
    {
        var result = RingPlanRules.Threshold(Counts(2001, 2000), 100);
        Assert.Equal(99.9, result.Percent);
        Assert.Contains("below the 100% threshold", result.Reason);
    }

    private static async Task<RingRun> HeldRunAsync(RingHarness h, string purpose)
    {
        var plan = RingHarness.ThreeRings(hold: true, purpose: purpose) with { Rings = [.. RingHarness.ThreeRings(purpose: purpose).Rings.Select(r => r with { SuccessThresholdPercent = null })] };
        var outcome = await h.CreateRunAsync(plan, DeploymentHarness.Package, required: purpose == "Required");
        Assert.True(outcome.Succeeded, outcome.Message);
        return h.Run(outcome.Run!.RunId);
    }

    private static void Edit(RingHarness h, RingRun run, Action<JsonArray> edit)
    {
        var path = Path.Combine(h.RunsFolder, run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json["Rings"]!.AsArray());
        File.WriteAllText(path, json.ToJsonString());
    }

    [Fact]
    public async Task PromoteChecksThatNoTwoRingsOfTheRunShareACollection()
    {
        using var h = new RingHarness();
        var run = await HeldRunAsync(h, "Available");
        Edit(h, run, rings => rings[2]!["CollectionID"] = RingHarness.C1);
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.Contains(promote.Checks, c => c.Result == "Fail" && c.Message.Contains("must not share a collection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromoteChecksTheDeadlineOrderOfEveryRequiredRingOfTheRun()
    {
        using var h = new RingHarness();
        var run = await HeldRunAsync(h, "Required");
        Edit(h, run, rings => rings[2]!["DeadlineDateTime"] = "2026-10-23T08:00:00");
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2, required: true));
        Assert.Contains(promote.Checks, c => c.Result == "Fail" && c.Message.Contains("Ring 3 'Prod'", StringComparison.Ordinal) && c.Message.Contains("must be later than", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromoteChecksForABuiltInCollectionAnywhereInTheRun()
    {
        using var h = new RingHarness();
        var run = await HeldRunAsync(h, "Available");
        Edit(h, run, rings => rings[2]!["CollectionID"] = "SMS00001");
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.Contains(promote.Checks, c => c.Result == "Fail" && c.Message.Contains("built-in collection SMS00001", StringComparison.Ordinal));
    }
}
