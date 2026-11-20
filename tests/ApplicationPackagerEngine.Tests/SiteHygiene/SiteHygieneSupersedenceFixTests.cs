using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// SUP-01 and its fix. The site removes the relation row of a supersedence when the superseded application is deleted, so the check reads the
/// supersedence rules of the application definitions. The fix edits the definition of the superseding application: through the real script and
/// the stub site, with the plan, the saved original data, the removal of only the broken rules and the read-back.
/// </summary>
public sealed class SiteHygieneSupersedenceFixTests
{
    private const string Scope = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E";
    private const string NewModel = Scope + "/Application_aaaa0000-0000-4000-8000-000000000001";
    private const string GoneOne = Scope + "/Application_dead0001-0000-4000-8000-000000000001";
    private const string GoneTwo = Scope + "/Application_dead0002-0000-4000-8000-000000000002";
    private const string KeptModel = Scope + "/Application_bbbb0000-0000-4000-8000-000000000003";

    private static JsonObject Rule(string deploymentType, string title, string id, string reference) =>
        new() { ["Dt"] = deploymentType, ["Title"] = title, ["RuleId"] = id, ["Ref"] = reference };

    private static JsonObject AppRow(string model, int ci, string name) => new()
    {
        ["CI_ID"] = ci.ToString(), ["ModelID"] = (ci + 1000).ToString(), ["ModelName"] = model, ["LocalizedDisplayName"] = name, ["IsLatest"] = true,
        ["IsDeployed"] = "False", ["IsExpired"] = "False", ["IsSuperseded"] = "False", ["IsSuperseding"] = "False", ["IsEnabled"] = "True", ["HasContent"] = "True",
        ["Manufacturer"] = "Lab", ["SoftwareVersion"] = "1.0", ["DateCreated"] = "2026-10-01T00:00:00", ["NumberOfDeploymentTypes"] = "1", ["CIVersion"] = "1"
    };

    /// <summary>
    /// The CM01 rows plus one superseding application whose definition holds three rules: two deployment types point at deleted applications
    /// (one of the two deleted applications from both deployment types) and one rule points at an application that exists.
    /// </summary>
    private static JsonObject BrokenState()
    {
        var state = SiteHygieneFixtures.StubState();
        var apps = state["Classes"]!["SMS_Application"]!.AsArray();
        apps.Add(AppRow(NewModel, 16790001, "ZZ-Superseding"));
        apps.Add(AppRow(KeptModel, 16790011, "ZZ-Kept"));
        state["Supersedence"]!.AsObject()[NewModel] = new JsonArray(
            Rule("DeploymentType_A", "DT A", "DTRule_1", GoneOne),
            Rule("DeploymentType_B", "DT B", "DTRule_2", GoneOne),
            Rule("DeploymentType_A", "DT A", "DTRule_3", GoneTwo),
            Rule("DeploymentType_A", "DT A", "DTRule_4", KeptModel));
        return state;
    }

    private static HygieneFinding Sup01(IReadOnlyList<HygieneFinding> findings, string reference) =>
        Assert.Single(findings, f => f.CheckId == "SUP-01" && f.FixParameters[HygieneFixParameters.ReferencedModel] == reference);

    private static string[] RuleIds(StubHygieneSite site) =>
        site.State["Supersedence"]![NewModel]!.AsArray().Select(r => (string)r!["RuleId"]!).ToArray();

