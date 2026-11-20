using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// Retired and superseded applications: APP-07, APP-08, TSQ-05, TSQ-06 and DPL-06. The rule: no active application depends on or supersedes
/// a retired application, and no task sequence installs a retired or superseded one.
/// </summary>
public sealed class SiteHygieneRetiredTests
{
    private static readonly string[] Rel = ["FromApplicationCIID", "FromDeploymentTypeCIID", "ToApplicationCIID", "ToDeploymentTypeCIID", "RelationType"];
    private static readonly string[] AppCols = ["CI_ID", "ModelID", "ModelName", "LocalizedDisplayName", "IsDeployed", "IsExpired", "IsSuperseded", "IsSuperseding", "IsEnabled", "HasContent", "Manufacturer", "SoftwareVersion", "DateCreated", "NumberOfDeploymentTypes", "CIVersion"];
    private static readonly string[] DtCols = ["CI_ID", "ModelName", "AppModelName", "LocalizedDisplayName", "ContentId", "IsLatest"];
    private static readonly string[] TsCols = ["PackageID", "Name", "BootImageID", "ProgramFlags"];
    private static readonly string[] RefCols = ["PackageID", "ObjectID"];
    private static readonly string[] StepCols = ["PackageID", "ObjectID", "Type"];
    private static readonly string[] DepCols = ["PackageID", "SoftwareName", "ProgramName", "ModelName", "CollectionID", "CollectionName", "DeploymentIntent", "FeatureType", "NumberTargeted", "NumberSuccess", "NumberInProgress", "NumberErrors", "EnforcementDeadline", "CreationTime", "DeploymentID"];

    private static string Model(string name) => "ScopeId_X/Application_" + name;

    private static string[] App(int ci, string name, bool retired = false, bool superseded = false, bool deployed = false) =>
        [ci.ToString(), (ci + 1000).ToString(), Model(name), name, deployed ? "True" : "False", retired ? "True" : "False", superseded ? "True" : "False", "False", "True", "True", "Contoso", "1.0", "2026-01-01T00:00:00", "1", "1"];

    private static string[] Dt(int appCi, string name) => [(appCi * 10).ToString(), "ScopeId_X/DeploymentType_" + name, Model(name), name + " DT", "", "True"];

    private static HygieneSiteRead Site(params string[][] apps)
    {
        var read = SiteHygieneParity.EmptySite().Put("Applications", AppCols, apps);
        read.Put("DeploymentTypes", DtCols, apps.Select(a => Dt(int.Parse(a[0]), a[3])).ToArray());
        return read;
    }

    private static IReadOnlyList<HygieneFinding> Scan(HygieneSiteRead read, params string[] areas) => SiteHygieneParity.Native(read, areas).Findings;

    [Fact]
    public void ARetiredApplicationThatSupersedesOrDependsOnAnActiveOneIsOneFinding()
    {
        var read = Site(App(1, "Retired", retired: true), App(2, "Active"), App(3, "ActiveToo"))
            .Put("AppRelations", Rel, ["1", "10", "2", "20", "15"], ["1", "10", "3", "30", "10"]);
        var f = Scan(read, "AppRelationships");
        var one = Assert.Single(f, x => x.CheckId == "APP-07");
        Assert.Equal(Model("Retired"), one.ObjectKey);
        Assert.Contains("supersedes 'Active'", one.Evidence);
        Assert.Contains("depends on 'ActiveToo'", one.Evidence);
        Assert.Equal(HygieneFixKind.ClearRetiredApplicationRules, one.FixKind);
        Assert.Equal(Model("Active"), one.FixParameters[HygieneFixParameters.SupersedesModels]);
        Assert.Equal(Model("ActiveToo"), one.FixParameters[HygieneFixParameters.DependsOnModels]);
    }

