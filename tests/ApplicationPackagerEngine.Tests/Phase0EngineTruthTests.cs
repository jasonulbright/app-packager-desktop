using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Overhaul V1 phase 0 (Q-126): the engine says what the V1 workspace will show. The combined publish path,
/// the per-destination result cells, the readiness gate of Publish All, the status tab of a row,
/// the hidden-selection counts and the stored "Not found in ConfigMgr".
/// </summary>
public sealed class Phase0EngineTruthTests
{
    private static PackagerExecutionResult Ok(string target) => new()
    {
        ExitCode = 0,
        ConfigMgrOutcome = DestinationSet.FromTarget(target).ConfigMgr ? InventoryOutcome.Succeeded : null,
        IntuneOutcome = DestinationSet.FromTarget(target).Intune ? InventoryOutcome.Succeeded : null
    };

    private static WsusRowRun WsusOk() => new(new PackagerExecutionResult { ExitCode = 0 },
        new WsusStepResult(WsusStepStatus.Published, "Published", [], [], "pkg-1"),
        PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: new WsusStepResult(WsusStepStatus.Published, "Published", [], [], "pkg-1")));

    private sealed class Calls
    {
        public List<string> Package { get; } = [];
        public int Wsus { get; set; }
    }

    private static Task<PublishRowRun> Run(string target, Calls calls,
        Func<string, PackagerExecutionResult>? package = null, Func<WsusRowRun>? wsus = null, CancellationToken ct = default) =>
        PublishRowRunner.RunAsync("uid-1", "1.2.3", target,
            t => { calls.Package.Add(t); return Task.FromResult((package ?? Ok)(t)); },
            () => { calls.Wsus++; return Task.FromResult((wsus ?? WsusOk)()); },
            _ => { }, ct);

    [Theory]
    [InlineData(PublishRunTargets.ConfigMgr, "ConfigMgr", 0)]
    [InlineData(PublishRunTargets.IntuneOnly, "Intune", 0)]
    [InlineData(PublishRunTargets.WsusOnly, "", 1)]
    [InlineData(PublishRunTargets.ConfigMgrAndIntune, "ConfigMgrAndIntune", 0)]
    [InlineData(PublishRunTargets.ConfigMgrAndWsus, "ConfigMgr", 1)]
    [InlineData(PublishRunTargets.IntuneAndWsus, "Intune", 1)]
    [InlineData(PublishRunTargets.ConfigMgrAndIntuneAndWsus, "ConfigMgrAndIntune", 1)]
    public async Task EachDestinationOfEachTargetRunsOnceAndLeavesOneCell(string target, string packageTarget, int wsusRuns)
    {
        var calls = new Calls();
        var run = await Run(target, calls);

        var set = DestinationSet.FromTarget(target);
        Assert.Equal(wsusRuns, calls.Wsus);
        Assert.Equal(packageTarget.Length == 0 ? 0 : 1, calls.Package.Count);
        if (calls.Package.Count == 1)
        {
            // The package call never carries WSUS: that target is refused on that path.
            Assert.False(DeploymentTargets.PublishesToWsus(calls.Package[0]));
            Assert.Equal(set.ConfigMgr, DestinationSet.FromTarget(calls.Package[0]).ConfigMgr);
            Assert.Equal(set.Intune, DestinationSet.FromTarget(calls.Package[0]).Intune);
        }
        Assert.Equal(set.Names.Count, run.Result.Destinations.Count);
        Assert.All(set.Names, name => Assert.Equal(PublishDestinationKind.Published, run.Result.For(name)!.Kind));
        Assert.True(run.Result.Succeeded);
    }

    [Fact]
    public async Task AWsusRefusalFollowsConfigMgrAndIntuneNoLongerRefusesTheWholeRun()
    {
        var calls = new Calls();
        var run = await Run(PublishRunTargets.ConfigMgrAndIntuneAndWsus, calls);
        Assert.Equal(["ConfigMgr", "Intune", "WSUS"], run.Result.Destinations.Select(d => d.Destination));
        Assert.Equal(PublishRunTargets.ConfigMgrAndIntune, calls.Package.Single());
    }

    [Fact]
    public async Task APartialFailureKeepsThePublishedCellsAndTheNextDestinationStillRuns()
    {
        var calls = new Calls();
        var run = await Run(PublishRunTargets.ConfigMgrAndIntuneAndWsus, calls, package: t => new PackagerExecutionResult
        {
            ExitCode = 1,
            ConfigMgrOutcome = InventoryOutcome.Succeeded,
            IntuneOutcome = InventoryOutcome.Partial,
            IntuneDetail = "content upload failed"
        });

        Assert.Equal(1, calls.Wsus);
        Assert.Equal(PublishDestinationKind.Published, run.Result.For("ConfigMgr")!.Kind);
        var intune = run.Result.For("Intune")!;
        Assert.Equal(PublishDestinationKind.Failed, intune.Kind);
        Assert.Equal("Failed at content upload", intune.Text);
        Assert.Equal(PublishDestinationKind.Published, run.Result.For("WSUS")!.Kind);
        Assert.True(run.Result.Partial);
        Assert.False(run.Result.Succeeded);
    }

    [Fact]
    public async Task AWsusFailureLeavesTheConfigMgrCellPublished()
    {
        var calls = new Calls();
        var failed = new WsusStepResult(WsusStepStatus.Failed, "server not reachable", [], []);
        var run = await Run(PublishRunTargets.ConfigMgrAndWsus, calls,
            wsus: () => new WsusRowRun(new PackagerExecutionResult { ExitCode = 0 }, failed, PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: failed)));

        Assert.Equal(PublishDestinationKind.Published, run.Result.For("ConfigMgr")!.Kind);
        Assert.Equal(PublishDestinationKind.Failed, run.Result.For("WSUS")!.Kind);
        Assert.True(run.Result.Partial);
    }

    [Fact]
    public async Task AFailedWsusStageIsAFailedCellNotAMissingOne()
    {
        var calls = new Calls();
        var run = await Run(PublishRunTargets.WsusOnly, calls,
            wsus: () => new WsusRowRun(new PackagerExecutionResult { ExitCode = 1, StdErr = "Stage manifest not found" }, null, null));

        var cell = run.Result.For("WSUS")!;
        Assert.Equal(PublishDestinationKind.Failed, cell.Kind);
        Assert.Equal("Failed at stage", cell.Text);
    }

    [Fact]
    public async Task AThrownPackageCallFailsItsCellsAndWsusStillRuns()
    {
        var calls = new Calls();
        var run = await PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.ConfigMgrAndWsus,
            _ => throw new InvalidOperationException("share unreachable"),
            () => { calls.Wsus++; return Task.FromResult(WsusOk()); }, _ => { }, default);

        Assert.Null(run.Package);
        Assert.Equal(PublishDestinationKind.Failed, run.Result.For("ConfigMgr")!.Kind);
        Assert.Equal("share unreachable", run.Result.For("ConfigMgr")!.Message);
        Assert.Equal(1, calls.Wsus);
    }

    [Fact]
    public async Task AThrownWsusStepIsAWsusFailureEvenWithoutAWsusRun()
    {
        var run = await PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.ConfigMgrAndWsus,
            _ => Task.FromResult(Ok(PublishRunTargets.ConfigMgr)),
            () => throw new InvalidOperationException("the build is gone"), _ => { }, default);
        Assert.Null(run.Wsus);
        Assert.True(run.WsusFailed);
        Assert.Equal("the build is gone", run.Result.For("WSUS")!.Message);
        Assert.False((await Run(PublishRunTargets.ConfigMgrAndWsus, new Calls())).WsusFailed);
    }

    [Fact]
    public async Task ACancelledRunStopsBeforeWsus()
    {
        using var cts = new CancellationTokenSource();
        var calls = new Calls();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.ConfigMgrAndWsus,
            _ => { cts.Cancel(); return Task.FromResult(Ok(PublishRunTargets.ConfigMgr)); },
            () => { calls.Wsus++; return Task.FromResult(WsusOk()); }, _ => { }, cts.Token));
        Assert.Equal(0, calls.Wsus);
    }

    [Fact]
    public async Task ACancelledRunKeepsTheCellsOfWhatItAlreadyReached()
    {
        using var cts = new CancellationTokenSource();
        var cells = new List<PublishDestinationResult>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.ConfigMgrAndWsus,
            _ => { cts.Cancel(); return Task.FromResult(Ok(PublishRunTargets.ConfigMgr)); },
            () => Task.FromResult(WsusOk()), _ => { }, cts.Token, null, cells));
        Assert.Equal(["ConfigMgr"], cells.Select(c => c.Destination));
        Assert.Equal(PublishDestinationKind.Published, cells[0].Kind);
    }

    [Fact]
    public async Task ACancelInsideThePackageCallLeavesCellsThatSayTheStateIsNotKnown()
    {
        using var cts = new CancellationTokenSource();
        var cells = new List<PublishDestinationResult>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.ConfigMgrAndIntune,
            _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); },
            () => Task.FromResult(WsusOk()), _ => { }, cts.Token, null, cells));
        Assert.Equal(["ConfigMgr", "Intune"], cells.Select(c => c.Destination));
        Assert.All(cells, c => Assert.Contains("Check the system.", c.Message));
    }

    [Fact]
    public async Task ATimeoutThatNobodyAskedForFailsTheCellsAndWsusStillRuns()
    {
        var calls = new Calls();
        var run = await PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.IntuneAndWsus,
            _ => throw new TaskCanceledException("the request timed out"),
            () => { calls.Wsus++; return Task.FromResult(WsusOk()); }, _ => { }, default);
        Assert.Equal(PublishDestinationKind.Failed, run.Result.For("Intune")!.Kind);
        Assert.Equal(1, calls.Wsus);
    }

    [Fact]
    public async Task ARunStoppedAfterThePackageStepSkipsWsusWithACell()
    {
        var calls = new Calls();
        var run = await PublishRowRunner.RunAsync("uid-1", "1", PublishRunTargets.ConfigMgrAndWsus,
            _ => Task.FromResult(Ok(PublishRunTargets.ConfigMgr)),
            () => { calls.Wsus++; return Task.FromResult(WsusOk()); }, _ => { }, default, () => true);
        Assert.Equal(0, calls.Wsus);
        Assert.Equal(PublishDestinationKind.Skipped, run.Result.For("WSUS")!.Kind);
    }

    [Fact]
    public void AKeptExistingApplicationIsASkippedCell()
    {
        var result = new PackagerExecutionResult { ExitCode = 0, Conflict = new ExistingApplicationConflict("Notepad++", "8.6", "8.7") };
        var cell = PublishDestinationResult.FromConfigMgr(result);
        Assert.Equal(PublishDestinationKind.Skipped, cell.Kind);
        Assert.Contains("kept", cell.Message);
    }

    [Theory]
    [InlineData(InventoryOutcome.Succeeded, PublishDestinationKind.Published)]
    [InlineData(InventoryOutcome.PublishedSupersedenceNotApplied, PublishDestinationKind.Published)]
    [InlineData(InventoryOutcome.AlreadyPresent, PublishDestinationKind.Published)]
    [InlineData(InventoryOutcome.Partial, PublishDestinationKind.Failed)]
    [InlineData(InventoryOutcome.Failed, PublishDestinationKind.Failed)]
    [InlineData(InventoryOutcome.Skipped, PublishDestinationKind.Skipped)]
    [InlineData(InventoryOutcome.Refused, PublishDestinationKind.NotSupported)]
    public void EveryConfigMgrOutcomeMapsToACell(InventoryOutcome outcome, PublishDestinationKind expected) =>
        Assert.Equal(expected, PublishDestinationResult.FromConfigMgr(new PackagerExecutionResult { ExitCode = outcome == InventoryOutcome.Failed ? 1 : 0, ConfigMgrOutcome = outcome }).Kind);

    [Theory]
    [InlineData(WsusStepStatus.Published, PublishDestinationKind.Published)]
    [InlineData(WsusStepStatus.AlreadyPublished, PublishDestinationKind.Published)]
    [InlineData(WsusStepStatus.Refused, PublishDestinationKind.NotSupported)]
    [InlineData(WsusStepStatus.Failed, PublishDestinationKind.Failed)]
    [InlineData(WsusStepStatus.NotRun, PublishDestinationKind.Skipped)]
    public void EveryWsusStatusMapsToACell(WsusStepStatus status, PublishDestinationKind expected) =>
        Assert.Equal(expected, PublishDestinationResult.FromWsus(new WsusStepResult(status, "m", [], [])).Kind);

    [Fact]
    public void PackageTargetDropsOnlyWsus()
    {
        Assert.Null(PublishRowRunner.PackageTarget(PublishRunTargets.WsusOnly));
        Assert.Equal(PublishRunTargets.ConfigMgr, PublishRowRunner.PackageTarget(PublishRunTargets.ConfigMgrAndWsus));
        Assert.Equal(PublishRunTargets.IntuneOnly, PublishRowRunner.PackageTarget(PublishRunTargets.IntuneAndWsus));
        Assert.Equal(PublishRunTargets.ConfigMgrAndIntune, PublishRowRunner.PackageTarget(PublishRunTargets.ConfigMgrAndIntuneAndWsus));
        Assert.Equal(PublishRunTargets.ConfigMgrAndIntune, PublishRowRunner.PackageTarget(PublishRunTargets.ConfigMgrAndIntune));
    }

    // Publish All and readiness

    private static PublishReadiness Ready(bool configMgr, bool intune, bool wsus, bool console = true, bool creds = true, bool server = true) =>
        new(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }, console, "MCM", "\\\\cm01\\share", creds ? "t" : "", creds ? "c" : "", creds, server ? "wsus01" : "");

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void PublishAllIsEnabledWhenEverySystemInUseIsReady(bool configMgr, bool intune, bool wsus)
    {
        var (enabled, _, target) = PublishAllState.For(Ready(configMgr, intune, wsus));
        Assert.True(enabled);
        Assert.Equal(new DestinationSet(configMgr, wsus, intune).ToTargetName(), target);
    }

    [Fact]
    public void ASystemInUseThatIsNotReadyBlocksPublishAllAndNamesTheReason()
    {
        var (enabled, tip, target) = PublishAllState.For(Ready(true, true, true, creds: false));
        Assert.False(enabled);
        Assert.Equal(PublishRunTargets.ConfigMgrAndIntuneAndWsus, target);
        Assert.Contains("Intune", tip);
        Assert.Contains("Intune credentials", tip);
        Assert.DoesNotContain("WSUS server", tip);

        var noConsole = PublishAllState.For(Ready(true, false, false, console: false));
        Assert.False(noConsole.Enabled);
        Assert.Contains("ConfigMgr Console", noConsole.ToolTip);

        var noServer = PublishAllState.For(Ready(false, false, true, server: false));
        Assert.False(noServer.Enabled);
        Assert.Contains("WSUS server", noServer.ToolTip);
    }

    [Fact]
    public void ASystemThatIsOffNeverBlocksPublishAll()
    {
        // Intune is off and has no credentials: that does not matter.
        var state = PublishAllState.For(Ready(true, false, false, creds: false));
        Assert.True(state.Enabled);
    }

    [Fact]
    public void NoSystemInUseStillDisablesPublishAllWithReadiness()
    {
        var state = PublishAllState.For(Ready(false, false, false));
        Assert.False(state.Enabled);
        Assert.Contains("no system is in use", state.ToolTip);
    }

    [Fact]
    public void ARunGateOfACombinedTargetChecksEveryDestination()
    {
        var readiness = Ready(true, true, true, creds: false);
        Assert.Contains("Intune credentials", PublishActionState.RunGate(PublishRunTargets.ConfigMgrAndIntune, readiness));
        Assert.Contains("WSUS server", PublishActionState.RunGate(PublishRunTargets.ConfigMgrAndWsus, readiness with { WsusServerName = "" }));
        Assert.Null(PublishActionState.RunGate(PublishRunTargets.ConfigMgrAndWsus, readiness));
        var blocked = PublishActionState.BlockedDestinations(PublishRunTargets.ConfigMgrAndIntuneAndWsus, readiness with { WsusServerName = "" });
        Assert.Equal(["Intune", "WSUS"], blocked.Select(b => b.Destination));
    }

    // Status tabs

    [Theory]
    [InlineData("Update available", "1.0", PackagesTabKind.Update)]
    [InlineData("Update available (16 CVEs, 1 in KEV)", "1.0", PackagesTabKind.Update)]
    [InlineData("Absent from ConfigMgr", "", PackagesTabKind.NotFound)]
    [InlineData("Ready", "", PackagesTabKind.NotChecked)]
    [InlineData("Cached", "", PackagesTabKind.NotChecked)]
    [InlineData("Latest retrieved", "", PackagesTabKind.NotChecked)]
    [InlineData("", "", PackagesTabKind.NotChecked)]
    [InlineData("Ready", "1.0", PackagesTabKind.Other)]
    [InlineData("Cached", "1.0", PackagesTabKind.Other)]
    [InlineData("Up to date", "1.0", PackagesTabKind.UpToDate)]
    [InlineData("Current newer", "2.0", PackagesTabKind.Other)]
    [InlineData("ConfigMgr version retrieved", "1.0", PackagesTabKind.Other)]
    [InlineData("Staged", "", PackagesTabKind.Other)]
    [InlineData("Packaged", "1.0", PackagesTabKind.Other)]
    [InlineData("Stage error", "", PackagesTabKind.Other)]
    [InlineData("Publish error", "", PackagesTabKind.Other)]
    [InlineData("Error", "", PackagesTabKind.Other)]
    [InlineData("Cancelled", "", PackagesTabKind.Other)]
    [InlineData("Skipped", "", PackagesTabKind.Other)]
    [InlineData("Staging...", "", PackagesTabKind.Other)]
    [InlineData("Packaging...", "", PackagesTabKind.Other)]
    [InlineData("Publishing to WSUS...", "", PackagesTabKind.Other)]
    [InlineData("Querying ConfigMgr...", "", PackagesTabKind.Other)]
    [InlineData("Checking latest...", "", PackagesTabKind.Other)]
    [InlineData("Vendor older than last seen", "", PackagesTabKind.Other)]
    [InlineData("Unknown", "", PackagesTabKind.Other)]
    [InlineData("Published (updates only)", "", PackagesTabKind.Other)]
    [InlineData("Packaged, WSUS failed", "1.0", PackagesTabKind.Other)]
    public void EveryStatusTextBelongsToOneTab(string status, string currentVersion, PackagesTabKind expected) =>
        Assert.Equal(expected, PackagesTab.Of(status, currentVersion));

    [Fact]
    public void TheCountsFollowAChangingRow()
    {
        var rows = new[]
        {
            new PackagerInfo { Uid = "a", Status = "Ready" },
            new PackagerInfo { Uid = "b", Status = "Ready" },
            new PackagerInfo { Uid = "c", Status = "Cached", CurrentVersion = "1.0" }
        };
        Assert.Equal(new PackagesTabCounts(3, 0, 0, 2), PackagesTab.Count(rows));

        rows[0].Status = PackagesTab.NotFoundStatus;
        rows[1].CurrentVersion = "1.0";
        rows[1].Status = CveRowState.UpdateAvailable;
        Assert.Equal(new PackagesTabCounts(3, 1, 1, 0), PackagesTab.Count(rows));
        Assert.True(PackagesTab.Contains(null, rows[2]));
        Assert.True(PackagesTab.Contains(PackagesTabKind.Update, rows[1]));
        Assert.False(PackagesTab.Contains(PackagesTabKind.Update, rows[0]));
    }

    // Hidden selection

    [Fact]
    public void TheSelectionCountsSeparateTheCheckedRowsTheViewHides()
    {
        var all = Enumerable.Range(1, 6).Select(i => new PackagerInfo { Uid = "u" + i, Application = "App" + i }).ToList();
        foreach (var row in all.Take(4)) row.IsSelected = true;
        var visible = all.Where(r => r.Uid is "u1" or "u2" or "u5");

        var (selected, hidden) = SelectionCounts.Of(all, visible);
        Assert.Equal(4, selected);
        Assert.Equal(2, hidden);
        Assert.Equal((0, 0), SelectionCounts.Of(all.Select(r => { r.IsSelected = false; return r; }).ToList(), visible));
    }

    // NotFound preload

    [Fact]
    public void AStoredCompletedCheckWithNoVersionIsNotFound()
    {
        InventoryEnvironmentState State(string? version, DateTimeOffset? at, InventoryOutcome last) =>
            new("u", "ConfigMgr", "MCM", null, null, version, "Check ConfigMgr", at, at, last, "Check ConfigMgr", null);

        Assert.True(CveRowState.IsStoredNotFound(State(null, DateTimeOffset.UtcNow, InventoryOutcome.NotFound)));
        // A later failed attempt does not erase the last completed observation.
        Assert.True(CveRowState.IsStoredNotFound(State("", DateTimeOffset.UtcNow, InventoryOutcome.Failed)));
        Assert.False(CveRowState.IsStoredNotFound(State("1.0", DateTimeOffset.UtcNow, InventoryOutcome.Succeeded)));
        Assert.False(CveRowState.IsStoredNotFound(State(null, null, InventoryOutcome.Failed)));
        Assert.False(CveRowState.IsStoredNotFound(null));
    }
}
