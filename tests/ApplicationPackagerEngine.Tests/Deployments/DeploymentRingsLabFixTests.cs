using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Deployments.Rings;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using RadioButton = System.Windows.Controls.RadioButton;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The plan editor purpose choice, the removal question size and the ring texts in real windows and over the page.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DeploymentRingsLabFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ringlab-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static CollectionRow Row(string id, string name) => new(CollectionId: id, Name: name, FolderPath: "Rings", LimitingCollectionId: "SMS00001",
        LimitingCollectionName: "All Systems", MemberCount: 10, IsBuiltIn: false, RefreshType: "Periodic", Comment: "", DirectRuleCount: 0, QueryRuleCount: 0,
        IncludeRuleCount: 0, ExcludeRuleCount: 0);

    private static readonly CollectionRow[] Collections = [Row("LAB00010", "Pilot"), Row("LAB00011", "Wave 1"), Row("LAB00012", "Wave 2")];

    private static RingPlan Plan(string name, bool required) => new(Name: name, Description: "Three rings.", HoldLaterRings: false, TimeBasedOn: "LocalTime",
        Rings: Collections.Select((c, i) => FakeRingService.Ring(c.Name, c.CollectionId, c.Name, required ? "Required" : "Available", i * 7,
            required ? i * 7 + 2 : null)).ToList(), IsSeed: false);

    private static void Click(ButtonBase button) =>
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, null);

    private static void WithEditor(ApplicationTheme theme, RingPlanEditorViewModel editor, Action<RingPlanEditorWindow> test) =>
        WithApplication(theme, () =>
        {
            var window = new RingPlanEditorWindow(editor);
            Show(window);
            try
            {
                Settle(window);
                test(window);
            }
            finally
            {
                editor.GetType().GetProperty(nameof(RingPlanEditorViewModel.Saved))!.SetValue(editor, true);
                window.Close();
            }
        });

    private static RingPlanEditorViewModel Editor(RingPlan plan, bool copy, Func<RingPlanEditorViewModel, RingPlan, Task<bool>>? save = null, Func<bool>? discard = null) =>
        new(plan, copy, [plan], Collections, save ?? ((_, _) => Task.FromResult(true)), _ => null, confirmDiscard: discard);

    private static void Select(Window window, RingPlanEditorViewModel editor, int index)
    {
        ById<DataGrid>(window, "dgEditorRings").SelectedItem = editor.Rings[index];
        Settle(window);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void ARingSetRequiredStaysRequiredWhenAnotherRingIsSelected(ApplicationTheme theme)
    {
        var editor = Editor(Plan("Three Rings", required: false), copy: false);
        WithEditor(theme, editor, window =>
        {
            Select(window, editor, 0);
            Click(ById<RadioButton>(window, "rbRingRequired"));
            Settle(window);
            editor.Rings[0].DeadlineOffsetText = "2";
            Assert.Equal("Required", editor.Rings[0].Purpose);

            Select(window, editor, 1);
            Assert.Equal("Required", editor.Rings[0].Purpose);
            Assert.Equal("Available", editor.Rings[1].Purpose);
            Assert.True(ById<RadioButton>(window, "rbRingAvailable").IsChecked);

            Select(window, editor, 0);
            Assert.Equal("Required", editor.Rings[0].Purpose);
            Assert.True(ById<RadioButton>(window, "rbRingRequired").IsChecked);
            Assert.False(ById<RadioButton>(window, "rbRingAvailable").IsChecked);

            Select(window, editor, 2);
            Click(ById<RadioButton>(window, "rbRingAvailable"));
            Select(window, editor, 0);
            Assert.Equal(["Required", "Available", "Available"], editor.Rings.Select(r => r.Purpose));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, false, false)]
    [InlineData(ApplicationTheme.Dark, false, true)]
    [InlineData(ApplicationTheme.Light, true, false)]
    [InlineData(ApplicationTheme.Dark, true, true)]
    public void EditAndCopyShowThePurposeOfTheFileAndAskNothingWithoutAChange(ApplicationTheme theme, bool required, bool copy)
    {
        var plan = Plan("Three Rings", required);
        var asked = 0;
        var editor = Editor(plan, copy, discard: () => { asked++; return false; });
        WithEditor(theme, editor, window =>
        {
            for (var i = 0; i < 3; i++)
            {
                Select(window, editor, i);
                Assert.Equal(plan.Rings[i].Purpose, editor.Rings[i].Purpose);
                Assert.Equal(required, ById<RadioButton>(window, "rbRingRequired").IsChecked);
                Assert.Equal(!required, ById<RadioButton>(window, "rbRingAvailable").IsChecked);
            }
            Select(window, editor, 0);
            Assert.Equal(plan.Rings.Select(r => r.Purpose), editor.Rings.Select(r => r.Purpose));
            Assert.False(editor.HasChanges);
            Assert.True(editor.ConfirmClose());
            Assert.Equal(0, asked);
        });
    }

    [Fact]
    public void ANewPlanStartsWithoutRingsTakes100CollectionsAndRefusesAnEmptySave()
    {
        var chosen = Enumerable.Range(1, 100).Select(i => Row($"LABH{i:0000}", $"Wave {i}")).ToList();
        var editor = new RingPlanEditorViewModel(null, false, [], Collections, (_, _) => Task.FromResult(true), _ => null, () => chosen);
        Assert.Empty(editor.Rings);
        Assert.Null(editor.SelectedRing);
        Assert.False(editor.HasChanges);
        editor.Name = "Empty";
        Assert.Null(editor.Build(out var problem));
        Assert.Equal(RingPlanEditorViewModel.NoRings, problem);

        editor.DaysBetweenText = "1";
        editor.AddRingsFromCollectionsCommand.Execute(null);
        Assert.Equal(100, editor.Rings.Count);
        Assert.Equal("Wave 1", editor.Rings[0].Name);
        Assert.NotNull(editor.Build(out problem));
        Assert.Null(problem);
    }

    private static async Task<(DeploymentRingsViewModel Rings, DeploymentPageHarness H)> ExpandedAsync(RingPlan plan, DeploymentPageHarness? harness = null, bool hold = false)
    {
        var h = harness ?? new DeploymentPageHarness();
        h.Rings.Plans.Add(plan);
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedObject = rings.Objects.Single(o => o.Object.Id == h.SevenZip.Id);
        rings.SelectedPlan = rings.Plans.Single(p => p.Name == plan.Name);
        rings.HoldLaterRings = hold;
        await rings.ExpandCommand.ExecuteAsync(null);
        return (rings, h);
    }

    [Fact]
    public async Task AValidateFailureOfARingIsListedOnceWithThePlainLabelOfThePlanRules()
    {
        const string Message = "The deadline of ring Wave 1 is earlier than the deadline of ring Pilot.";
        var h = new DeploymentPageHarness();
        h.Rings.AfterPrepare = c =>
        {
            if (c.Kind != RingChangeKind.CreateRun) return c;
            var check = new PreflightCheck(RingPlanRules.CheckName, DeploymentCheckResults.Fail, Message);
            var rows = c.Rows.Select(r => r.Index == 2 ? r with { Checks = [.. r.Checks, check] } : r).ToList();
            return c with
            {
                Rows = rows,
                Checks = [.. c.Checks, check with { Message = $"{RingPlanRules.Label(2, "Wave 1")}: {Message}" }],
                RingChanges = [.. c.RingChanges.Select(k => k.Ring?.RingIndex == 2 ? k with { Checks = [.. k.Checks, check] } : k)]
            };
        };
        var (rings, _) = await ExpandedAsync(Plan("Three Rings", required: false), h);
        await rings.ValidateCommand.ExecuteAsync(null);
        var failed = rings.Checks.Where(c => c.Result == DeploymentCheckResults.Fail).ToList();
        Assert.Equal(("Wave 1", "Plan rules", Message), Assert.Single(failed) is var f ? (f.Ring, f.Check, f.Message) : default);
        Assert.Equal("1 check did not pass. The run is blocked.", rings.ValidationMessage);
        Assert.DoesNotContain(rings.Checks, c => c.Check == RingPlanRules.CheckName);

        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Equal($"The run cannot be created: Ring Wave 1: Plan rules: {Message}", rings.FormMessage);
    }

    [Fact]
    public async Task AFailedCheckOfAHeldRingStillRefusesTheRunAndNamesItsReason()
    {
        const string Message = "Ring 3 (Wave 2) names collection LAB00012, which does not exist.";
        var h = new DeploymentPageHarness();
        h.Rings.AfterPrepare = c =>
        {
            if (c.Kind != RingChangeKind.CreateRun) return c;
            var check = new PreflightCheck(RingPlanRules.CheckName, DeploymentCheckResults.Fail, Message);
            return c with { Rows = [.. c.Rows.Select(r => r.Index == 3 ? r with { Checks = [check] } : r)], Checks = [.. c.Checks, check] };
        };
        var (rings, _) = await ExpandedAsync(Plan("Three Rings", required: false), h, hold: true);
        await rings.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(("Wave 2", "Plan rules"), Assert.Single(rings.Checks, c => c.Result == DeploymentCheckResults.Fail) is var f ? (f.Ring, f.Check) : default);

        await rings.CreateRunCommand.ExecuteAsync(null);
        Assert.Equal($"The run cannot be created: Plan rules: {Message}", rings.FormMessage);
        Assert.Empty(h.Rings.Applied);
    }

    [Fact]
    public async Task TheEditorRefusesATaskSequenceOptionCombinationThatValidateBlocks()
    {
        var plan = Plan("Three Rings", required: false);
        var editor = Editor(plan, copy: false);
        editor.Rings[1].TaskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities.Single(c => c.Value == "MediaAndPxeHidden");
        editor.Rings[1].TaskSequenceContent = RingPlanEditorViewModel.TaskSequenceContentChoices.Single(c => c.Value == "DownloadAllContentLocallyBeforeStartingTaskSequence");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.Saved);
        Assert.Equal("Ring Wave 1: task sequence options: Download all content before the task sequence starts works only when the task sequence is available to clients only.",
            editor.ValidationMessage);

        editor.Rings[1].TaskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities.Single(c => c.Value == "Clients");
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(editor.Saved);
    }

    [Fact]
    public async Task ASummaryRowWithoutCountsReadsAsNotSummarizedAndShowsUnknownInEveryCountCell()
    {
        using var h = new RingHarness();
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), DeploymentHarness.App);
        var run = h.Run(outcome.Run!.RunId);
        var id = run.Rings[0].DeploymentId!;
        var summaries = h.Site.List("Summaries");
        foreach (var old in summaries.OfType<System.Text.Json.Nodes.JsonObject>().Where(s => string.Equals((string?)s["DeploymentID"], id, StringComparison.OrdinalIgnoreCase)).ToList())
            summaries.Remove(old);
        summaries.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["DeploymentID"] = id, ["AssignmentID"] = 0, ["CollectionID"] = RingHarness.C1, ["NumberTargeted"] = null, ["NumberSuccess"] = null, ["NumberErrors"] = null,
            ["NumberInProgress"] = null, ["NumberUnknown"] = null, ["NumberOther"] = null, ["SummarizationTime"] = null
        });

        var counts = (await h.Service.LiveCountsAsync(run, CancellationToken.None)).Single();
        Assert.Equal((RingLiveStates.Found, false, "The site has not summarized this deployment yet."), (counts.State, counts.CountsKnown, counts.Message));
        var row = new RingRunRingRow(run.Rings[0], run.TimeBasedOn) { Counts = counts };
        Assert.All(new[] { row.Targeted, row.Success, row.Error, row.InProgress, row.Unknown }, c => Assert.Equal(RingText.UnknownCount, c));
        Assert.Equal("", row.SuccessRate);
        Assert.Contains("live counts are not known yet", RingText.PromoteState(run, new Dictionary<int, RingLiveCounts> { [1] = counts }).Reason);

        var none = counts with { State = RingLiveStates.Summarized, CountsKnown = true, Message = "" };
        row.Counts = none;
        Assert.Equal("0", row.Targeted);
        Assert.Equal("Ring QA has no targeted clients yet; ring Pilot needs 90 % success. Select Refresh and reconcile later.",
            RingText.PromoteState(run, new Dictionary<int, RingLiveCounts> { [1] = none }).Reason);
    }

    [Fact]
    public async Task AnUndoRunThatKeepsDeploymentsReportsRemovedAndKeptAndCountsNoFailure()
    {
        var h = new DeploymentPageHarness();
        h.Rings.AddRun("RUN-100", h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Created", null, "Required"),
            ("Wide", "LAB00013", "Created", null, "Available"), ("Broad", "LAB00012", "Held", null, "Required"));
        h.Rings.ChangedByOthers.Add("MCM20001");
        h.Rings.ChangedByOthers.Add("MCM20002");
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-100");
        await rings.CountsTask;
        await rings.UndoRunCommand.ExecuteAsync(null);

        var plan = h.Plans.Shown.Single();
        Assert.Contains("The run stays open because 2 deployments are kept.", plan.Items[^1].Detail);
        Assert.Single(plan.Items[^1].Detail!.Split(Environment.NewLine), l => l.StartsWith("The run stays open", StringComparison.Ordinal));
        var lastLines = plan.Items[^1].Detail!.Split(Environment.NewLine);
        Assert.Equal(lastLines.Length, lastLines.Distinct().Count());
        var earlier = plan.Items.Take(plan.Items.Count - 1).SelectMany(i => i.Detail!.Split(Environment.NewLine)).ToHashSet();
        var cancelLines = plan.Items[^1].Detail!.Split(Environment.NewLine).Skip(1);
        Assert.DoesNotContain(cancelLines, l => earlier.Contains(l));
        var dialog = h.Plans.LastViewModel!;
        Assert.Equal(["Changed", "Skipped", "Skipped", "Changed"], dialog.ResultRows.Select(r => r.OutcomeText));
        Assert.Equal(RingWriter.CancelledText, dialog.ResultRows[3].Message);
        Assert.StartsWith("1 removed, 2 kept, 1 held ring cancelled. Kept: Ring Early: ", dialog.ResultText);
        Assert.DoesNotContain("failed", dialog.ResultText);
        var entry = h.History.Entries.Last();
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, entry.Outcome);
        Assert.Equal((2, 0, 2), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["kept"]));
    }

    [Fact]
    public async Task AnUndoRunWhoseRemovalIsRefusedAtApplySkipsItsCloseLineAndCountsNoFailure()
    {
        var h = new DeploymentPageHarness();
        h.Rings.AddRun("RUN-100", h.SevenZip, ("Pilot", "LAB00010", "Created", null, "Available"), ("Early", "LAB00011", "Created", null, "Available"));
        h.Rings.BeforeApply = () => h.Rings.ChangedByOthers.Add("MCM20001");
        var rings = (await h.OpenAsync(DeploymentHelperViewModel.RingsTab)).Rings;
        rings.SelectedRun = rings.Runs.Single(r => r.RunId == "RUN-100");
        await rings.CountsTask;
        await rings.UndoRunCommand.ExecuteAsync(null);

        var dialog = h.Plans.LastViewModel!;
        Assert.Equal(["Remove deployment", "Remove deployment", "Close run"], dialog.ResultRows.Select(r => r.Item.Action));
        Assert.Equal(RingWriter.StaysOpenText, dialog.ResultRows[2].Message);
        Assert.Equal(0, dialog.ResultRows.Count(r => r.OutcomeText == "Failed"));
        Assert.StartsWith("1 removed, 1 kept. The run stays open. Kept: ", dialog.ResultText);
        Assert.Equal(0, h.History.Entries.Last().Counts["failed"]);
    }

    [Fact]
    public async Task TheUndoPlanOfTheRingServiceStatesEachFactOnce()
    {
        using var h = new RingHarness();
        var outcome = await h.CreateRunAsync(RingHarness.ThreeRings(hold: true), DeploymentHarness.App);
        var run = h.Run(outcome.Run!.RunId);
        var prepared = await h.PrepareAsync(RingHarness.Request(RingChangeKind.UndoRun, run.RunId, null));
        var (plan, problem) = RingText.BuildUndoRunPlan(prepared, run.RunId, run);
        Assert.Null(problem);
        Assert.Equal(["Remove deployment", "Cancel held ring", "Cancel held ring", "Close run"], plan!.Plan.Items.Select(i => i.Action));
        var last = plan.Plan.Items[^1].Detail!.Split(Environment.NewLine);
        Assert.Equal(last.Length, last.Distinct().Count());
        Assert.DoesNotContain(last, l => l.StartsWith("Ring ", StringComparison.Ordinal) || l.StartsWith("Cancels ", StringComparison.Ordinal));
        Assert.Single(last, l => l is "The run file moves to the closed folder." or "Then closes the run.");
    }

    private static DeploymentChange Removal(int i) => new(DeploymentChangeKind.RemoveDeployment, null, null, null, [], false, false, false,
        new DeploymentRecord(DeploymentId: $"{{DEP-{i:000}}}", Type: DeploymentObjectType.Package, ObjectId: "LAB000A0", ObjectName: "Contoso Fonts 1.0",
            CollectionId: $"LABC{i:0000}", CollectionName: $"RG-Test-C{i:000}", Purpose: "Available", AvailableTime: new DateTime(2026, 10, 19, 8, 0, 0), Deadline: null,
            Created: new DateTime(2026, 10, 19, 8, 0, 0), CreatedBy: @"LAB\admin"), [], [], null);

    [Fact]
    public void TheRemovalQuestionOfManyDeploymentsStatesTheCountAndListsOneDeploymentPerLine()
    {
        var text = DeploymentWriter.RemovalQuestion(Enumerable.Range(1, 100).Select(Removal).ToList());
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("Remove these 100 deployments? Software that clients already installed stays installed.", lines[0]);
        Assert.Equal(101, lines.Length);
        Assert.Equal("Contoso Fonts 1.0 from RG-Test-C100 (LABC0100)", lines[^1]);
        Assert.Equal("Remove the deployment Contoso Fonts 1.0 from RG-Test-C001 (LABC0001)? Software that clients already installed stays installed.",
            DeploymentWriter.RemovalQuestion([Removal(1)]));
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1080 - 48)]
    [InlineData(ApplicationTheme.Dark, 1080 - 48)]
    [InlineData(ApplicationTheme.Light, 768 - 40)]
    [InlineData(ApplicationTheme.Dark, 768 - 40)]
    public void TheRemovalQuestionOf100DeploymentsScrollsAndKeepsItsButtonsInsideTheWorkArea(ApplicationTheme theme, double workArea)
    {
        WithApplication(theme, () =>
        {
            var window = new RemovalConfirmWindow(DeploymentWriter.RemovalQuestion(Enumerable.Range(1, 100).Select(Removal).ToList()));
            window.FitWorkArea(workArea);
            Show(window);
            try
            {
                Settle(window);
                Assert.True(window.ActualHeight <= workArea + 0.5, $"The question is {window.ActualHeight:0} px high in a {workArea:0} px work area.");
                foreach (var id in new[] { "btnRemovalConfirm", "btnRemovalCancel" })
                    AssertInside(window, ById<FrameworkElement>(window, id), window.ActualHeight, id);
                var scroller = ById<ScrollViewer>(window, "scrollRemovalQuestion");
                Assert.True(scroller.ScrollableHeight > 0, "The list of 100 deployments does not scroll.");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheChangePlanOf100RingsKeepsItsCheckBoxesAndButtonsVisible(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var rows = Enumerable.Range(1, 100).Select(i => new RingExpansionRow(Index: i, Name: $"Wave {i} of the finance department rollout", CollectionId: $"LABC{i:0000}",
                CollectionName: $"Finance {i}", MemberCount: 10, Purpose: "Required", AvailableTime: new DateTime(2026, 10, 1), Deadline: new DateTime(2026, 10, 3),
                DpGroup: "", SuccessThresholdPercent: null, Checks: [])).ToList();
            var extra = RingText.SecondConfirmation(100, 1000, null, rows, "local time");
            Assert.DoesNotContain("Wave 50", extra);
            var items = Enumerable.Range(1, 100).Select(i => new ChangePlanItem("Remove deployment", $"Ring Wave {i}: Contoso Fonts 1.0 from Finance {i} (LABC{i:0000})",
                string.Join(Environment.NewLine, Enumerable.Repeat("A line of detail that the plan shows for this ring.", 6)))).ToList();
            var plan = new ChangePlanWindow(new ChangePlanViewModel(new ChangePlan("Undo ring run", "Remove 100 deployments", items, isDestructive: true) { ExtraConfirmation = extra },
                (_, _, _) => Task.FromResult(new ChangePlanResult([]))));
            Show(plan);
            try
            {
                plan.Height = 768 - 40;
                Settle(plan);
                foreach (var id in new[] { "chkChangePlanDestructive", "chkChangePlanExtra", "btnChangePlanConfirm", "btnChangePlanCancel" })
                    AssertInside(plan, ById<FrameworkElement>(plan, id), plan.ActualHeight, id);
            }
            finally { plan.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACopySavedUnchangedEqualsTheOriginalFileExceptTheName(bool required)
    {
        var store = new RingPlanStore(() => _root);
        store.List();
        var original = store.Save(Plan("Lab Plan", required), replace: false);
        var listed = store.List().Single(p => p.Name == "Lab Plan");
        RingPlan? saved = null;
        var editor = Editor(listed, copy: true, save: (_, p) => { saved = store.Save(p, replace: false); return Task.FromResult(true); });
        WithEditor(ApplicationTheme.Light, editor, window =>
        {
            Select(window, editor, 1);
            Select(window, editor, 0);
            Assert.False(editor.HasChanges);
            Run(editor.SaveCommand.ExecuteAsync(null));
            Assert.Equal("", editor.ValidationMessage);
        });
        Assert.NotNull(saved);
        var first = File.ReadAllText(Path.Combine(_root, original.FileName!));
        var second = File.ReadAllText(Path.Combine(_root, saved!.FileName!));
        Assert.Equal(first.Replace("\"Lab Plan\"", "\"Lab Plan copy\"", StringComparison.Ordinal), second);
    }
}