    [Fact]
    public void ARuleOfARetiredApplicationToARetiredTargetIsLeftToSup04AndDep04()
    {
        var read = Site(App(1, "Retired", retired: true), App(2, "AlsoRetired", retired: true))
            .Put("AppRelations", Rel, ["1", "10", "2", "20", "15"]);
        var f = Scan(read, "AppRelationships");
        Assert.DoesNotContain(f, x => x.CheckId == "APP-07");
        Assert.Contains(f, x => x.CheckId == "SUP-04");
    }

    [Fact]
    public void ASupersededApplicationNothingElseRefersToQualifiesForDeletion()
    {
        var read = Site(App(1, "New"), App(2, "Old", superseded: true)).Put("AppRelations", Rel, ["1", "10", "2", "20", "15"]);
        var f = Assert.Single(Scan(read, "AppRelationships"), x => x.CheckId == "APP-08");
        Assert.Equal(Model("Old"), f.ObjectKey);
        Assert.Contains("superseded by 'New'", f.Evidence);
        Assert.Contains("Blocked by: nothing", f.Evidence);
    }

    [Fact]
    public void ASupersededApplicationIsNotReportedWhenADeploymentADependencyATaskSequenceOrAStaleRowRefersToIt()
    {
        var sup = new[] { "1", "10", "2", "20", "15" };
        // deployed
        Assert.DoesNotContain(Scan(Site(App(1, "New"), App(2, "Old", superseded: true, deployed: true)).Put("AppRelations", Rel, sup), "AppRelationships"), x => x.CheckId == "APP-08");
        // a dependency on it
        Assert.DoesNotContain(Scan(Site(App(1, "New"), App(2, "Old", superseded: true), App(3, "Dependent")).Put("AppRelations", Rel, sup, ["3", "30", "2", "20", "10"]), "AppRelationships"), x => x.CheckId == "APP-08");
        // a task sequence step
        var ts = Site(App(1, "New"), App(2, "Old", superseded: true)).Put("AppRelations", Rel, sup)
            .Put("TaskSequences", TsCols, ["MCM00010", "Build", "", "0"]).Put("TsSteps", StepCols, ["MCM00010", Model("Old"), "1"]);
        Assert.DoesNotContain(Scan(ts, "AppRelationships"), x => x.CheckId == "APP-08");
        // a stale reference row
        var stale = Site(App(1, "New"), App(2, "Old", superseded: true)).Put("AppRelations", Rel, sup).Put("TsReferences", RefCols, ["MCM000AA", Model("Old")]);
        Assert.DoesNotContain(Scan(stale, "AppRelationships"), x => x.CheckId == "APP-08");
    }

    [Fact]
    public void ARetiredSupersededApplicationIsLeftToSup04()
    {
        var read = Site(App(1, "New"), App(2, "Old", retired: true, superseded: true)).Put("AppRelations", Rel, ["1", "10", "2", "20", "15"]);
        var f = Scan(read, "AppRelationships");
        Assert.DoesNotContain(f, x => x.CheckId == "APP-08");
        Assert.Contains(f, x => x.CheckId == "SUP-04");
    }

    [Fact]
    public void ATaskSequenceStepThatInstallsARetiredOrSupersededApplicationIsAFindingWithTheNameAndThePackageId()
    {
        var read = Site(App(1, "Retired", retired: true), App(2, "Old", superseded: true), App(3, "Active"))
            .Put("AppRelations", Rel, ["3", "30", "2", "20", "15"])
            .Put("TaskSequences", TsCols, ["MCM00010", "Build PC", "", "0"])
            .Put("TsReferences", RefCols, ["MCM00010", Model("Active")])
            .Put("TsSteps", StepCols, ["MCM00010", Model("Retired"), "1"], ["MCM00010", Model("Old"), "1"], ["MCM00010", Model("Active"), "1"]);
        var f = Scan(read, "AppRelationships").Where(x => x.CheckId == "TSQ-05").ToList();
        Assert.Equal(2, f.Count);
        var retired = f.Single(x => x.ObjectName.EndsWith("Retired", StringComparison.Ordinal));
        Assert.Equal("MCM00010|" + Model("Retired"), retired.ObjectKey);
        Assert.Contains("Task sequence 'Build PC' (package MCM00010)", retired.Evidence);
        Assert.Contains("retired application", retired.Evidence);
        Assert.Contains("superseded by 'Active'", f.Single(x => x.ObjectName.EndsWith("Old", StringComparison.Ordinal)).Evidence);
        Assert.All(f, x => Assert.Equal(HygieneFixKind.None, x.FixKind));
    }

