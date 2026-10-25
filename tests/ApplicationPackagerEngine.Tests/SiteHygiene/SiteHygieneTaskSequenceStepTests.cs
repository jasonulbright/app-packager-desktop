using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygieneFixTests;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// An application without content in an Install Application step has no row in
/// SMS_TaskSequencePackageReference_All; the References property of the task sequence lists it, and
/// SMS_Application.NumberOfDependentTS counts it. APP-01, TSQ-03 and the delete guard read
/// both.
/// </summary>
public sealed class SiteHygieneTaskSequenceStepTests
{
    private static HygieneFinding CmTrace => Finding(f => f.CheckId == "APP-01" && f.ObjectName == "CMTrace Open");

    /// <summary>The stub site with CMTrace Open in an Install Application step of MCM00008 and no reference row for it, as CM01 holds an application without content.</summary>
    private static JsonObject StepOnly(bool autoInstall)
    {
        var state = SiteHygieneFixtures.StubState();
        var ts = state["Classes"]!["SMS_TaskSequencePackage"]!.AsArray().OfType<JsonObject>().Single(t => (string?)t["PackageID"] == "MCM00008");
        ((JsonArray)ts["References"]!).Add(new JsonObject { ["Package"] = CmTrace.ObjectKey, ["Type"] = 1 });
        state["AutoInstall"]![CmTrace.ObjectKey] = autoInstall;
        return state;
    }

    [Fact]
    public async Task AnApplicationThatOnlyTheStepsOfATaskSequenceNameIsNotUnusedAndTsq03ReportsIt()
    {
        using var h = new SiteHygieneHarness(StepOnly(autoInstall: false));
        var result = await h.ScanAsync("Applications", "TaskSequences");

        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "APP-01").State);
        Assert.DoesNotContain(result.Findings, f => f.CheckId == "APP-01" && f.ObjectKey == CmTrace.ObjectKey);
        var tsq03 = Assert.Single(result.Findings, f => f.CheckId == "TSQ-03" && f.ObjectKey == CmTrace.ObjectKey);
        Assert.Contains("'Build Win11' installs this application", tsq03.Evidence);
        Assert.Contains(h.Site.Log, l => l == "Get-CMApplication:" + CmTrace.ObjectKey);
    }

    [Fact]
    public void AnApplicationTheSiteCountsAsUsedByATaskSequenceIsNotUnused()
    {
        var read = SiteHygieneFixtures.NativeRead();
        var apps = read.Table("Applications");
        var row = apps.Rows.Single(r => r[apps.Index("ModelName")] == CmTrace.ObjectKey);
        var withCount = new HygieneTable("Applications", [.. apps.Columns, "NumberOfDependentTS"]) { State = HygieneSetStates.Read, Total = apps.Total };
        withCount.Rows.AddRange(apps.Rows.Select(r => (string[])[.. r, ReferenceEquals(r, row) ? "1" : "0"]));
        read.Tables["Applications"] = withCount;

        var (findings, _) = SiteHygieneParity.Native(read, "Applications");
        Assert.DoesNotContain(findings, f => f.CheckId == "APP-01" && f.ObjectKey == CmTrace.ObjectKey);
        Assert.Contains(findings, f => f.CheckId == "APP-01");
    }

    [Fact]
    public async Task TheDeleteOfAnApplicationThatOnlyTheStepsOfATaskSequenceNameIsLeftOutAtPrepare()
    {
        using var h = new SiteHygieneHarness(StepOnly(autoInstall: true));
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplication, CmTrace);
        Assert.True(change.Blocked);
        Assert.Equal("Skipped: the finding no longer holds: 1 task sequence uses the application now.", Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task TheDeleteOfAnApplicationTheSiteStillCountsInATaskSequenceIsLeftOutWithTheReasonOfTheLag()
    {
        var state = SiteHygieneFixtures.StubState();
        foreach (var a in state["Classes"]!["SMS_Application"]!.AsArray().OfType<JsonObject>().Where(a => (string?)a["ModelName"] == CmTrace.ObjectKey)) a["NumberOfDependentTS"] = 1;
        using var h = new SiteHygieneHarness(state);
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplication, CmTrace);
        Assert.StartsWith("Skipped: the finding no longer holds: the site counts 1 task sequence that uses the application.", Assert.Single(change.Dropped).Message);
    }

    [Fact]
    public async Task AFailedReadOfTheStepsMakesApp01AndTheTaskSequenceChecksNotRun()
    {
        using var h = new SiteHygieneHarness();
        h.Site.Fail("query:SMS_TaskSequencePackage");
        var result = await h.ScanAsync("Applications", "TaskSequences");
        foreach (var id in new[] { "APP-01", "TSQ-01", "TSQ-02", "TSQ-03", "TSQ-04" })
        {
            var check = result.Checks.Single(c => c.CheckId == id);
            Assert.Equal(HygieneCheckStates.NotRun, check.State);
            Assert.DoesNotContain(result.Findings, f => f.CheckId == id);
        }
        Assert.Contains("the read of task sequence steps failed", result.Checks.Single(c => c.CheckId == "APP-01").Reason);
    }

    [Fact]
    public async Task MoreTaskSequencesThanTheCapMakeApp01NotRunAndTheDeleteGuardRefuses()
    {
        var state = SiteHygieneFixtures.StubState();
        state["Classes"]!["SMS_TaskSequencePackage"]!.AsArray().Add(new JsonObject { ["PackageID"] = "MCM00099", ["Name"] = "SHL-TS-Other", ["BootImageID"] = "", ["ProgramFlags"] = 0 });
        using var h = new SiteHygieneHarness(state);
        h.With(maxObjects: 1);
        var result = await h.ScanAsync("Applications");
        var app01 = result.Checks.Single(c => c.CheckId == "APP-01");
        Assert.Equal(HygieneCheckStates.NotRun, app01.State);
        Assert.Contains("task sequence steps were read for 1 of 2 objects", app01.Reason);
        Assert.True(result.Truncated);

        await Assert.ThrowsAsync<HygieneServiceException>(() => PrepareAsync(h, HygieneFixKind.RemoveApplication, CmTrace));
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task TheStateReadOfAnApplicationDeleteReadsTheStepsOfEveryTaskSequence()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplication, CmTrace);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        Assert.True(h.Site.Queries("SMS_TaskSequencePackage") >= 2);
    }
}
