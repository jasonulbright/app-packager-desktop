using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygieneFixTests;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The guards of the fix runner against what a scan cannot see: objects that use a collection outside the
/// deployment summary, an Auto Apply Drivers step, a finding whose check or parameters do not match its fix,
/// a read-back that fails, an unexpected error in the middle of a run, and the audit records of a large run.
/// The rows have the classes and columns the provider returns for these objects.
/// </summary>
public sealed class SiteHygieneFixGuardTests
{
    private static HygieneFinding EmptyPackage => Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM0010E");
    private static HygieneFinding OtherPackage => Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM00001");
    private static HygieneFinding EmptyCollection => Finding(f => f.CheckId == "COL-01" && f.ObjectKey == "MCM00215");

    private static JsonArray Class(JsonObject state, string name)
    {
        var classes = state["Classes"]!.AsObject();
        return classes[name] as JsonArray ?? (JsonArray)(classes[name] = new JsonArray());
    }

    public static TheoryData<string, string> CollectionUsers => new()
    {
        { "Adr", "1 automatic deployment rule targets the collection." },
        { "ClientSettings", "1 client settings deployment targets the collection." },
        { "Admin", "1 administrative user has the collection in its assigned collections." },
        { "Assignment", "1 deployment of an application, a software update, a baseline or a policy targets the collection now." }
    };

    /// <summary>The site deletes a collection that an automatic deployment rule targets (the rule keeps the deleted ID) and one with a client settings deployment.</summary>
    [Theory]
    [MemberData(nameof(CollectionUsers))]
    public async Task ACollectionThatAnotherObjectUsesIsNotDeleted(string user, string reason)
    {
        var state = SiteHygieneFixtures.StubState();
        switch (user)
        {
            case "Adr":
                Class(state, "SMS_AutoDeployment").Add(new JsonObject { ["AutoDeploymentID"] = "4", ["Name"] = "SHL-ADR-ColRef", ["AutoDeploymentEnabled"] = "False", ["LastRunTime"] = "", ["LastErrorCode"] = "0", ["CollectionID"] = "MCM00215" });
                break;
            case "ClientSettings":
                Class(state, "SMS_ClientSettingsAssignment").Add(new JsonObject { ["ClientSettingsID"] = "16777221", ["CollectionID"] = "MCM00215" });
                break;
            case "Admin":
                var admin = Class(state, "SMS_Admin").OfType<JsonObject>().Single(a => (string?)a["AdminID"] == "16777218");
                admin["Permissions"] = new JsonArray(
                    new JsonObject { ["CategoryID"] = "SMS00ALL", ["CategoryTypeID"] = 29 },
                    new JsonObject { ["CategoryID"] = "MCM00215", ["CategoryTypeID"] = 1 },
                    new JsonObject { ["CategoryID"] = "SMS00001", ["CategoryTypeID"] = 1 });
                break;
            default:
                Class(state, "SMS_CIAssignmentBaseClass").Add(new JsonObject { ["AssignmentID"] = "16777300", ["TargetCollectionID"] = "MCM00215" });
                break;
        }
        using var h = new SiteHygieneHarness(state);
        var change = await PrepareAsync(h, HygieneFixKind.RemoveCollection, EmptyCollection);
        Assert.True(change.Blocked);
        Assert.Equal("Skipped: the finding no longer holds: " + reason, Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Commands);
    }