    [Fact]
    public void ReferenceRowsOfATaskSequenceThatTheSiteDoesNotReturnAreAStaleReference()
    {
        var read = Site(App(1, "Active"))
            .Put("TaskSequences", TsCols, ["MCM00010", "Live", "", "0"])
            .Put("TsReferences", RefCols, ["MCM000AA", Model("Active")], ["MCM00010", Model("Active")]);
        var f = Assert.Single(Scan(read, "AppRelationships"), x => x.CheckId == "TSQ-06");
        Assert.Equal("MCM000AA", f.ObjectKey);
        Assert.StartsWith("Stale reference", f.Evidence);
        Assert.Contains("Active", f.Evidence);
    }

    private static string[] Deployment(string id, int ageDays, int intent = 1, int success = 5)
    {
        var created = SiteHygieneFixtures.CaptureLocal.AddDays(-ageDays).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        return ["PKG00001", "App " + id, "Install", Model(id), "MCM00001", "All", intent.ToString(), "1", "10", success.ToString(), "0", "0", "", created, id];
    }

    [Fact]
    public void ADeploymentOlderThanTheThresholdIsReportedForReviewAndOneThatDpl03ReportsIsLeftToDpl03()
    {
        var read = SiteHygieneParity.EmptySite().Put("Deployments", DepCols, Deployment("Old", 400), Deployment("Young", 100), Deployment("Unused", 400, intent: 2, success: 0));
        var f = Scan(read, "Deployments");
        var one = Assert.Single(f, x => x.CheckId == "DPL-06");
        Assert.Equal("Old", one.ObjectKey);
        Assert.Contains("more than 365 days ago", one.Evidence);
        Assert.Contains("Review whether the deployment is still needed", one.Recommendation);
        Assert.Equal(HygieneFixKind.None, one.FixKind);
        Assert.Contains(f, x => x.CheckId == "DPL-03" && x.ObjectKey == "Unused");
        Assert.DoesNotContain(f, x => x.CheckId == "DPL-06" && x.ObjectKey == "Unused");
    }

    [Fact]
    public void TheDeploymentAgeThresholdIsASetting()
    {
        var read = SiteHygieneParity.EmptySite().Put("Deployments", DepCols, Deployment("Mid", 200));
        var picked = SiteHygieneService.ResolveAreas(new HashSet<string> { "Deployments" });
        Assert.Equal(365, new HygieneThresholds().DeploymentOldDays);
        Assert.DoesNotContain(HygieneEngine.Run(new HygieneContext(read, new HygieneThresholds(), SiteHygieneFixtures.CaptureLocal, "MCM"), picked, false).Findings, x => x.CheckId == "DPL-06");
        Assert.Contains(HygieneEngine.Run(new HygieneContext(read, new HygieneThresholds { DeploymentOldDays = 180 }, SiteHygieneFixtures.CaptureLocal, "MCM"), picked, false).Findings, x => x.CheckId == "DPL-06");
    }

    [Fact]
    public void TheNewChecksAreInTheCatalogAndRunOnTheCm01Rows()
    {
        foreach (var id in new[] { "APP-07", "APP-08", "TSQ-05", "TSQ-06", "DPL-06" })
            Assert.Contains(HygieneCatalog.Checks, c => c.Id == id);
        var (findings, checks) = SiteHygieneParity.Native();
        foreach (var id in new[] { "APP-07", "APP-08", "TSQ-05", "TSQ-06", "DPL-06" })
            Assert.Equal(HygieneCheckStates.Ran, checks.Single(c => c.CheckId == id).State);
    }
}
