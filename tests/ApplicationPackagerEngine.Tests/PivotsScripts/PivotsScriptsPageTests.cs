using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.PivotsScripts.PivotsScriptsHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The Pivots and Scripts page without a window, over the fake service: every flow of the brief.</summary>
public sealed class PivotsScriptsPageTests
{
    private static async Task<VM> ResolvedAsync(PivotsScriptsHarness h, int tab = VM.PivotTab, RunTargetKind kind = RunTargetKind.Collection, string text = "LAB00012")
    {
        var vm = await h.OpenAsync(tab);
        await ResolveAsync(vm, kind, text);
        Assert.NotNull(vm.CurrentTargets());
        return vm;
    }

    [Fact]
    public async Task TheConstructorReadsNothingAndThePivotTabMakesNoSiteCall()
    {
        var h = new PivotsScriptsHarness();
        var vm = h.Create();
        Assert.Equal(0, h.Library.Loads);
        await vm.OnNavigatedToAsync();
        Assert.Equal(0, h.Service.ResolveCalls + h.Service.LoadScriptCalls + h.Service.PrepareCalls);
        Assert.Equal(2, vm.QueryLibrary.Count);
        vm.SelectedTabIndex = VM.ScriptTab;
        await vm.EnsureTabLoadedAsync();
        Assert.Equal(1, h.Service.LoadScriptCalls);
        Assert.Equal(["Disk space", "Read key"], vm.ApprovedScripts.Select(r => r.Name));
        vm.SelectedTabIndex = VM.SiteScriptsTab;
        await vm.EnsureTabLoadedAsync();
        Assert.Equal(1, h.Service.LoadScriptCalls);
        Assert.Equal(3, vm.SiteScriptRows.Count);
    }

    [Fact]
    public async Task APageWithoutAProviderSaysWhatToSetAndReadsNothing()
    {
        var h = new PivotsScriptsHarness();
        h.Settings.ProviderMachine = "";
        var vm = await h.OpenAsync(VM.ScriptTab);
        Assert.Equal(VM.ProviderMessage, vm.ConnectionMessage);
        await ResolveAsync(vm);
        Assert.Equal(0, h.Service.ResolveCalls + h.Service.LoadScriptCalls);
        Assert.Equal(VM.ProviderMessage, vm.StatusMessage);
    }