    [Fact]
    public async Task ACollectionDeleteReadsEveryUserOfTheCollectionAndAFailedReadDeletesNothing()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemoveCollection, EmptyCollection);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        foreach (var cls in new[] { "SMS_CIAssignmentBaseClass", "SMS_AutoDeployment", "SMS_ClientSettingsAssignment", "SMS_Admin" })
            Assert.True(h.Site.Queries(cls) > 0, cls);
        Assert.Contains(change.Impact, l => l.Contains("automatic deployment rules, client settings deployments and administrative users", StringComparison.Ordinal));

        using var failed = new SiteHygieneHarness();
        failed.Site.Fail("query:SMS_Admin");
        await Assert.ThrowsAsync<HygieneServiceException>(() => PrepareAsync(failed, HygieneFixKind.RemoveCollection, EmptyCollection));
        Assert.Empty(failed.Site.Commands);
    }

    /// <summary>A task sequence with an Auto Apply Drivers step has no reference row, and the site deletes the driver package that holds its driver.</summary>
    [Fact]
    public async Task ADriverPackageIsNotDeletedWhileATaskSequenceHasAnAutoApplyDriversStep()
    {
        var state = SiteHygieneFixtures.StubState();
        var sequence = Class(state, "SMS_TaskSequencePackage").OfType<JsonObject>().Single(t => (string?)t["PackageID"] == "MCM00008");
        sequence["Sequence"] = "<sequence version=\"3.10\"><step type=\"SMS_TaskSequence_AutoApplyAction\" name=\"Auto Apply Drivers\" /></sequence>";
        using var h = new SiteHygieneHarness(state);
        var finding = DriverPackage;
        var change = await PrepareAsync(h, HygieneFixKind.RemoveDriverPackage, finding);
        Assert.True(change.Blocked);
        Assert.Equal("Skipped: the finding no longer holds: 1 task sequence has an Auto Apply Drivers step, which can use the content of any driver package without a reference, so the page does not delete a driver package.",
            Assert.Single(change.Dropped).Message);

        using var plain = new SiteHygieneHarness();
        var allowed = await PrepareAsync(plain, HygieneFixKind.RemoveDriverPackage, finding);
        Assert.False(allowed.Blocked);
        Assert.Contains(allowed.Impact, l => l.StartsWith("An Auto Apply Drivers step finds drivers", StringComparison.Ordinal));
        Assert.True(plain.Site.Queries("SMS_TaskSequencePackage") >= 2);
    }

    [Fact]
    public async Task ABootImagePlanSaysThatMediaIsNotRecordedOnTheSite()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemoveBootImage, Finding(f => f.CheckId == "TSQ-02" && f.ObjectKey == "MCM00110"));
        Assert.Contains(change.Impact, l => l.StartsWith("Boot media, prestaged media and PXE images", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFindingOfAnotherCheckCannotRemoveTheDeploymentsOfAnApplication()
    {
        using var h = new SiteHygieneHarness();
        var retired = Finding(f => f.CheckId == "APP-02" && f.ObjectName == "SHL-App-Retired");
        var forged = retired with { CheckId = "APP-01" };
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplicationDeployment, forged);
        Assert.True(change.Blocked);
        Assert.Equal("Skipped: check APP-01 has no fix of this kind on this page.", Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Runs);

        var expiredWithModel = Finding(f => f.CheckId == "DPL-01");
        var withModel = expiredWithModel with { FixParameters = new Dictionary<string, string>(expiredWithModel.FixParameters) { [HygieneFixParameters.ModelName] = retired.FixParameters[HygieneFixParameters.ModelName] } };
        var refused = await PrepareAsync(h, HygieneFixKind.RemoveApplicationDeployment, withModel);
        Assert.Equal("Skipped: an expired-deployment finding names exactly one deployment.", Assert.Single(refused.Dropped).Message);
        Assert.Empty(h.Site.Runs);
    }

    [Fact]
    public async Task ADaysValueBelowTheThresholdDoesNotMoveTheDeviceWindow()
    {
        var state = SiteHygieneFixtures.StubState();
        var device = Class(state, "SMS_CM_RES_COLL_SMS00001").OfType<JsonObject>().Single(d => (string?)d["Name"] == "CLIENT02");
        device["LastActiveTime"] = "2026-09-24T00:00:00";
        Class(state, "SMS_R_System").OfType<JsonObject>().Single(d => (string?)d["ResourceId"] == "16777222")["AgentTime"] =
            new JsonArray("2026-09-24T00:00:00", "2026-09-24T00:00:00", "2026-09-24T00:00:00");
        using var h = new SiteHygieneHarness(state);
        var finding = HygieneChecks.F("DEV-01", "Device", "16777222", "CLIENT02", "e", "r", HygieneFixKind.RemoveDevice,
            HygieneChecks.Params((HygieneFixParameters.Id, "16777222"), (HygieneFixParameters.Days, "1")));
        var change = await PrepareAsync(h, HygieneFixKind.RemoveDevice, finding);
        Assert.True(change.Blocked);
        Assert.Contains("inside the 90-day window", Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Commands);
    }

    [Theory]
    [InlineData("COL-05", "Manual", "COL-05 sets the refresh type Continuous only.")]
    [InlineData("MNT-02", "Delete Aged Discovery Data", "MNT-02 enables only the site backup task.")]
    public async Task AParameterTheCheckNeverSetsIsRefused(string checkId, string value, string reason)
    {
        using var h = new SiteHygieneHarness();
        var finding = checkId == "COL-05"
            ? HygieneChecks.F("COL-05", "Collection", "MCM00217", "SHL-Col-Both", "e", "r", HygieneFixKind.SetCollectionRefreshType, HygieneChecks.Params((HygieneFixParameters.Id, "MCM00217"), (HygieneFixParameters.RefreshType, value)))
            : HygieneChecks.F("MNT-02", "MaintenanceTask", value, value, "e", "r", HygieneFixKind.SetSiteMaintenanceTask, HygieneChecks.Params((HygieneFixParameters.Id, value), (HygieneFixParameters.SiteCode, "MCM")));
        var change = await PrepareAsync(h, finding.FixKind, finding);
        Assert.Equal("Skipped: " + reason, Assert.Single(change.Dropped).Message);
        Assert.Empty(h.Site.Runs);
    }

    [Fact]
    public async Task ARequestOverTheCapIsRefusedBeforeTheSiteIsRead()
    {
        using var h = new SiteHygieneHarness();
        var findings = Enumerable.Range(0, 4).Select(i => EmptyPackage with { ObjectKey = $"MCM0090{i}", FixParameters = new Dictionary<string, string> { [HygieneFixParameters.Id] = $"MCM0090{i}" } }).ToArray();
        var site = new StubHygieneSite(SiteHygieneFixtures.StubState());
        var service = new SiteHygieneService(site, h.Audit, utcNow: () => h.Clock) { MaxDeletes = 3 };
        var change = await service.PrepareAsync(new HygieneChange(HygieneFixKind.RemovePackage, findings, false, [], [], null), CancellationToken.None);
        Assert.True(change.Blocked);
        Assert.Null(change.Fingerprint);
        Assert.Empty(site.Runs);
        var cap = Assert.Single(change.Checks);
        Assert.Equal((SiteHygieneService.CheckNames.Cap, DeploymentCheckResults.Fail), (cap.Name, cap.Result));
        Assert.Contains("The plan holds 4 findings. One run deletes at most 3 objects.", cap.Message);
    }

    [Fact]
    public async Task AReadBackThatFailsAfterTheWriteIsAFailureWithAnUnknownResultNotUnchanged()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        h.Site.BeforeRun = (p, site) => { if ((string?)p["Operation"] == "State" && site.Commands.Any()) site.Fail("query:SMS_Package"); };
        var outcome = await ApplyAsync(h, change);
        var r = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Failed, r.Result);
        Assert.Equal(SiteHygieneService.ReadBackFailed, r.Message);
        Assert.Single(h.Site.Commands);
        var finished = h.Audit.Read(10).Last();
        Assert.Equal(("Finished", "Failed", "Failed"), (finished.Phase, finished.Result, Assert.Single(finished.Objects).Result));
    }

    [Fact]
    public async Task AnUnexpectedErrorInTheMiddleOfARunKeepsTheDoneResultAndWritesOneFinishedRecord()
    {
        var site = new StubHygieneSite(SiteHygieneFixtures.StubState());
        var applies = 0;
        var session = new BreakingSession(site, p => (string?)p["Operation"] == "Apply" && ++applies == 2);
        using var h = new SiteHygieneHarness();
        var service = new SiteHygieneService(session, h.Audit, utcNow: () => h.Clock);
        var prepared = await service.PrepareAsync(new HygieneChange(HygieneFixKind.RemovePackage, [EmptyPackage, OtherPackage], false, [], [], null), CancellationToken.None);
        var outcome = await service.ApplyAsync(prepared with { ChangeConfirmed = true, DeleteConfirmed = true }, new SiteHygieneHarness.SyncProgress<HygieneOutcome>(_ => { }), CancellationToken.None);
        Assert.Equal([HygieneResults.Done, HygieneResults.Failed], outcome.Objects.Select(o => o.Result));
        Assert.Equal(SiteHygieneService.UnexpectedStop, outcome.Objects[1].Message);
        Assert.False(outcome.Succeeded);
        Assert.Single(site.Commands);
        var lines = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], lines.Select(l => l.Phase));
        Assert.Equal(("Partial", lines[0].RunId), (lines[1].Result, lines[1].RunId));
        Assert.Equal(["Done", "Failed"], lines[1].Objects.Select(o => o.Result));
    }

    [Fact]
    public async Task AFailureTextAboutAnObjectReferenceIsAnErrorNotAReferenceRefusal()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        h.Site.Fail("nullref:cmd:Remove-CMPackage");
        var outcome = await ApplyAsync(h, change);
        Assert.Equal("ConfigMgr reported an error. The log has the kind of failure.", Assert.Single(outcome.Objects).Message);
    }

    [Fact]
    public async Task ARunWithMoreObjectsThanOneRecordHoldsWritesOneStartedAndOneFinishedRecord()
    {
        var site = new StubHygieneSite(SiteHygieneFixtures.StubState());
        var audit = new RecordingAuditLog();
        var service = new SiteHygieneService(site, audit, utcNow: () => SiteHygieneFixtures.CaptureUtc) { AuditObjectsPerRecord = 1 };
        var prepared = await service.PrepareAsync(new HygieneChange(HygieneFixKind.RemovePackage, [EmptyPackage, OtherPackage], false, [], [], null), CancellationToken.None);
        audit.FailWhen = r => r.Phase == HygieneAuditPhases.Objects && r.Result.Length > 0;
        var outcome = await service.ApplyAsync(prepared with { ChangeConfirmed = true, DeleteConfirmed = true }, new SiteHygieneHarness.SyncProgress<HygieneOutcome>(_ => { }), CancellationToken.None);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(["Started", "Objects", "Finished"], audit.Written.Select(r => r.Phase));
        Assert.Single(audit.Written.Select(r => r.RunId).Distinct());
        Assert.Equal(("Succeeded", "MCM00001"), (audit.Written[2].Result, Assert.Single(audit.Written[2].Objects).ObjectKey));
        Assert.Equal(1, audit.Failed);

        var site2 = new StubHygieneSite(SiteHygieneFixtures.StubState());
        var audit2 = new RecordingAuditLog();
        var service2 = new SiteHygieneService(site2, audit2, utcNow: () => SiteHygieneFixtures.CaptureUtc) { AuditObjectsPerRecord = 1 };
        var prepared2 = await service2.PrepareAsync(new HygieneChange(HygieneFixKind.RemovePackage, [EmptyPackage, OtherPackage], false, [], [], null), CancellationToken.None);
        audit2.FailWhen = r => r.Phase == HygieneAuditPhases.Objects && r.Result.Length == 0;
        var refused = await service2.ApplyAsync(prepared2 with { ChangeConfirmed = true, DeleteConfirmed = true }, new SiteHygieneHarness.SyncProgress<HygieneOutcome>(_ => { }), CancellationToken.None);
        Assert.StartsWith("Skipped: the audit log could not be written", refused.Message);
        Assert.Empty(site2.Commands);
        Assert.Equal(["Started", "Objects", "Finished"], audit2.Written.Select(r => r.Phase));
        Assert.Equal(["", "Skipped", "Skipped"], audit2.Written.Select(r => r.Result));
        Assert.Single(audit2.Written, r => r.Phase == HygieneAuditPhases.Finished);
    }

    [Fact]
    public void AnAuditLogThatARunHoldsOpenStillProbesAsWritable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shaudit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new HygieneAuditLog(() => Path.Combine(folder, "audit.jsonl"));
            using (var held = log.Reserve())
            {
                Assert.True(held.Probe.Writable, held.Probe.Message);
                Assert.True(log.Probe().Writable);
            }
            Assert.True(log.Probe().Writable);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ALastScanLargerThanAReadTakesIsNotSavedAndTheStoredOneStays()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, HygieneScanStore.FileName);
            var small = new HygieneScanResult([], [], SiteHygieneFixtures.CaptureUtc, false, null) { SiteCode = "MCM" };
            var store = new HygieneScanStore(path) { MaxBytes = 4096 };
            store.Save(small);
            var before = File.ReadAllBytes(path);
            var finding = HygieneChecks.F("APP-01", "Application", "ScopeId_X/Application_1", "App", new string('e', 8000), "r");
            var large = small with { Findings = [finding] };
            var refused = Assert.Throws<HygieneServiceException>(() => store.Save(large));
            Assert.Contains("was not saved", refused.Message);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.NotNull(store.Load());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task TheScanDoesNotReportAnInactiveRecordWhoseClientSentAHeartbeatInsideTheWindow()
    {
        var state = SiteHygieneFixtures.StubState();
        Class(state, "SMS_CM_RES_COLL_SMS00001").OfType<JsonObject>().Single(d => (string?)d["Name"] == "CLIENT02")["LastActiveTime"] = "2026-05-01T00:00:00";
        Class(state, "SMS_R_System").OfType<JsonObject>().Single(d => (string?)d["ResourceId"] == "16777222")["AgentTime"] =
            new JsonArray("2026-05-01T00:00:00", "2026-05-01T00:00:00", "2026-10-12T04:25:53");
        using var h = new SiteHygieneHarness(state);
        var result = await h.ScanAsync("Devices");
        Assert.DoesNotContain(result.Findings, f => f.CheckId == "DEV-01");

        h.Site.Fail("query:SMS_R_System");
        var cut = await h.ScanAsync("Devices");
        Assert.Equal(HygieneCheckStates.NotRun, cut.Checks.Single(c => c.CheckId == "DEV-01").State);
    }

    private sealed class BreakingSession(IConfigMgrSession inner, Func<Dictionary<string, object?>, bool> breakWhen) : IConfigMgrSession
    {
        public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
        {
            if (breakWhen((parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value))) throw new ObjectDisposedException("ConfigMgrSession");
            return inner.InvokeAsync(script, parameters, ct, timeout);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAuditLog : IHygieneAuditLog
    {
        public List<HygieneAuditRecord> Written { get; } = [];
        public Func<HygieneAuditRecord, bool> FailWhen = _ => false;
        public int Failed;

        public AuditProbe Probe() => new(true, "The test log can be written.");

        public AuditWrite Append(HygieneAuditRecord r)
        {
            if (FailWhen(r))
            {
                Failed++;
                return new AuditWrite(false, "The test log refused the record.");
            }
            Written.Add(r);
            return new AuditWrite(true, "");
        }

        public IReadOnlyList<HygieneAuditRecord> Read(int max) => Written.TakeLast(max).ToList();

        public string Path => "audit.jsonl";
    }
}
