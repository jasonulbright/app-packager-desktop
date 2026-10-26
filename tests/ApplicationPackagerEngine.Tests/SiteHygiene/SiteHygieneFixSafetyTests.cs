using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygieneFixTests;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The refusal rules of the fix runner: confirmations, fresh re-evaluation, references, caps, plan age, audit, partial results.</summary>
public sealed class SiteHygieneFixSafetyTests
{
    private static HygieneFinding EmptyPackage => Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM0010E");
    private static HygieneFinding EmptyCollection => Finding(f => f.CheckId == "COL-01" && f.ObjectKey == "MCM00215");

    [Fact]
    public async Task ADeleteWithoutTheSecondConfirmationDeletesNothing()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        var outcome = await ApplyAsync(h, change, confirm: true, delete: false);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("Skipped: a delete needs the second confirmation", outcome.Message);
        Assert.All(outcome.Objects, o => Assert.Equal(HygieneResults.Skipped, o.Result));
        Assert.Empty(h.Site.Commands);
        Assert.Empty(h.Audit.Read(10));
    }

    [Fact]
    public async Task AnUnconfirmedPlanChangesNothing()
    {
        using var h = new SiteHygieneHarness();
        var task = Finding(f => f.CheckId == "MNT-01");
        var outcome = await ApplyAsync(h, await PrepareAsync(h, HygieneFixKind.SetSiteMaintenanceTask, task), confirm: false);
        Assert.StartsWith("Skipped: the plan was not confirmed", outcome.Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task APlanIsUsedOnceAndAPlanThisServiceDidNotPrepareIsRefused()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        Assert.True((await ApplyAsync(h, change)).Succeeded);
        var again = await ApplyAsync(h, change);
        Assert.StartsWith("Skipped: the plan was not prepared by this session, or it already ran", again.Message);
        var forged = await ApplyAsync(h, change with { Fingerprint = new string('a', 64) });
        Assert.StartsWith("Skipped: the plan was not prepared", forged.Message);
        Assert.Single(h.Site.Commands);
    }

    [Fact]
    public async Task APlanOlderThanTheLimitIsRefused()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        h.Clock += TimeSpan.FromMinutes(16);
        var outcome = await ApplyAsync(h, change);
        Assert.StartsWith("Skipped: the plan is older than 15 minutes", outcome.Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task APlanWithOtherFindingsThanThePreparedOneIsRefused()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        var other = Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM00001");
        var outcome = await ApplyAsync(h, change with { Findings = [other] });
        Assert.StartsWith("Skipped: the plan holds other findings", outcome.Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task AnApplicationThatGetsADeploymentBetweenPrepareAndApplyIsSkipped()
    {
        using var h = new SiteHygieneHarness();
        var app = Finding(f => f.CheckId == "APP-01" && f.ObjectName == "CMTrace Open");
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplication, app);
        var modelId = h.Site.Class("SMS_Application").OfType<JsonObject>().First(a => (string?)a["ModelName"] == app.ObjectKey && (bool?)a["IsLatest"] == true)["ModelID"]!.ToString();
        h.Site.Class("SMS_ApplicationAssignment").Add(new JsonObject { ["AssignmentID"] = "16777999", ["AssignmentUniqueID"] = "{00000000-0000-0000-0000-000000000099}", ["ApplicationName"] = "CMTrace Open", ["AppModelID"] = modelId, ["CollectionName"] = "Pilot", ["TargetCollectionID"] = "MCM00015", ["ExpirationTime"] = "" });
        var outcome = await ApplyAsync(h, change);
        var r = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Skipped, r.Result);
        Assert.Equal("Skipped: the finding no longer holds: the application has 1 deployment now.", r.Message);
        Assert.Empty(h.Site.Commands);
        var audit = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], audit.Select(a => a.Phase));
        Assert.Equal("Skipped", audit[1].Result);
    }

    [Fact]
    public async Task ACollectionThatAnotherCollectionStartsToIncludeIsNotDeletedEvenThoughTheCheckDidNotSeeIt()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemoveCollection, EmptyCollection);
        h.Site.Class("SMS_CollectionDependencies").Add(new JsonObject { ["DependentCollectionID"] = "MCM00016", ["SourceCollectionID"] = "MCM00215", ["RelationshipType"] = "2" });
        var outcome = await ApplyAsync(h, change);
        Assert.Equal("Skipped: the finding no longer holds: 1 collection includes, excludes or limits to the collection now.", Assert.Single(outcome.Objects).Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task APackageThatATaskSequenceStartsToReferenceIsNotDeleted()
    {
        using var h = new SiteHygieneHarness();
        h.Site.Class("SMS_TaskSequencePackageReference_All").Add(new JsonObject { ["PackageID"] = "MCM00008", ["ObjectID"] = "MCM0010E" });
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        Assert.True(change.Blocked);
        Assert.Null(change.Fingerprint);
        Assert.Equal("Skipped: the finding no longer holds: 1 task sequence uses the package now.", Assert.Single(change.Dropped).Message);
    }

    [Fact]
    public async Task ADeleteOverTheCapIsRefusedAtPrepare()
    {
        using var h = new SiteHygieneHarness();
        var service = new SiteHygieneService(h.Site, h.Audit, utcNow: () => SiteHygieneFixtures.CaptureUtc) { MaxDeletes = 3 };
        var unused = Finding(f => f.CheckId == "APP-05" && f.ObjectName == "SHL-App-Unused");
        var change = await service.PrepareAsync(new HygieneChange(HygieneFixKind.RemoveApplicationRevisionHistory, [unused], false, [], [], null), CancellationToken.None);
        Assert.True(change.Blocked);
        Assert.Null(change.Fingerprint);
        var cap = change.Checks.Single(c => c.Name == SiteHygieneService.CheckNames.Cap);
        Assert.Equal(DeploymentCheckResults.Fail, cap.Result);
        Assert.Contains("deletes at most 3. Filter the findings", cap.Message);
    }

    [Fact]
    public async Task AnAuditLogThatCannotBeWrittenBlocksTheFix()
    {
        using var h = new SiteHygieneHarness();
        var blocker = Path.Combine(h.Folder, "blocked");
        File.WriteAllText(blocker, "a file where the audit folder should be");
        var service = new SiteHygieneService(h.Site, new HygieneAuditLog(() => Path.Combine(blocker, "audit.jsonl")), utcNow: () => h.Clock);
        var change = await service.PrepareAsync(new HygieneChange(HygieneFixKind.RemovePackage, [EmptyPackage], false, [], [], null), CancellationToken.None);
        Assert.True(change.Blocked);
        Assert.Contains("The fix is blocked", change.Checks.Single(c => c.Name == SiteHygieneService.CheckNames.AuditLog).Message);
        var outcome = await service.ApplyAsync(change with { ChangeConfirmed = true, DeleteConfirmed = true }, new Progress<HygieneOutcome>(), CancellationToken.None);
        Assert.StartsWith("Skipped: ", outcome.Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task AFailedWriteKeepsThePartialResultsAndAPlainMessage()
    {
        using var h = new SiteHygieneHarness();
        var boot = Finding(f => f.CheckId == "TSQ-02" && f.ObjectKey == "MCM00110");
        var change = await PrepareAsync(h, HygieneFixKind.RemoveBootImage, boot);
        h.Site.Fail("cmd:Remove-CMBootImage");
        var outcome = await ApplyAsync(h, change);
        var r = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Failed, r.Result);
        Assert.Equal("ConfigMgr reported an error. The log has the kind of failure.", r.Message);
        Assert.DoesNotContain("SECRET", outcome.Message);
        Assert.Equal("Failed", h.Audit.Read(10).Last().Result);

        using var two = new SiteHygieneHarness();
        var pkg = await PrepareAsync(two, HygieneFixKind.RemovePackage, EmptyPackage, Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM00001"));
        two.Site.BeforeRun = (p, site) => { if ((string?)p["Operation"] == "Apply" && site.Commands.Any()) site.Fail("cmd:Remove-CMPackage"); };
        var mixed = await ApplyAsync(two, pkg);
        Assert.Equal([HygieneResults.Done, HygieneResults.Failed], mixed.Objects.Select(o => o.Result));
        Assert.False(mixed.Succeeded);
        Assert.Equal("Partial", two.Audit.Read(10).Last().Result);
        Assert.Contains(two.Reports, r2 => !r2.Final && r2.Objects.Count == 1);
    }

    [Fact]
    public async Task CancelBetweenObjectsSkipsTheRest()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage, Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM00001"));
        using var cts = new CancellationTokenSource();
        h.Site.BeforeRun = (p, site) => { if ((string?)p["Operation"] == "Apply") cts.Cancel(); };
        var outcome = await ApplyAsync(h, change, ct: cts.Token);
        Assert.Equal([HygieneResults.Done, HygieneResults.Skipped], outcome.Objects.Select(o => o.Result));
        Assert.Equal("Skipped: the run was cancelled before this object.", outcome.Objects[1].Message);
        Assert.Single(h.Site.Commands);
    }

    [Fact]
    public async Task AReportOnlyKindHasNoPlan()
    {
        using var h = new SiteHygieneHarness();
        var finding = Finding(f => f.CheckId == "COL-02");
        var change = await h.Service.PrepareAsync(new HygieneChange(HygieneFixKind.SetBoundaryGroup, [finding with { FixKind = HygieneFixKind.SetBoundaryGroup }], false, [], [], null), CancellationToken.None);
        Assert.True(change.Blocked);
        Assert.Empty(h.Site.Runs);
        Assert.StartsWith("Skipped: ", Assert.Single(change.Dropped).Message);
    }

    [Fact]
    public async Task AHostileNameStaysInsideItsLiteralInThePlanText()
    {
        var state = SiteHygieneFixtures.StubState();
        const string hostile = "Delete Aged Log Data'; Remove-CMCollection -Id 'SMS00001' -Force; '";
        state["Classes"]!["SMS_SCI_SQLTask"]!.AsArray().Add(new JsonObject { ["ItemName"] = hostile, ["SiteCode"] = "MCM", ["Enabled"] = "False" });
        using var h = new SiteHygieneHarness(state);
        var finding = HygieneChecks.F("MNT-01", "MaintenanceTask", hostile, hostile, "e", "r", HygieneFixKind.SetSiteMaintenanceTask, HygieneChecks.Params((HygieneFixParameters.Id, hostile), (HygieneFixParameters.SiteCode, "MCM")));
        Assert.Equal("Set-CMSiteMaintenanceTask -SiteCode 'MCM' -Name 'Delete Aged Log Data''; Remove-CMCollection -Id ''SMS00001'' -Force; ''' -Enabled $true", finding.FixText);
        var change = await PrepareAsync(h, HygieneFixKind.SetSiteMaintenanceTask, finding);
        Assert.Equal("Skipped: MNT-01 enables only the recommended cleanup tasks.", Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Runs);
        Assert.StartsWith("Skipped: ", (await ApplyAsync(h, change)).Message);
        Assert.Empty(h.Site.Commands);
    }
}
