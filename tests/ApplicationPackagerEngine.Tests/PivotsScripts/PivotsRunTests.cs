using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>CMPivot and script runs through the real service, the real session script on the stub site, and the stub administration service.</summary>
public sealed class PivotsRunTests
{
    private const string Secret = "SECRET-DETAIL-1234";

    private static void NoSecrets(PivotsOutcome o)
    {
        Assert.DoesNotContain(Secret, o.Message);
        Assert.All(o.Devices, d => Assert.DoesNotContain(Secret, d.Message));
        Assert.DoesNotContain("Exception", o.Message);
    }

    [Fact]
    public async Task ACollectionPivotRunSavesTheSessionAndAuditsBeforeTheSendAndReadsTheResult()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014");
        Assert.False(plan.Blocked);
        Assert.Equal(1, plan.DeviceCount);
        Assert.Contains(plan.Impact, l => l.Contains("cannot be undone", StringComparison.Ordinal));
        var seenAtSend = (Audit: 0, Sessions: 0);
        h.Admin.BeforeSend = () =>
        {
            seenAtSend = (h.AuditLines().Count(l => l.Contains("\"Phase\":\"Started\"", StringComparison.Ordinal)), Directory.GetFiles(h.Sessions.Folder, "*.json").Length);
            return Task.CompletedTask;
        };
        var o = await h.ApplyAsync(plan);
        Assert.Equal((1, 1), seenAtSend);
        Assert.True(o.Succeeded, o.Message);
        Assert.True(o.Sent);
        Assert.False(o.Partial);
        var d = Assert.Single(o.Devices);
        Assert.Equal(PivotsDeviceStates.Completed, d.State);
        Assert.Equal("Microsoft Windows 11 Enterprise Evaluation", d.Rows.Single()["Caption"]);
        Assert.Single(h.Admin.Requests, r => r.Method == "POST" && r.PathAndQuery == "/AdminService/v1.0/Collections('MCM00014')/AdminService.RunCMPivot");
        Assert.Equal(h.Admin.StatusReads, h.Delays + 1);
        var session = h.Sessions.Read(o.SessionId!)!;
        Assert.Equal("Completed", session.Status);
        Assert.Equal(o.OperationId, session.OperationId);
        Assert.Equal(PivotsDeviceStates.Completed, session.Results.Single().State);
        Assert.Contains(session.Raw, r => r.Phase == "Submit" && r.HttpStatus == 200);
        var records = h.Audit.Read(10);
        Assert.Equal(["Started", "Finished"], records.Select(r => r.Phase));
        Assert.Equal("Succeeded", records[1].Result);
        Assert.Equal(o.OperationId, records[1].OperationId);
        Assert.All(records, r => Assert.True(r.Verified));
        Assert.Contains(h.Reports, r => !r.Final && r.Devices.All(x => x.State == PivotsDeviceStates.Sent));
    }

    [Fact]
    public async Task AnOfflineDeviceEndsAsNoResponseAndTheResultIsPartial()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00015", window: TimeSpan.FromSeconds(30));
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.Offline && c.Result == DeploymentCheckResults.Warn);
        Assert.Contains(plan.Impact, l => l.Contains("waits up to 30 seconds", StringComparison.Ordinal));
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Partial);
        Assert.False(o.Succeeded);
        Assert.Equal(PivotsDeviceStates.Completed, o.Devices.Single(d => d.Name == "CLIENT01").State);
        var offline = o.Devices.Single(d => d.Name == "CLIENT02");
        Assert.Equal(PivotsDeviceStates.NoResponse, offline.State);
        Assert.Contains("offline device does not get the request", offline.Message);
        Assert.Contains("partial", o.Message);
        Assert.Equal("Partial", h.Audit.Read(5).Last().Result);
    }

    [Fact]
    public async Task AListRunSendsTheFixedClientIdsOnly()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.List, "CLIENT01, CLIENT02, x64 Unknown Computer (x64 Unknown Computer)", window: TimeSpan.FromSeconds(30));
        Assert.Equal(2, plan.DeviceCount);
        Assert.Contains(plan.Impact, l => l.Contains("without a ConfigMgr client", StringComparison.Ordinal));
        var o = await h.ApplyAsync(plan);
        var post = Assert.Single(h.Admin.Requests, r => r.Method == "POST");
        Assert.Equal("/AdminService/v1.0/SMS_CMPivotStatus/AdminService.RunCMPivot", post.PathAndQuery);
        Assert.Contains("\"ResourceIds\":\"16777221,16777222\"", post.Body);
        Assert.Equal(2, o.Devices.Count);
    }

    [Fact]
    public async Task AQuerySyntaxErrorIsRefusedPlainlyAndNothingIsPolled()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", "OS | wherez bogus");
        var o = await h.ApplyAsync(plan);
        Assert.False(o.Succeeded);
        Assert.False(o.Partial);
        Assert.Contains("HTTP 400", o.Message);
        Assert.Contains("Nothing reached a client", o.Message);
        Assert.Equal(PivotsDeviceStates.Failed, o.Devices.Single().State);
        Assert.Equal(0, h.Admin.StatusReads);
        Assert.Equal("Failed", h.Audit.Read(5).Last().Result);
        Assert.Equal("Failed", h.Sessions.Read(o.SessionId!)!.Status);
    }

    [Fact]
    public async Task ABuiltInCollectionNeedsTheEveryDeviceBoxInTheService()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "SMS00001", window: TimeSpan.FromSeconds(30));
        Assert.True(plan.NeedsEveryDeviceAcknowledgement);
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.BuiltIn);
        var refused = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, refused.Message);
        Assert.Contains("every device of the site", refused.Message);
        Assert.Equal(0, h.Admin.Sends);
        Assert.Empty(h.AuditLines());
        var o = await h.ApplyAsync(plan with { EveryDeviceAcknowledged = true });
        Assert.True(o.Sent);
        Assert.Equal(1, h.Admin.Sends);
    }

    [Fact]
    public async Task APlanThatWasNotPreparedOrWasChangedIsRefused()
    {
        using var h = new PivotsHarness();
        var targets = await h.ResolveAsync(RunTargetKind.Device, "CLIENT01");
        var raw = PivotsHarness.Request(PivotsChangeKind.RunPivot, targets, "OS");
        Assert.Contains("not prepared", (await h.ApplyAsync(raw with { Fingerprint = "abc" })).Message);
        var plan = await h.Service.PrepareAsync(raw, CancellationToken.None);
        Assert.Contains("differs from the prepared plan", (await h.ApplyAsync(plan with { QueryText = "Process" })).Message);
        var more = targets with { Devices = [.. targets.Devices, new ResolvedDevice(16777219, "CM01")] };
        Assert.Contains("differs from the prepared plan", (await h.ApplyAsync(plan with { Targets = more })).Message);
        Assert.Equal(0, h.Admin.Sends);
    }

    [Fact]
    public async Task ACollectionThatGrewAfterThePlanRefusesTheRun()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014");
        h.Site.List("Members").Add(StubPivotsSite.Member("MCM00014", 16777219, "CM01"));
        var o = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.Contains("1 device that the plan does not hold (CM01)", o.Message);
        Assert.Equal(0, h.Admin.Sends);
        Assert.Empty(h.AuditLines());
    }

    [Fact]
    public async Task ACollectionThatLostAMemberStillRuns()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00015", window: TimeSpan.FromSeconds(30));
        var members = h.Site.List("Members");
        members.Remove(members.OfType<JsonObject>().First(m => (string?)m["CollectionID"] == "MCM00015" && (int)m["ResourceID"]! == 16777222));
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent);
    }

    [Fact]
    public async Task MoreDevicesThanTheCapAreRefusedAtPrepareAndApply()
    {
        var state = StubPivotsSite.Lab01();
        var collections = state["Collections"]!.AsArray();
        collections.Add(new JsonObject { ["CollectionID"] = "MCM00099", ["Name"] = "Big", ["MemberCount"] = 2001 });
        for (var i = 0; i < PivotsScriptsService.MaxDevicesPerRun + 1; i++)
        {
            state["Devices"]!.AsArray().Add(StubPivotsSite.Device(40_000_000 + i, "BIG" + i));
            state["Members"]!.AsArray().Add(StubPivotsSite.Member("MCM00099", 40_000_000 + i, "BIG" + i));
        }
        using var h = new PivotsHarness(state);
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00099");
        Assert.True(plan.Blocked);
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.Cap && c.Message.Contains("at most 2,000", StringComparison.Ordinal));
        Assert.Null(plan.Fingerprint);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, (await h.ApplyAsync(plan)).Message);
        Assert.Equal(0, h.Admin.Sends);
    }

    [Fact]
    public async Task AnAuditLogThatCannotBeWrittenBlocksTheRunAtPrepareAndAtApply()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-psq-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "audit.jsonl");
        try
        {
            using (var h = new PivotsHarness(auditPath: path))
            {
                var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
                Assert.False(plan.Blocked);
                File.WriteAllText(path, "");
                File.SetAttributes(path, FileAttributes.ReadOnly);
                var o = await h.ApplyAsync(plan);
                Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
                Assert.Contains("audit log cannot be written, so nothing was sent", o.Message);
                Assert.Equal(0, h.Admin.Sends);
                Assert.False(Directory.Exists(h.Sessions.Folder) && Directory.GetFiles(h.Sessions.Folder).Length > 0);
                var again = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
                Assert.True(again.Blocked);
                Assert.Contains(again.Checks, c => c.Name == PivotsScriptsService.CheckNames.AuditLog && c.Result == DeploymentCheckResults.Fail);
            }
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ASessionThatCannotBeSavedBlocksTheRun()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        Directory.CreateDirectory(Path.GetDirectoryName(h.Sessions.Folder)!);
        File.WriteAllText(h.Sessions.Folder, "a file where the folder goes");
        var o = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.Contains("session could not be saved", o.Message);
        Assert.Equal(0, h.Admin.Sends);
        Assert.Empty(h.AuditLines());
    }

    [Fact]
    public async Task CancelBeforeTheSendSendsNothing()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var o = await h.ApplyAsync(plan, cts.Token);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.False(o.Sent);
        Assert.Equal(0, h.Admin.Sends);
    }

    [Fact]
    public async Task StopWaitingEndsTheWaitOnlyAndSaysThatClientsStillRunIt()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 100;
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014");
        using var cts = new CancellationTokenSource();
        h.OnDelay = n => { if (n == 2) cts.Cancel(); };
        var o = await h.ApplyAsync(plan, cts.Token);
        Assert.True(o.Sent);
        Assert.True(o.Partial);
        Assert.False(o.Succeeded);
        Assert.Contains("Stopped waiting", o.Message);
        Assert.Contains("still run it", o.Message);
        var d = o.Devices.Single();
        Assert.Equal(PivotsDeviceStates.Sent, d.State);
        Assert.Contains("still runs it", d.Message);
        Assert.Equal("Stopped waiting", h.Sessions.Read(o.SessionId!)!.Status);
        Assert.Equal("Partial", h.Audit.Read(5).Last().Result);

        h.Admin.AnswerAfterPolls = 0;
        var again = await h.Service.WaitForResultsAsync(o.OperationId!, new PivotsHarness.SyncProgress<PivotsOutcome>(_ => { }), CancellationToken.None);
        Assert.Equal(PivotsDeviceStates.Completed, again.Devices.Single().State);
        Assert.Equal(1, h.Admin.Sends);
        await Assert.ThrowsAsync<PivotsServiceException>(() => h.Service.WaitForResultsAsync("123", new PivotsHarness.SyncProgress<PivotsOutcome>(_ => { }), CancellationToken.None));
    }

    [Fact]
    public async Task ASendThatBreaksOnTheWireIsNotReportedAsNotSent()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        h.Admin.BreakNextSend = HttpRequestError.ResponseEnded;
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent);
        Assert.True(o.Partial);
        Assert.Contains("can have reached the site", o.Message);
        Assert.Equal(PivotsDeviceStates.NoResponse, o.Devices.Single().State);
        NoSecrets(o);
        Assert.Equal("Unknown", h.Sessions.Read(o.SessionId!)!.Status);
    }

    [Fact]
    public async Task AProviderRestartDuringTheWaitDoesNotLoseTheResult()
    {
        using var h = new PivotsHarness();
        h.Admin.Unavailable = 3;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Succeeded, o.Message);
        Assert.Equal(PivotsDeviceStates.Completed, o.Devices.Single().State);
    }

    [Fact]
    public async Task ACutStatusRowGetsTheWholeResultWithOneMoreRequest()
    {
        using var h = new PivotsHarness();
        h.Admin.Large.Add(16777221);
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", "File('C:\\Windows\\System32\\*.dll') | project FileName, Size");
        var o = await h.ApplyAsync(plan);
        var d = o.Devices.Single();
        Assert.Equal(1200, d.Rows.Count);
        Assert.False(d.MoreResults);
        Assert.False(d.Rows[0].ContainsKey("Device"));
        Assert.Single(h.Admin.Requests, r => r.PathAndQuery.Contains("CMPivotResult", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedDeviceShowsAFixedMessageAndKeepsTheSiteTextInTheOutput()
    {
        using var h = new PivotsHarness();
        h.Admin.Fails.Add(16777221);
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        var o = await h.ApplyAsync(plan);
        var d = o.Devices.Single();
        Assert.Equal(PivotsDeviceStates.Failed, d.State);
        Assert.DoesNotContain(Secret, d.Message);
        Assert.Contains(Secret, d.Output);
        Assert.Equal("Completed with failures", h.Sessions.Read(o.SessionId!)!.Status);
    }

    [Fact]
    public async Task APreparedPlanRunsOnceAndARefusedConfirmationDoesNotUseItUp()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01");
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, (await h.ApplyAsync(plan)).Message);
        var first = h.ApplyAsync(plan with { RunConfirmed = true });
        var second = await h.ApplyAsync(plan with { RunConfirmed = true });
        var one = await first;
        Assert.True(one.Sent);
        Assert.Contains("used already", second.Message);
        Assert.Equal(1, h.Site.Count("Invoke-CMScript:"));
    }

    [Fact]
    public async Task EachPreparationHasItsOwnTokenSoAnOlderCopyCannotUseALaterOne()
    {
        using var h = new PivotsHarness();
        var first = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        var second = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01");
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(PivotsScriptsService.ContentOf(first.Fingerprint), PivotsScriptsService.ContentOf(second.Fingerprint));
        Assert.True((await h.ApplyAsync(first)).Sent);
        Assert.Contains("used already", (await h.ApplyAsync(first)).Message);
        Assert.True((await h.ApplyAsync(second)).Sent);
        Assert.Equal(2, h.Admin.Sends);
    }

    // ---- Scripts --------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Typed = new() { ["Label"] = "typed", ["Count"] = "3" };

    [Fact]
    public async Task AScriptRunNeedsTheSecondConfirmationInTheService()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01", Typed);
        Assert.False(plan.Blocked, string.Join(" ", plan.Checks.Select(c => c.Message)));
        Assert.True(plan.NeedsRunConfirmation);
        Assert.Equal(StubPivotsSite.ScriptText, plan.Script!.Text);
        Assert.Equal(PivotsLibrary.HashOf(StubPivotsSite.ScriptText), plan.ScriptHash);
        Assert.Contains(plan.Impact, l => l.Contains(plan.ScriptHash!, StringComparison.Ordinal) && l.Contains(StubPivotsSite.ScriptGuid, StringComparison.Ordinal));
        var refused = await h.ApplyAsync(plan);
        Assert.Contains("second confirmation for 1 device", refused.Message);
        Assert.Equal(0, h.Site.Count("Invoke-CMScript:"));
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Succeeded, o.Message);
        Assert.Contains(h.Site.Log, l => l.StartsWith("Invoke-CMScript:" + StubPivotsSite.ScriptGuid + ":devices 16777221:Count=3(Int32);Label=typed(String)", StringComparison.Ordinal));
        Assert.Equal("none", o.Devices.Single().Rows.Single()["Label"]);
    }

    [Fact]
    public async Task AScriptWhoseTextChangedAfterThePlanIsRefused()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01");
        var row = h.Site.Script(StubPivotsSite.ScriptGuid)!;
        row["Script"] = StubPivotsSite.Encoded(StubPivotsSite.ScriptText + "\n# changed");
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.Contains("text on the site changed", o.Message);
        Assert.Equal(0, h.Site.Count("Invoke-CMScript:"));
        row["Script"] = StubPivotsSite.Encoded(StubPivotsSite.ScriptText);
        var again = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01");
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ScriptHash"] = "ANOTHER";
        Assert.Contains("text on the site changed", (await h.ApplyAsync(again with { RunConfirmed = true })).Message);
        Assert.Equal(0, h.Site.Count("Invoke-CMScript:"));
    }

    [Fact]
    public async Task OnlyAnApprovedScriptRuns()
    {
        using var h = new PivotsHarness();
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var waiting = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01");
        Assert.Contains(waiting.Checks, c => c.Name == PivotsScriptsService.CheckNames.Approved && c.Result == DeploymentCheckResults.Fail);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 3;
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01");
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 1;
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.Contains("declined on the site now", o.Message);
        Assert.Equal(0, h.Site.Count("Invoke-CMScript:"));
    }

    [Theory]
    [InlineData("Nope", "x", "does not define the parameter \"Nope\"")]
    [InlineData("Label", "O'Brien", "single quote")]
    [InlineData("Label", "O\u2019Brien", "single quote")]
    [InlineData("Label", "a\"b", "double quote")]
    [InlineData("Count", "seven", "takes a whole number")]
    [InlineData("Count", "7.5", "takes a whole number")]
    public async Task AValueTheSiteWouldDropOrBreakOnIsRefusedAtPrepare(string name, string value, string message)
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01", new Dictionary<string, string> { [name] = value });
        Assert.True(plan.Blocked);
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.Parameters && c.Message.Contains(message, StringComparison.Ordinal));
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, (await h.ApplyAsync(plan with { RunConfirmed = true })).Message);
    }

    [Fact]
    public void AValueOverTheLengthLimitAndAMissingRequiredValueAreRefused()
    {
        var script = new SiteScript("G", "S", "A", "A", SiteScriptStates.Approved, 1, "PowerShell", "h",
            [new ScriptParameter("Name", "System.String", true, null), new ScriptParameter("Flag", "System.Boolean", false, null)], "x");
        var problems = PivotsScriptsService.ParameterProblems(script, new Dictionary<string, string> { ["Flag"] = "1" });
        Assert.Contains(problems, p => p.Contains("Name needs a value", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("does not take", StringComparison.Ordinal));
        Assert.Contains(PivotsScriptsService.ParameterProblems(script, new Dictionary<string, string> { ["Name"] = new string('x', 1025) }), p => p.Contains("more than 1024", StringComparison.Ordinal));
        var none = script with { Parameters = [] };
        Assert.Contains(PivotsScriptsService.ParameterProblems(none, new Dictionary<string, string> { ["Name"] = "x" }), p => p.Contains("no parameter definitions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScriptThatStopsWithAnErrorIsFailedAndAnOfflineDeviceIsNoResponse()
    {
        using var h = new PivotsHarness();
        h.Site.SetOption("Throw:" + StubPivotsSite.ScriptGuid, true);
        var plan = await h.PrepareScriptAsync(RunTargetKind.Collection, "MCM00015", window: TimeSpan.FromSeconds(30));
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.Contains(h.Site.Log, l => l.StartsWith("Invoke-CMScript:" + StubPivotsSite.ScriptGuid + ":collection MCM00015", StringComparison.Ordinal));
        var failed = o.Devices.Single(d => d.Name == "CLIENT01");
        Assert.Equal(PivotsDeviceStates.Failed, failed.State);
        Assert.Equal(-2147467259, failed.ExitCode);
        Assert.Equal(PivotsDeviceStates.NoResponse, o.Devices.Single(d => d.Name == "CLIENT02").State);
        Assert.True(o.Partial);
    }

    [Fact]
    public async Task ScriptOutputWithEscapedJsonBecomesRows()
    {
        using var h = new PivotsHarness();
        h.Site.SetOption("Output:" + StubPivotsSite.ScriptGuid, "[{\\\"Name\\\":\\\"=cmd|' /C calc'!A0\\\",\\\"Count\\\":3},{\\\"Name\\\":\\\"<b>x</b>\\\",\\\"Count\\\":4}]");
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01");
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        var rows = o.Devices.Single().Rows;
        Assert.Equal(["=cmd|' /C calc'!A0", "<b>x</b>"], rows.Select(r => r["Name"]));
        Assert.Equal("3", rows[0]["Count"]);
    }

    [Fact]
    public async Task ABuiltInFeatureScriptIsNotListedAndCannotBePlanned()
    {
        using var h = new PivotsHarness();
        var scripts = await h.Service.LoadScriptsAsync(CancellationToken.None);
        Assert.Equal([StubPivotsSite.ScriptGuid], scripts.Select(s => s.Guid));
        Assert.Null(scripts[0].Text);
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01", guid: "7DC6B6F1-E7F6-43C1-96E0-E1D16BC25C14");
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.Script && c.Result == DeploymentCheckResults.Fail);
    }

    [Fact]
    public async Task ASessionFailureGivesAFixedMessage()
    {
        using var h = new PivotsHarness();
        h.Site.Fail("query:SMS_CombinedDeviceResources");
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => h.ResolveAsync(RunTargetKind.Device, "CLIENT01"));
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.Contains("The log has the kind of failure", ex.Message);
    }
}
