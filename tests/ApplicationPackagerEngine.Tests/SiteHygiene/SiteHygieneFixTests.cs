using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>Every fix kind through the real script and the stub site: Prepare on fresh data, the writes, the read-back, and the plan text equal to the commands the service ran.</summary>
public sealed class SiteHygieneFixTests
{
    internal static HygieneFinding Finding(string checkId) =>
        Finding(f => f.CheckId == checkId);

    internal static HygieneFinding Finding(Func<HygieneFinding, bool> pick)
    {
        var (findings, _) = SiteHygieneParity.Native(SiteHygieneService.ResolveAreas(new HashSet<string>()).ToArray());
        return findings.First(pick);
    }

    /// <summary>
    /// The TSQ-02 finding of SHL-DriverPkg-Unused with its delete, as a site without an Auto Apply Drivers step
    /// gives it. On CM01 MCM00008 has the step, so the scan of the fixture reports the package for report only.
    /// </summary>
    internal static HygieneFinding DriverPackage =>
        HygieneChecks.F("TSQ-02", "DriverPackage", "MCM00111", "SHL-DriverPkg-Unused", "Driver package is referenced by no task sequence.", "r",
            HygieneFixKind.RemoveDriverPackage, HygieneChecks.Params((HygieneFixParameters.Id, "MCM00111")));

    internal static Task<HygieneChange> PrepareAsync(SiteHygieneHarness h, HygieneFixKind kind, params HygieneFinding[] findings) =>
        h.Service.PrepareAsync(new HygieneChange(kind, findings, false, [], [], null), CancellationToken.None);

    internal static async Task<HygieneOutcome> ApplyAsync(SiteHygieneHarness h, HygieneChange change, bool confirm = true, bool delete = true, CancellationToken ct = default) =>
        await h.Service.ApplyAsync(change with { ChangeConfirmed = confirm, DeleteConfirmed = delete && change.IsDelete }, new SiteHygieneHarness.SyncProgress<HygieneOutcome>(h.Reports.Add), ct);

