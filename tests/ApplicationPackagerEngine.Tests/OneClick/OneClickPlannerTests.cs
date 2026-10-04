using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>The One Click plan, its count line, readiness, scope, the run decisions and the row outcomes.</summary>
public sealed class OneClickPlannerTests
{
    internal static PublishReadiness Ready(string[]? inUse = null, string wsusServer = "wsus01", string siteCode = "MCM", bool console = true) =>
        new(new SystemsInUse
            {
                ConfigMgr = (inUse ?? ["ConfigMgr", "Intune", "Wsus"]).Contains("ConfigMgr"),
                Intune = (inUse ?? ["ConfigMgr", "Intune", "Wsus"]).Contains("Intune"),
                Wsus = (inUse ?? ["ConfigMgr", "Intune", "Wsus"]).Contains("Wsus")
            },
            console, siteCode, @"\\server\share", "t", "c", true, wsusServer);

    internal static OneClickPlanSettings Settings(string target = "MECM", string[]? tracked = null,
        Dictionary<string, DestinationSet>? destinations = null, DestinationSet? defaults = null, PublishReadiness? readiness = null,
        string approvalGroup = "Pilot", Dictionary<string, int>? cadence = null, int defaultCadence = 7) => new()
    {
        Tracked = tracked ?? ["package-a", "package-b"],
        Destinations = destinations ?? new Dictionary<string, DestinationSet>(),
        DefaultDestinations = defaults,
        LegacyTarget = target,
        DefaultCadenceDays = defaultCadence,
        CadenceOverrides = cadence ?? new Dictionary<string, int>(),
        Readiness = OneClickPlanner.Readiness(readiness ?? Ready()),
        Scope = new OneClickScopeSettings(true, "All DPs", true, "Pilot Devices", approvalGroup)
    };

    private static OneClickApp App(string key, string latest = "", string current = "") => new(key, key, "Vendor", latest, current);

    private static OneClickHistoryEntry Published(string destination, string version, string id = "") => new()
    {
        Published = new Dictionary<string, OneClickPublishRecord> { [destination] = new() { Version = version, Id = id } }
    };

    private static IReadOnlyList<OneClickPlanRow> Plan(OneClickPlanSettings settings, IEnumerable<OneClickApp> apps,
        Dictionary<string, OneClickHistoryEntry>? history = null, string action = OneClickActionNames.StageAndPackage, bool force = false, DateTime? now = null) =>
        OneClickPlanner.Plan(apps, settings, history ?? new Dictionary<string, OneClickHistoryEntry>(), action, force, now ?? DateTime.UtcNow);

    [Theory]
    [InlineData("MECM", true, false, false)]
    [InlineData("MECMAndWSUS", true, true, false)]
    [InlineData("WSUSOnly", false, true, false)]
    [InlineData("IntuneOnly", false, false, true)]
    [InlineData("MECMAndIntune", true, false, true)]
    [InlineData("nonsense", true, false, false)]
    public void EveryDeploymentTargetMapsToItsDestinations(string target, bool cm, bool wsus, bool intune) =>
        Assert.Equal(new DestinationSet(cm, wsus, intune), DestinationSet.FromTarget(target));

    [Theory]
    [InlineData(true, true, true, "MECMAndWSUS")]
    [InlineData(true, false, true, "MECMAndIntune")]
    [InlineData(false, true, true, "WSUSOnly")]
    [InlineData(false, false, true, "IntuneOnly")]
    [InlineData(false, false, false, "MECM")]
    public void TheNearestSingleTargetKeepsTheConfigMgrOrWsusPart(bool cm, bool wsus, bool intune, string expected) =>
        Assert.Equal(expected, new DestinationSet(cm, wsus, intune).ToTargetName());

    [Fact]
    public void TheSetStoresWsusUnderItsUpstreamName()
    {
        var json = JsonSerializer.Serialize(new DestinationSet(false, true, false));
        Assert.Equal("""{"ConfigMgr":false,"WSUS":true,"Intune":false}""", json);
        Assert.True(JsonSerializer.Deserialize<DestinationSet>(json)!.Wsus);
        Assert.Equal("ConfigMgr and WSUS", new DestinationSet(true, true, false).Describe());
        Assert.Equal("no destination", new DestinationSet().Describe());
    }

