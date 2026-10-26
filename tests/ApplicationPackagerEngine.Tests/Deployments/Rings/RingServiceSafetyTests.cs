using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

/// <summary>Unreadable run values, the ring cap and pinned folders through the real ring service and the stub site.</summary>
public sealed class RingServiceSafetyTests
{
    private static DeployableObject App => DeploymentHarness.App;

    private static void Edit(RingHarness h, RingRun run, Action<JsonObject> edit)
    {
        var path = Path.Combine(h.RunsFolder, run.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    private static async Task<RingRun> HeldRunAsync(RingHarness h)
    {
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), App);
        Assert.True(outcome.Succeeded, outcome.Message);
        return h.Run(outcome.Run!.RunId);
    }

    [Theory]
    [InlineData(0, "SuccessThresholdPercent", "99.5")]
    [InlineData(1, "RebootOutsideServiceWindow", "1")]
    [InlineData(1, "OverrideServiceWindow", "maybe")]
    public async Task AnUnreadableGateOrOptionBlocksPromoteAndUndoRunWithItsReason(int ring, string field, string value)
    {
        using var h = new RingHarness();
        var run = await HeldRunAsync(h);
        h.Summarize(run.Rings[0].DeploymentId!, 10, 10);
        Edit(h, run, json => json["Rings"]![ring]![field] = value);

        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.True(promote.Blocked);
        Assert.Contains(promote.Checks, c => c.Result == "Fail" && c.Message.Contains($"Unreadable ring {ring + 1}", StringComparison.Ordinal));
        Assert.Empty(promote.RingChanges);
        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId));
        Assert.True(undo.Blocked);
        Assert.Contains(undo.Checks, c => c.Message.Contains($"Unreadable ring {ring + 1}", StringComparison.Ordinal));
        Assert.Equal(1, h.CreateCalls);
    }

    [Fact]
    public async Task ARunFileWithMoreThanOneHundredRingsIsRefusedForPromoteAndUndoRun()
    {
        using var h = new RingHarness();
        var run = await HeldRunAsync(h);
        Edit(h, run, json =>
        {
            var rings = json["Rings"]!.AsArray();
            for (var i = 4; i <= 101; i++)
            {
                var copy = rings[2]!.DeepClone().AsObject();
                copy["Index"] = i;
                copy["CollectionID"] = "MCMX" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
                rings.Add(copy);
            }
        });
        var promote = await h.PrepareAsync(RingHarness.Request(RingChangeKind.Promote, run.RunId, 2));
        Assert.Contains(promote.Checks, c => c.Result == "Fail" && c.Message.Contains("at most 100 rings", StringComparison.Ordinal));
        var undo = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId));
        Assert.Contains(undo.Checks, c => c.Result == "Fail" && c.Message.Contains("at most 100 rings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderChangeDuringACreateRunKeepsEverySaveInTheFolderTheLockProtects()
    {
        using var h = new RingHarness();
        var first = h.RunsFolder;
        var current = first;
        var runs = new RingRunStore(() => current, utcNow: () => h.UtcNow, local: DeploymentHarness.Local, user: () => RingHarness.Account);
        var service = new RingService(h.Deployments, h.Deployments, h.Audit, h.Plans, runs, utcNow: () => h.UtcNow, local: DeploymentHarness.Local);
        var plan = RingHarness.ThreeRings();
        var prepared = await service.PrepareAsync(RingHarness.CreateRun(plan, App, await service.ExpandAsync(plan, App, RingHarness.Start, CancellationToken.None)), CancellationToken.None);
        h.Site.AfterRun = p =>
        {
            if ((string?)p["Operation"] == "Create") current = Path.Combine(Path.GetDirectoryName(first)!, "elsewhere");
            return null;
        };

        var outcome = (await service.ApplyAsync([prepared], new Progress<RingOutcome>(), CancellationToken.None)).Single();

        Assert.True(outcome.Succeeded, outcome.Message);
        var run = Assert.Single(new RingRunStore(() => first).List(false, out _));
        Assert.All(run.Rings, r => Assert.Equal(RingStatuses.Created, r.Status));
        Assert.False(Directory.Exists(current) && Directory.GetFiles(current, "*.json").Length > 0);
    }
}
