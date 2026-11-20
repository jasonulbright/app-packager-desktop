using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// SUP-04 and DEP-04 and their fix: an active application that supersedes or depends on a retired application. The supported cmdlets remove the
/// rule or the dependency (both applications exist), the change makes one new revision, and with the check box on the fix deletes the old
/// revisions of exactly the changed application. Through the real script and the stub site.
/// </summary>
public sealed class SiteHygieneRetiredFixTests
{
    private const string Scope = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E";
    private const string ActiveModel = Scope + "/Application_cccc0000-0000-4000-8000-000000000001";
    private const string RetiredModel = Scope + "/Application_cccc0000-0000-4000-8000-000000000002";
    private const string OtherModel = Scope + "/Application_cccc0000-0000-4000-8000-000000000003";
    private const string ActiveCi = "16791001";
    private const string RetiredCi = "16791002";

    private static JsonObject AppRow(string model, string ci, string name, bool retired = false) => new()
    {
        ["CI_ID"] = ci, ["ModelID"] = (int.Parse(ci) + 1000).ToString(), ["ModelName"] = model, ["LocalizedDisplayName"] = name, ["IsLatest"] = true,
        ["IsDeployed"] = "False", ["IsExpired"] = retired ? "True" : "False", ["IsSuperseded"] = "False", ["IsSuperseding"] = "False", ["IsEnabled"] = "True", ["HasContent"] = "True",
        ["Manufacturer"] = "Lab", ["SoftwareVersion"] = "1.0", ["DateCreated"] = "2026-10-01T00:00:00", ["NumberOfDeploymentTypes"] = "1", ["CIVersion"] = "1"
    };

    private static JsonObject Relation(string type) => new()
    {
        ["FromApplicationCIID"] = ActiveCi, ["FromDeploymentTypeCIID"] = "167910011", ["ToApplicationCIID"] = RetiredCi, ["ToDeploymentTypeCIID"] = "167910021", ["RelationType"] = type, ["Level"] = "1"
    };