    [Fact]
    public void TheSavedDefaultIsUsedWhenTheRowHasNoSelection()
    {
        var (set, source) = OneClickPlanner.DestinationsFor(Settings("MECMAndWSUS"), "package-a");
        Assert.True(set.ConfigMgr && set.Wsus);
        Assert.Equal("Default", source);
        var (all, _) = OneClickPlanner.DestinationsFor(Settings("MECM", defaults: new DestinationSet(true, true, true)), "package-a");
        Assert.True(all.ConfigMgr && all.Wsus && all.Intune);
    }

    [Fact]
    public void TheRowSelectionWinsOverTheDefault()
    {
        var settings = Settings(destinations: new() { ["package-a"] = new DestinationSet(false, true, false) });
        var (set, source) = OneClickPlanner.DestinationsFor(settings, "PACKAGE-A");
        Assert.False(set.ConfigMgr);
        Assert.True(set.Wsus);
        Assert.Equal("Row", source);
        Assert.Equal("Default", OneClickPlanner.DestinationsFor(settings, "package-b").Source);
    }

    [Fact]
    public void ADestinationIsReadyWhenInUseAndConfigured()
    {
        var r = OneClickPlanner.Readiness(Ready());
        Assert.True(r["ConfigMgr"].Ready && r["WSUS"].Ready && r["Intune"].Ready);
        Assert.Equal("WSUS is not in use", OneClickPlanner.Readiness(Ready(["ConfigMgr"]))["WSUS"].Reason);
        Assert.Equal("WSUS server not set", OneClickPlanner.Readiness(Ready(wsusServer: ""))["WSUS"].Reason);
        Assert.Equal("site code not set", OneClickPlanner.Readiness(Ready(siteCode: ""))["ConfigMgr"].Reason);
        Assert.Equal("ConfigMgr console not detected", OneClickPlanner.Readiness(Ready(console: false))["ConfigMgr"].Reason);
    }

    [Fact]
    public void TheScopeNamesWhereEachDestinationLands()
    {
        var scope = Settings().Scope;
        Assert.Equal("distribute to DP group All DPs; deploy to collection Pilot Devices", OneClickPlanner.Scope("ConfigMgr", scope));
        Assert.Equal("approved for group Pilot", OneClickPlanner.Scope("WSUS", scope));
        Assert.Equal("published, not approved", OneClickPlanner.Scope("WSUS", Settings(approvalGroup: "").Scope));
        Assert.Equal("app created, not assigned", OneClickPlanner.Scope("Intune", scope));
        Assert.Equal("application only, no distribution, no deployment", OneClickPlanner.Scope("ConfigMgr", new OneClickScopeSettings()));
    }

    [Fact]
    public void OnlyTrackedApplicationsArePlanned()
    {
        var plan = Plan(Settings(), [App("package-a"), App("package-x")]);
        Assert.Equal(["package-a"], plan.Select(r => r.Key));
        Assert.Empty(Plan(Settings(tracked: []), [App("package-a")]));
    }

    [Fact]
    public void ARowWithoutADestinationIsSkippedAndSaysSo()
    {
        var row = Plan(Settings(destinations: new() { ["package-a"] = new DestinationSet() }), [App("package-a")])[0];
        Assert.False(row.Include);
        Assert.Equal("Skip", row.Planned);
        Assert.Equal("no destination selected", row.Reason);
    }

    [Fact]
    public void CheckStageAndThePublishDestinationsArePlanned()
    {
        var row = Plan(Settings("MECMAndWSUS"), [App("package-a", "2.0")])[0];
        Assert.Equal("Check, Stage, Publish to ConfigMgr+WSUS", row.Planned);
        Assert.Equal(["ConfigMgr", "WSUS"], row.PublishTo);
        Assert.True(row.Include);
    }

    [Fact]
    public void ADestinationThatHasTheVersionIsDroppedUnlessForced()
    {
        var history = new Dictionary<string, OneClickHistoryEntry> { ["package-a"] = Published("WSUS", "2.0") };
        var row = Plan(Settings("MECMAndWSUS"), [App("package-a", "2.0")], history)[0];
        Assert.Equal(["ConfigMgr"], row.PublishTo);
        Assert.Equal("WSUS: 2.0 already published", row.Reason);
        Assert.Equal("2.0", row.LastPublished["WSUS"]);
        Assert.Equal("WSUS 2.0", row.LastPublishedText);
        Assert.Equal(["ConfigMgr", "WSUS"], Plan(Settings("MECMAndWSUS"), [App("package-a", "2.0")], history, force: true)[0].PublishTo);
    }

