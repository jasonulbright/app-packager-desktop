using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>One Click always runs the whole chain before the plan (D-47): the plan never shows an empty version because Check Latest did not run.</summary>
public sealed class OneClickPrechecksTests
{
    private sealed class FakeChecks(List<string> calls) : IOneClickChecks
    {
        public bool CancelAt { get; set; }
        public string? CancelStep { get; set; }

        public Task<bool> CheckLatestAsync(IReadOnlyList<PackagerInfo> rows)
        {
            calls.Add("Check Latest");
            foreach (var r in rows) r.LatestVersion = "3.0";
            return Task.FromResult(CancelStep != "Check Latest");
        }

        public Task<bool> CheckConfigMgrAsync(IReadOnlyList<PackagerInfo> rows)
        {
            calls.Add("Check ConfigMgr");
            foreach (var r in rows) r.CurrentVersion = "2.0";
            return Task.FromResult(CancelStep != "Check ConfigMgr");
        }

        public Task<bool> CheckCvesAsync(IReadOnlyList<PackagerInfo> rows)
        {
            calls.Add("Check CVEs");
            return Task.FromResult(true);
        }
    }

    private static PackagerInfo Row() => new() { Uid = "package-alpha", Application = "Alpha" };

    [Fact]
    public async Task TheChainRunsCheckLatestThenCheckConfigMgrThenCheckCves()
    {
        var calls = new List<string>();
        var ok = await OneClickPrechecks.RunAsync([Row()], new FakeChecks(calls), _ => { });
        Assert.True(ok);
        Assert.Equal(OneClickPrechecks.Order, calls);
    }

    [Fact]
    public async Task ThePlanRunsTheVersionCheckWhenNoVersionExists()
    {
        var row = Row();
        Assert.Equal("", row.LatestVersion);
        var emptyPlan = OneClickPlanner.Plan([new OneClickApp(row.Uid, row.Application, "V", row.LatestVersion, row.CurrentVersion)],
            Settings(), new Dictionary<string, OneClickHistoryEntry>(), OneClickActionNames.Report, false, DateTime.Now);
        Assert.Equal("", emptyPlan[0].Version);

        var calls = new List<string>();
        await OneClickPrechecks.RunAsync([row], new FakeChecks(calls), _ => { });

        Assert.Contains("Check Latest", calls);
        var plan = OneClickPlanner.Plan([new OneClickApp(row.Uid, row.Application, "V", row.LatestVersion, row.CurrentVersion)],
            Settings(), new Dictionary<string, OneClickHistoryEntry>(), OneClickActionNames.Report, false, DateTime.Now);
        Assert.Equal("3.0", plan[0].Version);
        Assert.Equal("2.0", plan[0].Current);
    }

    [Theory]
    [InlineData("Check Latest", 1)]
    [InlineData("Check ConfigMgr", 2)]
    public async Task ACancelledCheckStopsTheChainBeforeThePlan(string step, int expectedCalls)
    {
        var calls = new List<string>();
        var ok = await OneClickPrechecks.RunAsync([Row()], new FakeChecks(calls) { CancelStep = step }, _ => { });
        Assert.False(ok);
        Assert.Equal(expectedCalls, calls.Count);
    }

    private static OneClickPlanSettings Settings() => new()
    {
        Tracked = ["package-alpha"],
        DefaultDestinations = new DestinationSet(true, false, false),
        Readiness = new Dictionary<string, OneClickReadiness>
        {
            [OneClickDestinations.ConfigMgr] = new OneClickReadiness(true, "")
        }
    };
}
