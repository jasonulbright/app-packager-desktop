using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// The binding of a script action to the planned script, the plan confirmation of every change kind,
/// cut CMPivot results, the saved session without parameter values, the Finished record of a reloaded
/// run and the CSV import, through the real service and the real session script on the stub site.
/// </summary>
public sealed class PivotsBindingTests
{
    private const string Client01 = "CLIENT01";
    private const int Client01Id = 16777221;
    private const string PlannedSiteHash = "D08DF3F2F20F5C3A822B0FEDA476096F857C66ADC4CF60218C6118E85C7B0B16";

    private static Task<PivotsChange> PrepareAction(PivotsHarness h, PivotsChangeKind kind, string? comment = null) =>
        h.Service.PrepareAsync(PivotsHarness.Request(kind, guid: StubPivotsSite.ScriptGuid, comment: comment), CancellationToken.None);

    // ---- The send names the planned script ------------------------------------------------------

    [Fact]
    public async Task AScriptRunSendsThePlannedSiteHashAndVersionInTheOperationMessage()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01, new Dictionary<string, string> { ["Label"] = "a&b<c>", ["Count"] = "3" });
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Succeeded, o.Message);
        var run = Assert.Single(h.Site.Log, l => l.StartsWith("RunScript:", StringComparison.Ordinal));
        Assert.Equal($"RunScript:{StubPivotsSite.ScriptGuid}:devices {Client01Id}:Count=3(Int32);Label=a&b<c>(String):hash {PlannedSiteHash}:version 1", run);
        Assert.False(o.Partial);
    }

    [Fact]
    public async Task AScriptRunSendsTheDefaultOfAParameterWithoutAValue()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01, new Dictionary<string, string> { ["Label"] = "only" });
        await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.Contains(":Count=1(Int32);Label=only(String):", Assert.Single(h.Site.Log, l => l.StartsWith("RunScript:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AScriptThatChangesDuringTheSendMakesTheRunPartialAndIsInTheAuditLog()
    {
        using var h = new PivotsHarness();
        h.Site.SetOption("ChangeOnSend:" + StubPivotsSite.ScriptGuid, true);
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Sent);
        Assert.True(o.Partial);
        Assert.False(o.Succeeded);
        Assert.Contains("script changed during the send", o.Message);
        var records = h.Audit.Read(10);
        Assert.Contains(records, r => r.Phase == "Note" && r.Result == "Partial" && r.Message.Contains("script changed during the send", StringComparison.Ordinal));
        var finished = Assert.Single(records, r => r.Phase == "Finished");
        Assert.Equal("Partial", finished.Result);
        var session = h.Sessions.Read(o.SessionId!)!;
        Assert.True(session.ChangedDuringSend);
        Assert.True(session.Partial);
    }

    [Fact]
    public async Task ApproveSendsThePlannedHashesAndRefusesATextThatChangesAfterTheServiceCheck()
    {
        using var h = new PivotsHarness();
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var plan = await PrepareAction(h, PivotsChangeKind.ApproveScript, "lab");
        h.Site.BeforeRun = (values, site) =>
        {
            if ((string?)values["Operation"] == "Approve") site.Script(StubPivotsSite.ScriptGuid)!["Script"] = StubPivotsSite.Encoded("Remove-Item C:\\Data");
        };
        var o = await h.ApplyAsync(plan);
        Assert.False(o.Succeeded);
        Assert.Contains("changed after the plan was made", o.Message);
        var approve = h.Site.Runs.Single(r => (string?)r["Operation"] == "Approve");
        Assert.Equal(PlannedSiteHash, approve["SiteHash"]);
        Assert.Equal(plan.ScriptHash, approve["TextHash"]);
        Assert.Equal(0, h.Site.Count("Approve:"));
        Assert.Equal(0, (int)h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"]!);
    }

    [Fact]
    public async Task ATextThatChangesWhileTheApprovalIsStoredMakesTheActionPartial()
    {
        using var h = new PivotsHarness();
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        h.Site.SetOption("ChangeOnDecision:" + StubPivotsSite.ScriptGuid, true);
        var o = await h.ApplyAsync(await PrepareAction(h, PivotsChangeKind.ApproveScript, "lab"));
        Assert.False(o.Succeeded);
        Assert.True(o.Partial);
        Assert.Contains("script changed during the approval", o.Message);
        Assert.Equal("Partial", h.Audit.Read(5).Last().Result);
    }

    // ---- The service needs the plan confirmation for every kind ---------------------------------

    [Fact]
    public async Task EveryKindOfChangeIsRefusedWithoutThePlanConfirmation()
    {
        using var h = new PivotsHarness();
        var pivot = await h.PreparePivotAsync(RunTargetKind.Device, Client01);
        var script = await h.PrepareScriptAsync(RunTargetKind.Device, Client01);
        var submit = await h.Service.PrepareAsync(PivotsHarness.Request(PivotsChangeKind.SubmitScript, submitName: "PSQ-New", submitText: "Get-Date"), CancellationToken.None);
        var deny = await PrepareAction(h, PivotsChangeKind.DenyScript);
        var remove = await PrepareAction(h, PivotsChangeKind.RemoveScript);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var approve = await PrepareAction(h, PivotsChangeKind.ApproveScript);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 3;
        foreach (var plan in new[] { pivot, script with { RunConfirmed = true }, submit, approve, deny, remove })
        {
            var o = await h.ApplyAsync(plan with { ChangeConfirmed = false }, confirmed: false);
            Assert.StartsWith(PivotsScriptsService.SkippedPrefix + "the plan was not confirmed.", o.Message);
            Assert.False(o.Sent);
        }
        Assert.Equal(0, h.Admin.Sends);
        Assert.Equal(0, h.Site.Count("RunScript:") + h.Site.Count("New-CMScript:") + h.Site.Count("Approve:") + h.Site.Count("Deny:") + h.Site.Count("Remove-CMScript:"));
        Assert.Empty(h.AuditLines());
        Assert.False((await h.Service.PrepareAsync(pivot with { ChangeConfirmed = true }, CancellationToken.None)).ChangeConfirmed);
    }

    [Fact]
    public async Task TheWriterConfirmsAScriptActionOnlyFromTheConfirmedPlan()
    {
        using var h = new PivotsHarness();
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var prepared = await PrepareAction(h, PivotsChangeKind.ApproveScript, "lab");
        Assert.False(prepared.ChangeConfirmed);
        var (plan, problem) = PivotsScriptsText.BuildPlan(prepared);
        Assert.Null(problem);
        var writer = new PivotsScriptsWriter(h.Service, new RecordingSiteHistory(), new RecordingMaintenanceLog(), () => "MCM");
        var dialog = new ChangePlanViewModel(plan!.Plan, writer.RunFor(plan)) { DestructiveAcknowledged = true, ExtraAcknowledged = true, SecondAcknowledged = true };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        Assert.True(plan.Outcome!.Succeeded, plan.Outcome.Message);
        Assert.Equal(1, h.Site.Count("Approve:"));
    }

    // ---- Cut CMPivot results -----------------------------------------------------------------------

    [Fact]
    public async Task ACutResultWhoseWholeResultCannotBeReadMakesTheDeviceAndTheRunPartial()
    {
        using var h = new PivotsHarness();
        h.Admin.Large.Add(Client01Id);
        h.Admin.DeviceResultRefusals.Enqueue(404);
        h.Admin.DeviceResultRefusals.Enqueue(404);
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, "File('C:\\Windows\\System32\\*.dll') | project FileName, Size");
        var o = await h.ApplyAsync(plan);
        var d = o.Devices.Single();
        Assert.Equal(PivotsDeviceStates.Completed, d.State);
        Assert.True(d.MoreResults);
        Assert.Equal(3, d.Rows.Count);
        Assert.Contains("partial", d.Message);
        Assert.True(o.Partial);
        Assert.False(o.Succeeded);
        Assert.Equal(2, h.Admin.Requests.Count(r => r.PathAndQuery.Contains("CMPivotResult", StringComparison.Ordinal)));
        Assert.Equal("Partial", h.Audit.Read(5).Last().Result);
        Assert.True(h.Sessions.Read(o.SessionId!)!.Partial);
    }

    [Fact]
    public async Task ACutResultGetsTheWholeResultOnTheRetryAfterOneFailedRead()
    {
        using var h = new PivotsHarness();
        h.Admin.Large.Add(Client01Id);
        h.Admin.DeviceResultRefusals.Enqueue(404);
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, "File('C:\\Windows\\System32\\*.dll') | project FileName, Size");
        var o = await h.ApplyAsync(plan);
        var d = o.Devices.Single();
        Assert.Equal(1200, d.Rows.Count);
        Assert.False(d.MoreResults);
        Assert.False(o.Partial);
        Assert.True(o.Succeeded, o.Message);
        Assert.Equal(2, h.Admin.Requests.Count(r => r.PathAndQuery.Contains("CMPivotResult", StringComparison.Ordinal)));
    }

    // ---- The saved session holds no parameter value -------------------------------------------------

    [Fact]
    public async Task TheSessionFileHoldsTheParameterNamesAndHashesButNotTheValues()
    {
        using var h = new PivotsHarness();
        const string secret = "Hunter2-Token-9F3A";
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01, new Dictionary<string, string> { ["Label"] = secret }, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Sent, o.Message);
        var text = File.ReadAllText(Path.Combine(h.Sessions.Folder, o.SessionId + ".json"));
        Assert.DoesNotContain(secret, text);
        var session = h.Sessions.Read(o.SessionId!)!;
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), session.Parameters["Label"]);
        h.Restart();
        var waited = await h.WaitAsync(o.OperationId!, window: TimeSpan.FromSeconds(30), sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, waited.Devices.Single().State);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(h.Sessions.Folder, o.SessionId + ".json")));
    }

    [Fact]
    public void TheStoreHashesAValueThatACallerSaves()
    {
        using var h = new PivotsHarness();
        var record = new PivotsSessionRecord(Id: PivotsSessionStore.NewId(), CreatedUtc: DateTime.UtcNow, User: "u", Site: "MCM", Provider: StubAdminService.Host, Kind: PivotsChangeKind.RunScript,
            TargetDescription: "Device CLIENT01", CollectionId: null, Devices: [new ResolvedDevice(Client01Id, Client01)], QueryText: null, ScriptGuid: StubPivotsSite.ScriptGuid, ScriptName: "x",
            ScriptHash: "abc", Parameters: new Dictionary<string, string> { ["Password"] = "PlainValue-77" });
        h.Sessions.Save(record);
        h.Sessions.Update(record with { Status = "Sent" });
        var text = File.ReadAllText(Path.Combine(h.Sessions.Folder, record.Id + ".json"));
        Assert.DoesNotContain("PlainValue-77", text);
        Assert.StartsWith("sha256:", h.Sessions.Read(record.Id)!.Parameters["Password"]);
    }

    // ---- The Finished record of a reloaded run --------------------------------------------------------

    [Fact]
    public async Task AReloadedSendOnlyRunWhoseStartedRecordIsOlderThanTheReadWindowGetsItsFinishedRecord()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent, o.Message);
        var filler = string.Concat(Enumerable.Range(0, 5010).Select(i =>
            $"{{\"Timestamp\":\"2026-10-24T08:00:00\",\"User\":\"u\",\"Event\":\"RunPivot\",\"Phase\":\"Note\",\"RunId\":\"filler{i}\",\"SessionId\":\"other\",\"Message\":\"\"}}\n"));
        File.AppendAllText(h.Audit.Path, filler);
        h.Restart();
        var waited = await h.WaitAsync(o.OperationId!, window: TimeSpan.FromSeconds(30), sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, waited.Devices.Single().State);
        var started = h.Audit.Read(int.MaxValue).Single(r => r.SessionId == o.SessionId && r.Phase == "Started");
        var finished = Assert.Single(h.Audit.Read(10), r => r.SessionId == o.SessionId && r.Phase == "Finished");
        Assert.Equal(started.RunId, finished.RunId);
        Assert.True(h.Sessions.Read(o.SessionId!)!.FinishedRecorded);

        h.Restart();
        await h.WaitAsync(o.OperationId!, window: TimeSpan.FromSeconds(30), sessionId: o.SessionId);
        Assert.Single(h.Audit.Read(int.MaxValue), r => r.SessionId == o.SessionId && r.Phase == "Finished");
    }

    // ---- The CSV import --------------------------------------------------------------------------------

    [Fact]
    public void ACsvCommentWithALineBreakDoesNotAddATarget()
    {
        var csv = "Name,Comment\r\nWKS-001,\"first line\r\nCLIENT01\"\r\nWKS-002,\"say \"\"hi\"\", then\nCM01, DC01\"\r\n\"WKS-003\",plain\r\n";
        Assert.Equal(["WKS-001", "WKS-002", "WKS-003"], PivotsTargetFile.FromCsv(csv));
    }

    [Fact]
    public void ACsvNameThatHoldsALineBreakOrASeparatorIsNotImported()
    {
        var csv = "Comment;Name\n\"x\";\"WKS-001\nCLIENT01\"\n\"y\";\"WKS-002,CM01\"\n\"z\";WKS-004\n";
        Assert.Equal(["WKS-004"], PivotsTargetFile.FromCsv(csv));
    }

    // ---- The page says that parameter values are not stored ---------------------------------------

    [Fact]
    public async Task TheHistorySaysThatParameterValuesAreNotStored()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync(VM.HistoryTab);
        h.Sessions.Sessions.Add(new PivotsSessionRecord("session-2", new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc), @"LAB\admin", "LAB", "cm01.lab.local", PivotsChangeKind.RunScript,
            "Device WKS-001", null, [new ResolvedDevice(16777201, "WKS-001")], null, h.DiskScript.Guid, h.DiskScript.Name, h.DiskScript.Hash, new Dictionary<string, string> { ["Drive"] = "sha256:00" })
        {
            OperationId = "9", Status = "Completed",
            Results = [new DeviceResult(16777201, "WKS-001", PivotsDeviceStates.Completed, "ok", "")]
        });
        h.Audit.Records.Add(new PivotsAuditRecord(new DateTime(2026, 10, 24, 8, 0, 0), @"LAB\admin", "RunScript", "Started", "", "run-2", "Device WKS-001", 1, h.DiskScript.Guid, "abc", null, "Sending to 1 device."));
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        vm.SelectedHistory = vm.HistoryRows.Single();
        Assert.Contains("Parameter values are not stored.", vm.HistoryDetails);
        vm.SelectedSession = vm.SessionRows.Single(r => r.Key == "session-2");
        await vm.OpenSessionCommand.ExecuteAsync(null);
        Assert.Contains("Parameters: Drive. The values are not stored.", vm.ResultHeader);
    }
}