    [Fact]
    public void ADestinationNewlySelectedSinceTheLastPublishIsPublished()
    {
        var history = new Dictionary<string, OneClickHistoryEntry> { ["package-a"] = Published("ConfigMgr", "2.0") };
        var settings = Settings(destinations: new() { ["package-a"] = new DestinationSet(true, true, false) });
        var row = Plan(settings, [App("package-a", "2.0")], history)[0];
        Assert.Equal(["WSUS"], row.PublishTo);
        Assert.Equal("Check, Stage, Publish to WSUS", row.Planned);
    }

    [Fact]
    public void TheSiteVersionCountsAsPublishedToConfigMgrWithoutARecord()
    {
        var row = Plan(Settings("MECM"), [App("package-a", "2.0", current: "2.0")])[0];
        Assert.Empty(row.PublishTo);
        Assert.Equal("ConfigMgr: 2.0 already published", row.Reason);
        Assert.Equal("Check, Stage", row.Planned);
    }

    [Fact]
    public void ADestinationNotInUseIsDroppedAndNamed()
    {
        var row = Plan(Settings("MECMAndWSUS", readiness: Ready(["ConfigMgr"])), [App("package-a", "1.0")])[0];
        Assert.Equal(["ConfigMgr"], row.PublishTo);
        Assert.Equal("WSUS: WSUS is not in use", row.Reason);
    }

    [Fact]
    public void ARefusalOfTheSameVersionIsSkippedUnlessForcedAndANewerVersionIsTried()
    {
        var notSupported = new Dictionary<string, OneClickRefusalRecord>();
        OneClickHistory.SetNotSupported(notSupported, "WSUS", "2.0", "not supported: DetectionNotMappable", DateTimeOffset.Now);
        var entry = new OneClickHistoryEntry { LastKnownVersion = "2.0", NotSupported = notSupported };
        Assert.Equal("2.0", entry.RefusedVersion("WSUS"));
        Assert.Equal("not supported: DetectionNotMappable", entry.RefusedReason("WSUS"));
        Assert.Equal("", entry.RefusedReason("ConfigMgr"));
        var history = new Dictionary<string, OneClickHistoryEntry> { ["package-a"] = entry };

        var row = Plan(Settings("MECMAndWSUS"), [App("package-a", "2.0")], history)[0];
        Assert.Equal(["ConfigMgr"], row.PublishTo);
        Assert.Equal("WSUS: not supported", row.Reason);
        Assert.Equal(["ConfigMgr", "WSUS"], Plan(Settings("MECMAndWSUS"), [App("package-a", "2.0")], history, force: true)[0].PublishTo);
        Assert.Equal(["ConfigMgr", "WSUS"], Plan(Settings("MECMAndWSUS"), [App("package-a", "2.1")], history)[0].PublishTo);
    }

    [Fact]
    public void APublishClearsThatDestinationsRefusal()
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        var notSupported = new Dictionary<string, OneClickRefusalRecord>();
        OneClickHistory.SetNotSupported(notSupported, "WSUS", "2.0", "not supported: CustomInstall", DateTimeOffset.Now);
        OneClickHistory.SetPublished(published, notSupported, "WSUS", "2.0", "guid-1", new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
        Assert.Empty(notSupported);
        Assert.Equal("2026-02-03T04:05:06Z", published["WSUS"].At);
        Assert.Equal("guid-1", published["WSUS"].Id);
    }

    [Fact]
    public void AnUnknownVersionWaitsForTheCheck()
    {
        var row = Plan(Settings("MECM"), [App("package-a")])[0];
        Assert.Equal("Check, Stage, Publish to ConfigMgr", row.Planned);
        Assert.Equal("", row.Version);

        var blocked = Plan(Settings("MECMAndWSUS", readiness: Ready(["ConfigMgr", "Wsus"], wsusServer: "")), [App("package-a")],
            action: OneClickActionNames.StageAndPackage)[0];
        Assert.Equal("Check, Stage, Publish to ConfigMgr", blocked.Planned);
        var nothingReady = Plan(Settings("WSUSOnly", readiness: Ready(["Wsus"], wsusServer: "")), [App("package-a")])[0];
        Assert.Equal("Check, Stage, Publish after check", nothingReady.Planned);
    }

