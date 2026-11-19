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
        Assert.Equal(HygieneFixKind.RemoveBrokenSupersedence, one.FixKind);
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
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, finding);
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
        Assert.Contains(change.Impact, l => l.Contains("saves the original data", StringComparison.Ordinal));
        Assert.Contains(change.Impact, l => l.Contains("Set-CMApplicationSupersedence -RemoveSupersedence is not used", StringComparison.Ordinal));
        Assert.Empty(h.Site.Commands);
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
    }

    [Fact]
    public async Task TheFixSavesTheOriginalDataThenRemovesOnlyTheRulesThatPointAtTheDeletedApplicationAndReadsTheResultBack()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, finding);
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", h.Site.Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal))));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["Cmd:SupersedenceRemoval|" + NewModel + "|DTRule_1,DTRule_2"], h.Site.Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal)).ToArray());
        Assert.Equal(["DTRule_3", "DTRule_4"], RuleIds(h.Site));

        var folder = Directory.GetDirectories(Path.Combine(h.Folder, "supersedence-backups")).Single();
        Assert.Contains(folder, result.Message);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "supersedence-backup.json")));
        var entries = doc.RootElement.GetProperty("Entries").EnumerateArray().ToList();
        Assert.Equal(["DTRule_1", "DTRule_2"], entries.Select(e => e.GetProperty("RuleId").GetString()).ToArray());
        Assert.All(entries, e => Assert.Equal(GoneOne, e.GetProperty("ReferencedApplicationModel").GetString()));
        Assert.Equal(NewModel, entries[0].GetProperty("ApplicationModelName").GetString());
        Assert.Equal("ZZ-Superseding", entries[0].GetProperty("ApplicationName").GetString());
        Assert.Equal(["DeploymentType_A", "DeploymentType_B"], entries.Select(e => e.GetProperty("DeploymentTypeLogicalName").GetString()).ToArray());
        var original = File.ReadAllText(Path.Combine(folder, entries[0].GetProperty("ApplicationDocumentFile").GetString()!));
        Assert.Contains("DTRule_1", original);
        Assert.Contains("DTRule_4", original);

        var lines = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], lines.Select(l => l.Phase));
        Assert.Equal("Succeeded", lines[1].Result);
    }

    [Fact]
    public async Task TheSecondDeletedApplicationIsFixedByItsOwnFindingAndTheKeptReferenceIsNeverTouched()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var findings = (await h.ScanAsync("AppRelationships")).Findings;
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, Sup01(findings, GoneOne), Sup01(findings, GoneTwo));
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
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, finding);
        Assert.True(change.Blocked);
        Assert.Contains("exists again", Assert.Single(change.Dropped).Message);
        Assert.Empty(change.Targets);
    }

    [Fact]
    public async Task AWriteStopsWhenTheReferencedApplicationReturnsBetweenThePlanAndTheWrite()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, finding);
        h.Site.Class("SMS_Application").Add(AppRow(GoneOne, 16790021, "ZZ-Back"));
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Skipped, result.Result);
        Assert.Contains("exists again", result.Message);
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task AFailedSaveOfTheOriginalDataChangesNothing()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, finding);
        // A file where the backup folder has to be: the folder cannot be made.
        File.WriteAllText(Path.Combine(h.Folder, "supersedence-backups"), "in the way");
        var outcome = await SiteHygieneFixTests.ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Failed, result.Result);
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Cmd:", StringComparison.Ordinal));
        Assert.Equal(["DTRule_1", "DTRule_2", "DTRule_3", "DTRule_4"], RuleIds(h.Site));
    }

    [Fact]
    public async Task TheSiteRefusingTheWriteIsReportedAsFailed()
    {
        using var h = new SiteHygieneHarness(BrokenState());
        var finding = Sup01((await h.ScanAsync("AppRelationships")).Findings, GoneOne);
        var change = await SiteHygieneFixTests.PrepareAsync(h, HygieneFixKind.RemoveBrokenSupersedence, finding);
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
        var good = HygieneChecks.F("SUP-01", "Supersedence", "k", "n", "e", "r", HygieneFixKind.RemoveBrokenSupersedence, parameters);
        var other = HygieneChecks.F("APP-01", "Application", "k", "n", "e", "r", HygieneFixKind.RemoveBrokenSupersedence, parameters);
        var noReference = HygieneChecks.F("SUP-01", "Supersedence", "k", "n", "e", "r", HygieneFixKind.RemoveBrokenSupersedence, HygieneChecks.Params((HygieneFixParameters.ModelName, NewModel)));
        Assert.Null(HygieneFixRules.Gate(HygieneFixKind.RemoveBrokenSupersedence, good));
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.RemoveBrokenSupersedence, other));
        Assert.NotNull(HygieneFixRules.Gate(HygieneFixKind.RemoveBrokenSupersedence, noReference));
        Assert.False(HygieneFixKinds.IsDelete(HygieneFixKind.RemoveBrokenSupersedence));
        Assert.Contains(HygieneFixKind.RemoveBrokenSupersedence, SiteHygieneService.SupportedKinds);
    }

    [Fact]
    public void ThePlanTextEditsTheDefinitionByTheScopeAndTheLogicalNameOfTheDeletedApplication()
    {
        var text = HygieneFixText.Text(HygieneFixKind.RemoveBrokenSupersedence, HygieneChecks.Params((HygieneFixParameters.ModelName, NewModel), (HygieneFixParameters.ReferencedModel, GoneOne)))!;
        Assert.Contains($"Get-CMApplication -ModelName '{NewModel}'", text);
        Assert.Contains("ApplicationAuthoringScopeId -eq '" + Scope + "'", text);
        Assert.Contains("ApplicationLogicalName -eq 'Application_dead0001-0000-4000-8000-000000000001'", text);
        Assert.EndsWith("$app.Put()", text);
        Assert.DoesNotContain("Remove-CMDeploymentTypeSupersedence", text);
    }
}