    /// <summary>An active application that supersedes and depends on one retired application, and one that stays untouched.</summary>
    private static JsonObject State(bool supersedes = true, bool depends = true)
    {
        var state = SiteHygieneFixtures.StubState();
        var apps = state["Classes"]!["SMS_Application"]!.AsArray();
        apps.Add(AppRow(ActiveModel, ActiveCi, "ZZ-Active"));
        apps.Add(AppRow(RetiredModel, RetiredCi, "ZZ-Retired", retired: true));
        apps.Add(AppRow(OtherModel, "16791003", "ZZ-Other"));
        var relations = state["Classes"]!["SMS_AppRelation_Flat"]!.AsArray();
        if (supersedes)
        {
            relations.Add(Relation("15"));
            state["Supersedence"]!.AsObject()[ActiveModel] = new JsonArray(new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["RuleId"] = "DTRule_1", ["Ref"] = RetiredModel });
        }
        if (depends)
        {
            relations.Add(Relation("10"));
            state["Dependencies"]!.AsObject()[ActiveModel] = new JsonArray(
                new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["To"] = RetiredModel },
                new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["To"] = OtherModel });
        }
        return state;
    }

    private static HygieneFinding Find(IReadOnlyList<HygieneFinding> findings, string check) =>
        Assert.Single(findings, f => f.CheckId == check && f.FixParameters.GetValueOrDefault(HygieneFixParameters.ModelName) == ActiveModel);

    private static string[] SiteCommands(SiteHygieneHarness h) => h.Site.Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal)).ToArray();

    private static int OldRevisions(StubHygieneSite site) =>
        site.Class("SMS_Application").Count(r => (string?)r!["ModelName"] == ActiveModel && r["IsLatest"]!.GetValue<bool>() == false);

    [Fact]
    public async Task TheScanOffersTheFixForARuleOrADependencyOnARetiredApplication()
    {
        using var h = new SiteHygieneHarness(State());
        var result = await h.ScanAsync("AppRelationships");
        var sup = Find(result.Findings, "SUP-04");
        var dep = Find(result.Findings, "DEP-04");
        Assert.Equal(HygieneFixKind.ClearRetiredRelation, sup.FixKind);
        Assert.Equal(HygieneFixKind.ClearRetiredRelation, dep.FixKind);
        Assert.Equal(RelationKinds.Supersedes, sup.FixParameters[HygieneFixParameters.RelationKind]);
        Assert.Equal(RelationKinds.DependsOn, dep.FixParameters[HygieneFixParameters.RelationKind]);
        Assert.Equal(RetiredModel, dep.FixParameters[HygieneFixParameters.ReferencedModel]);
        Assert.True(HygieneFixKinds.MakesRevision(HygieneFixKind.ClearRetiredRelation));
    }

    [Fact]
    public async Task ThePlanNamesTheApplicationTheDeploymentTypeAndTheRetiredTargetAndChangesNothing()
    {
        using var h = new SiteHygieneHarness(State());
        var finding = Find((await h.ScanAsync("AppRelationships")).Findings, "SUP-04");
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredRelation, finding);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        var target = Assert.Single(change.Targets);
        Assert.Equal("ZZ-Active", target.ObjectName);
        Assert.Contains("'DT A'", target.Detail);
        Assert.Contains("ZZ-Retired", target.Detail);
        Assert.Contains("new revision", target.Detail);
        Assert.Contains("Set-CMApplicationSupersedence", change.FixText);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task TheSupersedenceFixRemovesTheRuleWithTheCmdletAndDeletesTheOldRevisionsOfThatApplicationOnly()
    {
        using var h = new SiteHygieneHarness(State());
        var finding = Find((await h.ScanAsync("AppRelationships")).Findings, "SUP-04");
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredRelation, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", SiteCommands(h)));
        Assert.Contains("1 old revision(s) deleted.", result.Message);
        Assert.Equal("Cmd:SupersedenceCmdlet|" + ActiveModel + "|" + RetiredModel, SiteCommands(h)[0]);
        Assert.Equal(0, OldRevisions(h.Site));
        Assert.Empty(h.Site.State["Supersedence"]![ActiveModel]!.AsArray());
        // The dependency and the other application are untouched.
        Assert.Equal(2, h.Site.State["Dependencies"]![ActiveModel]!.AsArray().Count);
        var rescan = await h.ScanAsync("AppRelationships", "Applications");
        Assert.DoesNotContain(rescan.Findings, f => f.CheckId == "SUP-04" && f.ObjectKey.StartsWith(ActiveModel, StringComparison.Ordinal));
        Assert.DoesNotContain(rescan.Findings, f => f.CheckId == "APP-05" && f.ObjectKey == ActiveModel);
    }

    [Fact]
    public async Task TheDependencyFixRemovesOnlyTheDependencyOnTheRetiredApplicationAndKeepsTheOldRevisionWithoutTheOption()
    {
        using var h = new SiteHygieneHarness(State(supersedes: false));
        var finding = Find((await h.ScanAsync("AppRelationships")).Findings, "DEP-04");
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredRelation, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", SiteCommands(h)));
        Assert.Equal(["Cmd:DependencyRemoval|" + ActiveModel + "|DeploymentType_A|" + RetiredModel], SiteCommands(h));
        var left = h.Site.State["Dependencies"]![ActiveModel]!.AsArray();
        Assert.Equal(OtherModel, (string?)Assert.Single(left)!["To"]);
        Assert.Equal(1, OldRevisions(h.Site));
        Assert.DoesNotContain("old revision", result.Message);
    }

    [Fact]
    public async Task ARevisionThatAnotherObjectUsesStaysAndTheRunDoesNotFail()
    {
        var state = State(supersedes: false);
        state["Options"]!["RevisionInUse"] = new JsonArray(1);
        using var h = new SiteHygieneHarness(state);
        var finding = Find((await h.ScanAsync("AppRelationships")).Findings, "DEP-04");
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredRelation, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Done, result.Result);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("Kept because another object uses them: 1.", result.Message);
    }

    [Fact]
    public async Task ATargetThatIsNotRetiredAnyMoreIsSkippedAndNothingIsChanged()
    {
        using var h = new SiteHygieneHarness(State(supersedes: false));
        var finding = Find((await h.ScanAsync("AppRelationships")).Findings, "DEP-04");
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredRelation, finding);
        foreach (var row in h.Site.Class("SMS_Application").OfType<JsonObject>().Where(r => (string?)r["ModelName"] == RetiredModel)) row["IsExpired"] = "False";
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.NotEqual(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Empty(SiteCommands(h));
        Assert.Equal(2, h.Site.State["Dependencies"]![ActiveModel]!.AsArray().Count);
    }

    [Fact]
    public async Task AFindingThatNamesTheWrongRelationKindIsRefused()
    {
        using var h = new SiteHygieneHarness(State());
        var finding = Find((await h.ScanAsync("AppRelationships")).Findings, "DEP-04");
        var wrong = finding with { FixParameters = new Dictionary<string, string>(finding.FixParameters) { [HygieneFixParameters.RelationKind] = RelationKinds.Supersedes } };
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.ClearRetiredRelation, wrong));
    }
}