    [Fact]
    public void TheLastKnownVersionFillsARowWithoutOne()
    {
        var history = new Dictionary<string, OneClickHistoryEntry> { ["package-a"] = new() { LastKnownVersion = "5.5", LastChecked = new DateTime(2020, 1, 1) } };
        var row = Plan(Settings(), [App("package-a")], history, OneClickActionNames.Stage)[0];
        Assert.Equal("5.5", row.Version);
        Assert.Equal("Check, Stage", row.Planned);
    }

    [Fact]
    public void AReportRowInsideItsCadenceIsSkippedAndRunsWhenForcedOrOverdue()
    {
        var now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var history = new Dictionary<string, OneClickHistoryEntry> { ["package-a"] = new() { LastChecked = new DateTime(2026, 3, 8, 12, 0, 0, DateTimeKind.Utc) } };
        var row = Plan(Settings(), [App("package-a")], history, OneClickActionNames.Report, now: now)[0];
        Assert.False(row.Include);
        Assert.Equal("checked within the 7-day cadence", row.Reason);
        Assert.True(Plan(Settings(), [App("package-a")], history, OneClickActionNames.Report, force: true, now: now)[0].Include);
        Assert.True(Plan(Settings(defaultCadence: 1), [App("package-a")], history, OneClickActionNames.Report, now: now)[0].Include);
        Assert.True(Plan(Settings(cadence: new() { ["package-a"] = 1 }, defaultCadence: 30), [App("package-a")], history, OneClickActionNames.Report, now: now)[0].Include);
    }

    [Fact]
    public void TheCadenceDoesNotApplyToStageRuns()
    {
        var history = new Dictionary<string, OneClickHistoryEntry> { ["package-a"] = new() { LastChecked = DateTime.UtcNow } };
        Assert.True(Plan(Settings(), [App("package-a")], history, OneClickActionNames.Stage)[0].Include);
    }

    [Fact]
    public void TheCountLineCountsThePublishesPerDestinationAndTheSkippedRows()
    {
        var settings = Settings("MECMAndWSUS", ["package-a", "package-b", "package-c"], new() { ["package-c"] = new DestinationSet() });
        var plan = Plan(settings, [App("package-a", "1"), App("package-b", "1"), App("package-c", "1")]);
        var summary = OneClickPlanner.Summarize(plan, settings, OneClickActionNames.StageAndPackage);
        Assert.Equal("2 application(s), 1 skipped: 2 to ConfigMgr, 2 to WSUS, 0 to Intune", summary.Line);
        Assert.Equal(2, summary.Counts["WSUS"]);
        Assert.Empty(summary.Blocking);
    }

    [Fact]
    public void ADestinationThatRowsSelectButThatIsNotReadyIsNamed()
    {
        var settings = Settings("MECMAndWSUS", readiness: Ready(wsusServer: ""));
        var plan = Plan(settings, [App("package-a", "1")]);
        Assert.Equal(["WSUS: WSUS server not set (1 application(s) select it)"], OneClickPlanner.Summarize(plan, settings, OneClickActionNames.StageAndPackage).Blocking);
    }

    [Fact]
    public void AReportRunSaysCheckOnly()
    {
        var plan = Plan(Settings(tracked: ["package-a"]), [App("package-a")], action: OneClickActionNames.Report);
        Assert.Equal("1 application(s): check only, no publish", OneClickPlanner.Summarize(plan, Settings(), OneClickActionNames.Report).Line);
        Assert.Equal("Action: check only. Existing ConfigMgr application: skip.", OneClickPlanner.ActionLine("Report", "Skip"));
        Assert.Equal("Action: check, stage and publish. Existing ConfigMgr application: overwrite.", OneClickPlanner.ActionLine("StageAndPackage", "Overwrite"));
    }

    [Fact]
    public void TheRunGuardSkipsADestinationThatHasTheVersionTheCheckFound()
    {
        var decision = OneClickPlanner.Decide("StageAndPackage", ["ConfigMgr", "WSUS"], Published("WSUS", "3.0"), "3.0", "", false, true, false, true);
        Assert.Equal(["ConfigMgr"], decision.PublishTo);
        Assert.Equal("already published 3.0", decision.GuardResults["WSUS"]);
        Assert.True(decision.Stage);
    }