    [Fact]
    public async Task ResolveShowsTheCountTheUnmatchedNamesAndTheBuiltInWarningBeforeAnyRun()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm, RunTargetKind.List, "WKS-001\r\nWKS-404\r\nwks-002");
        Assert.Equal(2, vm.Resolved!.Devices.Count);
        Assert.Contains("Not found (1): WKS-404.", vm.TargetSummary);
        Assert.False(vm.ShowBuiltInWarning);
        await ResolveAsync(vm, RunTargetKind.Collection, "All Systems");
        Assert.True(vm.ShowBuiltInWarning);
        Assert.Contains(VM.BuiltInWarning, vm.TargetSummary);
        Assert.Contains("7 devices", vm.TargetSummary);
        Assert.Contains("Online: 6.", vm.TargetSummary);
        Assert.Contains("Targets: 7 devices.", vm.PageSummary);
        Assert.Equal(0, h.Service.PrepareCalls);
    }

    [Fact]
    public async Task ChangingTheTargetAfterResolveClearsItAndARunNeedsResolveAgain()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        vm.TargetText = "LAB00013";
        Assert.Null(vm.CurrentTargets());
        Assert.Equal(VM.NotResolvedText, vm.TargetSummary);
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.Equal(VM.ResolveFirst, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);
        Assert.Empty(h.Plans.Shown);
    }

    [Fact]
    public async Task AResolveThatEndsAfterTheOperatorLeftIsDiscarded()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        h.Service.ResolveGate = new TaskCompletionSource();
        vm.TargetText = "LAB00012";
        var resolve = vm.ResolveCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.ResolveCalls == 1);
        await vm.OnNavigatedFromAsync();
        h.Service.ResolveGate.SetResult();
        await resolve;
        Assert.Null(vm.Resolved);
        Assert.False(vm.IsResolving);
        Assert.Equal(VM.NotResolvedText, vm.TargetSummary);
    }

    [Fact]
    public async Task AResolveWhoseTextChangedDuringTheReadIsDiscarded()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        h.Service.ResolveGate = new TaskCompletionSource();
        vm.TargetText = "LAB00012";
        var resolve = vm.ResolveCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.ResolveCalls == 1);
        vm.TargetText = "SMS00001";
        h.Service.ResolveGate.SetResult();
        await resolve;
        Assert.Null(vm.CurrentTargets());
        Assert.False(vm.IsResolving);
    }

    [Fact]
    public async Task TheQueryPlanShowsTheQueryTextTheDeviceCountAndEveryDeviceAndAsksOneConfirmation()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS | project Caption, Version";
        await vm.RunPivotCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.Equal("Run on 6 devices", plan.ActionName);
        Assert.Equal(6, plan.Items.Count);
        Assert.Equal("WKS-001 (ResourceID 16777201)", plan.Items[0].Target);
        Assert.Equal("Online state: Offline.", plan.Items[5].Detail);
        Assert.Equal("OS | project Caption, Version", plan.Body);
        Assert.False(plan.IsDestructive);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Null(plan.SecondConfirmation);
        Assert.Contains(plan.Notes, n => n.StartsWith("Targets: Collection Workstations (LAB00012). 6 devices.", StringComparison.Ordinal));
        Assert.Contains(PivotsScriptsText.StopWaitingNote, plan.Notes);
        var sent = Assert.Single(h.Service.Applied);
        Assert.False(sent.RunConfirmed);
        Assert.False(sent.EveryDeviceAcknowledged);
        Assert.Equal(PivotsChange.SendOnly, sent.ResultWindow);
        Assert.True(sent.IsSendOnly);
        await vm.WaitTask;
        Assert.Equal(1, h.Service.WaitCalls);
    }

    [Fact]
    public async Task ABuiltInCollectionNeedsTheEveryDeviceBoxAndWithoutItNothingIsSent()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, text: "SMS00001");
        vm.QueryText = "OS";
        h.Plans.AcknowledgeSecond = false;
        await vm.RunPivotCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.Equal("This runs on every device of the site: Collection All Systems (SMS00001).", plan.SecondConfirmation);
        Assert.Empty(h.Service.Applied);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);

        h.Plans.AcknowledgeSecond = true;
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(h.Service.Applied).EveryDeviceAcknowledged);
        await vm.WaitTask;
    }

    [Fact]
    public async Task TheScriptPlanShowsTheTextTheHashAndTheVersionThePrepareReadAndNeedsBothBoxes()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        await vm.ScriptDetailTask;
        h.Service.ChangeScriptText(h.DiskScript.Guid, "Get-PSDrive -PSProvider FileSystem");
        h.Plans.AcknowledgeExtra = false;
        await vm.RunScriptCommand.ExecuteAsync(null);
        var plan = Assert.Single(h.Plans.Shown);
        Assert.Equal("Get-PSDrive -PSProvider FileSystem", plan.Body);
        Assert.Equal("Script text", plan.BodyName);
        Assert.True(plan.IsDestructive);
        Assert.Equal(PivotsScriptsText.ScriptRunBox, plan.DestructiveConfirmation);
        Assert.Equal($"Run Disk space version 2 on 6 devices. Text SHA-256 starts with {FakePivotsService.Hash("Get-PSDrive -PSProvider FileSystem")[..16]}. "
            + "Parameters: the script has no parameter definitions on the site.", plan.ExtraConfirmation);
        Assert.Contains($"Text SHA-256: {FakePivotsService.Hash("Get-PSDrive -PSProvider FileSystem")}.", plan.Notes);
        Assert.Contains(PivotsScriptsText.NoUndo, plan.Notes);
        Assert.Empty(h.Service.Applied);

        h.Plans.AcknowledgeExtra = true;
        h.Plans.AcknowledgeDestructive = false;
        await vm.RunScriptCommand.ExecuteAsync(null);
        Assert.Empty(h.Service.Applied);

        h.Plans.AcknowledgeDestructive = true;
        await vm.RunScriptCommand.ExecuteAsync(null);
        var sent = Assert.Single(h.Service.Applied);
        Assert.True(sent.RunConfirmed);
        Assert.Equal(FakePivotsService.Hash("Get-PSDrive -PSProvider FileSystem"), sent.ScriptHash);
        await vm.WaitTask;
    }

    [Fact]
    public async Task AScriptWhoseTextChangedAfterThePlanIsRefusedAsSkippedAndNoWaitStarts()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        await vm.ScriptDetailTask;
        h.Service.BetweenPrepareAndApply = () => h.Service.ChangeScriptText(h.DiskScript.Guid, "Restart-Computer -Force");
        await vm.RunScriptCommand.ExecuteAsync(null);
        var result = h.Plans.LastOutcome!.Result!;
        Assert.All(result.Items, r => Assert.True(r.Skipped));
        Assert.All(result.Items, r => Assert.Equal(FakePivotsService.HashRefusal, r.Message));
        Assert.Equal(0, h.Service.WaitCalls);
        Assert.False(vm.IsWaiting);
        Assert.All(vm.DeviceRows, r => Assert.Equal("Skipped", r.State));
        Assert.Equal(SiteOperationOutcome.Failed, h.History.Entries.Single().Outcome);
    }

    [Fact]
    public async Task ACollectionThatGrewBetweenPrepareAndApplyIsRefused()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.BetweenPrepareAndApply = () => h.Service.Collections["LAB00012"].Members.Add(16777300);
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.All(h.Plans.LastOutcome!.Result!.Items, r => Assert.Equal(FakePivotsService.GrewRefusal, r.Message));
        Assert.Equal(0, h.Service.WaitCalls);
    }

    [Fact]
    public async Task ARunAboveTheCapIsRefusedAtThePlanWithTheMessageOfTheCheck()
    {
        var h = new PivotsScriptsHarness();
        for (var i = 0; i < FakePivotsService.MaxDevices + 1; i++) h.Service.AddDevice(20_000_000 + i, $"BULK-{i:00000}");
        var vm = await ResolvedAsync(h, kind: RunTargetKind.Pattern, text: "BULK-*");
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.Empty(h.Plans.Shown);
        Assert.StartsWith("The plan cannot run: The run targets 25001 devices; the cap is 25000.", vm.StatusMessage);
    }

    [Fact]
    public async Task APlanWhoseFormChangedDuringPrepareIsDiscarded()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.PrepareGate = new TaskCompletionSource();
        var run = vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.PrepareCalls == 1);
        vm.QueryText = "Service";
        h.Service.PrepareGate.SetResult();
        await run;
        Assert.Equal(VM.FormChanged, vm.StatusMessage);
        Assert.Empty(h.Plans.Shown);
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public async Task ADoubleClickOnRunPreparesOnce()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.PrepareGate = new TaskCompletionSource();
        var first = vm.RunPivotCommand.ExecuteAsync(null);
        Assert.False(vm.RunPivotCommand.CanExecute(null));
        var second = vm.RunPivotCommand.ExecuteAsync(null);
        h.Service.PrepareGate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, h.Service.PrepareCalls);
        Assert.Single(h.Plans.Shown);
        await vm.WaitTask;
    }

    [Fact]
    public async Task AProgressReportOfTheWaitShowsTheAnsweredDevicesAndOnlyTheFinalOutcomeSetsTheHeadline()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS | project Caption, Version";
        h.Service.WaitGate = new TaskCompletionSource();
        h.Service.ProgressState = id => id % 2 == 0 ? PivotsDeviceStates.Completed : PivotsDeviceStates.Running;
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.True(vm.IsWaiting);
        await WaitUntil(() => vm.WaitProgress.StartsWith("Waiting for results: 3 of", StringComparison.Ordinal));
        await WaitUntil(() => vm.DeviceRows.Count(r => r.State == "Completed") == 3);
        Assert.Equal(3, vm.DeviceRows.Count(r => r.State == "Running"));
        Assert.True(vm.IsPartial);
        Assert.DoesNotContain("Caption", vm.ResultColumns);
        Assert.Equal("", vm.LastRunHeadline);
        Assert.Contains("Waiting for results", vm.PageSummary);

        h.Service.WaitGate.SetResult();
        await vm.WaitTask;
        Assert.False(vm.IsWaiting);
        Assert.All(vm.DeviceRows, r => Assert.Equal("Completed", r.State));
        Assert.Equal(["Caption", "Version"], vm.ResultColumns);
        Assert.Equal(6, vm.ResultRows.Count);
        Assert.Equal("Last run: Completed 6.", vm.LastRunHeadline);
        Assert.False(vm.IsPartial);
        Assert.Equal("Completed", Assert.Single(vm.PipelineRows).State);
    }

    [Fact]
    public async Task StopWaitingShowsAPartialSetAndNeverMarksADeviceThatDidNotAnswerAsDone()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.WaitGate = new TaskCompletionSource();
        h.Service.ProgressState = id => id == 16777201 ? PivotsDeviceStates.Completed : PivotsDeviceStates.Running;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.WaitCalls == 1);
        Assert.True(vm.StopWaitingCommand.CanExecute(null));
        vm.StopWaitingCommand.Execute(null);
        await vm.WaitTask;
        Assert.True(vm.IsPartial);
        Assert.Contains(VM.StoppedText, vm.ResultHeader);
        Assert.Equal(1, vm.DeviceRows.Count(r => r.State == "Completed"));
        Assert.Equal(5, vm.DeviceRows.Count(r => r.State == "Running"));
        Assert.Equal("Stopped waiting", vm.PipelineRows.Single().State);
        Assert.EndsWith("Partial.", vm.LastRunHeadline);
        Assert.Contains("still run it", vm.StatusMessage);
    }

    [Fact]
    public async Task TimedOutAndNoResponseDevicesShowAsSuchAndTheSetIsPartial()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.FinalState = id => id == 16777206 ? PivotsDeviceStates.NoResponse : id == 16777205 ? PivotsDeviceStates.TimedOut : PivotsDeviceStates.Completed;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal("No response", vm.DeviceRows.Single(r => r.Device == "WKS-006").State);
        Assert.Equal("Timed out", vm.DeviceRows.Single(r => r.Device == "WKS-005").State);
        Assert.True(vm.IsPartial);
        Assert.Contains("Completed 4, Timed out 1, No response 1.", vm.ResultHeader);
        Assert.Contains(VM.PartialText, vm.ResultHeader);
    }

    [Fact]
    public async Task AWaitThatFailsShowsAPlainMessageWithoutTheExceptionText()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.WaitFailure = new HttpRequestException(@"https://cm01/AdminService/v1.0 returned 500 for \\cm01\secret");
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Contains(VM.WaitFailed, vm.ResultHeader);
        Assert.DoesNotContain("secret", vm.ResultHeader);
        Assert.True(vm.IsPartial);
    }

    [Fact]
    public async Task LeavingThePageDuringAWaitKeepsTheWaitAndShowsTheResultAndTheSummaryOnReturn()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.EnsureTabLoadedAsync();
        vm.SelectedTabIndex = VM.PivotTab;
        h.Service.WaitGate = new TaskCompletionSource();
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.WaitCalls == 1);
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.OnNavigatedFromAsync();
        Assert.False(h.Service.WaitTokens.Single().IsCancellationRequested);
        Assert.Empty(vm.DeviceRows);
        Assert.Null(vm.Resolved);
        h.Service.WaitGate.SetResult();
        await vm.WaitTask;
        Assert.Empty(vm.DeviceRows);
        await vm.OnNavigatedToAsync();
        Assert.Equal(6, vm.DeviceRows.Count(r => r.State == "Completed"));
        Assert.Contains("Last run: Completed 6.", vm.PageSummary);
        Assert.StartsWith("Site LAB on cm01.lab.local.", vm.PageSummary);
    }

    [Fact]
    public async Task ANewRunWaitsUntilThePageStoppedWaitingForTheLastOne()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.WaitGate = new TaskCompletionSource();
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.Equal(VM.WaitRunning, vm.StatusMessage);
        Assert.Equal(1, h.Service.PrepareCalls);
        h.Service.WaitGate.SetResult();
        await vm.WaitTask;
    }

    [Fact]
    public async Task TheWaitWindowIsTheOptionAndSurvivesAReload()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.WaitMinutes = 15;
        await vm.SaveAsync();
        Assert.Equal(15, h.Saved!.WaitMinutes);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal(TimeSpan.FromMinutes(15), vm.WaitWindow);
        Assert.Equal(TimeSpan.FromMinutes(15), h.Service.WaitWindows.Single().Window);
        Assert.False(h.Service.WaitTokens.Single().IsCancellationRequested);
        var again = h.Create();
        await again.OnNavigatedToAsync();
        Assert.Equal(15, again.WaitMinutes);
    }

    [Fact]
    public async Task ARequiredParameterWithoutAValueStopsTheRunAndTypedValuesReachThePrepare()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Read key");
        await vm.ScriptDetailTask;
        await vm.RunScriptCommand.ExecuteAsync(null);
        Assert.Equal(VM.NeedsParameters, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);

        h.Dialogs.OnParameters = p =>
        {
            p.Rows.Single(r => r.Name == "Depth").Value = "two";
            Assert.False(p.OkCommand.CanExecute(null));
            p.Rows.Single(r => r.Name == "Depth").Value = " 3 ";
            p.Rows.Single(r => r.Name == "Path").Value = @"HKLM:\SOFTWARE\Contoso; Restart-Computer";
        };
        await vm.EditParametersCommand.ExecuteAsync(null);
        Assert.Contains("Depth = 3", vm.ParametersText);
        await vm.RunScriptCommand.ExecuteAsync(null);
        var prepared = Assert.Single(h.Service.Prepared);
        Assert.Equal("3", prepared.Parameters!["Depth"]);
        Assert.Equal(@"HKLM:\SOFTWARE\Contoso; Restart-Computer", prepared.Parameters["Path"]);
        Assert.Contains(h.Plans.Shown.Single().Notes, n => n.StartsWith("Parameters: Path = ", StringComparison.Ordinal) && n.EndsWith("; Depth = 3.", StringComparison.Ordinal));
        await vm.WaitTask;
    }

    [Fact]
    public async Task ParametersTypedForAnOlderScriptTextDoNotTravelWithTheNewOne()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Read key");
        await vm.ScriptDetailTask;
        h.Dialogs.OnParameters = p => p.Rows.Single(r => r.Name == "Path").Value = "HKLM:\\SOFTWARE";
        vm.EditParametersCommand.Execute(null);
        h.Service.ChangeScriptText(h.ParamScript.Guid, "param([string]$Path) Get-Item $Path");
        await vm.RefreshScriptsCommand.ExecuteAsync(null);
        Assert.Equal("Read key", vm.SelectedApproved!.Name);
        await vm.RunScriptCommand.ExecuteAsync(null);
        Assert.Equal(VM.NeedsParameters, vm.StatusMessage);
    }

    [Fact]
    public async Task AFailedResolveOrPrepareShowsThePlainServiceTextOrTheFallbackAndNeverTheExceptionText()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        h.Service.ResolveFailure = new PivotsServiceException("The account has no Read right on the collection LAB00012.");
        await ResolveAsync(vm);
        Assert.Equal("The account has no Read right on the collection LAB00012.", vm.StatusMessage);
        h.Service.ResolveFailure = new InvalidOperationException(@"Provider \\cm01 said secret");
        await ResolveAsync(vm, text: "SMS00001");
        Assert.Equal(VM.ResolveFailed, vm.StatusMessage);
        h.Service.ResolveFailure = null;
        await ResolveAsync(vm);
        vm.QueryText = "OS";
        h.Service.PrepareFailure = new HttpRequestException("403 Forbidden at https://cm01/AdminService secret");
        await vm.RunPivotCommand.ExecuteAsync(null);
        Assert.Equal(VM.PrepareFailed, vm.StatusMessage);
        Assert.Empty(h.Plans.Shown);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheParametersDialogCancelKeepsTheEarlierValues()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Read key");
        await vm.ScriptDetailTask;
        h.Dialogs.OnParameters = p => p.Rows.Single(r => r.Name == "Path").Value = "HKLM:\\SOFTWARE";
        vm.EditParametersCommand.Execute(null);
        h.Dialogs.OnParameters = p => p.Rows.Single(r => r.Name == "Path").Value = "";
        vm.EditParametersCommand.Execute(null);
        Assert.Contains(@"Path = HKLM:\SOFTWARE", vm.ParametersText);
    }

    [Fact]
    public void TheParameterChecksFollowTheTypesTheSiteHolds()
    {
        static (string?, string) C(string type, string value, bool required = false) => PivotsParameterCheck.Check(new ScriptParameter("P", type, required, null), value);
        Assert.Equal(("42", ""), C("System.Int32", " 42 "));
        Assert.NotEqual("", C("System.Int32", "4294967296").Item2);
        Assert.NotEqual("", C("System.Boolean", "$True").Item2);
        Assert.Equal((null, "A value is required."), C("System.String", "  ", required: true));
        Assert.Equal(((string?)null, ""), C("System.String", ""));
        Assert.NotEqual("", C("System.String", "a'b").Item2);
        Assert.NotEqual("", C("System.DateTime", "2026-10-24 08:00").Item2);
    }

    [Fact]
    public async Task ApproveDenyAndRemoveEachShowTheirOwnPlanAndReadTheScriptsAgain()
    {
        var h = new PivotsScriptsHarness();
        h.Service.CurrentUser = @"LAB\approver";
        var vm = await h.OpenAsync(VM.SiteScriptsTab);
        vm.SelectedSiteScript = vm.SiteScriptRows.Single(r => r.Name == "Clear temp");
        await vm.ApproveScriptCommand.ExecuteAsync(null);
        var approve = h.Plans.Shown.Last();
        Assert.Equal("Approve Clear temp", approve.ActionName);
        Assert.Equal(@"Remove-Item $env:TEMP\* -Recurse", approve.Body);
        Assert.False(approve.IsDestructive);
        Assert.Equal("Approved", vm.SiteScriptRows.Single(r => r.Name == "Clear temp").State);
        Assert.Equal(3, vm.ApprovedScripts.Count);

        vm.SelectedSiteScript = vm.SiteScriptRows.Single(r => r.Name == "Clear temp");
        h.Dialogs.Comment = null;
        await vm.DenyScriptCommand.ExecuteAsync(null);
        Assert.Equal(VM.NotConfirmed, vm.StatusMessage);
        Assert.Single(h.Plans.Shown);
        h.Dialogs.Comment = "Removes user files.";
        await vm.DenyScriptCommand.ExecuteAsync(null);
        Assert.Contains("Comment: Removes user files.", h.Plans.Shown.Last().Notes);
        Assert.Equal("Removes user files.", h.Service.Applied.Last().Comment);

        vm.SelectedSiteScript = vm.SiteScriptRows.Single(r => r.Name == "Clear temp");
        await vm.RemoveScriptCommand.ExecuteAsync(null);
        var remove = h.Plans.Shown.Last();
        Assert.True(remove.IsDestructive);
        Assert.Equal("Remove Clear temp. Its run history goes with it.", remove.ExtraConfirmation);
        Assert.DoesNotContain(vm.SiteScriptRows, r => r.Name == "Clear temp");
        Assert.Equal(3, h.History.Entries.Count);
        Assert.Equal(["Approve script", "Deny script", "Remove script"], h.History.Entries.Select(e => e.Action));
    }

    [Fact]
    public async Task AnApprovalByTheAuthorIsReportedPlainlyAndTheScriptStaysWaiting()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync(VM.SiteScriptsTab);
        vm.SelectedSiteScript = vm.SiteScriptRows.Single(r => r.Name == "Clear temp");
        await vm.ApproveScriptCommand.ExecuteAsync(null);
        var row = h.Plans.LastOutcome!.Result!.Items.Single();
        Assert.Equal("Failed", row.OutcomeText);
        Assert.Equal(FakePivotsService.AuthorRefusal, row.Message);
        Assert.Contains(FakePivotsService.AuthorRefusal, vm.StatusMessage);
        Assert.Equal("Waiting for approval", vm.SiteScriptRows.Single(r => r.Name == "Clear temp").State);
        Assert.Single(h.Service.Applied);
    }

    [Fact]
    public async Task ASubmitThatFailsAfterTheCreateOffersToRemoveOnlyTheCreatedScript()
    {
        var h = new PivotsScriptsHarness();
        h.Service.SubmitFailsAfterCreate = true;
        var vm = await h.OpenAsync(VM.SiteScriptsTab);
        vm.DraftName = "Lab probe";
        vm.DraftText = "param([string]$Name) Get-Service $Name";
        await vm.SubmitDraftCommand.ExecuteAsync(null);
        var submit = h.Plans.Shown.Single();
        Assert.Equal("Submit Lab probe", submit.ActionName);
        Assert.Equal("param([string]$Name) Get-Service $Name", submit.Body);
        Assert.True(vm.HasLeftover);
        var created = vm.LeftoverGuid!;
        Assert.Contains(created, vm.StatusMessage);
        Assert.Contains("Remove created script", vm.StatusMessage);
        Assert.Contains(vm.SiteScriptRows, r => r.Guid == created);

        await vm.RemoveLeftoverCommand.ExecuteAsync(null);
        Assert.Equal(created, h.Service.Applied.Last().ScriptGuid);
        Assert.Equal(PivotsChangeKind.RemoveScript, h.Service.Applied.Last().Kind);
        Assert.False(vm.HasLeftover);
        Assert.DoesNotContain(vm.SiteScriptRows, r => r.Guid == created);
        Assert.Equal(3, vm.SiteScriptRows.Count);
    }

    [Fact]
    public async Task CheckReportsSyntaxErrorsAndAMissingCheckPlainly()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync(VM.SiteScriptsTab);
        vm.DraftText = "if ($true) {{";
        await vm.CheckDraftCommand.ExecuteAsync(null);
        Assert.Equal("1 syntax error: Line 1: Missing closing '}'.", vm.CheckResult);
        vm.DraftText = "Get-Date";
        await vm.CheckDraftCommand.ExecuteAsync(null);
        Assert.Equal(VM.CheckPassed + " The script has no parameters.", vm.CheckResult);
        vm.DraftText = "param([string]$Name) Get-Service $Name";
        await vm.CheckDraftCommand.ExecuteAsync(null);
        Assert.Equal(VM.CheckPassed + " Parameters the site will store: Name (System.String, required).", vm.CheckResult);
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public async Task TheLibraryPutsAQueryIntoTheEditorAndTheFolderOptionReachesTheLibrary()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        vm.SelectedQueryItem = vm.QueryLibrary[1];
        vm.UseQueryItemCommand.Execute(null);
        Assert.Equal("Service | where State == 'Stopped'", vm.QueryText);
        h.Dialogs.Folder = @"D:\Library";
        vm.BrowseLibraryFolderCommand.Execute(null);
        await WaitUntil(() => vm.QueryLibrary.Count > 0 && h.Library.Folders.Contains(@"D:\Library"));
        await vm.SaveAsync();
        Assert.Equal(@"D:\Library", h.Saved!.LibraryFolder);
    }

    [Fact]
    public async Task HistoryKeepsTheSelectedRecordByKeyAcrossAReloadAndOpensASessionReadOnly()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        var change = h.Service.Applied.Single();
        h.Sessions.Sessions.Add(new PivotsSessionRecord("session-1", new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc), @"LAB\admin", "LAB", "cm01.lab.local", PivotsChangeKind.RunScript,
            "Collection Workstations (LAB00012)", "LAB00012", change.Targets!.Devices, null, h.DiskScript.Guid, h.DiskScript.Name, h.DiskScript.Hash, [])
        {
            OperationId = "op-9", Status = "Partial", Partial = true,
            Results = [new DeviceResult(16777201, "WKS-001", PivotsDeviceStates.Failed, "", "Access is denied.") { ExitCode = 5 }],
            Raw = [new PivotsRawRecord(new DateTime(2026, 10, 24, 8, 0, 1, DateTimeKind.Utc), "Script", 200, "{\"ScriptOutput\":\"denied\"}")]
        });
        for (var i = 0; i < 3; i++)
            h.Audit.Records.Add(new PivotsAuditRecord(new DateTime(2026, 10, 24, 8, 0, 0), @"LAB\admin", "RunPivot", i == 0 ? "Started" : "Finished", i == 0 ? "" : "Succeeded", "run-1",
                "Collection Workstations (LAB00012)", 6, null, "abc", "op-1", "Sent to 6 devices.") { Verified = true, HasChain = true });
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.EnsureTabLoadedAsync();
        Assert.Equal(3, vm.HistoryRows.Count);
        vm.SelectedHistory = vm.HistoryRows.Single(r => r.Record.Phase == "Started");
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal(2, h.Audit.Reads);
        Assert.NotNull(vm.SelectedHistory);
        Assert.Equal("Started", vm.SelectedHistory!.Record.Phase);
        Assert.Contains(AuditVerification.Verified, vm.HistoryDetails);

        Assert.Contains("Text SHA-256: abc. Operation op-1.", vm.HistoryDetails);

        Assert.Equal(2, vm.SessionRows.Count);
        vm.SelectedSession = vm.SessionRows.Single(r => r.Key == "session-1");
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        Assert.Equal("session-1", vm.SelectedSession!.Key);
        await vm.OpenSessionCommand.ExecuteAsync(null);
        Assert.Equal(VM.ScriptTab, vm.SelectedTabIndex);
        Assert.StartsWith("Saved session of", vm.ResultHeader);
        Assert.Contains("read only", vm.ResultHeader);
        Assert.Equal("Failed", Assert.Single(vm.DeviceRows).State);
        Assert.Equal("5", vm.DeviceRows.Single().ExitCode);
        Assert.Contains("\"ScriptOutput\":\"denied\"", vm.RawText);
        Assert.True(vm.IsPartial);
        Assert.Equal("Last run: Completed 6.", vm.LastRunHeadline);
        Assert.Contains("Last run: Completed 6.", vm.PageSummary);
    }

    [Fact]
    public async Task WaitAgainReadsTheResultsOfASavedRunAndSendsNothing()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.WaitGate = new TaskCompletionSource();
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.WaitCalls == 1);
        vm.StopWaitingCommand.Execute(null);
        await vm.WaitTask;
        Assert.True(vm.IsPartial);
        h.Service.WaitGate = null;
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.EnsureTabLoadedAsync();
        vm.SelectedSession = vm.SessionRows.Single();
        Assert.True(vm.WaitAgainCommand.CanExecute(null));
        vm.WaitAgainCommand.Execute(null);
        await vm.WaitTask;
        Assert.Single(h.Service.Applied);
        Assert.Equal(2, h.Service.WaitCalls);
        Assert.Equal("session-op-1", h.Service.WaitWindows[1].SessionId);
        Assert.False(vm.IsPartial);
        Assert.All(vm.DeviceRows, r => Assert.Equal("Completed", r.State));
        Assert.Contains("\"OperationId\":\"op-1\"", vm.RawText);
        Assert.Equal(["Stopped waiting", "Completed"], vm.PipelineRows.Select(r => r.State));
    }

    [Fact]
    public async Task ADeviceWithoutAClientIsSkippedInThePlanAndTheCountLeavesItOut()
    {
        var h = new PivotsScriptsHarness();
        h.Service.AddDevice(16777400, "PRN-001", online: null, client: false);
        h.Service.Collections["LAB00012"].Members.Add(16777400);
        var vm = await ResolvedAsync(h);
        Assert.Contains("Without a ConfigMgr client: 1. A run skips them.", vm.TargetSummary);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        var plan = h.Plans.Shown.Single();
        Assert.Equal("Run on 6 devices", plan.ActionName);
        Assert.Equal(7, plan.Items.Count);
        Assert.Equal(("Skip", "No ConfigMgr client: the run skips the device."), (plan.Items[6].Action, plan.Items[6].Detail));
        Assert.Equal("Skipped", h.Plans.LastOutcome!.Result!.Items[6].OutcomeText);
        await vm.WaitTask;
        Assert.Equal(6, vm.DeviceRows.Count);
    }

    [Fact]
    public async Task AScriptWhoseParameterDefinitionsCannotBeReadCannotRun()
    {
        var h = new PivotsScriptsHarness();
        lock (h.Service.Scripts)
        {
            var i = h.Service.Scripts.FindIndex(s => s.Guid == h.DiskScript.Guid);
            h.Service.Scripts[i] = h.Service.Scripts[i] with { ParametersUnreadable = true };
        }
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        await vm.ScriptDetailTask;
        Assert.Contains(VM.ParametersUnreadable, vm.ScriptDetail);
        await vm.RunScriptCommand.ExecuteAsync(null);
        Assert.Equal(VM.ParametersUnreadable, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);
    }

    [Fact]
    public async Task SelectionToPivotAndToScriptSetAListTargetThatMustBeResolvedAgain()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        vm.ResultTabIndex = VM.DevicesView;
        vm.SetSelectedDevices(vm.DeviceRows.Take(2));
        vm.SelectionToScriptCommand.Execute(null);
        Assert.Equal(VM.ScriptTab, vm.SelectedTabIndex);
        Assert.Equal(RunTargetKind.List, vm.TargetKind);
        Assert.Equal($"WKS-001{Environment.NewLine}WKS-002", vm.TargetText);
        Assert.Null(vm.CurrentTargets());
        vm.ResultTabIndex = VM.ResultsView;
        vm.SetSelectedResults([]);
        vm.SelectionToPivotCommand.Execute(null);
        Assert.StartsWith("Select one or more rows", vm.StatusMessage);
    }

    [Fact]
    public async Task ExportsNameAPartialSetAndCopyAsPowerShellCarriesNoCellAsCode()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        h.Service.FinalState = id => id == 16777206 ? PivotsDeviceStates.NoResponse : PivotsDeviceStates.Completed;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        vm.ResultTabIndex = VM.DevicesView;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        var (format, report) = Assert.Single(h.Reports.Written);
        Assert.Equal("csv", format);
        Assert.True(report.Partial);
        Assert.Equal(6, report.Rows.Count);
        Assert.Contains("Partial", vm.StatusMessage);
        vm.ResultTabIndex = VM.RawView;
        await vm.ExportJsonCommand.ExecuteAsync(null);
        Assert.Equal(VM.ExportNotThisView, vm.StatusMessage);

        vm.ResultTabIndex = VM.ResultsView;
        vm.CopyAsPowerShellCommand.Execute(null);
        var text = Assert.Single(h.Dialogs.Clipboard);
        Assert.Matches(@"^\[Text\.Encoding\]::UTF8\.GetString\(\[Convert\]::FromBase64String\('[A-Za-z0-9+/=]+'\)\) \| ConvertFrom-Json$", text);
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text.Split('\'')[1]));
        Assert.Contains("\"Caption\":\"Windows 11 Enterprise\"", json);
    }

    [Fact]
    public void CopyAsPowerShellKeepsHostileCellsInsideTheEncodedText()
    {
        var text = PivotsScriptsText.CopyAsPowerShell(["Name", "Name"], [["x'); Remove-Item C:\\ -Recurse; ('", "‘quoted’\r\n$(Get-Date)"]]);
        Assert.Equal(2, text.Count(c => c == '\''));
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text.Split('\'')[1]));
        Assert.Contains("\"Name_\"", json);
    }

    [Fact]
    public async Task TheResultFilterWaitsForTheDelayAndCountsTheShownRows()
    {
        var h = new PivotsScriptsHarness { FilterDelay = TimeSpan.FromMilliseconds(250) };
        var vm = await ResolvedAsync(h);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        vm.ResultFilter = "WKS-00";
        vm.ResultFilter = "WKS-003";
        Assert.Equal(6, vm.DeviceRows.Count);
        await WaitUntil(() => vm.DeviceRows.Count == 1);
        Assert.Equal("1 of 6 result rows, 1 of 6 devices.", vm.ResultCount);
    }

    [Fact]
    public async Task TheTestConnectionResultIsPlainAndAFailureShowsNoExceptionText()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync(VM.OptionsTab);
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("The AdminService on cm01.lab.local answered.", vm.ConnectionResult);
        h.Service.ConnectionFailure = new System.Security.Authentication.AuthenticationException("The remote certificate is invalid: RemoteCertificateChainErrors at cm01 secret");
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(VM.ConnectionFailed, vm.ConnectionResult);
        h.Service.ConnectionFailure = new PivotsServiceException("The SMS Provider certificate is not trusted. Install the issuing CA certificate on this computer.");
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("The SMS Provider certificate is not trusted. Install the issuing CA certificate on this computer.", vm.ConnectionResult);
        Assert.Equal("LAB", vm.SiteCodeText);
    }

    [Fact]
    public async Task ImportReadsNamesFromACsvColumnAndSetsAListTarget()
    {
        var h = new PivotsScriptsHarness();
        var path = Path.Combine(Path.GetTempPath(), "apkg-pas-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, "Owner,Name\r\nIT,WKS-001\r\n\"IT, East\",\"WKS-002\"\r\nIT,wks-001\r\n");
        try
        {
            h.Dialogs.TargetFile = path;
            var vm = await h.OpenAsync();
            await vm.ImportTargetsCommand.ExecuteAsync(null);
            Assert.Equal(RunTargetKind.List, vm.TargetKind);
            Assert.Equal($"WKS-001{Environment.NewLine}WKS-002", vm.TargetText);
            Assert.StartsWith("Read 2 names from", vm.StatusMessage);
            Assert.Equal(0, h.Service.ResolveCalls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TheChoicesRoundTripAndAnOlderSettingsFileGivesTheDefaults()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        vm.SelectedTabIndex = VM.SiteScriptsTab;
        vm.ResultTabIndex = VM.PipelineView;
        vm.TargetKind = RunTargetKind.Pattern;
        await vm.SaveAsync();
        Assert.Equal((VM.SiteScriptsTab, VM.PipelineView, "Pattern"), (h.Saved!.SelectedTab, h.Saved.ResultTab, h.Saved.TargetKind));
        var again = h.Create();
        await again.OnNavigatedToAsync();
        Assert.Equal(VM.SiteScriptsTab, again.SelectedTabIndex);
        Assert.Equal(RunTargetKind.Pattern, again.TargetKind);

        var fresh = new PivotsScriptsHarness { Saved = new ApplicationPackagerUI.Models.PivotsScriptsSettings { SelectedTab = 99, WaitMinutes = 7, TargetKind = "Bogus" } };
        var defaults = fresh.Create();
        await defaults.OnNavigatedToAsync();
        Assert.Equal(VM.OptionsTab, defaults.SelectedTabIndex);
        Assert.Equal(VM.DefaultWaitMinutes, defaults.WaitMinutes);
        Assert.Equal(RunTargetKind.Collection, defaults.TargetKind);
    }

    [Fact]
    public async Task ThePageLogsCountsOnlyAndNoDeviceNameOrScriptText()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ResolvedAsync(h, VM.ScriptTab);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        await vm.ScriptDetailTask;
        await vm.RunScriptCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.NotEmpty(h.Log.Entries);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("WKS-", StringComparison.Ordinal) || e.Message.Contains("Get-CimInstance", StringComparison.Ordinal)
            || e.Message.Contains("LAB00012", StringComparison.Ordinal));
        var entry = h.History.Entries.Single();
        Assert.Equal(["done", "failed", "skipped", "targets"], entry.Counts!.Keys.Order());
        Assert.Equal(6, entry.Counts["done"]);
    }

    [Fact]
    public async Task TwentyThousandDevicesResolveRunAndShowWithOneResolveOnePrepareAndOneWait()
    {
        var h = new PivotsScriptsHarness();
        for (var i = 0; i < 20_000; i++) h.Service.AddDevice(30_000_000 + i, $"FLEET-{i:00000}", online: i % 3 != 0);
        var vm = await ResolvedAsync(h, kind: RunTargetKind.Pattern, text: "FLEET-*");
        Assert.Equal(20_000, vm.Resolved!.Devices.Count);
        vm.QueryText = "OS";
        h.Service.FinalState = id => id % 10 == 0 ? PivotsDeviceStates.NoResponse : PivotsDeviceStates.Completed;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal((1, 1, 1), (h.Service.ResolveCalls, h.Service.PrepareCalls, h.Service.WaitCalls));
        Assert.Equal(20_000, h.Plans.Shown.Single().Items.Count);
        Assert.Equal(20_000, vm.DeviceRows.Count);
        Assert.Equal(18_000, vm.ResultRows.Count);
        Assert.Contains($"Completed {PivotsLabels.Number(18_000)}, No response {PivotsLabels.Number(2_000)}.", vm.ResultHeader);
    }
}
