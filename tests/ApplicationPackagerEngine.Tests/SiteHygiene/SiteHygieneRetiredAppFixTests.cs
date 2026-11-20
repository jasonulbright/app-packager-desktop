using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// APP-07 (a retired application that holds rules toward active applications) and APP-08 (a superseded application that nothing but the supersedence of
/// its replacement refers to) with their fixes. The supported cmdlets remove the rules (both applications exist), each change makes a new revision, and with
/// the check box on the fix deletes the old revisions of exactly the changed applications. The APP-08 delete runs only when nothing refers to the application.
/// Through the real script and the stub site.
/// </summary>
public sealed class SiteHygieneRetiredAppFixTests
{
    private const string Scope = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E";
    private const string RetiredModel = Scope + "/Application_dddd0000-0000-4000-8000-000000000001";
    private const string ActiveModel = Scope + "/Application_dddd0000-0000-4000-8000-000000000002";
    private const string OtherModel = Scope + "/Application_dddd0000-0000-4000-8000-000000000003";
    private const string OldModel = Scope + "/Application_dddd0000-0000-4000-8000-000000000004";
    private const string NewModel = Scope + "/Application_dddd0000-0000-4000-8000-000000000005";
    private const string RetiredCi = "16792001";
    private const string ActiveCi = "16792002";
    private const string OtherCi = "16792003";
    private const string OldCi = "16792004";
    private const string NewCi = "16792005";

    private static JsonObject AppRow(string model, string ci, string name, bool retired = false, bool superseded = false) => new()
    {
        ["CI_ID"] = ci, ["ModelID"] = (int.Parse(ci) + 1000).ToString(), ["ModelName"] = model, ["LocalizedDisplayName"] = name, ["IsLatest"] = true,
        ["IsDeployed"] = "False", ["IsExpired"] = retired ? "True" : "False", ["IsSuperseded"] = superseded ? "True" : "False", ["IsSuperseding"] = "False", ["IsEnabled"] = "True", ["HasContent"] = "True",
        ["Manufacturer"] = "Lab", ["SoftwareVersion"] = "1.0", ["DateCreated"] = "2026-10-01T00:00:00", ["NumberOfDeploymentTypes"] = "1", ["CIVersion"] = "1"
    };

    private static JsonObject Relation(string from, string to, string type) => new()
    {
        ["FromApplicationCIID"] = from, ["FromDeploymentTypeCIID"] = from + "1", ["ToApplicationCIID"] = to, ["ToDeploymentTypeCIID"] = to + "1", ["RelationType"] = type, ["Level"] = "1"
    };

