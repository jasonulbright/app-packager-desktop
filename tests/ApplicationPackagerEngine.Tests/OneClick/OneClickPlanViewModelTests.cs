using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>
/// The plan window without a window: plan rows, the count, scope and blocking lines, a
/// destination change, Include kept across a replan, Force, Plan only, the run view and
/// the run report. The run itself is a stand-in.
/// </summary>
public sealed class OneClickPlanViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-oneclick-plan-" + Guid.NewGuid().ToString("N"));
    private string _wsusServer = "wsus01";

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class FakeRunner(Action<IReadOnlyList<OneClickPlanItem>>? work = null) : IOneClickRunner
    {
        public IReadOnlyList<OneClickPlanItem>? Ran { get; private set; }
        public OneClickRunOptions? Options { get; private set; }

        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            Ran = rows;
            Options = options;
            work?.Invoke(rows);
            return Task.FromResult(false);
        }
    }

    private OneClickPlanViewModel Window(IOneClickRunner runner, string action = OneClickActionNames.StageAndPackage,
        Func<IReadOnlyList<PackagerInfo>, IReadOnlyList<PackagerInfo>>? confirm = null)
    {
        var published = new Dictionary<string, OneClickPublishRecord>();
        OneClickHistory.SetPublished(published, new Dictionary<string, OneClickRefusalRecord>(), "WSUS", "2.0", "guid-beta", DateTimeOffset.Now);
        var rows = new[] { ("package-alpha", "Alpha", "1.0"), ("package-beta", "Beta", "2.0"), ("package-gamma", "Gamma", "") };
        return new OneClickPlanViewModel(new OneClickPlanContext
        {
            Apps = rows.Select(r => (new OneClickApp(r.Item1, r.Item2, "V", r.Item3), (PackagerInfo?)new PackagerInfo { Uid = r.Item1, Application = r.Item2 })).ToList(),
            Settings = () => new OneClickPlanSettings
            {
                Tracked = ["package-alpha", "package-beta", "package-gamma"],
                Destinations = new Dictionary<string, DestinationSet> { ["package-gamma"] = new DestinationSet() },
                DefaultDestinations = new DestinationSet(true, true, false),
                Readiness = OneClickPlanner.Readiness(new PublishReadiness(
                    new SystemsInUse { ConfigMgr = true, Intune = false, Wsus = true }, true, "MCM", @"\\server\share", "", "", false, _wsusServer)),
                Scope = new OneClickScopeSettings(WsusApprovalGroup: "Pilot")
            },
            History = new Dictionary<string, OneClickHistoryEntry> { ["package-beta"] = new() { Published = published } },
            Action = action,
            OnExisting = OneClickOnExisting.Skip,
            ReportFolder = _root,
            ConfirmLocalSources = confirm
        }, runner);
    }

    private static OneClickPlanItem RowOf(OneClickPlanViewModel vm, string key) => vm.Rows.Single(r => r.Key == key);

    [Fact]
    public void ThePlanShowsOneRowPerTrackedApplicationWithItsDestinationsAndTheGuard()
    {
        var vm = Window(new FakeRunner());
        Assert.Equal(3, vm.Rows.Count);
        var alpha = RowOf(vm, "package-alpha");
        Assert.Equal("Check, Stage, Publish to ConfigMgr+WSUS", alpha.Planned);
        Assert.True(alpha.Include);
        var beta = RowOf(vm, "package-beta");
        Assert.Equal("Check, Stage, Publish to ConfigMgr", beta.Planned);
        Assert.Equal("WSUS: 2.0 already published", beta.Reason);
        Assert.Equal("WSUS 2.0", beta.LastPublished);
        var gamma = RowOf(vm, "package-gamma");
        Assert.False(gamma.Include);
        Assert.Equal("no destination selected", gamma.Reason);

        Assert.Equal("2 application(s), 1 skipped: 2 to ConfigMgr, 1 to WSUS, 0 to Intune", vm.SummaryText);
        Assert.Contains("WSUS: approved for group Pilot", vm.ScopeText);
        Assert.Contains("ConfigMgr: application only", vm.ScopeText);
        Assert.False(vm.HasBlocking);
        Assert.True(vm.RunCommand.CanExecute(null));
        Assert.Equal("Action: check, stage and publish. Existing ConfigMgr application: skip.", vm.ActionText);
        Assert.Equal("Include and the destination boxes apply to this run only; One Click Settings holds the saved values. Run asks nothing after it starts.", OneClickPlanViewModel.HintText);
    }

    [Fact]
    public void ADestinationBoxReplansThisRunOnlyAndIncludeKeepsTheOperatorsValue()
    {
        var vm = Window(new FakeRunner());
        var gamma = RowOf(vm, "package-gamma");
        gamma.Wsus = true;
        Assert.True(gamma.Include);
        Assert.Equal("Check, Stage, Publish to WSUS", gamma.Planned);
        Assert.Equal("3 application(s): 2 to ConfigMgr, 2 to WSUS, 0 to Intune", vm.SummaryText);

        gamma.Include = false;
        Assert.True(gamma.IncludeTouched);
        Assert.Equal("2 application(s), 1 skipped: 2 to ConfigMgr, 1 to WSUS, 0 to Intune", vm.SummaryText);

        // A replan keeps the cleared Include although the plan would include the row.
        vm.Refresh();
        Assert.False(gamma.Include);
        gamma.Include = true;
        Assert.Equal("3 application(s): 2 to ConfigMgr, 2 to WSUS, 0 to Intune", vm.SummaryText);
    }

    [Fact]
    public void AReplanStartsNoFurtherReplan()
    {
        var vm = Window(new FakeRunner());
        var before = vm.RefreshCount;
        RowOf(vm, "package-alpha").Intune = true;
        Assert.Equal(before + 1, vm.RefreshCount);
        vm.Refresh();
        Assert.Equal(before + 2, vm.RefreshCount);
    }

    [Fact]
    public void ForcePublishesTheVersionWsusAlreadyHas()
    {
        var vm = Window(new FakeRunner());
        vm.Force = true;
        Assert.Equal("Check, Stage, Publish to ConfigMgr+WSUS", RowOf(vm, "package-beta").Planned);
        vm.Force = false;
        Assert.Equal("Check, Stage, Publish to ConfigMgr", RowOf(vm, "package-beta").Planned);
    }

    [Fact]
    public void PlanOnlyWritesTheIncludedRowsAsPlanned()
    {
        var vm = Window(new FakeRunner());
        vm.PlanOnlyCommand.Execute(null);
        Assert.True(vm.CanOpenReport);
        Assert.StartsWith("Plan written: ", vm.SummaryText);
        var md = File.ReadAllText(vm.ReportPath);
        Assert.Contains("| Alpha | 1.0 | Planned |", md);
        Assert.Contains("| Beta | 2.0 | Planned |", md);
        Assert.DoesNotContain("Gamma", md);
        Assert.Contains("- ConfigMgr: retire the application", md);
        Assert.Contains("- WSUS: expire the package ID", md);
        Assert.DoesNotContain("- Intune:", md);
    }

    [Fact]
    public void ABlockedDestinationIsNamedWhenARowSelectsIt()
    {
        var vm = Window(new FakeRunner());
        _wsusServer = "";
        vm.Refresh();
        Assert.True(vm.HasBlocking);
        Assert.Contains("WSUS: WSUS server not set", vm.BlockingText);
    }

    [Fact]
    public async Task RunShowsTheResultColumnsOfTheDestinationsInUseAndWritesTheReport()
    {
        var runner = new FakeRunner(rows =>
        {
            var alpha = rows.Single(r => r.Key == "package-alpha");
            alpha.SetResult("WSUS", "published guid-alpha", "guid-alpha");
            alpha.SetResult("ConfigMgr", "published Alpha");
            alpha.Outcome = OneClickOutcomes.Published;
            var beta = rows.Single(r => r.Key == "package-beta");
            beta.SetResult("WSUS", "not supported: CustomInstall", "", "not supported: CustomInstall. This application cannot be published to WSUS: CustomInstall: steps");
            beta.Outcome = OneClickOutcomes.NotSupported;
        });
        var vm = Window(runner);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(["package-alpha", "package-beta"], runner.Ran!.Select(r => r.Key));
        Assert.Equal(OneClickOnExisting.Skip, runner.Options!.OnExisting);
        Assert.True(vm.IsRunView);
        Assert.True(vm.ShowResultConfigMgr && vm.ShowResultWsus);
        Assert.False(vm.ShowResultIntune);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.StartsWith("Complete: 1 published, 0 failed. Report: one-click-", vm.SummaryText);
        var md = File.ReadAllText(vm.ReportPath);
        Assert.Contains("| Alpha | 1.0 | Published | published Alpha | published guid-alpha |", md);
        Assert.Contains("- Beta, WSUS: not supported: CustomInstall. This application cannot be published to WSUS", md);
        Assert.DoesNotContain("- Intune:", md);
    }

    [Fact]
    public async Task ARowWithoutALocalSourceFolderIsLeftOutBeforeTheRunStarts()
    {
        var runner = new FakeRunner();
        var vm = Window(runner, confirm: rows => rows.Where(r => r.Uid != "package-beta").ToList());
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(["package-alpha"], runner.Ran!.Select(r => r.Key));
        var beta = RowOf(vm, "package-beta");
        Assert.False(beta.Include);
        Assert.Equal("no local source folder", beta.Reason);
    }

    private sealed class ThrowingRunner(Action<IReadOnlyList<OneClickPlanItem>> before) : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            before(rows);
            throw new InvalidOperationException("the share went away");
        }
    }

    private sealed class CancelingRunner(Action<IReadOnlyList<OneClickPlanItem>> work) : IOneClickRunner
    {
        public Task<bool> RunAsync(IReadOnlyList<OneClickPlanItem> rows, OneClickRunOptions options, CancellationToken ct)
        {
            work(rows);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ARunnerThatThrowsEndsTheRunWithAReportAndASummaryNotARunningWindow()
    {
        var runner = new ThrowingRunner(rows => rows[0].Step = "staging Alpha");
        var vm = Window(runner);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.StartsWith("Failed: the share went away. Report: one-click-", vm.SummaryText);
        Assert.True(File.Exists(vm.ReportPath));
        Assert.All(vm.Rows.Where(r => r.Include), r => Assert.DoesNotContain(r.Step, new[] { "Pending", "staging Alpha" }));
    }

    [Fact]
    public async Task ACanceledRunMarksTheRowsItNeverReachedNotStartedNotPending()
    {
        var runner = new CancelingRunner(rows =>
        {
            rows[0].SetResult("ConfigMgr", "published Alpha");
            rows[0].Outcome = OneClickOutcomes.Published;
            rows[0].Step = OneClickOutcomes.Published;
        });
        var vm = Window(runner);

        await vm.RunCommand.ExecuteAsync(null);

        var reached = vm.Rows.Single(r => r.Key == "package-alpha");
        var notReached = vm.Rows.Single(r => r.Key == "package-beta");
        Assert.Equal(OneClickOutcomes.Published, reached.Step);
        Assert.Equal("Not started", notReached.Step);
        Assert.StartsWith("Canceled:", vm.SummaryText);
    }
}
