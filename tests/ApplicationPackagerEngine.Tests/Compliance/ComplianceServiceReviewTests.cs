using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.Compliance.StubComplianceSite;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>Deployment, catalog, setting and read rules through the real embedded scripts on the stub site.</summary>
public sealed class ComplianceServiceReviewTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static StubComplianceSite Site(string collection = "MCM00080", bool enforcement = false, params object[] settings) => new(NewState(
        items: [Ci("16780001", "Lab CI", 4, settings.Length > 0 ? settings : [RegistrySetting("RegSetting_a", "EnableMulticast"), ScriptSetting("ScriptSetting_b", "Probe")])],
        baselines: [CiBaseline("16780002", "Lab Baseline", "", "16780001")],
        assignments: [Deployment(16777270, "16780002", collection, enforcement, summary: new { Success = 2, Other = 1, Time = "2026-10-18T10:00:00Z" })]));

    private static ComplianceService Service(StubComplianceSite site, ICatalogBuilderRunner? runner = null) => ComplianceDeploymentTests.Service(site, runner);

    private static async Task<ComplianceOutcome> Apply(ComplianceService service, ComplianceChange change) =>
        (await service.ApplyAsync([change], new ComplianceDeploymentTests.Recorder<ComplianceOutcome>(), None)).Single();

    private static ComplianceChange Change(ComplianceChangeKind kind, BaselineDeploymentRow? deployment = null, string? to = null, bool confirmed = false) =>
        new(kind, null, new Dictionary<string, string>(), [], deployment, null, to, null, null, null, [], [], confirmed);

    private static ComplianceChange SettingChange(ComplianceChangeKind kind, CiSetting? before, CiSetting? after, string ciId = "16780001") =>
        new(kind, null, new Dictionary<string, string>(), [], null, null, null, ciId, before, after, [], []);

    private static ComplianceChange Deploy(string id, Dictionary<string, string> values) =>
        new(ComplianceChangeKind.CatalogDeploy, id, values, [], null, null, null, null, null, null, [], []);

    private static async Task<BaselineDeploymentRow> Current(ComplianceService service) => (await service.ListDeploymentsAsync("16780002", None)).Single();

    private static JsonArray Settings(StubComplianceSite site) => (JsonArray)site.Item("16780001")!["Settings"]!;

    private static JsonNode Node(object value) => JsonNode.Parse(JsonSerializer.Serialize(value))!;

    // ---- Built-in collections --------------------------------------------------------------

    [Fact]
    public async Task ADeletedDeploymentOnABuiltInCollectionComesBackWithUndo()
    {
        var site = Site("SMS00001");
        var service = Service(site);
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, await Current(service)), None);
        Assert.Contains(plan.Impact, l => l.Contains("built-in collection", StringComparison.Ordinal));

        var outcome = await Apply(service, plan);
        Assert.True(outcome.Succeeded, outcome.Message);
        var undo = await service.PrepareAsync(outcome.UndoChange!, None);
        var undone = await Apply(service, undo);

        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal("SMS00001", (string)site.Assignments.Single()!["CollectionID"]!);
    }

    [Fact]
    public async Task ADeploymentMovedOffABuiltInCollectionMovesBackWithUndo()
    {
        var site = Site("SMS00001");
        var service = Service(site);
        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.Reassign, await Current(service), to: "MCM00081"), None));
        Assert.True(outcome.Succeeded, outcome.Message);

        var undone = await Apply(service, await service.PrepareAsync(outcome.UndoChange!, None));

        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal("SMS00001", (string)site.Assignments.Single()!["CollectionID"]!);
    }

    [Fact]
    public async Task ANewDeploymentToABuiltInCollectionIsStillRefused()
    {
        var site = Site();
        var service = Service(site);
        var row = new BaselineDeploymentRow("", "16780002", "Lab Baseline", "SMS00001", "All Systems", false, "", 0, 0, 0, 0, null);

        await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(Change(ComplianceChangeKind.CreateDeployment, row), None));
        var outcome = await Apply(service, Change(ComplianceChangeKind.CreateDeployment, row));
        var reassign = await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(Change(ComplianceChangeKind.Reassign, row with { DeploymentId = "16777270", CollectionId = "MCM00080" }, to: "SMS00001"), None));

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Contains("built-in", reassign.Message);
        Assert.Single(site.Assignments);
    }

    // ---- Stored deployment values ------------------------------------------------------------

    private static void Alerting(StubComplianceSite site, bool scom = true)
    {
        var a = site.Assignment(16777270)!;
        a["Override"] = true;
        a["Alert"] = true;
        a["AlertThreshold"] = 75;
        a["Mom"] = scom;
        a["AlertAfter"] = "2026-11-01T15:00:00Z";
    }

    [Fact]
    public async Task ARedeployCarriesTheWindowOverrideTheAlertAndOperationsManagerSettings()
    {
        var site = Site(enforcement: true);
        Alerting(site);
        var service = Service(site);
        var current = await Current(service);
        var s = current.Settings!;
        Assert.Equal((true, true, 75, true, new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc)), (s.OverrideServiceWindow, s.GenerateAlert, s.AlertThresholdPercent, s.MonitoredByScom, s.AlertAfter));

        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.Redeploy, current, confirmed: true), None) with { RemediationConfirmed = true });

        Assert.True(outcome.Succeeded, outcome.Message);
        var made = site.Assignment(16777300)!;
        Assert.Equal((true, true, 75, true, "2026-11-01T15:00:00Z"),
            ((bool)made["Override"]!, (bool)made["Alert"]!, (int)made["AlertThreshold"]!, (bool)made["Mom"]!, (string)made["AlertAfter"]!));
        Assert.Null(site.Assignment(16777270));
    }

    [Fact]
    public async Task AChangeOfTheOperationsManagerSettingSinceThePlanSkipsTheDelete()
    {
        var site = Site();
        Alerting(site);
        var service = Service(site);
        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, await Current(service)), None);
        site.BeforeRun = (s, script, _) => { if (script == "WriteCompliance") s.Assignment(16777270)!["Mom"] = false; };

        var outcome = await Apply(service, plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.NotNull(site.Assignment(16777270));
    }

    [Fact]
    public async Task AWindowOverrideWithoutRemediationIsNamedInThePlanAndDoesNotFailTheRedeploy()
    {
        var site = Site();
        site.Assignment(16777270)!["Override"] = true;
        var service = Service(site);

        var plan = await service.PrepareAsync(Change(ComplianceChangeKind.Redeploy, await Current(service)), None);
        var outcome = await Apply(service, plan);

        Assert.Contains(plan.Impact, l => l.Contains("does not store that on a new deployment without remediation", StringComparison.Ordinal));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False((bool)site.Assignment(16777300)!["Override"]!);
    }

    // ---- Redeploy and Reassign keep the old deployment until the new one is proven ----------

    [Theory]
    [InlineData(ComplianceChangeKind.Redeploy, null)]
    [InlineData(ComplianceChangeKind.Reassign, "MCM00081")]
    public async Task ANewDeploymentThatFailsAfterItsCreateKeepsTheOldOneAndUndoRemovesOnlyTheNewOne(ComplianceChangeKind kind, string? to)
    {
        var site = Site();
        site.Fail("after-create:16780002");
        var service = Service(site);

        var outcome = await Apply(service, await service.PrepareAsync(Change(kind, await Current(service), to: to), None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("was kept", outcome.Message);
        Assert.NotNull(site.Assignment(16777270));
        Assert.NotNull(site.Assignment(16777300));
        Assert.DoesNotContain("SECRET-DETAIL", outcome.Message);
        Assert.Equal((ComplianceChangeKind.DeleteDeployment, "16777300"), (outcome.UndoChange!.Kind, outcome.UndoChange.DeploymentBefore!.DeploymentId));
        var undone = await Apply(service, outcome.UndoChange);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal([16777270L], site.Assignments.Select(a => (long)a!["AssignmentID"]!));
    }

    [Fact]
    public async Task ANewDeploymentThatStoresOtherValuesKeepsTheOldOne()
    {
        var site = Site();
        site.SetOption("IgnoreSchedule", true);
        var service = Service(site);

        var outcome = await Apply(service, await service.PrepareAsync(Change(ComplianceChangeKind.Redeploy, await Current(service)), None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both exist", outcome.Message);
        Assert.NotNull(site.Assignment(16777270));
        Assert.Equal(ComplianceChangeKind.DeleteDeployment, outcome.UndoChange!.Kind);
    }

    // ---- Catalog deploy ownership ----------------------------------------------------------

    /// <summary>Runs the site builder, then lets another account add objects while the run is going.</summary>
    private sealed class BusySite(StubComplianceSite site, Action<StubComplianceSite> meanwhile) : ICatalogBuilderRunner
    {
        private readonly ComplianceCatalogDeployTests.SiteBuilder _inner = new(site);

        public async Task<CatalogBuilderRun> RunAsync(CatalogEntry entry, byte[] builder, IReadOnlyList<string> arguments, Action<string> line, TimeSpan timeout, CancellationToken ct)
        {
            var run = await _inner.RunAsync(entry, builder, arguments, line, timeout, ct);
            meanwhile(site);
            return run;
        }
    }

    private static string NewBaselineId(StubComplianceSite site) =>
        (string)site.Baselines.Select(b => b!.AsObject()).Single(b => (string)b["Name"]! == "Hardening LLMNR Disable")["Id"]!;

    [Fact]
    public async Task ADeploymentAnotherAccountMadeOfTheNewBaselineIsNotClaimedByTheUndo()
    {
        var site = ComplianceCatalogDeployTests.Site();
        var service = Service(site, new BusySite(site, s =>
        {
            var foreign = Node(Deployment(16777390, NewBaselineId(s), "MCM00081"));
            foreign["CreatedBy"] = @"CONTOSO\Other";
            s.Assignments.Add(foreign);
        }));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None);

        var outcome = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.DoesNotContain("Deployment:16777390", outcome.UndoChange!.CreatedIds);
        Assert.Equal(3, outcome.UndoChange.CreatedIds.Count);
        Assert.Contains("Not claimed", outcome.Message);
        Assert.Contains("16777390", outcome.Message);
    }

    [Fact]
    public async Task AnObjectWithAProducedNameFromAnotherAccountLeavesTheRunsOwnObjectClaimed()
    {
        var site = ComplianceCatalogDeployTests.Site();
        var service = Service(site, new BusySite(site, s =>
        {
            var foreign = Node(Ci("16795555", "Hardening: LLMNR Disable", 1));
            foreign["CreatedBy"] = @"CONTOSO\Other";
            s.Items.Add(foreign);
        }));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None);

        var outcome = await Apply(service, plan);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("ConfigurationItem:16790000", outcome.UndoChange!.CreatedIds);
        Assert.DoesNotContain("ConfigurationItem:16795555", outcome.UndoChange.CreatedIds);
        Assert.Contains("16795555", outcome.Message);
    }

    [Fact]
    public async Task AnObjectCreatedBeforeTheRunStartIsNotClaimed()
    {
        var site = ComplianceCatalogDeployTests.Site();
        var service = Service(site, new BusySite(site, s =>
        {
            var item = s.Items.Select(i => i!.AsObject()).Single(i => (string)i["Name"]! == "Hardening: LLMNR Disable");
            item["Created"] = "2026-01-01T00:00:00Z";
        }));
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None);

        var outcome = await Apply(service, plan);

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain(outcome.UndoChange!.CreatedIds, id => id.StartsWith("ConfigurationItem:", StringComparison.Ordinal));
        Assert.Contains("created before the run", outcome.Message);
    }

    // ---- Catalog collection values -------------------------------------------------------

    private static StubComplianceSite SpecExecSite() => ComplianceCatalogDeployTests.Site(
        new { CollectionID = "SMS00001", Name = "All Systems", CollectionType = 2, Members = 8 },
        new { CollectionID = "MCM00080", Name = "CCM Test 1", CollectionType = 2, Members = 1 },
        new { CollectionID = "MCM00081", Name = "CCM Test 2", CollectionType = 2, Members = 3 },
        new { CollectionID = "MCM00083", Name = "Devices: Hyperthreading Enabled", CollectionType = 2, Members = 4 });

    [Fact]
    public async Task AnEmptyValueForACollectionWithABuilderDefaultIsRefused()
    {
        var site = SpecExecSite();
        var builder = new ComplianceCatalogDeployTests.SiteBuilder(site);
        var service = Service(site, builder);
        var change = Deploy("specexec-mitigations", new() { ["HtEnabledCollectionName"] = "", ["HtDisabledCollectionName"] = "CCM Test 2" });

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(change, None));
        var outcome = await Apply(service, change with { TargetCollectionIds = ["MCM00081"] });

        Assert.Contains("Select a collection for HtEnabledCollectionName", error.Message);
        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Equal(0, builder.Runs);
    }

    [Fact]
    public async Task TheSameCollectionForBothSpecExecBaselinesIsRefused()
    {
        var site = SpecExecSite();
        var service = Service(site, new ComplianceCatalogDeployTests.SiteBuilder(site));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Deploy("specexec-mitigations", new() { ["HtEnabledCollectionName"] = "CCM Test 1", ["HtDisabledCollectionName"] = "ccm test 1" }), None));

        Assert.Contains("same collection", error.Message);
    }

    [Fact]
    public async Task AForgedBuiltInTargetIsRefusedAtApply()
    {
        var site = SpecExecSite();
        var builder = new ComplianceCatalogDeployTests.SiteBuilder(site);
        var service = Service(site, builder);
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None);
        var forged = plan with { Values = new Dictionary<string, string>(plan.Values) { ["CollectionName"] = "All Systems" }, TargetCollectionIds = ["SMS00001"] };

        var outcome = await Apply(service, forged);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Contains("built-in", outcome.Message);
        Assert.Equal(0, builder.Runs);
    }

    // ---- Drift tags --------------------------------------------------------------------------

    [Fact]
    public async Task AnOversizedVersionInADriftTagIsUnknownAndDoesNotStopTheList()
    {
        var site = new StubComplianceSite(NewState(baselines:
        [
            CiBaseline("16780002", "Hostile", "[ccm:llmnr-disable@99999999999999999999.0.0]"),
            CiBaseline("16780003", "Fine", "[ccm:llmnr-disable@1.0.0]")
        ]));

        var rows = await Service(site).GetDriftAsync(None);

        Assert.Equal("Unknown", rows.Single(r => r.BaselineId == "16780002").State);
        Assert.Equal("Current", rows.Single(r => r.BaselineId == "16780003").State);
        Assert.True(DriftTag.CompareVersions("99999999999999999999.0.0", "1.0.0") > 0);
    }

    // ---- Not attempted means Skipped ----------------------------------------------------------

    [Fact]
    public async Task ARefusedBuilderAndAMissingBuilderAreReportedAsSkipped()
    {
        var site = ComplianceCatalogDeployTests.Site();
        var refusing = Service(site, new ComplianceCatalogDeployTests.SiteBuilder(site) { Refuse = true });
        var plan = await refusing.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None);
        var missing = new ComplianceService(site, new CatalogService(), new ComplianceCatalogDeployTests.SiteBuilder(site), () => new ConfigMgrConnection("MCM", "cm01.contoso.com"),
            builderBytes: _ => null);

        Assert.StartsWith(ComplianceService.SkippedPrefix, (await Apply(refusing, plan)).Message);
        Assert.StartsWith(ComplianceService.SkippedPrefix, (await Apply(missing, plan)).Message);
    }

    [Fact]
    public async Task ASiteThatCannotBeReadBeforeTheBuilderIsReportedAsSkipped()
    {
        var site = ComplianceCatalogDeployTests.Site();
        var builder = new ComplianceCatalogDeployTests.SiteBuilder(site);
        var service = Service(site, builder);
        var plan = await service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None);
        site.Fail("query");

        var outcome = await Apply(service, plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Equal(0, builder.Runs);
    }

    private sealed class NoModuleForWrites(IConfigMgrSession inner) : IConfigMgrSession
    {
        public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null) =>
            script.Name == "WriteCompliance"
                ? Task.FromException<ConfigMgrCommandResult>(new ConfigMgrSessionException(ConfigMgrFailure.ModuleNotFound, "no module"))
                : inner.InvokeAsync(script, parameters, ct, timeout);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AWriteThatNeverStartedIsReportedAsSkipped()
    {
        var site = Site();
        var reader = Service(site);
        var plan = await reader.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, await Current(reader)), None);

        var outcome = await Apply(ComplianceDeploymentTests.Service(new NoModuleForWrites(site)), plan);

        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.NotNull(site.Assignment(16777270));
    }

    // ---- Names without case ----------------------------------------------------------------

    [Fact]
    public async Task ABaselineNameThatDiffersOnlyInCaseBlocksTheRerun()
    {
        var site = ComplianceCatalogDeployTests.Site();
        site.Baselines.Add(Node(CiBaseline("16780050", "hardening llmnr DISABLE", "")));
        var service = Service(site, new ComplianceCatalogDeployTests.SiteBuilder(site));

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() =>
            service.PrepareAsync(Deploy("llmnr-disable", new() { ["CollectionName"] = "CCM Test 1" }), None));

        Assert.Contains("second run", error.Message);
    }

    [Fact]
    public async Task ASettingNameThatDiffersOnlyInCaseIsTaken()
    {
        var site = Site();
        var service = Service(site);
        var setting = new CiSetting("", "enablemulticast", "Registry", "Integer", "", "1", Hive: "LocalMachine", KeyName: @"SOFTWARE\Contoso", ValueName: "X",
            RuleName: "rule", Severity: "Warning", RemediateDword: true);

        var error = await Assert.ThrowsAsync<ComplianceServiceException>(() => service.PrepareAsync(SettingChange(ComplianceChangeKind.AddSetting, null, setting), None));
        var plan = await service.PrepareAsync(SettingChange(ComplianceChangeKind.AddSetting, null, setting with { Name = "Fresh" }), None);
        site.BeforeRun = (s, script, _) => { if (script == "WriteCompliance") ((JsonArray)s.Item("16780001")!["Settings"]!)[0]!["Name"] = "FRESH"; };
        var outcome = await Apply(service, plan);

        Assert.Contains("already has a setting named", error.Message);
        Assert.StartsWith(ComplianceService.SkippedPrefix, outcome.Message);
        Assert.Equal(2, Settings(site).Count);
    }

    // ---- Compliance counts and freshness ------------------------------------------------------

    [Fact]
    public async Task ACountConfigMgrDoesNotReturnIsUnknownNotZero()
    {
        var site = Site();
        site.SetOption("MissingCounts", true);
        var service = Service(site);

        var deployment = await Current(service);
        var compliance = (await service.GetComplianceAsync(None)).Single();
        var baseline = (await service.ListBaselinesAsync(None)).Single();

        Assert.False(deployment.CountsKnown);
        Assert.False(compliance.CountsKnown);
        Assert.False(baseline.CountsKnown);
    }

    [Fact]
    public async Task ADeploymentWithoutASummaryHasUnknownCountsAndIsStale()
    {
        var site = Site();
        site.Assignment(16777270)!["Summary"] = null;
        var service = Service(site);

        var row = (await service.GetComplianceAsync(None)).Single();
        var known = (await Service(Site()).GetComplianceAsync(None)).Single();

        Assert.Equal((false, true, (DateTime?)null), (row.CountsKnown, row.Stale, row.LastEvaluated));
        Assert.Equal((true, false, 2, 1), (known.CountsKnown, known.Stale, known.Compliant, known.NonCompliant));
        Assert.Equal(new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc), known.LastEvaluated);
    }

    // ---- Prepare reads only the deployment it plans ------------------------------------------

    [Fact]
    public async Task APlanForOneDeploymentReadsOnlyThatDeployment()
    {
        var site = Site();
        var service = Service(site);
        var current = await Current(service);
        site.Log.Clear();

        await service.PrepareAsync(Change(ComplianceChangeKind.DeleteDeployment, current), None);

        var queries = site.Log.Where(l => l.StartsWith("Q:", StringComparison.Ordinal)).ToList();
        Assert.All(queries.Where(q => q.Contains("SMS_DeploymentSummary", StringComparison.Ordinal) || q.Contains("SMS_ConfigurationBaselineInfo", StringComparison.Ordinal)),
            q => Assert.Contains(" IN (", q));
        Assert.All(queries.Where(q => q.Contains("SMS_Alert", StringComparison.Ordinal)), q => Assert.Contains("TypeInstanceID", q));
        Assert.Contains(queries, q => q.Contains("SMS_DeploymentSummary", StringComparison.Ordinal));
    }

    // ---- Setting changes ---------------------------------------------------------------------

    [Theory]
    [InlineData("bump")]
    [InlineData("same-revision")]
    public async Task AnEditOfTheOldSettingDuringAReplaceKeepsBothSettings(string revision)
    {
        var site = Site();
        site.SetOption("EditOnAdd", $"EnableMulticast (edit t1)|EnableMulticast|7|{revision}");
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", None)).Single(s => s.Kind == "Registry");

        var outcome = await Apply(service, await service.PrepareAsync(SettingChange(ComplianceChangeKind.EditSetting, before, before with { ExpectedValue = "2" }), None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("Both exist now: EnableMulticast and EnableMulticast (edit t1)", outcome.Message);
        var old = Settings(site).Single(s => (string)s!["SettingId"]! == "RegSetting_a")!;
        Assert.Equal("7", (string)((JsonArray)old["Values"]!)[0]!);
        Assert.Equal(3, Settings(site).Count);
        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(2, Settings(site).Count);
    }

    [Fact]
    public async Task AnInPlaceEditThatFailsAfterItsCommitIsReconciledWithUndo()
    {
        var site = Site();
        site.Fail("after-set:EnableMulticast");
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", None)).Single(s => s.Kind == "Registry");

        var outcome = await Apply(service, await service.PrepareAsync(SettingChange(ComplianceChangeKind.EditSetting, before, before with { KeyName = @"SOFTWARE\Other" }), None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("then reported an error", outcome.Message);
        Assert.DoesNotContain("SECRET-DETAIL", outcome.Message);
        Assert.Equal(@"SOFTWARE\Other", (string)Settings(site).Single(s => (string)s!["SettingId"]! == "RegSetting_a")!["KeyName"]!);
        var undone = await Apply(service, outcome.UndoChange!);
        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal(@"SOFTWARE\Contoso", (string)Settings(site).Single(s => (string)s!["SettingId"]! == "RegSetting_a")!["KeyName"]!);
    }

    [Fact]
    public async Task AnInPlaceEditWhoseStoredResultCannotBeEstablishedOffersNoUndo()
    {
        var site = Site();
        site.Fail("after-set:EnableMulticast");
        site.SetOption("SetIgnoresKey", true);
        var service = Service(site);
        var before = (await service.ListSettingsAsync("16780001", None)).Single(s => s.Kind == "Registry");

        var outcome = await Apply(service, await service.PrepareAsync(SettingChange(ComplianceChangeKind.EditSetting, before, before with { KeyName = @"SOFTWARE\Other" }), None));

        Assert.False(outcome.Succeeded);
        Assert.Contains("is not known", outcome.Message);
        Assert.Contains("Check the setting in ConfigMgr", outcome.Message);
        Assert.Null(outcome.UndoChange);
    }

    [Fact]
    public async Task ABaselineThatHoldsAnEarlierRevisionOfTheItemStillCounts()
    {
        var site = Site();
        var service = Service(site);
        var added = await Apply(service, await service.PrepareAsync(SettingChange(ComplianceChangeKind.AddSetting, null,
            new CiSetting("", "Fresh", "Registry", "Integer", "", "1", Hive: "LocalMachine", KeyName: @"SOFTWARE\Contoso", ValueName: "Fresh", RuleName: "rule", Severity: "Warning", RemediateDword: true)), None));
        Assert.True(added.Succeeded, added.Message);
        Assert.Equal(["16780001"], ((JsonArray)site.Baseline("16780002")!["Items"]!).Select(i => (string)i!));

        var item = (await service.ListConfigurationItemsAsync(None)).Single();
        var plan = await service.PrepareAsync(SettingChange(ComplianceChangeKind.RemoveSetting, (await service.ListSettingsAsync(item.CiId, None)).Single(s => s.Name == "Fresh"), null, item.CiId), None);

        Assert.NotEqual("16780001", item.CiId);
        Assert.Equal(1, item.BaselineCount);
        Assert.Contains(plan.Impact, l => l.Contains("deployed baseline (1 deployment)", StringComparison.Ordinal));
    }

    // ---- Remove catalog objects ---------------------------------------------------------------

    [Fact]
    public async Task AnObjectThatIsNotFoundIsReportedAsNotFound()
    {
        var site = Site();
        var service = Service(site);
        var remove = new ComplianceChange(ComplianceChangeKind.RemoveCatalogObjects, "llmnr-disable", new Dictionary<string, string>(), [], null, null, null, null, null, null, [], ["Deployment:16777999"]);

        var outcome = await Apply(service, remove);

        Assert.Contains(outcome.Steps, s => s.StartsWith("Not found in ConfigMgr", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Steps, s => s.Contains("Already gone", StringComparison.Ordinal));
    }
}