    /// <summary>A retired application that supersedes one active application and depends on another.</summary>
    private static JsonObject RetiredState()
    {
        var state = SiteHygieneFixtures.StubState();
        var apps = state["Classes"]!["SMS_Application"]!.AsArray();
        apps.Add(AppRow(RetiredModel, RetiredCi, "ZZ-Retired", retired: true));
        apps.Add(AppRow(ActiveModel, ActiveCi, "ZZ-Active"));
        apps.Add(AppRow(OtherModel, OtherCi, "ZZ-Other"));
        var relations = state["Classes"]!["SMS_AppRelation_Flat"]!.AsArray();
        relations.Add(Relation(RetiredCi, ActiveCi, "15"));
        relations.Add(Relation(RetiredCi, OtherCi, "10"));
        state["Supersedence"]!.AsObject()[RetiredModel] = new JsonArray(new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["RuleId"] = "DTRule_1", ["Ref"] = ActiveModel });
        state["Dependencies"]!.AsObject()[RetiredModel] = new JsonArray(new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["To"] = OtherModel });
        return state;
    }

    /// <summary>An application that is superseded by one replacement and that nothing else refers to.</summary>
    private static JsonObject SupersededState()
    {
        var state = SiteHygieneFixtures.StubState();
        var apps = state["Classes"]!["SMS_Application"]!.AsArray();
        apps.Add(AppRow(OldModel, OldCi, "ZZ-Old", superseded: true));
        apps.Add(AppRow(NewModel, NewCi, "ZZ-New"));
        state["Classes"]!["SMS_AppRelation_Flat"]!.AsArray().Add(Relation(NewCi, OldCi, "15"));
        state["Supersedence"]!.AsObject()[NewModel] = new JsonArray(new JsonObject { ["Dt"] = "DeploymentType_A", ["Title"] = "DT A", ["RuleId"] = "DTRule_1", ["Ref"] = OldModel });
        return state;
    }

    private static HygieneFinding Find(IReadOnlyList<HygieneFinding> findings, string check, string model) =>
        Assert.Single(findings, f => f.CheckId == check && f.ObjectKey == model);

    private static string[] SiteCommands(SiteHygieneHarness h) => h.Site.Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal)).ToArray();

    private static int OldRevisions(StubHygieneSite site, string model) =>
        site.Class("SMS_Application").Count(r => (string?)r!["ModelName"] == model && r["IsLatest"]!.GetValue<bool>() == false);

    private static bool Exists(StubHygieneSite site, string model) =>
        site.Class("SMS_Application").Any(r => (string?)r!["ModelName"] == model);

    // ---- APP-07 --------------------------------------------------------------------------------

    [Fact]
    public async Task App07OffersTheFixWithTheTargetsOfBothKindsOfRule()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        Assert.Equal(HygieneFixKind.ClearRetiredApplicationRules, f.FixKind);
        Assert.Equal(ActiveModel, f.FixParameters[HygieneFixParameters.SupersedesModels]);
        Assert.Equal(OtherModel, f.FixParameters[HygieneFixParameters.DependsOnModels]);
        Assert.True(HygieneFixKinds.MakesRevision(HygieneFixKind.ClearRetiredApplicationRules));
        Assert.False(HygieneFixKinds.IsDelete(HygieneFixKind.ClearRetiredApplicationRules));
        Assert.Equal(2, f.FixText!.Split("\r\n").Length);
        Assert.Contains(HygieneFixKind.ClearRetiredApplicationRules, SiteHygieneService.SupportedKinds);
    }

    [Fact]
    public async Task App07PlansOneLineForEachRuleAndChangesNothing()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredApplicationRules, f);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        Assert.Equal(2, change.Targets.Count);
        Assert.All(change.Targets, t => Assert.Equal("ZZ-Retired", t.ObjectName));
        Assert.Contains(change.Targets, t => t.ObjectType == "Supersedence" && t.Detail.Contains("ZZ-Active", StringComparison.Ordinal) && t.Detail.Contains("new revision", StringComparison.Ordinal));
        Assert.Contains(change.Targets, t => t.ObjectType == "Dependency" && t.Detail.Contains("ZZ-Other", StringComparison.Ordinal));
        Assert.Contains("Set-CMApplicationSupersedence", change.FixText);
        Assert.Contains("Remove-CMDeploymentTypeDependency", change.FixText);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task App07RemovesBothRulesWithTheCmdletsAndDeletesTheOldRevisionsOfTheRetiredApplicationOnly()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredApplicationRules, f);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", SiteCommands(h)));
        Assert.Equal(2, result.Targets.Count);
        // A retired application cannot be changed: the site ignores both cmdlets for it. The fix reinstates it, changes it and retires it again, for each rule.
        Assert.Equal(
        [
            "Cmd:Resume-CMApplication|" + RetiredModel, "Cmd:SupersedenceCmdlet|" + RetiredModel + "|" + ActiveModel, "Cmd:Suspend-CMApplication|" + RetiredModel,
            "Cmd:Resume-CMApplication|" + RetiredModel, "Cmd:DependencyRemoval|" + RetiredModel + "|DeploymentType_A|" + OtherModel, "Cmd:Suspend-CMApplication|" + RetiredModel
        ], SiteCommands(h).Take(6).ToArray());
        Assert.All(h.Site.Class("SMS_Application").OfType<JsonObject>().Where(r => (string?)r["ModelName"] == RetiredModel), r => Assert.Equal("True", r["IsExpired"]!.ToString()));
        Assert.Empty(h.Site.State["Supersedence"]![RetiredModel]!.AsArray());
        Assert.Empty(h.Site.State["Dependencies"]![RetiredModel]!.AsArray());
        Assert.Equal(0, OldRevisions(h.Site, RetiredModel));
        Assert.Equal(0, OldRevisions(h.Site, ActiveModel));
        Assert.Equal(0, OldRevisions(h.Site, OtherModel));
        var rescan = await h.ScanAsync("AppRelationships", "Applications");
        Assert.DoesNotContain(rescan.Findings, x => x.CheckId == "APP-07");
        Assert.DoesNotContain(rescan.Findings, x => x.CheckId == "APP-05" && x.ObjectKey == RetiredModel);
    }

    [Fact]
    public async Task App07KeepsTheOldRevisionsWithoutTheOptionAndKeepsOneThatAnotherObjectUses()
    {
        using var keep = new SiteHygieneHarness(RetiredState());
        var f = Find((await keep.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change = await SiteHygieneFixTests.PrepareAsync(keep, HygieneFixKind.ClearRetiredApplicationRules, f);
        var outcome = await SiteHygieneFixTests.ApplyAsync(keep, change);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(OldRevisions(keep.Site, RetiredModel) >= 1);

        var state = RetiredState();
        state["Options"]!["RevisionInUse"] = new JsonArray(1);
        using var inUse = new SiteHygieneHarness(state);
        var g = Find((await inUse.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var plan = await SiteHygieneFixTests.PrepareAsync(inUse, HygieneFixKind.ClearRetiredApplicationRules, g);
        var done = await SiteHygieneFixTests.ApplyAsync(inUse, plan with { DeleteOldRevisions = true });
        Assert.True(done.Succeeded, done.Message);
        Assert.Contains(done.Objects.SelectMany(o => o.Targets), t => t.Message.Contains("Kept because another object uses them: 1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task App07SkipsAndChangesNothingWhenTheApplicationIsNotRetiredAnyMoreOrTheTargetIsRetiredNow()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredApplicationRules, f);
        foreach (var row in h.Site.Class("SMS_Application").OfType<JsonObject>().Where(r => (string?)r["ModelName"] == RetiredModel)) row["IsExpired"] = "False";
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.NotEqual(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Empty(SiteCommands(h));

        using var h2 = new SiteHygieneHarness(RetiredState());
        var f2 = Find((await h2.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change2 = await SiteHygieneFixTests.PrepareAsync(h2, HygieneFixKind.ClearRetiredApplicationRules, f2);
        foreach (var row in h2.Site.Class("SMS_Application").OfType<JsonObject>().Where(r => (string?)r["ModelName"] == ActiveModel)) row["IsExpired"] = "True";
        var outcome2 = await SiteHygieneFixTests.ApplyAsync(h2, change2);
        Assert.Empty(SiteCommands(h2).Where(c => c.Contains(ActiveModel, StringComparison.Ordinal)));
        Assert.NotEmpty(outcome2.Objects);
    }

    [Fact]
    public async Task App07RetiresTheApplicationAgainWhenTheChangeFails()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredApplicationRules, f);
        h.Site.Fail("cmd:RemoveSupersedence");
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.NotEqual(HygieneResults.Done, result.Result);
        Assert.Contains("Cmd:Suspend-CMApplication|" + RetiredModel, SiteCommands(h));
        Assert.All(h.Site.Class("SMS_Application").OfType<JsonObject>().Where(r => (string?)r["ModelName"] == RetiredModel && r["IsLatest"]!.GetValue<bool>()), r => Assert.Equal("True", r["IsExpired"]!.ToString()));
    }

    [Fact]
    public async Task App07DoesNotReinstateARetiredApplicationThatHasADeployment()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearRetiredApplicationRules, f);
        h.Site.Class("SMS_ApplicationAssignment").Add(new JsonObject { ["AssignmentID"] = "16799002", ["AssignmentUniqueID"] = "{11111111-2222-3333-4444-666666666666}", ["ApplicationName"] = "ZZ-Retired", ["AppModelID"] = "16793001", ["CollectionName"] = "All", ["TargetCollectionID"] = "SMS00001" });
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.All(Assert.Single(outcome.Objects).Targets, t => Assert.Contains("has a deployment", t.Message));
        Assert.DoesNotContain(SiteCommands(h), c => c.StartsWith("Cmd:Resume", StringComparison.Ordinal));
        Assert.Single(h.Site.State["Supersedence"]![RetiredModel]!.AsArray());
    }

    [Fact]
    public async Task App07RefusesAFindingOfAnotherCheckOrWithoutATarget()
    {
        using var h = new SiteHygieneHarness(RetiredState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-07", RetiredModel);
        Assert.Null(HygieneFixRules.Gate(HygieneFixKind.ClearRetiredApplicationRules, f));
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.ClearRetiredApplicationRules, f with { CheckId = "APP-08" }));
        var none = f with { FixParameters = new Dictionary<string, string> { [HygieneFixParameters.ModelName] = RetiredModel } };
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.ClearRetiredApplicationRules, none));
    }

    // ---- APP-08 --------------------------------------------------------------------------------

    [Fact]
    public async Task App08OffersTheDeleteFixWithTheReplacement()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        Assert.Equal(HygieneFixKind.RemoveSupersededApplication, f.FixKind);
        Assert.Equal(NewModel, f.FixParameters[HygieneFixParameters.ReplacementModels]);
        Assert.True(HygieneFixKinds.IsDelete(HygieneFixKind.RemoveSupersededApplication));
        Assert.True(HygieneFixKinds.MakesRevision(HygieneFixKind.RemoveSupersededApplication));
        Assert.StartsWith("Remove", HygieneFixKind.RemoveSupersededApplication.ToString(), StringComparison.Ordinal);
        // A kind whose name starts with Remove is a delete for the plan: every such kind is in IsDelete.
        Assert.All(Enum.GetValues<HygieneFixKind>().Where(k => k.ToString().StartsWith("Remove", StringComparison.Ordinal)), k => Assert.True(HygieneFixKinds.IsDelete(k), k.ToString()));
    }

    [Fact]
    public async Task App08PlansTheRuleRemovalAndTheDeleteAsOneObjectAndChangesNothing()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveSupersededApplication, f);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        var target = Assert.Single(change.Targets);
        Assert.Equal("ZZ-Old", target.ObjectName);
        Assert.Contains("remove the supersedence rule of 'ZZ-New'", target.Detail);
        Assert.Contains("then delete the application", target.Detail);
        Assert.Contains("Set-CMApplicationSupersedence", change.FixText);
        Assert.Contains("Remove-CMApplication", change.FixText);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task App08RemovesTheRuleDeletesTheApplicationAndTheOldRevisionsOfTheReplacementOnly()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveSupersededApplication, f);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", SiteCommands(h)));
        Assert.Contains("1 old revision(s) deleted.", result.Message);
        Assert.Equal("Cmd:SupersedenceCmdlet|" + NewModel + "|" + OldModel, SiteCommands(h)[0]);
        Assert.Equal("Cmd:Remove-CMApplication -Id " + OldCi + " -Force", SiteCommands(h)[1]);
        Assert.False(Exists(h.Site, OldModel));
        Assert.True(Exists(h.Site, NewModel));
        Assert.Equal(0, OldRevisions(h.Site, NewModel));
        Assert.Empty(h.Site.State["Supersedence"]![NewModel]!.AsArray());
    }

    [Fact]
    public async Task App08KeepsTheOldRevisionsOfTheReplacementWithoutTheOption()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveSupersededApplication, f);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(Exists(h.Site, OldModel));
        Assert.Equal(1, OldRevisions(h.Site, NewModel));
    }

    [Fact]
    public async Task App08ASecondConfirmationIsRequiredAndNothingChangesWithoutIt()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveSupersededApplication, f);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change, delete: false);
        Assert.False(outcome.Succeeded);
        Assert.Empty(SiteCommands(h));
        Assert.True(Exists(h.Site, OldModel));
    }

    [Fact]
    public async Task App08ADeploymentADependencyOrATaskSequenceThatAppearsAfterThePlanStopsTheDeleteAndChangesNothing()
    {
        // A deployment.
        using var deployed = new SiteHygieneHarness(SupersededState());
        var f = Find((await deployed.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(deployed, HygieneFixKind.RemoveSupersededApplication, f);
        deployed.Site.Class("SMS_ApplicationAssignment").Add(new JsonObject { ["AssignmentID"] = "16799001", ["AssignmentUniqueID"] = "{11111111-2222-3333-4444-555555555555}", ["ApplicationName"] = "ZZ-Old", ["AppModelID"] = "16793004", ["CollectionName"] = "All", ["TargetCollectionID"] = "SMS00001" });
        var o1 = await SiteHygieneFixTests.ApplyAsync(deployed, change);
        Assert.NotEqual(HygieneResults.Done, Assert.Single(o1.Objects).Result);
        Assert.Empty(SiteCommands(deployed));
        Assert.True(Exists(deployed.Site, OldModel));
        Assert.Single(deployed.Site.State["Supersedence"]![NewModel]!.AsArray());

        // A dependency of another application.
        using var depended = new SiteHygieneHarness(SupersededState());
        var g = Find((await depended.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var plan = await SiteHygieneFixTests.PrepareAsync(depended, HygieneFixKind.RemoveSupersededApplication, g);
        depended.Site.Class("SMS_AppDependenceRelation").Add(new JsonObject { ["FromApplicationCIID"] = OtherCi, ["FromDeploymentTypeCIID"] = OtherCi + "1", ["ToApplicationCIID"] = OldCi, ["ToDeploymentTypeCIID"] = OldCi + "1", ["TypeFlag"] = "1" });
        depended.Site.Class("SMS_AppRelation_Flat").Add(Relation(OtherCi, OldCi, "10"));
        var o2 = await SiteHygieneFixTests.ApplyAsync(depended, plan);
        Assert.NotEqual(HygieneResults.Done, Assert.Single(o2.Objects).Result);
        Assert.Empty(SiteCommands(depended));
        Assert.True(Exists(depended.Site, OldModel));

        // A task sequence that installs it.
        using var sequenced = new SiteHygieneHarness(SupersededState());
        var k = Find((await sequenced.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var plan3 = await SiteHygieneFixTests.PrepareAsync(sequenced, HygieneFixKind.RemoveSupersededApplication, k);
        sequenced.Site.Class("SMS_TaskSequencePackage").Add(new JsonObject
        {
            ["PackageID"] = "ZZQ00001", ["Name"] = "ZZ TS", ["BootImageID"] = "", ["ProgramFlags"] = "0",
            ["References"] = new JsonArray(new JsonObject { ["Package"] = OldModel, ["Type"] = 1 })
        });
        var o3 = await SiteHygieneFixTests.ApplyAsync(sequenced, plan3);
        Assert.NotEqual(HygieneResults.Done, Assert.Single(o3.Objects).Result);
        Assert.Empty(SiteCommands(sequenced));
        Assert.True(Exists(sequenced.Site, OldModel));
    }

    [Fact]
    public async Task App08AReplacementThatIsRetiredIsLeftToApp07()
    {
        var state = SupersededState();
        foreach (var row in state["Classes"]!["SMS_Application"]!.AsArray().OfType<JsonObject>().Where(r => (string?)r["ModelName"] == NewModel)) row["IsExpired"] = "True";
        using var h = new SiteHygieneHarness(state);
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveSupersededApplication, f);
        Assert.Empty(change.Targets);
        Assert.Contains("APP-07", Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task App08ASecondApplicationThatSupersedesItAfterThePlanIsNotRemovedAndNothingChanges()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveSupersededApplication, f);
        h.Site.Class("SMS_Application").Add(AppRow(OtherModel, OtherCi, "ZZ-Other"));
        h.Site.Class("SMS_AppRelation_Flat").Add(Relation(OtherCi, OldCi, "15"));
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.NotEqual(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Empty(SiteCommands(h));
        Assert.True(Exists(h.Site, OldModel));
    }

    [Fact]
    public async Task App08RefusesAFindingOfAnotherCheckOrWithoutAReplacement()
    {
        using var h = new SiteHygieneHarness(SupersededState());
        var f = Find((await h.ScanAsync("AppRelationships")).Findings, "APP-08", OldModel);
        Assert.Null(HygieneFixRules.Gate(HygieneFixKind.RemoveSupersededApplication, f));
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.RemoveSupersededApplication, f with { CheckId = "APP-01" }));
        var none = f with { FixParameters = new Dictionary<string, string> { [HygieneFixParameters.ModelName] = OldModel } };
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.RemoveSupersededApplication, none));
    }

    // ---- Show Blockers: the data the scan builds ------------------------------------------------

    private static readonly string[] AppCols = ["CI_ID", "ModelID", "ModelName", "LocalizedDisplayName", "IsDeployed", "IsExpired", "IsSuperseded", "IsSuperseding", "IsEnabled", "HasContent", "Manufacturer", "SoftwareVersion", "DateCreated", "NumberOfDeploymentTypes", "CIVersion"];
    private static readonly string[] DtCols = ["CI_ID", "ModelName", "AppModelName", "LocalizedDisplayName", "ContentId", "IsLatest"];
    private static readonly string[] Rel = ["FromApplicationCIID", "FromDeploymentTypeCIID", "ToApplicationCIID", "ToDeploymentTypeCIID", "RelationType"];
    private static readonly string[] DepRel = ["FromApplicationCIID", "FromDeploymentTypeCIID", "ToApplicationCIID", "ToDeploymentTypeCIID", "TypeFlag"];
    private static readonly string[] TsCols = ["PackageID", "Name", "BootImageID", "ProgramFlags"];
    private static readonly string[] RefCols = ["PackageID", "ObjectID"];
    private static readonly string[] StepCols = ["PackageID", "ObjectID", "Type"];
    private static readonly string[] DepCols = ["PackageID", "SoftwareName", "ProgramName", "ModelName", "CollectionID", "CollectionName", "DeploymentIntent", "FeatureType", "NumberTargeted", "NumberSuccess", "NumberInProgress", "NumberErrors", "EnforcementDeadline", "CreationTime", "DeploymentID"];

    private static string M(string name) => "ScopeId_X/Application_" + name;

    private static string[] A(int ci, string name) =>
        [ci.ToString(), (ci + 1000).ToString(), M(name), name, "False", "False", "False", "False", "True", "True", "Contoso", "1.0", "2026-01-01T00:00:00", "1", "1"];

    [Fact]
    public void TheBlockersListEveryObjectThatRefersToTheApplicationFromTheDataOfTheScan()
    {
        var read = SiteHygieneParity.EmptySite()
            .Put("Applications", AppCols, A(1, "Target"), A(2, "Dependent"), A(3, "Replacement"))
            .Put("AppRevisions", ["CI_ID", "ModelName", "CIVersion"])
            .Put("DeploymentTypes", DtCols, ["10", "ScopeId_X/DeploymentType_T", M("Target"), "T DT", "", "True"], ["20", "ScopeId_X/DeploymentType_D", M("Dependent"), "D DT", "", "True"], ["30", "ScopeId_X/DeploymentType_R", M("Replacement"), "R DT", "", "True"])
            .Put("AppRelations", Rel, ["2", "20", "1", "10", "10"], ["3", "30", "1", "10", "15"])
            .Put("AppDependencies", DepRel, ["2", "20", "1", "10", "2"])
            .Put("TaskSequences", TsCols, ["MCM00010", "Build PC", "", "0"], ["MCM00011", "Outer", "", "0"])
            .Put("TsReferences", RefCols, ["MCM00010", M("Target")], ["MCM00011", "MCM00010"], ["MCM000AA", M("Target")])
            .Put("TsSteps", StepCols, ["MCM00010", M("Target"), "1"])
            .Put("Deployments", DepCols, ["PKG00001", "Target", "Install", M("Target"), "MCM00001", "All Desktops", "1", "1", "10", "5", "0", "0", "", "2026-01-01T00:00:00", "{D1}"],
                ["PKG00002", "Target", "Install", "", "MCM00002", "A package", "1", "2", "10", "5", "0", "0", "", "2026-01-01T00:00:00", "{D2}"]);
        var (blockers, note) = HygieneChecks.BlockersOf(new HygieneContext(read, new HygieneThresholds(), DateTime.Now, "MCM"));
        Assert.NotNull(blockers);
        Assert.Equal("", note);
        var list = blockers![M("Target")];
        Assert.Equal([HygieneBlockerKinds.Deployment, HygieneBlockerKinds.TaskSequence, HygieneBlockerKinds.NestedTaskSequence, HygieneBlockerKinds.StaleReference, HygieneBlockerKinds.Dependency, HygieneBlockerKinds.Supersedence],
            list.Select(b => b.Kind).ToArray());
        Assert.Equal("All Desktops", list[0].Name);
        Assert.Equal("Build PC", list[1].Name);
        Assert.Equal("Outer", list[2].Name);
        Assert.Contains("MCM00011", list[2].Detail);
        Assert.Equal("MCM000AA", list[3].Name);
        Assert.Equal("Dependent", list[4].Name);
        Assert.Equal("Replacement", list[5].Name);
        // An application nothing refers to has no entry.
        Assert.False(blockers.ContainsKey(M("Replacement")));
        Assert.False(blockers.ContainsKey(M("Dependent")));
    }

    [Fact]
    public void ADatasetThatWasNotReadIsNamedAndTheApplicationsNotReadGiveNoBlockers()
    {
        var onlyApps = new HygieneSiteRead { SiteCode = "MCM" }.Put("Applications", AppCols, A(1, "Target"));
        var (blockers, note) = HygieneChecks.BlockersOf(new HygieneContext(onlyApps, new HygieneThresholds(), DateTime.Now, "MCM"));
        Assert.NotNull(blockers);
        Assert.Contains("not read", note);
        Assert.Contains("deployments", note);
        var (none, _) = HygieneChecks.BlockersOf(new HygieneContext(new HygieneSiteRead(), new HygieneThresholds(), DateTime.Now, "MCM"));
        Assert.Null(none);
    }
}