    [Fact]
    public async Task AReferenceToADeletedApplicationIsFoundInTheDefinitionWhereTheRelationTableHasNoRow()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var result = await h.ScanAsync("AppRelationships");
        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "SUP-01").State);
        Assert.DoesNotContain(h.Site.Class("SMS_AppRelation_Flat"), r => (string?)r!["FromApplicationCIID"] == "16790001");
        var sup = result.Findings.Where(f => f.CheckId == "SUP-01" && f.ObjectKey.StartsWith(NewModel + "|", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, sup.Count);
        var one = Sup01(sup, GoneOne);
        Assert.Equal(HygieneFixKind.ClearBrokenSupersedence, one.FixKind);
        Assert.Equal(NewModel + "|" + GoneOne, one.ObjectKey);
        Assert.Contains("'DT A', 'DT B'", one.Evidence);
        Assert.Contains(GoneOne, one.Evidence);
        Assert.Equal(NewModel, one.FixParameters[HygieneFixParameters.ModelName]);
        Assert.Contains("SccmSerializer", one.FixText);
        Assert.DoesNotContain(result.Findings, f => f.CheckId == "SUP-01" && f.FixParameters.Values.Contains(KeptModel));
    }

    [Fact]
    public async Task TheSupersedenceCheckKeepsRunningWhenTheRuleReadIsCutAndTheOtherRelationshipChecksStillRun()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        h.With(maxObjects: 3);
        var result = await h.ScanAsync("AppRelationships");
        var sup01 = result.Checks.Single(c => c.CheckId == "SUP-01");
        Assert.Equal(HygieneCheckStates.NotRun, sup01.State);
        Assert.Contains("supersedence rules", sup01.Reason);
        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "SUP-02").State);
        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "DEP-01").State);
    }

    [Fact]
    public async Task ThePlanNamesTheApplicationTheDeploymentTypesAndTheReferenceAndChangesNothing()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        var target = Assert.Single(change.Targets);
        Assert.Equal("ZZ-Superseding", target.ObjectName);
        Assert.Equal(NewModel + "|" + GoneOne, target.ObjectKey);
        Assert.Contains("'DT A', 'DT B'", target.Detail);
        Assert.Contains("2 rules", target.Detail);
        Assert.Contains(GoneOne, target.Detail);
        Assert.Contains("new revision", target.Detail);
        Assert.Contains("SccmSerializer", change.FixText);
        Assert.Contains("Application_aaaa0000-0000-4000-8000-000000000001", change.FixText);
        Assert.Contains(change.Impact, l => l.Contains("writes no file of the removed data", StringComparison.Ordinal));
        Assert.Contains(change.Impact, l => l.Contains("Set-CMApplicationSupersedence -RemoveSupersedence is not used", StringComparison.Ordinal));
        Assert.Contains(change.Impact, l => l.Contains("older revisions", StringComparison.Ordinal));
        Assert.Contains(change.Impact, l => l.Contains("serializer of the console", StringComparison.Ordinal));
        Assert.Empty(h.Site.Commands);
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
    }

    private static string[] SiteCommands(SiteHygieneHarness h) => h.Site.Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal)).ToArray();

    private static int OldRevisions(StubHygieneSite site) =>
        site.Class("SMS_Application").Count(r => (string?)r!["ModelName"] == NewModel && r["IsLatest"]!.GetValue<bool>() == false);

    [Fact]
    public async Task TheFixRemovesOnlyTheRulesThatPointAtTheDeletedApplicationReadsTheResultBackAndWritesNoFile()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", SiteCommands(h)));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["Cmd:SupersedenceRemoval|" + NewModel + "|DTRule_1,DTRule_2"], SiteCommands(h));
        Assert.Equal(["DTRule_3", "DTRule_4"], RuleIds(h.Site));
        // The option is off in the service unless the change carries it: the old revision stays.
        Assert.Equal(1, OldRevisions(h.Site));
        Assert.DoesNotContain("old revision", result.Message);
        Assert.False(Directory.Exists(Path.Combine(h.Folder, "supersedence-backups")));
        Assert.Equal(["site-hygiene-audit.jsonl"], Directory.GetFiles(h.Folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());

        var lines = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], lines.Select(l => l.Phase));
        Assert.Equal("Succeeded", lines[1].Result);
    }

    [Fact]
    public async Task WithTheOptionTheFixDeletesTheOldRevisionsOfTheChangedApplicationAndNoOther()
    {
        var state = BrokenState();
        // An application that the fix does not change has an old revision too.
        var apps = state["Classes"]!["SMS_Application"]!.AsArray();
        var old = AppRow(KeptModel, 16790012, "ZZ-Kept"); old["IsLatest"] = false; old["CIVersion"] = "0";
        apps.Add(old);
        using var h = new SiteHygieneHarness(state);
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Done, result.Result);
        Assert.Contains("1 old revision(s) deleted.", result.Message);
        Assert.Equal(0, OldRevisions(h.Site));
        Assert.Contains(SiteCommands(h), l => l.StartsWith("Cmd:Remove-CMApplicationRevisionHistory", StringComparison.Ordinal) && l.Contains("-Revision 1", StringComparison.Ordinal));
        Assert.Single(SiteCommands(h), l => l.StartsWith("Cmd:Remove-CMApplicationRevisionHistory", StringComparison.Ordinal));
        Assert.Equal(1, h.Site.Class("SMS_Application").Count(r => (string?)r!["ModelName"] == KeptModel && r["IsLatest"]!.GetValue<bool>() == false));
        // After the fix a new scan has neither SUP-01 nor an old revision of the changed application (the APP-05 input).
        var rescan = await h.ScanAsync("AppRelationships", "Applications");
        Assert.DoesNotContain(rescan.Findings, f => f.CheckId == "SUP-01" && f.ObjectKey == NewModel + "|" + GoneOne);
        Assert.DoesNotContain(rescan.Findings, f => f.CheckId == "APP-05" && f.ObjectKey == NewModel);
    }

    [Fact]
    public async Task ARevisionThatAnotherObjectUsesStaysAndTheRunDoesNotFail()
    {
        var state = BrokenState();
        state["Options"]!["RevisionInUse"] = new JsonArray(1);
        using var h = new SiteHygieneHarness(state);
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Done, result.Result);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("0 old revision(s) deleted. Kept because another object uses them: 1.", result.Message);
        Assert.Equal(1, OldRevisions(h.Site));
        Assert.Equal(["DTRule_3", "DTRule_4"], RuleIds(h.Site));
    }

    [Fact]
    public async Task ASerializerThatIsNotTheOneOfTheConsoleChangesNothing()
    {
        var state = BrokenState();
        state["Options"]!["WrongSerializer"] = "1";
        using var h = new SiteHygieneHarness(state);
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change with { DeleteOldRevisions = true });
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Failed, result.Result);
        Assert.Contains("not the one of the console", result.Message);
        Assert.Empty(SiteCommands(h));
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
    }

    [Fact]
    public async Task TheSecondDeletedApplicationIsFixedByItsOwnFindingAndTheKeptReferenceIsNeverTouched()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var findings = (await h.ScanAsync("AppRelationships")).Findings;
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, Sup01(findings, GoneOne), Sup01(findings, GoneTwo));
        Assert.Equal(2, change.Targets.Count);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        Assert.All(outcome.Objects, o => Assert.Equal(HygieneResults.Done, o.Result));
        Assert.Equal(["DTRule_4"], RuleIds(h.Site));
        var rescan = await h.ScanAsync("AppRelationships");
        Assert.DoesNotContain(rescan.Findings, f => f.CheckId == "SUP-01" && f.ObjectKey.StartsWith(NewModel + "|", StringComparison.Ordinal));
        Assert.Equal(HygieneCheckStates.Ran, rescan.Checks.Single(c => c.CheckId == "SUP-01").State);
    }

    [Fact]
    public async Task AFindingWhoseReferencedApplicationExistsAgainIsLeftOutOfThePlan()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        h.Site.Class("SMS_Application").Add(AppRow(GoneOne, 16790021, "ZZ-Back"));
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        Assert.True(change.Blocked);
        Assert.Contains("exists again", Assert.Single(change.Dropped).Message);
        Assert.Empty(change.Targets);
    }

    [Fact]
    public async Task AWriteStopsWhenTheReferencedApplicationReturnsBetweenThePlanAndTheWrite()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        h.Site.Class("SMS_Application").Add(AppRow(GoneOne, 16790021, "ZZ-Back"));
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Skipped, result.Result);
        Assert.Contains("exists again", result.Message);
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task TheSiteRefusingTheWriteIsReportedAsFailed()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.ClearBrokenSupersedence, finding);
        h.Site.Fail("cmd:Put");
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Failed, result.Result);
        Assert.Contains("ConfigMgr reported an error", result.Message);
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
    }

    [Fact]
    public void OnlyASup01FindingWithAnApplicationAndAReferenceHasThisFix()
    {
        var parameters = HygieneChecks.Params((HygieneFixParameters.ModelName, NewModel), (HygieneFixParameters.ReferencedModel, GoneOne));
        var good = HygieneChecks.F("SUP-01", "Supersedence", "k", "n", "e", "r", HygieneFixKind.ClearBrokenSupersedence, parameters);
        var other = HygieneChecks.F("APP-01", "Application", "k", "n", "e", "r", HygieneFixKind.ClearBrokenSupersedence, parameters);
        var noReference = HygieneChecks.F("SUP-01", "Supersedence", "k", "n", "e", "r", HygieneFixKind.ClearBrokenSupersedence, HygieneChecks.Params((HygieneFixParameters.ModelName, NewModel)));
        Assert.Null(HygieneFixRules.Gate(HygieneFixKind.ClearBrokenSupersedence, good));
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.ClearBrokenSupersedence, other));
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.ClearBrokenSupersedence, noReference));
        Assert.False(HygieneFixKinds.IsDelete(HygieneFixKind.ClearBrokenSupersedence));
        Assert.Contains(HygieneFixKind.ClearBrokenSupersedence, SiteHygieneService.SupportedKinds);
    }

    [Fact]
    public void ThePlanTextEditsTheDefinitionByTheScopeAndTheLogicalNameOfTheDeletedApplication()
    {
        var text = HygieneFixText.Text(HygieneFixKind.ClearBrokenSupersedence, HygieneChecks.Params((HygieneFixParameters.ModelName, NewModel), (HygieneFixParameters.ReferencedModel, GoneOne)))!;
        Assert.Contains($"Get-CMApplication -ModelName '{NewModel}'", text);
        Assert.Contains("ApplicationAuthoringScopeId -eq '" + Scope + "'", text);
        Assert.Contains("ApplicationLogicalName -eq 'Application_dead0001-0000-4000-8000-000000000001'", text);
        Assert.EndsWith("$app.Put()", text);
        Assert.DoesNotContain("Remove-CMDeploymentTypeSupersedence", text);
    }
}