    [Fact]
    public void TheGuardRunShowsTheRefusalCode()
    {
        var notSupported = new Dictionary<string, OneClickRefusalRecord>();
        OneClickHistory.SetNotSupported(notSupported, "WSUS", "3.0", "not supported: CustomInstall", DateTimeOffset.Now);
        var entry = new OneClickHistoryEntry { LastKnownVersion = "3.0", NotSupported = notSupported, LastStaged = DateTime.Now };
        var decision = OneClickPlanner.Decide("StageAndPackage", ["WSUS"], entry, "3.0", "", false, false, false, true);
        Assert.Equal("not supported: CustomInstall", decision.GuardResults["WSUS"]);
        Assert.False(decision.Stage);
        Assert.Equal(["WSUS: not supported"], decision.SkipReasons);
        Assert.Equal("up to date", decision.SkipReason);

        var newerCheck = OneClickPlanner.Decide("StageAndPackage", ["WSUS"], entry, "3.0", "", false, versionChanged: true, false, true);
        Assert.True(newerCheck.Stage);
        Assert.Empty(newerCheck.PublishTo);

        var legacy = new OneClickHistoryEntry { NotSupported = new Dictionary<string, OneClickRefusalRecord> { ["WSUS"] = new() { Version = "3.0" } } };
        Assert.Equal("not supported 3.0", OneClickPlanner.Decide("StageAndPackage", ["WSUS"], legacy, "3.0", "", false, true, false, true).GuardResults["WSUS"]);
    }

    [Fact]
    public void AnUnchangedRowWithACurrentBuildIsSkippedAsUpToDate()
    {
        var decision = OneClickPlanner.Decide("Stage", [], new OneClickHistoryEntry { LastKnownVersion = "1.0" }, "1.0", "1.0", false, false, false, true);
        Assert.False(decision.Stage);
        Assert.Equal("up to date", decision.SkipReason);
        Assert.True(OneClickPlanner.Decide("Stage", [], null, "1.0", "", false, false, false, staged: false).Stage);
    }

    [Fact]
    public void ARowRestagedOnlyForAStaleBuildIsNotRepublished()
    {
        // The site carries the version and the build record is stale: restage, publish nowhere.
        var decision = OneClickPlanner.Decide("StageAndPackage", ["ConfigMgr"], null, "4.1", "4.1", false, false, buildStale: true, staged: true);
        Assert.True(decision.Stage);
        Assert.Empty(decision.PublishTo);
        Assert.Equal(["ConfigMgr: 4.1 already published"], decision.SkipReasons);
    }

    [Fact]
    public void ForcePublishesAgainWhateverTheHistory()
    {
        var decision = OneClickPlanner.Decide("StageAndPackage", ["ConfigMgr", "WSUS"], Published("WSUS", "3.0"), "3.0", "3.0", true, false, false, true);
        Assert.Equal(["ConfigMgr", "WSUS"], decision.PublishTo);
    }

    [Theory]
    [InlineData(1, 0, 0, "Published", "Published")]
    [InlineData(1, 1, 0, "Failed", "Published, partial")]
    [InlineData(0, 1, 0, "Failed", "Publish error")]
    [InlineData(0, 0, 1, "Not supported", "WSUS: not supported")]
    [InlineData(0, 0, 0, "Skipped", "Skipped")]
    public void TheRowOutcomeFollowsThePublishCounts(int published, int failed, int notSupported, string outcome, string status) =>
        Assert.Equal((outcome, status), OneClickPlanner.Conclude(published, failed, notSupported));

    [Fact]
    public void TheWsusCellCarriesOnlyTheCodeAndAReusedUpdateIsNotANewPublish()
    {
        var refused = new WsusStepResult(WsusStepStatus.Refused, "not supported: CustomInstall. This application cannot be published to WSUS: CustomInstall: steps", ["CustomInstall"], []);
        var cell = OneClickPlanner.WsusResult(refused);
        Assert.Equal("not supported: CustomInstall", cell.Cell);
        Assert.Equal(refused.Message, cell.Detail);
        Assert.False(cell.Ok);
        Assert.True(OneClickPlanner.IsSkipOrRefusal(cell.Cell));

        var reused = OneClickPlanner.WsusResult(new WsusStepResult(WsusStepStatus.AlreadyPublished, "already", [], [], "id-1"));
        Assert.Equal(("already on the server id-1", "id-1", true, true), (reused.Cell, reused.Id, reused.Ok, reused.AlreadyPresent));
        Assert.Equal("published id-2", OneClickPlanner.WsusResult(new WsusStepResult(WsusStepStatus.Published, "p", [], [], "id-2")).Cell);
        Assert.Equal("failed: server down", OneClickPlanner.WsusResult(new WsusStepResult(WsusStepStatus.Failed, "server down", [], [])).Cell);
    }
}
