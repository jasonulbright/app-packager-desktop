using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.DpContent.DpContentHarness;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>The real DP content script through the stub site: application names, UTC times, deployment counts at load, the last-copy rule for never-installed content and the history result words.</summary>
public sealed class DpContentServiceRound3Tests
{
    private const string Cm01 = StubDpContentSite.Nal;
    private const string SecondModel = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_0b4c1d2e-3f40-4a5b-8c6d-7e8f90a1b2c3";
    private const string CutName = "DPCM-Test-202610231921-3";

    [Fact]
    public async Task ApplicationContentShowsTheApplicationNameAndNoTwoContentsShareOne()
    {
        var state = StubDpContentSite.Lab();
        var packages = (JsonArray)state["Packages"]!;
        packages.Add(new JsonObject { ["Class"] = "SMS_ContentPackage", ["PackageID"] = "MCM000A9", ["Name"] = CutName, ["PackageSize"] = 1, ["SecurityKey"] = StubDpContentSite.AppModel });
        packages.Add(new JsonObject { ["Class"] = "SMS_ContentPackage", ["PackageID"] = "MCM000AA", ["Name"] = CutName, ["PackageSize"] = 1, ["SecurityKey"] = SecondModel });
        ((JsonArray)state["Applications"]!).Add(new JsonObject { ["CI_ID"] = 16780970, ["ModelID"] = 16780971, ["ModelName"] = SecondModel, ["LocalizedDisplayName"] = "Second Application", ["IsLatest"] = true });
        using var h = new DpContentHarness(state);
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);

        var name = "DPCM Test Application 202610231921 with a display name longer than fifty characters";
        Assert.Equal(name + " (MCM000A8)", snapshot.Content.Single(c => c.PackageId == "MCM000A8").Name);
        Assert.Equal(name + " (MCM000A9)", snapshot.Content.Single(c => c.PackageId == "MCM000A9").Name);
        Assert.Equal("Second Application", snapshot.Content.Single(c => c.PackageId == "MCM000AA").Name);
        Assert.Equal(name + " (MCM000A8)", snapshot.Pairs.Single(p => p.PackageId == "MCM000A8").ContentName);
        Assert.Equal(snapshot.Content.Count, snapshot.Content.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task EveryContentPackageOfAnApplicationCountsItsDeploymentsAtLoadAndAtPrepare()
    {
        var state = StubDpContentSite.Lab();
        ((JsonArray)state["Packages"]!).Add(new JsonObject { ["Class"] = "SMS_ContentPackage", ["PackageID"] = "MCM000A9", ["Name"] = CutName, ["PackageSize"] = 1, ["SecurityKey"] = StubDpContentSite.AppModel });
        ((JsonArray)state["Summaries"]!).Add(StubDpContentSite.Sum("MCM000A9", Cm01, 0, 8));
        ((JsonArray)state["Instances"]!).Add(StubDpContentSite.Inst("MCM000A9", Cm01, 0, "2026-10-23T23:39:18"));
        using var h = new DpContentHarness(state);
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);
        Assert.Equal(1, snapshot.Content.Single(c => c.PackageId == "MCM000A8").DeploymentCount);
        Assert.Equal(1, snapshot.Content.Single(c => c.PackageId == "MCM000A9").DeploymentCount);

        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A8", Cm01, 0, DpContentTypes.Application), Pair("MCM000A9", Cm01, 0, DpContentTypes.Application));
        Assert.All(change.ContentImpact, i => Assert.Equal(1, i.DeploymentCount));
    }

    [Fact]
    public async Task TheLoadReadsTheDeploymentCountsWithThreeBulkQueriesAndMarksTheSummaryDateUtc()
    {
        using var h = new DpContentHarness();
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);

        Assert.Single(h.Site.Runs);
        Assert.Equal(3, snapshot.Content.Single(c => c.PackageId == "MCM0001B").DeploymentCount);
        Assert.Equal(1, snapshot.Content.Single(c => c.PackageId == "MCM000A8").DeploymentCount);
        Assert.Equal(1, snapshot.Content.Single(c => c.PackageId == "MCM00006").DeploymentCount);
        Assert.Equal(0, snapshot.Content.Single(c => c.PackageId == "MCM000A6").DeploymentCount);
        Assert.Equal(-1, snapshot.Content.Single(c => c.PackageId == "MCM00005").DeploymentCount);
        Assert.Equal(1, h.Site.Queries("SMS_Advertisement"));
        Assert.Equal(1, h.Site.Queries("SMS_TaskSequencePackageReference_Flat"));
        Assert.Equal(1, h.Site.Queries("SMS_ApplicationAssignment"));
        Assert.All(snapshot.Pairs, p => Assert.Equal(DateTimeKind.Utc, p.SummaryDate!.Value.Kind));
    }

    [Fact]
    public async Task AFailedDeploymentQueryAtLoadLeavesTheCountsUnknown()
    {
        using var h = new DpContentHarness();
        h.Site.Fail("query:SMS_ApplicationAssignment");
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);

        Assert.All(snapshot.Content, c => Assert.Equal(-1, c.DeploymentCount));
        Assert.Equal(7, snapshot.Pairs.Count);
    }

    [Fact]
    public async Task AFailedApplicationReadAtLoadKeepsThePackageNamesAndLeavesTheCountsUnknown()
    {
        using var h = new DpContentHarness();
        h.Site.Fail("query:SMS_Application");
        var snapshot = await h.Service.LoadAsync(new Progress<string>(), CancellationToken.None);

        Assert.Equal(CutName, snapshot.Content.Single(c => c.PackageId == "MCM000A8").Name);
        Assert.All(snapshot.Content, c => Assert.Equal(-1, c.DeploymentCount));
    }

    [Fact]
    public async Task RemovingANeverInstalledPairNeedsNoLastCopyBox()
    {
        using var h = new DpContentHarness();
        var change = await h.PrepareAsync(DpContentChangeKind.Remove, Pair("MCM000A7", Cm01, DpContentStates.ContentUpdating));

        Assert.False(change.NeedsLastCopyAcknowledgement);
        Assert.Equal(DeploymentCheckResults.Pass, change.Checks.Single(c => c.Name == DpContentService.CheckNames.LastCopy).Result);
        Assert.False(Assert.Single(change.ContentImpact).RemovesInstalledCopy);
        var outcome = await h.ApplyAsync(change with { RemovalConfirmed = true });
        Assert.NotEqual(DpContentResults.Skipped, Assert.Single(outcome.Pairs).Result);
        Assert.Contains("Delete:MCM000A7", h.Site.Log);
    }

    [Fact]
    public async Task ARunWithNoConfirmedPairIsRecordedRequestedAndItsMessageSaysTheWindowEnded()
    {
        using var h = new DpContentHarness(window: TimeSpan.FromSeconds(4));
        h.Site.SetOption("Ticks", 50);
        var change = await h.PrepareAsync(DpContentChangeKind.Redistribute, Pair("MCM000A6", Cm01, 0));
        var outcome = await h.ApplyAsync(change);

        var pair = Assert.Single(outcome.Pairs);
        Assert.Equal(DpContentResults.Requested, pair.Result);
        Assert.DoesNotContain("is copying", pair.Message);
        Assert.Contains("read-back window ended", pair.Message);
        Assert.Equal(DpContentResults.Requested, Assert.Single(h.Audit.Read(10)).Result);
    }
}