    /// <summary>The commands the stub ran, in order, equal the plan text of the prepared change.</summary>
    internal static void RanExactlyThePlan(SiteHygieneHarness h, HygieneChange change)
    {
        var expected = change.FixText.Split("\r\n").Where(l => !l.StartsWith('$') && !l.StartsWith("foreach (", StringComparison.Ordinal) && !l.StartsWith("if (", StringComparison.Ordinal)).ToList();
        var ran = h.Site.Commands.Where(c => !c.StartsWith("SMS_CollectionSettings", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected, ran);
    }

    [Theory]
    [InlineData("APP-01", HygieneFixKind.RemoveApplication, "CMTrace Open")]
    [InlineData("APP-02", HygieneFixKind.RemoveApplicationDeployment, "SHL-App-Retired")]
    [InlineData("APP-03", HygieneFixKind.RemoveApplicationDeployment, "SHL-App-Old")]
    [InlineData("DPL-01", HygieneFixKind.RemoveApplicationDeployment, "SHL-App-Expired -> SHL-Col-Target")]
    [InlineData("APP-05", HygieneFixKind.RemoveApplicationRevisionHistory, "SHL-App-Unused")]
    [InlineData("PKG-01", HygieneFixKind.RemovePackage, "SHL-Pkg-Empty")]
    [InlineData("COL-01", HygieneFixKind.RemoveCollection, "SHL-Col-Empty")]
    [InlineData("TSQ-02", HygieneFixKind.RemoveBootImage, "SHL-Boot-Unused")]
    [InlineData("TSQ-02", HygieneFixKind.RemoveDriverPackage, "SHL-DriverPkg-Unused")]
    [InlineData("UPD-03", HygieneFixKind.RemoveAutoDeploymentRule, "SHL-ADR-Disabled")]
    [InlineData("COL-10", HygieneFixKind.RemoveMaintenanceWindow, "SHL-MW-Expired on SHL-Col-MW")]
    [InlineData("COL-05", HygieneFixKind.SetCollectionRefreshType, "SHL-Col-Both")]
    [InlineData("MNT-01", HygieneFixKind.SetSiteMaintenanceTask, "Delete Obsolete Client Discovery Data")]
    [InlineData("MNT-02", HygieneFixKind.SetSiteMaintenanceTask, "Backup SMS Site Server")]
    public async Task EachFixKindRunsItsPlanAndReadsBackTheResult(string checkId, HygieneFixKind kind, string name)
    {
        using var h = new SiteHygieneHarness();
        var finding = kind == HygieneFixKind.RemoveDriverPackage ? DriverPackage : Finding(f => f.CheckId == checkId && f.FixKind == kind && f.ObjectName == name);
        Assert.Equal(name, finding.ObjectName);
        var change = await PrepareAsync(h, kind, finding);
        Assert.False(change.Blocked, string.Join(" ", change.Checks.Select(c => c.Message)));
        Assert.Single(change.Findings);
        Assert.NotEmpty(change.Targets);
        Assert.NotNull(change.Fingerprint);
        Assert.Equal(change.FixText, change.Findings[0].FixText);
        Assert.Empty(h.Site.Commands);

        var outcome = await ApplyAsync(h, change);
        var result = Assert.Single(outcome.Objects);
        Assert.True(result.Result == HygieneResults.Done, result.Message + " | " + string.Join(" ; ", h.Site.Log.Where(l => l.StartsWith("Cmd:"))));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(outcome.Final);
        RanExactlyThePlan(h, change);
        var lines = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], lines.Select(l => l.Phase));
        Assert.All(lines, l => Assert.True(l.Verified));
        Assert.Equal("Succeeded", lines[1].Result);
    }

    [Fact]
    public async Task OnlyTheChosenMaintenanceWindowIsRemovedNotTheOneWhoseNameContainsIt()
    {
        using var h = new SiteHygieneHarness();
        var first = Finding(f => f.CheckId == "COL-10" && f.ObjectName == "SHL-MW-Expired on SHL-Col-MW");
        var outcome = await ApplyAsync(h, await PrepareAsync(h, HygieneFixKind.RemoveMaintenanceWindow, first));
        Assert.Equal(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        var left = h.Site.State["Windows"]!["MCM00219"]!.AsArray().Select(w => (string?)w!["Name"]).ToList();
        Assert.Equal(["SHL-MW-Expired-2"], left);
        Assert.Contains(h.Site.Log, l => l == "Cmd:SMS_CollectionSettings.ServiceWindows|MCM00219|{51634A25-AF2B-400B-A492-CAF093FF542C}");
    }

    [Fact]
    public async Task TheTaskSequenceInstallSettingIsSetAndReadBackFromTheNewRevision()
    {
        var state = SiteHygieneFixtures.StubState();
        var model = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_b802345b-cfc1-4259-95ac-3e7f2c4bdbf1";
        state["AutoInstall"]![model] = false;
        using var h = new SiteHygieneHarness(state);
        var finding = HygieneChecks.F("TSQ-03", "Application", model, "7-Zip 26.03 (x64 edition)", "e", "r", HygieneFixKind.SetApplication, HygieneChecks.Params((HygieneFixParameters.ModelName, model)));
        var change = await PrepareAsync(h, HygieneFixKind.SetApplication, finding);
        var outcome = await ApplyAsync(h, change);
        Assert.Equal(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Equal($"Set-CMApplication -ModelName '{model}' -AutoInstall $true", Assert.Single(h.Site.Commands));
        Assert.True((bool)h.Site.State["AutoInstall"]![model]!);
    }

    [Fact]
    public async Task AStaleRuleRunsAndTheReadBackWaitsForItsNewRunTime()
    {
        var state = SiteHygieneFixtures.StubState();
        var rule = state["Classes"]!["SMS_AutoDeployment"]!.AsArray().Single()!.AsObject();
        rule["AutoDeploymentEnabled"] = "True";
        rule["LastRunTime"] = "2026-08-01T00:00:00";
        using var h = new SiteHygieneHarness(state);
        var finding = HygieneChecks.F("UPD-03", "ADR", "1", "SHL-ADR-Disabled", "e", "r", HygieneFixKind.RunAutoDeploymentRule, HygieneChecks.Params((HygieneFixParameters.Id, "1"), (HygieneFixParameters.Days, "45")));
        var change = await PrepareAsync(h, HygieneFixKind.RunAutoDeploymentRule, finding);
        var outcome = await ApplyAsync(h, change);
        Assert.Equal(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Equal("Invoke-CMSoftwareUpdateAutoDeploymentRule -Id 1", Assert.Single(h.Site.Commands));

        state = SiteHygieneFixtures.StubState();
        rule = state["Classes"]!["SMS_AutoDeployment"]!.AsArray().Single()!.AsObject();
        rule["AutoDeploymentEnabled"] = "True";
        rule["LastRunTime"] = "2026-08-01T00:00:00";
        state["Options"]!["AdrNoRun"] = true;
        using var quiet = new SiteHygieneHarness(state);
        var unchanged = await ApplyAsync(quiet, await PrepareAsync(quiet, HygieneFixKind.RunAutoDeploymentRule, finding));
        var r = Assert.Single(unchanged.Objects);
        Assert.Equal(HygieneResults.Unchanged, r.Result);
        Assert.Contains("no new run time within 60 seconds", r.Message);
        Assert.Equal(12, quiet.Delays.Count);
    }

    private static JsonObject InactiveClient02(string heartbeat)
    {
        var state = SiteHygieneFixtures.StubState();
        var device = state["Classes"]!["SMS_CM_RES_COLL_SMS00001"]!.AsArray().Single(d => (string?)d!["Name"] == "CLIENT02")!.AsObject();
        device["LastActiveTime"] = "2026-05-01T00:00:00";
        var agents = state["Classes"]!["SMS_R_System"]!.AsArray().Single(d => (string?)d!["ResourceId"] == "16777222")!.AsObject();
        agents["AgentTime"] = new JsonArray("2026-05-01T00:00:00", "2026-05-01T00:00:00", heartbeat);
        return state;
    }

    [Fact]
    public async Task AnInactiveClientRecordIsDeleted()
    {
        using var h = new SiteHygieneHarness(InactiveClient02("2026-05-01T00:00:00"));
        var result = await h.ScanAsync("Devices");
        var finding = Assert.Single(result.Findings, f => f.CheckId == "DEV-01");
        Assert.Equal("16777222", finding.ObjectKey);
        var change = await PrepareAsync(h, HygieneFixKind.RemoveDevice, finding);
        var outcome = await ApplyAsync(h, change);
        Assert.Equal(HygieneResults.Done, Assert.Single(outcome.Objects).Result);
        Assert.Equal("Remove-CMDevice -ResourceId 16777222 -Force", Assert.Single(h.Site.Commands));
    }

    [Fact]
    public async Task ADeviceWithAHeartbeatInsideTheWindowIsNeverDeleted()
    {
        using var h = new SiteHygieneHarness(InactiveClient02("2026-10-12T04:25:53"));
        var result = await h.ScanAsync("Devices");
        Assert.DoesNotContain(result.Findings, f => f.CheckId == "DEV-01");
        Assert.Equal(HygieneCheckStates.Ran, result.Checks.Single(c => c.CheckId == "DEV-01").State);
        var finding = HygieneChecks.F("DEV-01", "Device", "16777222", "CLIENT02", "e", "r", HygieneFixKind.RemoveDevice,
            HygieneChecks.Params((HygieneFixParameters.Id, "16777222"), (HygieneFixParameters.Days, "90")));
        var change = await PrepareAsync(h, HygieneFixKind.RemoveDevice, finding);
        Assert.True(change.Blocked);
        Assert.Contains("heartbeat 2026-10-12", Assert.Single(change.Dropped).Message);
        Assert.StartsWith("Skipped: ", change.Dropped[0].Message);
    }
}
