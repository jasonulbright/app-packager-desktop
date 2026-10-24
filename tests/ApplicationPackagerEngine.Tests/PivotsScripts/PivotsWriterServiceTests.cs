using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// The page's plan writer and the page itself over the REAL Pivots and Scripts service, the real
/// session script on the stub site and the stub administration service: the page sends with the
/// send-only window, the plan window closes after the send, and the refusals of the service reach
/// the plan as plain Skipped rows.
/// </summary>
public sealed class PivotsWriterServiceTests
{
    private static PivotsPlan Plan(PivotsHarness h, PivotsChangeKind kind, RunTargetKind target, string text, string? query = null)
    {
        var targets = h.ResolveAsync(target, text).Result;
        var request = PivotsHarness.Request(kind, targets, query: query, guid: kind == PivotsChangeKind.RunScript ? StubPivotsSite.ScriptGuid : null) with { ResultWindow = PivotsChange.SendOnly };
        var prepared = h.Service.PrepareAsync(request, CancellationToken.None).Result;
        var (plan, problem) = PivotsScriptsText.BuildPlan(prepared);
        Assert.Null(problem);
        return plan!;
    }

    private static PivotsScriptsWriter Writer(PivotsHarness h, RecordingSiteHistory history, RecordingMaintenanceLog log) => new(h.Service, history, log, () => "MCM");

