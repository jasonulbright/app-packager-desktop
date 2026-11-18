using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>
/// The supersedence-only pass of One Click, ported from the cases of the PowerShell app (Tests\OneClickSupersedence.Tests.ps1 and the
/// "Supersedence-only pass of One Click" cases of Packagers\AppPackagerOneClick.Tests.ps1): the pass for one application (against fakes
/// for the manifest, the plan and the step), the pending state of a publish, the plan, the count line and the report. The run of a row
/// is in <see cref="OneClickSupersedenceRunTests"/>.
/// </summary>
public sealed class OneClickSupersedencePassTests : IDisposable
{
    private const string Marker = "APD-Line:catalog:package-a|default|x64";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-oneclick-pass-" + Guid.NewGuid().ToString("N"));

    public OneClickSupersedencePassTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ---- the pass for one application ----

    private static SupersedencePlan PlanOf(SupersedenceMode mode = SupersedenceMode.Replace, string? marker = Marker, string? refusal = null,
        params string[] log) =>
        new(new StageManifest(), new StageManifest(), marker, refusal,
            log.Length > 0 ? log : ["Supersedence mode           : Replace (site default)"], mode, VersionRule.Minimum);

    private static SupersedenceStepResult Step(SupersedenceStepStatus status = SupersedenceStepStatus.Applied,
        string message = "App A 2.0 supersedes App A 1.0", params string[] warnings) => new(status, message, warnings);

    /// <summary>The environment of the pass: a stage manifest on disk, a plan, a step result, and a record of what was asked.</summary>
    private sealed class Fakes
    {
        public string? ManifestPath { get; set; }
        public SupersedencePlan Plan { get; set; } = PlanOf();
        public Func<SupersedenceStepResult> StepResult { get; set; } = () => Step();
        public Exception? ReadFailure { get; set; }
        public List<(string Name, string Version, string Marker, SupersedenceMode Mode)> StepCalls { get; } = [];
        public StageManifest? PlannedManifest { get; private set; }
        public string? PlannedFolder { get; private set; }
        public int PlanCalls { get; private set; }

        public OneClickSupersedencePassSteps Steps => new(
            _ => ReadFailure is null ? new StageManifest { AppName = "App A", SoftwareVersion = "2.0", ApplicationId = "catalog:package-a" } : throw ReadFailure,
            (manifest, folder) => { PlanCalls++; PlannedManifest = manifest; PlannedFolder = folder; return Plan; },
            (name, version, marker, mode, _, _) =>
            {
                StepCalls.Add((name, version, marker, mode));
                return Task.FromResult(StepResult());
            });
    }

    private Fakes WithManifest() => new() { ManifestPath = WriteManifest() };

    private string WriteManifest()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(folder, "stage-manifest.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static Task<OneClickSupersedencePassResult> Run(Fakes f, string site = "MCM", CancellationToken ct = default) =>
        OneClickSupersedencePass.RunAsync(f.ManifestPath, "App A 2.0", "2.0", site, f.Steps, null, ct);

    [Fact]
    public async Task ThePassRunsTheSupersedenceStepForTheApplicationThatIsOnTheSiteAndReturnsItsResult()
    {
        var f = WithManifest();

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.Applied, r.Status);
        Assert.Equal("App A 2.0 supersedes App A 1.0", r.Message);
        Assert.Equal(("App A 2.0", "2.0", Marker, SupersedenceMode.Replace), Assert.Single(f.StepCalls));
        Assert.Equal(Path.GetDirectoryName(f.ManifestPath), f.PlannedFolder);
    }

    [Fact]
    public void ThePassPassesTheIdentityOfTheLineOfThePackagerWhenTheManifestHasNoApplicationIdentifier()
    {
        // The service passes the definition identifier as the fallback; the planner uses it when the manifest has none.
        var manifest = new StageManifest { AppName = "App A", SoftwareVersion = "2.0" };
        var settings = new SupersedenceSettings { Mode = "Replace" };

        var plan = SupersedencePlanner.Plan(manifest, settings, null, true, false, _ => null, "catalog:package-a");

        Assert.Equal(Marker, plan.Marker);
        Assert.Equal(SupersedenceMode.Replace, plan.Mode);
        Assert.Null(SupersedencePlanner.Plan(manifest, settings, null, true, false, _ => null).Marker);
    }

