using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygieneFixTests;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// A finding whose delete the service refuses carries no fix: the scan reads the same facts the delete rule
/// reads (automatic deployment rule targets, client settings deployments, other deployments by target,
/// Auto Apply Drivers steps) and names the reason. Also the texts of the messages and the evidence.
/// </summary>
public sealed class SiteHygieneReportOnlyFindingTests
{
    [Fact]
    public void TheDriverPackagesOfASiteWithAnAutoApplyDriversStepAreReportOnlyWithTheReasonOfTheService()
    {
        var (findings, _) = SiteHygieneParity.Native("TaskSequences");
        var driver = Assert.Single(findings, f => f.CheckId == "TSQ-02" && f.ObjectType == "DriverPackage");
        Assert.Equal(HygieneFixKind.None, driver.FixKind);
        Assert.Null(driver.FixText);
        Assert.Equal("1 task sequence has an Auto Apply Drivers step, which can use the content of any driver package without a reference, so the page does not delete a driver package.", driver.FixRefusal);
        var boot = Assert.Single(findings, f => f.CheckId == "TSQ-02" && f.ObjectType == "BootImage");
        Assert.Equal(HygieneFixKind.RemoveBootImage, boot.FixKind);
        Assert.Equal("", boot.FixRefusal);
    }

    [Fact]
    public async Task TheDriverPackagesOfASiteWithoutAnAutoApplyDriversStepKeepTheirDelete()
    {
        using var h = new SiteHygieneHarness();
        var result = await h.ScanAsync("TaskSequences");
        var driver = Assert.Single(result.Findings, f => f.CheckId == "TSQ-02" && f.ObjectType == "DriverPackage");
        Assert.Equal(HygieneFixKind.RemoveDriverPackage, driver.FixKind);
        Assert.Equal("", driver.FixRefusal);
    }

    public static TheoryData<string, string> Users => new()
    {
        { "Adr", "1 automatic deployment rule targets the collection" },
        { "ClientSettings", "1 client settings deployment targets the collection" },
        { "Assignment", "1 deployment of an application, a software update, a baseline or a policy targets the collection" }
    };

    [Theory]
    [MemberData(nameof(Users))]
    public async Task AnEmptyCollectionThatAnotherObjectTargetsIsReportOnlyWithTheReason(string user, string use)
    {
        var state = SiteHygieneFixtures.StubState();
        JsonArray Class(string name) => state["Classes"]![name] as JsonArray ?? (JsonArray)(state["Classes"]![name] = new JsonArray());
        switch (user)
        {
            case "Adr": Class("SMS_AutoDeployment").Add(new JsonObject { ["AutoDeploymentID"] = "4", ["Name"] = "SHL-ADR-ColRef", ["AutoDeploymentEnabled"] = "True", ["LastRunTime"] = "", ["LastErrorCode"] = "0", ["CollectionID"] = "MCM00215" }); break;
            case "ClientSettings": Class("SMS_ClientSettingsAssignment").Add(new JsonObject { ["ClientSettingsID"] = "16777221", ["CollectionID"] = "MCM00215" }); break;
            default: Class("SMS_CIAssignmentBaseClass").Add(new JsonObject { ["AssignmentID"] = "16777300", ["TargetCollectionID"] = "MCM00215" }); break;
        }
        using var h = new SiteHygieneHarness(state);
        var result = await h.ScanAsync("Collections");
        var col = Assert.Single(result.Findings, f => f.CheckId == "COL-01" && f.ObjectKey == "MCM00215");
        Assert.Equal(HygieneFixKind.None, col.FixKind);
        Assert.Null(col.FixText);
        Assert.Equal(use + ", so the page does not delete the collection. Remove that use first.", col.FixRefusal);
        Assert.EndsWith(" " + use + ".", col.Evidence);
        Assert.Contains(result.Findings, f => f.CheckId == "COL-01" && f.ObjectKey != "MCM00215" && f.FixKind == HygieneFixKind.RemoveCollection);

        var prepared = await PrepareAsync(h, HygieneFixKind.RemoveCollection, col);
        Assert.True(prepared.Blocked);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task AFailedReadOfTheClientSettingsDeploymentsMakesCol01NotRun()
    {
        using var h = new SiteHygieneHarness();
        h.Site.Fail("query:SMS_ClientSettingsAssignment");
        var result = await h.ScanAsync("Collections");
        Assert.Equal(HygieneCheckStates.NotRun, result.Checks.Single(c => c.CheckId == "COL-01").State);
        Assert.DoesNotContain(result.Findings, f => f.CheckId == "COL-01");
    }

    [Fact]
    public async Task OneDeploymentThatTargetsTheCollectionNowIsNamedInTheSingular()
    {
        using var h = new SiteHygieneHarness();
        var finding = Finding(f => f.CheckId == "COL-01" && f.ObjectKey == "MCM00215");
        var change = await PrepareAsync(h, HygieneFixKind.RemoveCollection, finding);
        h.Site.Class("SMS_DeploymentSummary").Add(new JsonObject { ["DeploymentID"] = "MCM20999", ["SoftwareName"] = "SHL", ["CollectionID"] = "MCM00215", ["FeatureType"] = 1 });
        var outcome = await ApplyAsync(h, change);
        Assert.Equal("Skipped: the finding no longer holds: 1 deployment targets the collection now.", Assert.Single(outcome.Objects).Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public void TheNewestClientVersionIsPrintedAsTheSiteHoldsIt()
    {
        var (findings, _) = SiteHygieneParity.Native("Devices");
        var drift = Assert.Single(findings, f => f.CheckId == "DEV-03");
        Assert.Matches(@"below the newest seen \(5\.00\.\d+\.\d+\)", drift.Evidence);
        Assert.DoesNotContain("(5.0.", drift.Evidence);
    }

    [Fact]
    public void AStoredScanKeepsTheReasonOfAReportOnlyFinding()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shro-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HygieneScanStore(Path.Combine(folder, "last-scan.json"));
            var finding = HygieneChecks.F("TSQ-02", "DriverPackage", "MCM00111", "SHL-DriverPkg-Unused", "e", "r") with { FixRefusal = "The reason." };
            store.Save(new HygieneScanResult([finding], [], DateTime.UtcNow, false, null) { SiteCode = "MCM", Provider = "CM01" });
            Assert.Equal("The reason.", Assert.Single(store.Load()!.Findings).FixRefusal);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