    private static ChangePlanViewModel Confirm(PivotsPlan plan, ChangePlanRun run, bool destructive = true, bool extra = true, bool second = true)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, run) { DestructiveAcknowledged = destructive, ExtraAcknowledged = extra, SecondAcknowledged = second };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        return dialog;
    }

    /// <summary>A confirmed plan whose run is not sent: the boxes as given, captured from the real plan window.</summary>
    private static ConfirmedChangePlan Captured(PivotsPlan plan, bool destructive = true, bool extra = true, bool second = true)
    {
        ConfirmedChangePlan? captured = null;
        Confirm(plan, (confirmed, _, _) =>
        {
            captured = confirmed;
            return Task.FromResult(new ChangePlanResult([]));
        }, destructive, extra, second);
        return captured!;
    }

    [Fact]
    public void ACMPivotRunGoesThroughTheRealServiceAndThePlanWindowClosesAfterTheSend()
    {
        using var h = new PivotsHarness();
        var history = new RecordingSiteHistory();
        var log = new RecordingMaintenanceLog();
        var plan = Plan(h, PivotsChangeKind.RunPivot, RunTargetKind.List, "CLIENT01, CLIENT02, x64 Unknown Computer (x64 Unknown Computer)", "OS | project Caption");
        Assert.Equal(3, plan.Entries.Count);
        var dialog = Confirm(plan, Writer(h, history, log).RunFor(plan));
        var result = dialog.Outcome.Result!;
        Assert.Equal(["Sent", "Sent", "Skipped"], result.Items.Select(r => r.OutcomeText));
        Assert.StartsWith("Skipped", result.Items[2].Message);
        Assert.Equal("The page waits for the results and shows them in the result tabs.", result.Note);
        Assert.Equal(1, h.Admin.Sends);
        Assert.Equal(0, h.Admin.StatusReads);
        Assert.Equal(0, h.Delays);
        Assert.Equal(["Started"], h.Audit.Read(10).Select(r => r.Phase));
        var outcome = plan.Outcome!;
        Assert.True(outcome.Sent);
        Assert.All(outcome.Devices, d => Assert.False(d.IsFinal));
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, history.Entries.Single().Outcome);
    }

    [Fact]
    public void AScriptRunGoesThroughTheRealServiceWithBothBoxes()
    {
        using var h = new PivotsHarness();
        var plan = Plan(h, PivotsChangeKind.RunScript, RunTargetKind.Device, "CLIENT01");
        Assert.True(plan.Plan.IsDestructive);
        Assert.NotNull(plan.Plan.ExtraConfirmation);
        var history = new RecordingSiteHistory();
        var dialog = Confirm(plan, Writer(h, history, new RecordingMaintenanceLog()).RunFor(plan));
        Assert.Equal("Sent", dialog.Outcome.Result!.Items.Single().OutcomeText);
        Assert.Equal(1, h.Site.Count("RunScript:"));
        Assert.Equal(SiteOperationOutcome.Succeeded, history.Entries.Single().Outcome);
        var done = h.WaitAsync(plan.Outcome!.OperationId!, window: TimeSpan.FromSeconds(30)).Result;
        Assert.Equal(PivotsDeviceStates.Completed, done.Devices.Single().State);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task AScriptRunWithoutTheBoxesIsAPlainSkippedRowAndThePlanStaysForAResend()
    {
        using var h = new PivotsHarness();
        var plan = Plan(h, PivotsChangeKind.RunScript, RunTargetKind.Device, "CLIENT01");
        var writer = Writer(h, new RecordingSiteHistory(), new RecordingMaintenanceLog());
        var bare = new PivotsPlan(plan.Action, plan.Change, new ChangePlan(plan.Plan.Title, plan.Plan.ActionName, plan.Plan.Items), plan.Entries);
        var refused = Confirm(bare, writer.RunFor(bare)).Outcome.Result!.Items.Single();
        Assert.True(refused.Skipped);
        Assert.StartsWith("Skipped: a script run needs the second confirmation for 1 device. Nothing was sent.", refused.Message);
        Assert.DoesNotContain("Exception", refused.Message);
        Assert.Equal(0, h.Site.Count("RunScript:"));
        Assert.Empty(h.AuditLines());

        var sent = await writer.ApplyAsync(Captured(plan), plan, new Progress<ChangePlanItemResult>(), CancellationToken.None);
        Assert.Equal("Sent", sent.Items.Single().OutcomeText);
        Assert.Equal(1, h.Site.Count("RunScript:"));
    }

    [Fact]
    public void ABuiltInCollectionWithoutItsBoxIsAPlainSkippedRow()
    {
        using var h = new PivotsHarness();
        var plan = Plan(h, PivotsChangeKind.RunPivot, RunTargetKind.Collection, "SMS00001", "OS");
        Assert.NotNull(plan.Plan.SecondConfirmation);
        var bare = new PivotsPlan(plan.Action, plan.Change with { EveryDeviceAcknowledged = true },
            new ChangePlan(plan.Plan.Title, plan.Plan.ActionName, plan.Plan.Items), plan.Entries);
        var rows = Confirm(bare, Writer(h, new RecordingSiteHistory(), new RecordingMaintenanceLog()).RunFor(bare)).Outcome.Result!.Items;
        Assert.All(rows, r => Assert.True(r.Skipped));
        Assert.All(rows, r => Assert.Contains("This runs on every device of the site", r.Message));
        Assert.Equal(0, h.Admin.Sends);
    }

    [Fact]
    public void AScriptWhoseTextChangedAfterThePlanIsAPlainSkippedRow()
    {
        using var h = new PivotsHarness();
        var plan = Plan(h, PivotsChangeKind.RunScript, RunTargetKind.Device, "CLIENT01");
        h.Site.Script(StubPivotsSite.ScriptGuid)!["Script"] = StubPivotsSite.Encoded(StubPivotsSite.ScriptText + "\n# changed");
        var history = new RecordingSiteHistory();
        var row = Confirm(plan, Writer(h, history, new RecordingMaintenanceLog()).RunFor(plan)).Outcome.Result!.Items.Single();
        Assert.True(row.Skipped);
        Assert.Contains("text on the site changed", row.Message);
        Assert.DoesNotContain("Exception", row.Message);
        Assert.Equal(0, h.Site.Count("RunScript:"));
        Assert.Equal(SiteOperationOutcome.Failed, history.Entries.Single().Outcome);
    }

    [Fact]
    public async Task ASecondSendOfTheSamePlanIsRefusedPlainlyAsADoubleClick()
    {
        using var h = new PivotsHarness();
        var plan = Plan(h, PivotsChangeKind.RunPivot, RunTargetKind.Device, "CLIENT01", "OS");
        var writer = Writer(h, new RecordingSiteHistory(), new RecordingMaintenanceLog());
        var confirmed = Captured(plan);
        var first = await writer.ApplyAsync(confirmed, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None);
        var second = await writer.ApplyAsync(confirmed, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None);
        Assert.Equal("Sent", first.Items.Single().OutcomeText);
        Assert.True(second.Items.Single().Skipped);
        Assert.Contains("used already", second.Items.Single().Message);
        Assert.Equal(1, h.Admin.Sends);
    }

    // ---- The page over the real service ---------------------------------------------------------

    private static (VM Page, ScriptedPlanDialogs Plans) Page(PivotsHarness h)
    {
        var plans = new ScriptedPlanDialogs();
        var log = new RecordingMaintenanceLog();
        var writer = new PivotsScriptsWriter(h.Service, new RecordingSiteHistory(), log, () => "MCM");
        var services = new PivotsScriptsPageServices(h.Service, h.Sessions, h.Audit, new FakePivotsReports(), new FakePivotsLibrary(), new FakePivotsDialogs(), plans, writer,
            () => new ApplicationPackagerUI.Models.AppSettings { SiteCode = "MCM", ProviderMachine = StubAdminService.Host }, () => null, _ => true, log) { FilterDelay = TimeSpan.Zero };
        return (new VM(services), plans);
    }

    [Fact]
    public async Task ThePageRunsAQueryThroughTheRealServiceAndTheWaitWritesTheOneFinishedRecord()
    {
        using var h = new PivotsHarness();
        var (vm, plans) = Page(h);
        await vm.OnNavigatedToAsync();
        await PivotsScriptsHarness.ResolveAsync(vm, RunTargetKind.Collection, "MCM00014");
        vm.QueryText = "OS | project Caption";
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.Single(plans.Shown);
        await vm.WaitTask;
        Assert.Equal("Completed", Assert.Single(vm.DeviceRows).State);
        Assert.Contains("Caption", vm.ResultColumns);
        Assert.False(vm.IsPartial);
        Assert.Equal(["Started", "Finished"], h.Audit.Read(10).Select(r => r.Phase));
        Assert.Equal("Succeeded", h.Finished().Single().Result);
        Assert.Equal(1, h.Admin.Sends);
    }

    [Fact]
    public async Task ThePageWaitsAgainForASavedRunAfterARestart()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 1000;
        var (first, _) = Page(h);
        await first.OnNavigatedToAsync();
        await PivotsScriptsHarness.ResolveAsync(first, RunTargetKind.Device, "CLIENT01");
        first.QueryText = "OS";
        first.WaitMinutes = 1;
        await first.RunPivotCommand.ExecuteAsync(null);
        await first.WaitTask;
        Assert.Equal("No response", Assert.Single(first.DeviceRows).State);
        Assert.Equal("Partial", h.Finished().Single().Result);

        h.Restart();
        h.Admin.AnswerAfterPolls = 0;
        var (vm, _) = Page(h);
        await vm.OnNavigatedToAsync();
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.EnsureTabLoadedAsync();
        vm.SelectedSession = vm.SessionRows.Single();
        Assert.True(vm.WaitAgainCommand.CanExecute(null));
        vm.WaitAgainCommand.Execute(null);
        await vm.WaitTask;
        Assert.Equal("Completed", Assert.Single(vm.DeviceRows).State);
        Assert.False(vm.IsPartial);
        Assert.Single(h.Finished());
        Assert.Equal(1, h.Admin.Sends);
    }
}