    [Fact]
    public async Task ThePassResolvesTheModeLikeAPublishAndReturnsTheLinesOfThePlan()
    {
        var f = WithManifest();
        f.Plan = PlanOf(SupersedenceMode.ReplaceAndUninstall, Marker, null,
            "Version rule (ConfigMgr)    : Exact (title override)", "Supersedence mode           : Replace and uninstall (title override)");

        var r = await Run(f);

        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(SupersedenceMode.ReplaceAndUninstall, Assert.Single(f.StepCalls).Mode);
    }

    [Fact]
    public async Task AModeOfOffDoesNothingAndAsksNothingOfTheSite()
    {
        var f = WithManifest();
        f.Plan = PlanOf(SupersedenceMode.Off);

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NotRun, r.Status);
        Assert.Empty(f.StepCalls);
    }

    [Fact]
    public async Task ATitleWithoutAnIdentityMarkerDoesNothing()
    {
        var f = WithManifest();
        f.Plan = PlanOf(marker: null);

        Assert.Equal(SupersedenceStepStatus.NotRun, (await Run(f)).Status);
        Assert.Empty(f.StepCalls);
    }

    [Fact]
    public async Task ARefusedPlanSuchAsTheBlockedCombinationIsReportedAndWritesNothing()
    {
        var f = WithManifest();
        f.Plan = PlanOf(refusal: "Replace and uninstall needs the version rule Exact.");

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NotApplied, r.Status);
        Assert.Contains("needs the version rule Exact", r.Message);
        Assert.Empty(f.StepCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public async Task AMissingStageManifestIsReportedAndAsksNothingOfTheSite(string? path)
    {
        var f = new Fakes { ManifestPath = path == "missing" ? Path.Combine(_root, "none", "stage-manifest.json") : path };

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NotApplied, r.Status);
        Assert.Contains("stage-manifest.json not found", r.Message);
        Assert.Equal(0, f.PlanCalls);
        Assert.Empty(f.StepCalls);
    }

    [Fact]
    public async Task AStageManifestThatCannotBeReadIsReportedAndAsksNothingOfTheSite()
    {
        var f = WithManifest();
        f.ReadFailure = new JsonException("bad token");

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NotApplied, r.Status);
        Assert.Contains("could not be read: bad token", r.Message);
        Assert.Empty(f.StepCalls);
    }

    [Fact]
    public async Task ASiteThatCannotBeReachedIsReported()
    {
        // The connection to the site is made by the script of the step: a site that cannot be reached comes back as a step that is
        // not applied. Without a site code the pass asks nothing of the site.
        var noSite = WithManifest();
        var r = await Run(noSite, site: " ");
        Assert.Equal(SupersedenceStepStatus.NotApplied, r.Status);
        Assert.Contains("ConfigMgr site", r.Message);
        Assert.Empty(noSite.StepCalls);

        var unreachable = WithManifest();
        unreachable.StepResult = () => Step(SupersedenceStepStatus.NotApplied, "Could not connect to the site MCM.");
        var failed = await Run(unreachable);
        Assert.Equal(SupersedenceStepStatus.NotApplied, failed.Status);
        Assert.Contains("Could not connect to the site MCM", failed.Message);
    }

    [Fact]
    public async Task AStepThatIsNotAppliedKeepsItsReasonAndItsWarnings()
    {
        var f = WithManifest();
        f.StepResult = () => Step(SupersedenceStepStatus.NotApplied, "The application 'App A 2.0' is not on the site. Publish it first.", "w1");

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NotApplied, r.Status);
        Assert.Contains("is not on the site", r.Message);
        Assert.Equal(["w1"], r.Warnings);
    }

    [Fact]
    public async Task ALineWithoutAnOlderVersionIsPassedOn()
    {
        var f = WithManifest();
        f.StepResult = () => Step(SupersedenceStepStatus.NoOlderVersion, "No older version found");

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NoOlderVersion, r.Status);
        Assert.True(r.EndsPending);
    }

    [Theory]
    [InlineData(SupersedenceStepStatus.Applied, true)]
    [InlineData(SupersedenceStepStatus.NoOlderVersion, true)]
    [InlineData(SupersedenceStepStatus.NotApplied, false)]
    [InlineData(SupersedenceStepStatus.NotRun, false)]
    public void OnlyAnAppliedStepOrALineWithoutAnOlderVersionEndsThePendingState(SupersedenceStepStatus status, bool ends) =>
        Assert.Equal(ends, OneClickSupersedencePassResult.Of(status, "").EndsPending);

    [Fact]
    public async Task ThePassNeverThrowsExceptForACancellation()
    {
        var f = WithManifest();
        f.StepResult = () => throw new InvalidOperationException("provider gone");

        var r = await Run(f);

        Assert.Equal(SupersedenceStepStatus.NotApplied, r.Status);
        Assert.Equal("provider gone", r.Message);

        using var cts = new CancellationTokenSource();
        f.StepResult = () => { cts.Cancel(); throw new OperationCanceledException(cts.Token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(f, ct: cts.Token));
    }

    [Fact]
    public void ThePassStagesNothingAndPublishesNothing()
    {
        // The pass names no stage, package or publish step, and the service runs the step with the published-only requirement.
        var pass = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerEngine", "Services", "OneClick", "OneClickSupersedencePass.cs"));
        Assert.DoesNotMatch(@"StageAsync|PackageAsync|PublishWsus|PublishToIntune|New-CMApplication|Set-CMApplication\b", pass);

        var service = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Services", "PackagerExecutionService.cs"));
        var start = service.IndexOf("RunSupersedencePassAsync(", StringComparison.Ordinal);
        var body = service[start..service.IndexOf("What the \"Apply supersedence\" action did", start, StringComparison.Ordinal)];
        Assert.Contains("requirePublished: true", body);
        Assert.DoesNotMatch(@"StageAsync|PackageAsync|PublishWsus|PackageStagedManifestAsync|BuildAndPublishAsync", body);
    }

    // ---- the pending state of a publish ----

    private static Dictionary<string, OneClickPublishRecord> PendingRecords(string version = "2.0")
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "ConfigMgr", version, "App A 2.0",
            new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), supersedenceNotApplied: true, supersedenceReason: "access denied");
        return published;
    }

    private static OneClickHistoryEntry EntryOf(Dictionary<string, OneClickPublishRecord> published) => new() { Published = published };

    [Fact]
    public void APublishWhoseStepFailedKeepsTheVersionAsPublishedAndMarksItPendingWithTheReason()
    {
        var entry = EntryOf(PendingRecords());

        Assert.Equal("2.0", entry.PublishedVersion("ConfigMgr"));
        Assert.Equal("App A 2.0", entry.Published["ConfigMgr"].Id);
        Assert.True(entry.SupersedencePending("ConfigMgr"));
        Assert.Equal("access denied", entry.Published["ConfigMgr"].SupersedenceReason);
    }

    [Fact]
    public void APublishWhoseStepSucceededIsNotPending()
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "ConfigMgr", "2.0", "App A 2.0", DateTimeOffset.Now);

        Assert.False(EntryOf(published).SupersedencePending("ConfigMgr"));
    }

    [Fact]
    public void PublishingTheVersionAgainClearsThePendingState()
    {
        var published = PendingRecords();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "ConfigMgr", "2.0", "App A 2.0", DateTimeOffset.Now);

        Assert.False(EntryOf(published).SupersedencePending("ConfigMgr"));
        Assert.Equal("", published["ConfigMgr"].SupersedenceReason);
    }

    [Fact]
    public void WsusAndIntuneAreNeverMarkedPending()
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        var refused = new Dictionary<string, OneClickRefusalRecord>();
        OneClickHistory.SetPublished(published, refused, "WSUS", "2.0", "", DateTimeOffset.Now, supersedenceNotApplied: true);
        OneClickHistory.SetPublished(published, refused, "Intune", "2.0", "", DateTimeOffset.Now, supersedenceNotApplied: true);

        var entry = EntryOf(published);
        Assert.False(entry.SupersedencePending("WSUS"));
        Assert.False(entry.SupersedencePending("Intune"));
        Assert.False(published["WSUS"].SupersedencePending);
    }

    [Fact]
    public void ThePendingStateSurvivesAJsonRoundTrip()
    {
        var json = JsonSerializer.Serialize(PendingRecords());
        var back = JsonSerializer.Deserialize<Dictionary<string, OneClickPublishRecord>>(json)!;

        Assert.True(EntryOf(back).SupersedencePending("ConfigMgr"));
        Assert.Equal("access denied", back["ConfigMgr"].SupersedenceReason);
    }

    [Fact]
    public void EndingThePendingStateKeepsTheRestOfTheRecord()
    {
        var published = PendingRecords();

        OneClickHistory.ClearSupersedencePending(published, "ConfigMgr");

        var record = published["ConfigMgr"];
        Assert.False(record.SupersedencePending);
        Assert.Equal("", record.SupersedenceReason);
        Assert.Equal(("2.0", "App A 2.0", "2026-11-18T10:00:00Z"), (record.Version, record.Id, record.At));
    }

    [Fact]
    public void EndingThePendingStateOfARecordReadFromJson()
    {
        var back = JsonSerializer.Deserialize<Dictionary<string, OneClickPublishRecord>>(JsonSerializer.Serialize(PendingRecords()))!;

        OneClickHistory.ClearSupersedencePending(back, "ConfigMgr");

        Assert.False(EntryOf(back).SupersedencePending("ConfigMgr"));
    }

    [Fact]
    public void EndingThePendingStateDoesNothingForAnApplicationWithoutHistoryOrWithoutAConfigMgrRecord()
    {
        OneClickHistory.ClearSupersedencePending(new Dictionary<string, OneClickPublishRecord>(), "ConfigMgr");

        var published = new Dictionary<string, OneClickPublishRecord>();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "WSUS", "1.0", "", DateTimeOffset.Now);
        OneClickHistory.ClearSupersedencePending(published, "ConfigMgr");

        Assert.Equal("1.0", published["WSUS"].Version);
    }

    [Theory]
    [InlineData(InventoryOutcome.PublishedSupersedenceNotApplied, "2.0", "2.0", true)]
    [InlineData(InventoryOutcome.Succeeded, "2.0", "2.0", false)]
    [InlineData(InventoryOutcome.Failed, "2.0", "2.0", false)]
    [InlineData(InventoryOutcome.Partial, "2.0", "2.0", false)]
    [InlineData(InventoryOutcome.PublishedSupersedenceNotApplied, "2.1", "2.0", false)]
    [InlineData(InventoryOutcome.PublishedSupersedenceNotApplied, "2.0", null, false)]
    public void TheInventoryRecordIsPendingOnlyForTheVersionWhoseStepFailed(InventoryOutcome attempt, string attemptVersion, string? successVersion, bool pending) =>
        Assert.Equal(pending, OneClickHistory.IsSupersedencePending(attempt, attemptVersion, successVersion));

    // ---- the reason of a publish whose step failed ----

    [Fact]
    public void TheReasonIsReadFromTheDetailOfAPublishThatSucceeded()
    {
        var detail = PackagingOutcomeComposer.ConfigMgrDetail(null, null, null, "access denied");

        Assert.Equal(InventoryOutcome.PublishedSupersedenceNotApplied, PackagingOutcomeComposer.ConfigMgrInventoryOutcome(true, true, false, null, "access denied"));
        Assert.Equal("access denied", OneClickHistory.SupersedenceReasonOf(detail));
    }

    [Fact]
    public void AFailedContentStepOutranksAPendingSupersedence()
    {
        Assert.Equal(InventoryOutcome.Partial, PackagingOutcomeComposer.ConfigMgrInventoryOutcome(true, true, false, "distribution failed", "access denied"));
        Assert.Equal("", OneClickHistory.SupersedenceReasonOf(PackagingOutcomeComposer.ConfigMgrDetail(null, "distribution failed", null, "access denied")));
    }

    [Fact]
    public void ARunWithoutAFailedStepGivesNoReason()
    {
        Assert.Equal("", OneClickHistory.SupersedenceReasonOf("Supersedence                 : App A 2.0 supersedes App A 1.0"));
        Assert.Equal("", OneClickHistory.SupersedenceReasonOf(""));
        Assert.Equal("", OneClickHistory.SupersedenceReasonOf(null));
    }

    [Fact]
    public void ALineThatGivesNoReasonSaysThatTheSiteGaveNone()
    {
        Assert.Equal("the site gave no reason.", OneClickHistory.SupersedenceReasonOf("Published, supersedence not applied:"));
        Assert.Equal("the site gave no reason.", SupersedenceScripts.FailureText("", ""));
    }

    // ---- the plan ----

    private static OneClickApp App(string key, string latest) => new(key, key, "Vendor", latest, "");

    private static Dictionary<string, OneClickHistoryEntry> Pending(string version = "2.0") =>
        new() { ["package-a"] = EntryOf(PendingRecords(version)) };

    private static IReadOnlyList<OneClickPlanRow> Plan(OneClickPlanSettings settings, string latest, Dictionary<string, OneClickHistoryEntry>? history,
        string action = OneClickActionNames.StageAndPackage, bool force = false) =>
        OneClickPlanner.Plan([App("package-a", latest)], settings, history ?? new(), action, force, DateTime.UtcNow);

    private static OneClickPlanSettings Settings(string target = "MECM", PublishReadiness? readiness = null) =>
        OneClickPlannerTests.Settings(target, ["package-a"], readiness: readiness);

    [Fact]
    public void AVersionOnTheSiteWithItsStepPendingPlansThePassAndNoPublish()
    {
        var row = Plan(Settings(), "2.0", Pending())[0];

        Assert.True(row.SupersedenceOnly);
        Assert.Empty(row.PublishTo);
        Assert.Equal("Check, Supersedence", row.Planned);
        Assert.Equal("ConfigMgr: 2.0 already published, supersedence pending", row.Reason);
        Assert.True(row.Include);
    }

    [Fact]
    public void TheOtherDestinationsArePublishedAndThePassRunsInTheSameRow()
    {
        var row = Plan(Settings("MECMAndWSUS"), "2.0", Pending())[0];

        Assert.True(row.SupersedenceOnly);
        Assert.Equal(["WSUS"], row.PublishTo);
        Assert.Equal("Check, Stage, Publish to WSUS, Supersedence", row.Planned);
    }

    [Fact]
    public void ANewerVersionIsPublishedInFullAndRunsNoPass()
    {
        var row = Plan(Settings(), "2.1", Pending())[0];

        Assert.False(row.SupersedenceOnly);
        Assert.Equal(["ConfigMgr"], row.PublishTo);
    }

    [Fact]
    public void ForcePublishesAgainInsteadOfRunningThePass()
    {
        var row = Plan(Settings(), "2.0", Pending(), force: true)[0];

        Assert.False(row.SupersedenceOnly);
        Assert.Equal(["ConfigMgr"], row.PublishTo);
    }

    [Fact]
    public void AVersionWithoutAPendingStepRunsNoPass()
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "ConfigMgr", "2.0", "App A 2.0", DateTimeOffset.Now);

        var row = Plan(Settings(), "2.0", new() { ["package-a"] = EntryOf(published) })[0];

        Assert.False(row.SupersedenceOnly);
        Assert.Equal("ConfigMgr: 2.0 already published", row.Reason);
    }

    [Fact]
    public void ThePassIsPlannedOnlyInARunThatPublishes()
    {
        var stage = Plan(Settings(), "2.0", Pending(), OneClickActionNames.Stage)[0];
        Assert.False(stage.SupersedenceOnly);
        Assert.Equal("Check, Stage", stage.Planned);

        Assert.False(Plan(Settings(), "2.0", Pending(), OneClickActionNames.Report, force: true)[0].SupersedenceOnly);
    }

    [Fact]
    public void AnUnreadyDestinationPlansNoPass()
    {
        var row = Plan(Settings(readiness: OneClickPlannerTests.Ready(siteCode: "")), "2.0", Pending())[0];

        Assert.False(row.SupersedenceOnly);
        Assert.Contains("ConfigMgr", row.NotReady.Keys);
    }

    [Fact]
    public void ThePlanDoesNotCheckTheSupersedenceModeSoAModeOfOffKeepsThePendingState()
    {
        // The plan has no input for the mode: the pass is planned, and the pass itself reports NotRun when the mode is Off.
        Assert.DoesNotContain(typeof(OneClickPlanSettings).GetProperties(), p => p.Name.Contains("Supersedence", StringComparison.Ordinal));
        Assert.True(Plan(Settings(), "2.0", Pending())[0].SupersedenceOnly);
    }

    [Fact]
    public void TheCountLineNamesThePasses()
    {
        var plan = Plan(Settings(), "2.0", Pending());
        var line = OneClickPlanner.Summarize(plan, Settings(), OneClickActionNames.StageAndPackage).Line;
        Assert.EndsWith(", 1 supersedence only", line);

        var none = Plan(Settings(), "2.0", null);
        Assert.DoesNotContain("supersedence", OneClickPlanner.Summarize(none, Settings(), OneClickActionNames.StageAndPackage).Line);

        // A count line of a run that publishes nothing has no passes to name.
        Assert.DoesNotContain("supersedence", OneClickPlanner.Summarize(plan, Settings(), OneClickActionNames.Stage).Line);
    }

    [Fact]
    public void ARowThatDoesNotCountBecauseTheOperatorLeftItOutIsNotNamedInTheCountLine()
    {
        var plan = Plan(Settings(), "2.0", Pending());

        Assert.DoesNotContain("supersedence", OneClickPlanner.Summarize(plan, Settings(), OneClickActionNames.StageAndPackage, _ => false).Line);
    }

    // ---- the report ----

    private static OneClickReportRun Run() => new()
    {
        Started = new DateTime(2026, 4, 5, 10, 0, 0), Ended = new DateTime(2026, 4, 5, 10, 1, 0), Operator = "o", Computer = "c", Action = "Stage and Publish"
    };

    private static OneClickReportRow ReportRow(string app, string version, string outcome, string cm, string id = "", string detail = "") => new()
    {
        Key = "package-" + app.Last().ToString().ToLowerInvariant(), Application = app, Version = version, Outcome = outcome,
        Results = new Dictionary<string, string> { ["ConfigMgr"] = cm },
        Ids = new Dictionary<string, string> { ["ConfigMgr"] = id },
        Details = new Dictionary<string, string> { ["ConfigMgr"] = detail }
    };

    [Fact]
    public void TheReportCountsAPassAndAPublishWhoseStepFailedAndKeepsTheRowAsPublished()
    {
        var rows = new[]
        {
            ReportRow("App A", "2.0", "Published", "published App A 2.0, supersedence not applied", "App A 2.0", "Published, supersedence not applied: access denied"),
            ReportRow("App B", "1.1", "Superseded", "supersedence applied: App B 1.1 supersedes App B 1.0", "App B 1.1")
        };

        var md = OneClickReportWriter.Markdown(Run(), rows);

        Assert.Contains("| Published | 1 |", md);
        Assert.Contains("| Supersedence applied | 1 |", md);
        Assert.Contains("| Supersedence not applied | 1 |", md);
        Assert.Contains("- App A, ConfigMgr: Published, supersedence not applied: access denied", md);
        // The counts come in the order of the table: published, not supported, skipped, the two new lines, failed.
        Assert.True(md.IndexOf("| Skipped |", StringComparison.Ordinal) < md.IndexOf("| Supersedence applied |", StringComparison.Ordinal));
        Assert.True(md.IndexOf("| Supersedence not applied |", StringComparison.Ordinal) < md.IndexOf("| Failed |", StringComparison.Ordinal));
    }

    [Fact]
    public void AReportWithNeitherLeavesTheTwoLinesOut()
    {
        var md = OneClickReportWriter.Markdown(Run(), [ReportRow("App A", "2.0", "Published", "published App A 2.0")]);

        Assert.DoesNotContain("Supersedence", md);
    }
}
