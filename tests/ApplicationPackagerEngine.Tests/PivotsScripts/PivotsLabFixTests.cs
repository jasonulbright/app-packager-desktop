using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using static ApplicationPackagerEngine.Tests.PivotsScripts.PivotsScriptsHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The defects of the real-window and real-ConfigMgr lab run, each through the page and the real service.</summary>
public sealed class PivotsLabFixTests
{
    private const string Client01 = "CLIENT01";
    private const int Cm01Id = 16777219;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private static async Task<VM> ScriptTabAsync(PivotsScriptsHarness h, string script = "Read key")
    {
        var vm = await h.OpenAsync(VM.ScriptTab);
        await ResolveAsync(vm);
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == script);
        await vm.ScriptDetailTask;
        return vm;
    }

    // ---- D1: the parameter definitions of a site script ----------------------------------------------

    [Fact]
    public async Task TheListReadOfTheSiteHoldsNoParameterDefinitionsAndTheReadOfOneScriptHoldsThem()
    {
        using var h = new PivotsHarness();
        var listed = (await h.Service.LoadScriptsAsync(CancellationToken.None)).Single(s => s.Guid == StubPivotsSite.ScriptGuid);
        Assert.Empty(listed.Parameters);
        Assert.Null(listed.Text);
        var full = await h.Service.LoadScriptAsync(StubPivotsSite.ScriptGuid, CancellationToken.None);
        Assert.Equal(["Label", "Count"], full!.Parameters.Select(p => p.Name));
        Assert.Equal(["System.String", "System.Int32"], full.Parameters.Select(p => p.Type));
    }

    [Fact]
    public async Task TheScriptTabReadsTheSelectedScriptAndShowsItsParameterDefinitionsAndBothHashes()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ScriptTabAsync(h);
        Assert.Equal(1, h.Service.LoadOneCalls);
        Assert.Equal("2", vm.SelectedApproved!.Parameters);
        Assert.Equal("Parameters: Path: no value; Depth = 1 (default of the site)." + VM.TypeValuesHint, vm.ParametersText);
        Assert.Contains($"{PivotsScriptsText.TextHashLabel} {h.ParamScript.Hash}.", vm.ScriptDetail);
        Assert.Contains($"{PivotsScriptsText.SiteHashLabel} {h.ParamScript.SiteHash}.", vm.ScriptDetail);
        Assert.DoesNotContain("SHA-256 " + h.ParamScript.SiteHash, vm.ScriptDetail);
        Assert.Equal(ScriptGridRow.NotRead, vm.ApprovedScripts.Single(r => r.Name == "Disk space").Parameters);

        PivotsParametersViewModel? shown = null;
        h.Dialogs.OnParameters = p => shown = p;
        vm.EditParametersCommand.Execute(null);
        Assert.Equal(["Path", "Depth"], shown!.Rows.Select(r => r.Name));
        Assert.Equal("How many levels to read.", shown.Rows[1].Description);
    }

    [Fact]
    public async Task TheRunPlanAndItsDeviceCountBoxNameEveryParameterWithItsValueOrTheSiteDefault()
    {
        var h = new PivotsScriptsHarness();
        var vm = await ScriptTabAsync(h);
        h.Dialogs.OnParameters = p => p.Rows.Single(r => r.Name == "Path").Value = @"HKLM:\SOFTWARE\Contoso";
        vm.EditParametersCommand.Execute(null);
        await vm.RunScriptCommand.ExecuteAsync(null);
        var plan = h.Plans.Shown.Single();
        const string line = @"Parameters: Path = HKLM:\SOFTWARE\Contoso; Depth = 1 (default of the site).";
        Assert.Contains(line, plan.Notes);
        Assert.EndsWith(line, plan.ExtraConfirmation);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("Parameters: none", StringComparison.Ordinal));
        Assert.Equal(@"HKLM:\SOFTWARE\Contoso", h.Service.Applied.Single().Parameters!["Path"]);
        await vm.WaitTask;
    }

    [Fact]
    public async Task ARunDuringTheReadOfTheScriptWaitsForItAndARunAfterAFailedReadSendsNothing()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync(VM.ScriptTab);
        await ResolveAsync(vm);
        h.Service.LoadOneGate = new TaskCompletionSource();
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Disk space");
        Assert.Contains(VM.ScriptReading, vm.ScriptDetail);
        Assert.Equal("", vm.ParametersText);
        var run = vm.RunScriptCommand.ExecuteAsync(null);
        Assert.Equal(VM.ScriptReading, vm.StatusMessage);
        Assert.Equal(0, h.Service.PrepareCalls);
        h.Service.LoadOneGate.SetResult();
        await run;
        Assert.Equal(1, h.Service.PrepareCalls);
        Assert.Equal(VM.NoParameterDefinitions, vm.ParametersText);
        await vm.WaitTask;

        h.Service.LoadOneGate = null;
        h.Service.LoadOneFailure = new InvalidOperationException(@"WMI said \\cm01\secret");
        vm.SelectedApproved = vm.ApprovedScripts.Single(r => r.Name == "Read key");
        await vm.ScriptDetailTask;
        Assert.Equal(VM.ScriptReadFailed, vm.ScriptDetail);
        await vm.RunScriptCommand.ExecuteAsync(null);
        Assert.Equal(VM.ScriptReadFailed, vm.StatusMessage);
        await vm.EditParametersCommand.ExecuteAsync(null);
        Assert.Equal(VM.ScriptReadFailed, vm.StatusMessage);
        Assert.Equal(1, h.Service.PrepareCalls);
    }

    [Fact]
    public void TheParameterChecksFollowTheRunScriptsRulesOfTheService()
    {
        static string Error(string type, string value) => PivotsParameterCheck.Check(new ScriptParameter("P", type, false, null), value).Error;
        Assert.Equal(("42", ""), PivotsParameterCheck.Check(new ScriptParameter("P", "System.Int32", false, null), " 42 "));
        Assert.NotEqual("", Error("System.Int32", "4294967296"));
        Assert.NotEqual("", Error("System.Int32", "seven"));
        Assert.NotEqual("", Error("System.String", "O'Brien"));
        Assert.NotEqual("", Error("System.String", "O’Brien"));
        Assert.NotEqual("", Error("System.String", "a\"b"));
        Assert.NotEqual("", Error("System.String", new string('x', PivotsScriptsService.MaxParameterLength + 1)));
        Assert.Equal("", Error("System.String", new string('x', PivotsScriptsService.MaxParameterLength)));
        Assert.Equal("", Error("System.String", "a $env:USERNAME; exit 7"));
        Assert.NotEqual("", Error("System.Boolean", "true"));
        Assert.Equal("", Error("System.Boolean", ""));
        foreach (var value in new[] { "O'Brien", "a\"b", "seven" })
            Assert.NotEmpty(PivotsScriptsService.ParameterProblems(new SiteScript("G", "N", "a", "b", SiteScriptStates.Approved, 1, "PowerShell", "h",
                [new ScriptParameter("P", value == "seven" ? "System.Int32" : "System.String", false, null)], "x"), new Dictionary<string, string> { ["P"] = value }));
    }

    [Fact]
    public async Task AValueTheParametersDialogTakesReachesTheClientOfTheRealServiceAsATypedValue()
    {
        using var h = new PivotsHarness();
        var script = (await h.Service.LoadScriptAsync(StubPivotsSite.ScriptGuid, CancellationToken.None))!;
        var dialog = new PivotsParametersViewModel(script, null);
        dialog.Rows.Single(r => r.Name == "Label").Value = "PSL-value";
        dialog.Rows.Single(r => r.Name == "Count").Value = " 3 ";
        dialog.OkCommand.Execute(null);
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01, dialog.Result, window: PivotsChange.SendOnly);
        Assert.False(plan.Blocked, string.Join(" ", plan.Checks.Select(c => c.Message)));
        Assert.Contains("Parameters: Label = PSL-value; Count = 3.", plan.Impact);
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Sent, o.Message);
        Assert.Contains(h.Site.Log, l => l.StartsWith($"RunScript:{StubPivotsSite.ScriptGuid}:devices 16777221:Count=3(Int32);Label=PSL-value(String)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePlanOfTheRealServiceNamesTheSiteDefaultsAndBothHashesWithTheirOwnLabels()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        Assert.Contains("Parameters: Label = none (default of the site); Count = 1 (default of the site).", plan.Impact);
        var script = plan.Script!;
        Assert.Contains(plan.Impact, l => l.Contains($"{PivotsScriptsService.TextHashLabel} {script.Hash}. {PivotsScriptsService.SiteHashLabel} {script.SiteHash}.", StringComparison.Ordinal));
        var (built, problem) = PivotsScriptsText.BuildPlan(plan);
        Assert.Null(problem);
        Assert.Contains($"{PivotsScriptsText.TextHashLabel}: {script.Hash}.", built!.Plan.Notes);
        Assert.Contains($"{PivotsScriptsText.SiteHashLabel}: {script.SiteHash}.", built.Plan.Notes);
        Assert.DoesNotContain(built.Plan.Notes, n => n.StartsWith("SHA-256", StringComparison.Ordinal));
        Assert.Contains($"{PivotsScriptsText.TextHashLabel} starts with {script.Hash[..PivotsScriptsText.HashPrefixLength]}.", built.Plan.ExtraConfirmation);
    }

    // ---- L1 and L2: the wait shows the devices that answered, and the status line follows the wait ---

    [Fact]
    public async Task AWaitAgainInTheSameSessionReportsTheDevicesThatAnsweredBeforeItsFirstPoll()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00015", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        var first = await h.WaitAsync(o.OperationId!, window: Window);
        Assert.Equal(PivotsDeviceStates.NoResponse, first.Devices.Single(d => d.Name == "CLIENT02").State);
        h.Reports.Clear();
        await h.WaitAsync(o.OperationId!, window: Window);
        var start = h.Reports.First();
        Assert.False(start.Final);
        Assert.Equal(PivotsDeviceStates.Completed, start.Devices.Single(d => d.Name == Client01).State);
        Assert.Contains("1 device of 2 answered", start.Message);
    }

    [Fact]
    public async Task TheDevicesThatAnsweredShowDuringTheWaitAndTheHeadlineWaitsForTheEnd()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm);
        vm.QueryText = "OS";
        h.Service.WaitGate = new TaskCompletionSource();
        h.Service.ProgressState = id => id % 2 == 0 ? PivotsDeviceStates.Completed : PivotsDeviceStates.Running;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.DeviceRows.Count(r => r.State == "Completed") == 3);
        Assert.True(vm.IsWaiting);
        Assert.True(vm.IsPartial);
        Assert.Contains("Waiting for results: 3 of 6 devices answered.", vm.ResultHeader);
        Assert.Equal("", vm.LastRunHeadline);
        h.Service.WaitGate.SetResult();
        await vm.WaitTask;
        Assert.Equal(6, vm.DeviceRows.Count(r => r.State == "Completed"));
        Assert.Equal("Last run: Completed 6.", vm.LastRunHeadline);
    }

    [Fact]
    public async Task TheStatusLineSaysThatTheWaitEndedAfterARunAStopAndAWaitAgain()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm);
        vm.QueryText = "OS";
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal("Run 1 ended. Completed 6.", vm.StatusMessage);

        h.Service.WaitGate = new TaskCompletionSource();
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.WaitCalls == 2);
        vm.StopWaitingCommand.Execute(null);
        await vm.WaitTask;
        Assert.StartsWith("Run 2 ended. ", vm.StatusMessage);
        Assert.EndsWith(VM.StoppedStatus, vm.StatusMessage);
        Assert.DoesNotContain("stops waiting", vm.StatusMessage);

        h.Service.WaitGate = null;
        vm.SelectedTabIndex = VM.HistoryTab;
        await vm.EnsureTabLoadedAsync();
        vm.SelectedSession = vm.SessionRows.First();
        vm.WaitAgainCommand.Execute(null);
        Assert.Equal(VM.WaitAgainStarted, vm.StatusMessage);
        await vm.WaitTask;
        Assert.Equal("Run 3 ended. Completed 6.", vm.StatusMessage);
    }

    // ---- L3: a refusal at Apply is in the audit log ------------------------------------------------

    [Fact]
    public async Task ARunThatTheGrownCollectionStopsAtApplyWritesOneRefusedRecordAndSendsNothing()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014");
        h.Site.List("Members").Add(StubPivotsSite.Member("MCM00014", Cm01Id, "CM01"));
        var o = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.False(o.Sent);
        Assert.Equal(0, h.Admin.Sends);
        var record = Assert.Single(h.Audit.Read(10));
        Assert.Equal(PivotsScriptsService.RefusedPhase, record.Phase);
        Assert.Equal("Skipped", record.Result);
        Assert.Equal(0, record.DeviceCount);
        Assert.Equal("RunPivot", record.Event);
        Assert.Contains("CM01", record.Message);
        Assert.Null(record.OperationId);
    }

    [Fact]
    public async Task AScriptRunAndAnApprovalThatAChangedTextStopsAtApplyEachWriteOneRefusedRecord()
    {
        using var h = new PivotsHarness();
        var run = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["Script"] = StubPivotsSite.Encoded(StubPivotsSite.ScriptText + "\nRemove-Item C:\\Data");
        var o = await h.ApplyAsync(run with { RunConfirmed = true });
        Assert.Contains("changed after the plan was made", o.Message);
        Assert.Equal(0, h.Site.Count("RunScript:"));
        var record = Assert.Single(h.Audit.Read(10));
        Assert.Equal((PivotsScriptsService.RefusedPhase, "Skipped", "RunScript", StubPivotsSite.ScriptGuid), (record.Phase, record.Result, record.Event, record.ScriptGuid));

        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var approve = await h.Service.PrepareAsync(PivotsHarness.Request(PivotsChangeKind.ApproveScript, guid: StubPivotsSite.ScriptGuid), CancellationToken.None);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["Script"] = StubPivotsSite.Encoded(StubPivotsSite.ScriptText);
        var refused = await h.ApplyAsync(approve);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, refused.Message);
        Assert.Equal(2, h.Audit.Read(10).Count(r => r.Phase == PivotsScriptsService.RefusedPhase));
        Assert.DoesNotContain(h.Audit.Read(10), r => r.Phase is "Started" or "Finished");
    }

    // ---- L6: a CSV name that the import drops is named ------------------------------------------------

    [Fact]
    public async Task ACsvNameWithALineBreakIsNotImportedAndTheStatusSaysSo()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        var file = Path.Combine(Path.GetTempPath(), "apkg-psl6-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(file, "Name,Comment\r\nWKS-001,a\r\n\"WKS-\r\n002\",b\r\nWKS-003,\"has CM01, inside\"\r\n");
        try
        {
            h.Dialogs.TargetFile = file;
            await vm.ImportTargetsCommand.ExecuteAsync(null);
            Assert.Equal($"Read 2 names from {Path.GetFileName(file)}. {VM.DroppedNamesText(1)} Select Resolve.", vm.StatusMessage);
            Assert.Equal("WKS-001" + Environment.NewLine + "WKS-003", vm.TargetText);
        }
        finally { File.Delete(file); }
    }

    // ---- L8: the device count of a filter names the devices of the shown rows ---------------------------

    [Fact]
    public async Task AFilterThatMatchesOnlyAResultCellCountsTheDeviceOfThatRow()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm);
        vm.QueryText = "OS | project Caption, Version";
        h.Service.Shape = r => r.ResourceId == 16777201 ? r with { Rows = [new Dictionary<string, string> { ["Caption"] = "Windows Server 2025", ["Version"] = "10.0.26100" }] } : r;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        vm.ResultFilter = "Server";
        vm.FlushFilters();
        Assert.Equal($"1 of {PivotsLabels.Count(6, "result row")}, 1 of {PivotsLabels.Count(6, "device")}.", vm.ResultCount);
        Assert.Equal("WKS-001", vm.DeviceRows.Single().Device);
    }

    // ---- L9: the app closes while the page waits ------------------------------------------------------

    [Fact]
    public async Task ClosingTheAppDuringAWaitWritesTheFinishedRecordWithTheWaitAgainNoteAndALaterWaitWritesNoSecond()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 100;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        Assert.Equal(0, h.Service.EndWaitsAtExit());
        using var cts = new CancellationTokenSource();
        var written = -1;
        h.OnDelay = n =>
        {
            if (n != 2) return;
            written = h.Service.EndWaitsAtExit();
            cts.Cancel();
        };
        await h.WaitAsync(o.OperationId!, cts.Token, window: Window);
        Assert.Equal(1, written);
        var finished = Assert.Single(h.Finished());
        Assert.Equal("Partial", finished.Result);
        Assert.Equal(PivotsScriptsService.ExitDuringWaitMessage, finished.Message);
        Assert.Equal(o.SessionId, finished.SessionId);
        Assert.Equal(h.Audit.Read(10).First().RunId, finished.RunId);

        h.OnDelay = null;
        h.Admin.AnswerAfterPolls = 0;
        h.Restart();
        var again = await h.WaitAsync(o.OperationId!, window: Window, sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, again.Devices.Single().State);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task ThePageAsksTheServiceForTheFinishedRecordOnlyWhenItWaitsAndStopsItsWait()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        vm.OnAppClosing();
        Assert.Equal(0, h.Service.ExitCalls);
        await ResolveAsync(vm);
        vm.QueryText = "OS";
        h.Service.WaitGate = new TaskCompletionSource();
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.WaitCalls == 1);
        vm.OnAppClosing();
        Assert.Equal(1, h.Service.ExitCalls);
        Assert.True(h.Service.WaitTokens.Single().IsCancellationRequested);
        await vm.WaitTask;
    }
}
