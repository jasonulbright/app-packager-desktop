using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.SiteHygiene.SiteHygieneFixTests;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// A fix run is bound to the site and SMS Provider of its plan, re-checks references in the delete command,
/// stops on Cancel between objects, reports a result per object and says when its Finished audit record is missing.
/// </summary>
public sealed class SiteHygieneRunBindingTests
{
    private static HygieneFinding EmptyPackage => Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM0010E");
    private static HygieneFinding OtherEmptyPackage => Finding(f => f.CheckId == "PKG-01" && f.ObjectKey == "MCM00001");
    private static HygieneFinding EmptyCollection => Finding(f => f.CheckId == "COL-01" && f.ObjectKey == "MCM00215");
    private static HygieneFinding Retired => Finding(f => f.CheckId == "APP-02" && f.ObjectName == "SHL-App-Retired");

    private static bool IsApply(Dictionary<string, object?> p) => (string?)p["Operation"] == "Apply";

    private static int ApplyRuns(SiteHygieneHarness h) => h.Site.Runs.Count(IsApply);

    // ---- 1: provider binding --------------------------------------------------------------------

    [Fact]
    public async Task APlanIsRefusedWhenTheSettingsNameAnotherProviderOfTheSameSiteCode()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        Assert.Equal(("MCM", "CM01"), (change.SiteCode, change.Provider));
        h.Connection = new ConfigMgrConnection("MCM", "CM02");
        var outcome = await ApplyAsync(h, change);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("Skipped: the connection changed since the plan", outcome.Message);
        Assert.All(outcome.Objects, o => Assert.Equal(HygieneResults.Skipped, o.Result));
        Assert.Equal(0, ApplyRuns(h));
        Assert.Empty(h.Site.Commands);
        Assert.Empty(h.Audit.Read(10));
    }

    [Fact]
    public async Task TheDeleteCommandDoesNothingInASessionOnAnotherProviderAndTheRunStops()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage, OtherEmptyPackage);
        // The session reconnected to another provider between the State read and the write.
        h.Site.BeforeRun = (p, site) => { if (IsApply(p)) site.DriveProvider = "CM02"; };
        var outcome = await ApplyAsync(h, change);
        Assert.Empty(h.Site.Commands);
        Assert.Equal(1, ApplyRuns(h));
        Assert.Equal([HygieneResults.Skipped, HygieneResults.Skipped], outcome.Objects.Select(o => o.Result));
        Assert.All(outcome.Objects, o => Assert.StartsWith("Skipped: the connection changed since the plan", o.Message));
        Assert.Equal("Skipped", h.Audit.Read(10).Last().Result);
    }

    [Fact]
    public async Task AStateReadInASessionOnAnotherProviderStopsTheRunBeforeAnyWrite()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        h.Site.DriveProvider = "cm02.contoso.lab";
        var outcome = await ApplyAsync(h, change);
        Assert.Equal(0, ApplyRuns(h));
        Assert.StartsWith("Skipped: the connection changed since the plan", Assert.Single(outcome.Objects).Message);
    }

    [Fact]
    public async Task TheShortAndTheFullNameOfTheProviderAreTheSameProviderInTheScript()
    {
        using var h = new SiteHygieneHarness();
        h.Site.DriveProvider = "CM01.contoso.lab";
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        Assert.True((await ApplyAsync(h, change)).Succeeded);
        Assert.Single(h.Site.Commands);
    }

    [Fact]
    public async Task ThePlanAndEveryAuditRecordNameTheSiteAndTheProvider()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemovePackage, EmptyPackage);
        Assert.Contains(change.Impact, l => l == "The plan is for site MCM on CM01. The fix stops when the settings name another site or SMS Provider.");
        Assert.All(h.Site.Runs, p => Assert.Equal(("MCM", "CM01"), ((string?)p["ExpectSite"], (string?)p["ExpectProvider"])));
        await ApplyAsync(h, change);
        var records = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], records.Select(r => r.Phase));
        Assert.All(records, r => Assert.Equal(("MCM", "CM01"), (r.Site, r.Provider)));
    }

    [Fact]
    public async Task AScanRecordsTheProviderAndStopsWhenTheSettingsChangeDuringTheScan()
    {
        using var h = new SiteHygieneHarness();
        var result = await h.ScanAsync("Packages");
        Assert.Equal(("MCM", "CM01"), (result.SiteCode, result.Provider));
        Assert.True(result.IsFor("mcm", "cm01\\"));
        Assert.False(result.IsFor("MCM", "CM02"));

        using var moved = new SiteHygieneHarness();
        moved.Site.BeforeRun = (p, site) => moved.Connection = new ConfigMgrConnection("MCM", "CM02");
        var failure = await Assert.ThrowsAsync<HygieneServiceException>(() => moved.ScanAsync("Packages", "Collections"));
        Assert.Contains("another site or SMS Provider", failure.Message);
    }

    [Fact]
    public void TheStoredScanKeepsTheProviderAndAFileWithoutOneBelongsToNoConnection()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shbind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new HygieneScanStore(Path.Combine(folder, "last-scan.json"));
            store.Save(new HygieneScanResult([], [], DateTime.UtcNow, false, null) { SiteCode = "MCM", Provider = "cm01.contoso.lab" });
            var loaded = store.Load()!;
            Assert.True(loaded.IsFor("MCM", "CM01.contoso.lab"));
            Assert.False(loaded.IsFor("MCM", "cm02.contoso.lab"));

            var local = new HygieneScanStore(Path.Combine(folder, "local.json"));
            local.Save(new HygieneScanResult([], [], DateTime.UtcNow, false, null) { SiteCode = "MCM", Provider = "" });
            Assert.True(local.Load()!.IsFor("MCM", ""));
            Assert.False(local.Load()!.IsFor("MCM", "CM01"));

            var old = Path.Combine(folder, "old.json");
            File.WriteAllText(old, """{"SchemaVersion":1,"ScannedUtc":"2026-10-20T07:00:00Z","SiteCode":"MCM","Areas":[],"Truncated":false,"Note":null,"QueryCount":3,"Findings":[],"Checks":[]}""");
            var earlier = new HygieneScanStore(old).Load()!;
            Assert.Null(earlier.Provider);
            Assert.False(earlier.IsFor("MCM", "CM01"));
            Assert.False(earlier.IsFor("MCM", ""));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ASuppressionHoldsOnItsSiteAndProviderAndOlderEntriesKeepTheirScope()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shbind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "suppressions.json");
            File.WriteAllText(path, """
                {"SchemaVersion":2,"Suppressions":[
                  {"CheckId":"APP-01","ObjectKey":"A","Comment":"every site","CreatedUtc":"2026-10-01T00:00:00Z","SiteCode":null},
                  {"CheckId":"APP-01","ObjectKey":"B","Comment":"site, no provider","CreatedUtc":"2026-10-01T00:00:00Z","SiteCode":"MCM"}]}
                """);
            var store = new HygieneSuppressionStore(path);
            store.Add(new Suppression("APP-01", "C", "own provider", DateTime.UtcNow) { SiteCode = "MCM", Provider = "cm01.contoso.lab" });
            var all = store.Load();
            var a = all.Single(s => s.ObjectKey == "A");
            var b = all.Single(s => s.ObjectKey == "B");
            var c = all.Single(s => s.ObjectKey == "C");
            Assert.Null(a.Provider);
            Assert.Null(b.Provider);
            Assert.Equal("CM01.CONTOSO.LAB", c.Provider);
            Assert.True(a.AppliesTo("LAB", "cm09"));
            Assert.True(b.AppliesTo("MCM", "cm02.contoso.lab"));
            Assert.False(b.AppliesTo("LAB", "cm01.contoso.lab"));
            Assert.True(c.AppliesTo("MCM", "CM01.contoso.lab"));
            Assert.False(c.AppliesTo("MCM", "cm02.contoso.lab"));

            store.Add(new Suppression("APP-01", "C", "other provider", DateTime.UtcNow) { SiteCode = "MCM", Provider = "cm02.contoso.lab" });
            Assert.Equal(2, store.Load().Count(s => s.ObjectKey == "C"));
            store.RemoveRange("MCM", [("APP-01", "C")], "cm02.contoso.lab");
            Assert.Equal("own provider", store.Load().Single(s => s.ObjectKey == "C").Comment);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- 2: references re-checked in the delete command ----------------------------------------

    public static TheoryData<string, string> References() => new()
    {
        { "COL-01 MCM00215", "rule" },
        { "COL-01 MCM00215", "client-settings" },
        { "COL-01 MCM00215", "deployment" },
        { "COL-01 MCM00215", "include" },
        { "APP-01", "task-sequence" },
        { "PKG-01 MCM0010E", "task-sequence" },
        { "TSQ-02 MCM00110", "boot-image" },
        { "TSQ-02 driver", "task-sequence" },
        { "UPD-03", "enabled" }
    };

    private static (HygieneFinding Finding, HygieneFixKind Kind) Target(string which) => which switch
    {
        "COL-01 MCM00215" => (EmptyCollection, HygieneFixKind.RemoveCollection),
        "APP-01" => (Finding(f => f.CheckId == "APP-01" && f.ObjectName == "CMTrace Open"), HygieneFixKind.RemoveApplication),
        "PKG-01 MCM0010E" => (EmptyPackage, HygieneFixKind.RemovePackage),
        "TSQ-02 MCM00110" => (Finding(f => f.CheckId == "TSQ-02" && f.ObjectKey == "MCM00110"), HygieneFixKind.RemoveBootImage),
        "TSQ-02 driver" => (Finding(f => f.CheckId == "TSQ-02" && f.FixKind == HygieneFixKind.RemoveDriverPackage && f.ObjectName == "SHL-DriverPkg-Unused"), HygieneFixKind.RemoveDriverPackage),
        _ => (Finding(f => f.CheckId == "UPD-03" && f.FixKind == HygieneFixKind.RemoveAutoDeploymentRule), HygieneFixKind.RemoveAutoDeploymentRule)
    };

    private static void AddReference(StubHygieneSite site, string reference, HygieneFinding f)
    {
        var id = f.FixParameters.TryGetValue(HygieneFixParameters.Id, out var v) ? v : "";
        switch (reference)
        {
            case "rule": site.Class("SMS_AutoDeployment").Add(new JsonObject { ["AutoDeploymentID"] = 9, ["Name"] = "SHL-ADR-New", ["AutoDeploymentEnabled"] = "True", ["LastRunTime"] = "", ["LastErrorCode"] = 0, ["CollectionID"] = id }); break;
            case "client-settings": site.Class("SMS_ClientSettingsAssignment").Add(new JsonObject { ["ClientSettingsID"] = 16777220, ["CollectionID"] = id }); break;
            case "deployment": site.Class("SMS_CIAssignmentBaseClass").Add(new JsonObject { ["AssignmentID"] = 16777999, ["TargetCollectionID"] = id }); break;
            case "include": site.Class("SMS_CollectionDependencies").Add(new JsonObject { ["DependentCollectionID"] = "MCM00016", ["SourceCollectionID"] = id, ["RelationshipType"] = 2 }); break;
            case "task-sequence": site.Class("SMS_TaskSequencePackageReference_All").Add(new JsonObject { ["PackageID"] = "MCM00008", ["ObjectID"] = f.FixKind == HygieneFixKind.RemoveApplication ? f.ObjectKey : id }); break;
            case "boot-image": site.Class("SMS_TaskSequencePackage").Add(new JsonObject { ["PackageID"] = "MCM00099", ["Name"] = "SHL-TS-New", ["BootImageID"] = id, ["ProgramFlags"] = 0 }); break;
            case "enabled": foreach (var r in site.Class("SMS_AutoDeployment").OfType<JsonObject>().Where(r => r["AutoDeploymentID"]!.ToString() == id)) r["AutoDeploymentEnabled"] = "True"; break;
        }
    }

    [Theory]
    [MemberData(nameof(References))]
    public async Task AReferenceMadeAfterTheStateReadStopsTheDeleteInTheSameCommand(string which, string reference)
    {
        using var h = new SiteHygieneHarness();
        var (finding, kind) = Target(which);
        var change = await PrepareAsync(h, kind, finding);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        // The reference appears after the State read before the write and before the delete command runs.
        h.Site.BeforeRun = (p, site) => { if (IsApply(p)) AddReference(site, reference, finding); };
        var outcome = await ApplyAsync(h, change);
        Assert.Equal(1, ApplyRuns(h));
        Assert.Empty(h.Site.Commands);
        var r = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Skipped, r.Result);
        Assert.StartsWith("Skipped: the delete command found that the object is in use now", r.Message);
        Assert.EndsWith("It was not deleted.", r.Message);
        Assert.Equal(HygieneResults.Skipped, Assert.Single(r.Targets).Result);
        Assert.Equal("Skipped", h.Audit.Read(10).Last().Result);
    }

    [Fact]
    public async Task AnAdministrativeScopeMadeAfterTheStateReadStopsTheCollectionDelete()
    {
        using var h = new SiteHygieneHarness();
        var change = await PrepareAsync(h, HygieneFixKind.RemoveCollection, EmptyCollection);
        h.Site.BeforeRun = (p, site) =>
        {
            if (!IsApply(p)) return;
            site.Class("SMS_Admin").Add(new JsonObject
            {
                ["AdminID"] = 16777299, ["LogonName"] = @"LAB\shl-scoped", ["IsDeleted"] = false, ["RoleNames"] = "Read-only Analyst",
                ["Permissions"] = new JsonArray(new JsonObject { ["CategoryTypeID"] = 1, ["CategoryID"] = "MCM00215" })
            });
        };
        var outcome = await ApplyAsync(h, change);
        Assert.Empty(h.Site.Commands);
        Assert.Contains("an administrative user has it in the assigned collections", Assert.Single(outcome.Objects).Message);
    }

    // ---- 3: cancel between the objects of one finding, 4: a result per object ------------------

    internal static JsonObject RetiredWithTwoDeployments()
    {
        var state = SiteHygieneFixtures.StubState();
        var classes = state["Classes"]!.AsObject();
        var app = classes["SMS_Application"]!.AsArray().OfType<JsonObject>().First(a => (string?)a["LocalizedDisplayName"] == "SHL-App-Retired" && (bool?)a["IsLatest"] == true);
        var assignments = classes["SMS_ApplicationAssignment"]!.AsArray();
        var first = assignments.OfType<JsonObject>().First(a => a["AppModelID"]!.ToString() == app["ModelID"]!.ToString());
        var second = first.DeepClone().AsObject();
        second["AssignmentID"] = "16777998";
        second["AssignmentUniqueID"] = "{00000000-0000-0000-0000-000000000998}";
        second["CollectionName"] = "SHL-Col-Second";
        second["TargetCollectionID"] = "MCM00298";
        assignments.Add(second);
        return state;
    }

    [Fact]
    public async Task CancelAfterTheFirstDeploymentOfAFindingStopsBeforeTheSecondDeletion()
    {
        using var h = new SiteHygieneHarness(RetiredWithTwoDeployments());
        var superseded = Finding(f => f.CheckId == "APP-03" && f.ObjectName == "SHL-App-Old");
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplicationDeployment, Retired, superseded);
        Assert.Equal(2, change.Findings.Count);
        Assert.Equal(2, change.Targets.Count(t => t.FindingKey == Retired.Key));
        using var cts = new CancellationTokenSource();
        // Cancel arrives while the first deletion runs: that write finishes, nothing after it starts.
        h.Site.BeforeRun = (p, site) => { if (IsApply(p)) cts.Cancel(); };
        var outcome = await ApplyAsync(h, change, ct: cts.Token);
        Assert.Single(h.Site.Commands);
        Assert.Equal(1, ApplyRuns(h));
        var retired = outcome.Objects[0];
        Assert.Equal(HygieneResults.Partial, retired.Result);
        Assert.Equal([HygieneResults.Done, HygieneResults.Skipped], retired.Targets.Select(t => t.Result));
        Assert.Equal("Skipped: the run was cancelled before this object.", retired.Targets[1].Message);
        Assert.Equal(HygieneResults.Skipped, outcome.Objects[1].Result);
        Assert.Equal("Skipped: the run was cancelled before this object.", outcome.Objects[1].Message);
        Assert.False(outcome.Succeeded);
        var finished = h.Audit.Read(10).Last();
        Assert.Equal("Partial", finished.Result);
        Assert.Equal(change.Targets.Select((t, i) => i == 0 ? HygieneResults.Done : HygieneResults.Skipped), finished.Objects.Select(o => o.Result));
        Assert.Equal(change.Targets.Select(t => t.ObjectKey), finished.Objects.Select(o => o.ObjectKey));
    }

    [Fact]
    public async Task OneOfTwoDeploymentsFailsAndEachDeploymentKeepsItsOwnResult()
    {
        using var h = new SiteHygieneHarness(RetiredWithTwoDeployments());
        var change = await PrepareAsync(h, HygieneFixKind.RemoveApplicationDeployment, Retired);
        h.Site.BeforeRun = (p, site) => { if (IsApply(p) && site.Commands.Any()) site.Fail("cmd:Remove-CMApplicationDeployment"); };
        var outcome = await ApplyAsync(h, change);
        var r = Assert.Single(outcome.Objects);
        Assert.Equal(HygieneResults.Partial, r.Result);
        Assert.Equal([HygieneResults.Done, HygieneResults.Failed], r.Targets.Select(t => t.Result));
        Assert.Equal(change.Targets, r.Targets.Select(t => t.Target));
        Assert.Equal("Deleted. The site no longer lists the object.", r.Targets[0].Message);
        Assert.Equal("ConfigMgr reported an error. The log has the kind of failure.", r.Targets[1].Message);
        Assert.StartsWith("1 of 2 objects deleted (1 done, 1 failed). First object not done: ", r.Message);
        Assert.Contains(change.Targets[1].ObjectKey, r.Message);
        var finished = h.Audit.Read(10).Last();
        Assert.Equal("Partial", finished.Result);
        Assert.Equal([("Deployment", HygieneResults.Done), ("Deployment", HygieneResults.Failed)], finished.Objects.Select(o => (o.ObjectType, o.Result)));
    }

    // ---- 5: a Finished record that cannot be written ------------------------------------------

    /// <summary>An audit log whose appends fail after the first <see cref="Allowed"/> appends.</summary>
    private sealed class FailingAudit(HygieneAuditLog inner) : IHygieneAuditLog
    {
        public int Allowed = 1;
        public List<HygieneAuditRecord> Written { get; } = [];
        public AuditProbe Probe() => inner.Probe();
        public AuditWrite Append(HygieneAuditRecord r)
        {
            if (Allowed-- <= 0) return new AuditWrite(false, "The disk is full.");
            Written.Add(r);
            return inner.Append(r);
        }
        public IReadOnlyList<HygieneAuditRecord> Read(int max) => inner.Read(max);
        public string Path => inner.Path;
    }

    [Fact]
    public async Task AFinishedRecordThatCannotBeWrittenMarksTheOutcomeAuditIncompleteAndNamesTheLog()
    {
        using var h = new SiteHygieneHarness();
        var audit = new FailingAudit(h.Audit);
        var service = new SiteHygieneService(h.Site, audit, utcNow: () => h.Clock, connection: () => h.Connection);
        var change = await service.PrepareAsync(new HygieneChange(HygieneFixKind.RemovePackage, [EmptyPackage], false, [], [], null), CancellationToken.None);
        var outcome = await service.ApplyAsync(change with { ChangeConfirmed = true, DeleteConfirmed = true }, new Progress<HygieneOutcome>(), CancellationToken.None);
        Assert.Single(h.Site.Commands);
        Assert.Equal(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.AuditIncomplete);
        Assert.Contains("The audit log has no Finished record for this run", outcome.Message);
        Assert.Contains(h.Audit.Path, outcome.Message);
        Assert.Equal(h.Audit.Path, outcome.AuditPath);
        Assert.Equal(["Started"], h.Audit.Read(10).Select(r => r.Phase));

        using var fine = new SiteHygieneHarness();
        var complete = await ApplyAsync(fine, await PrepareAsync(fine, HygieneFixKind.RemovePackage, EmptyPackage));
        Assert.False(complete.AuditIncomplete);
        Assert.DoesNotContain("audit", complete.Message, StringComparison.OrdinalIgnoreCase);
    }
}
