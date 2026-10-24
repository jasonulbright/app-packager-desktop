using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// The send-only window of the page: Apply returns after the send, WaitForResultsAsync reads the
/// results and writes the one Finished record of the run, and a run that is not in memory is read
/// from its saved session.
/// </summary>
public sealed class PivotsSendOnlyTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ASendOnlyPlanIsNotBlockedAndSaysThatThePageWaits()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", window: PivotsChange.SendOnly);
        Assert.False(plan.Blocked);
        Assert.NotNull(plan.Fingerprint);
        Assert.True(plan.IsSendOnly);
        Assert.DoesNotContain(plan.Impact, l => l.Contains("waits up to", StringComparison.Ordinal));
        Assert.Contains(plan.Impact, l => l.Contains("wait time set in Options", StringComparison.Ordinal));
        Assert.Equal(0, h.Admin.StatusReads);
        Assert.Equal(0, h.Delays);
    }

    [Fact]
    public async Task ASendOnlyRunReturnsAfterTheSendAndWritesNoFinishedRecord()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent);
        Assert.True(o.Succeeded, o.Message);
        Assert.NotNull(o.OperationId);
        Assert.All(o.Devices, d => Assert.Equal(PivotsDeviceStates.Sent, d.State));
        Assert.Equal(0, h.Admin.StatusReads);
        Assert.Equal(0, h.Delays);
        Assert.Equal(["Started"], h.Audit.Read(10).Select(r => r.Phase));
        Assert.Equal("Sent", h.Sessions.Read(o.SessionId!)!.Status);
    }

    [Fact]
    public async Task TheWaitOfASendOnlyRunWritesTheOneFinishedRecordOfTheRun()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        var done = await h.WaitAsync(o.OperationId!, window: Window);
        Assert.True(done.Succeeded, done.Message);
        Assert.Equal(PivotsDeviceStates.Completed, done.Devices.Single().State);
        var finished = Assert.Single(h.Finished());
        Assert.Equal("Succeeded", finished.Result);
        Assert.Equal(o.OperationId, finished.OperationId);
        Assert.Equal(o.SessionId, finished.SessionId);
        Assert.Equal(h.Audit.Read(10).First().RunId, finished.RunId);
        Assert.Equal("Completed", h.Sessions.Read(o.SessionId!)!.Status);

        await h.WaitAsync(o.OperationId!, window: Window);
        Assert.Single(h.Finished());
        Assert.Equal(1, h.Admin.Sends);
    }

    [Fact]
    public async Task StopWaitingOnASendOnlyRunWritesAStoppedPartialRecordAndAWaitAgainWritesNoSecond()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 100;
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00014", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        using var cts = new CancellationTokenSource();
        h.OnDelay = n => { if (n == 2) cts.Cancel(); };
        var stopped = await h.WaitAsync(o.OperationId!, cts.Token, window: Window);
        Assert.True(stopped.Partial);
        Assert.Contains("Stopped waiting", stopped.Message);
        var finished = Assert.Single(h.Finished());
        Assert.Equal("Partial", finished.Result);
        Assert.Contains("Stopped waiting", finished.Message);
        Assert.Equal("Stopped waiting", h.Sessions.Read(o.SessionId!)!.Status);

        h.OnDelay = null;
        h.Admin.AnswerAfterPolls = 0;
        var again = await h.WaitAsync(o.OperationId!, window: Window);
        Assert.Equal(PivotsDeviceStates.Completed, again.Devices.Single().State);
        Assert.Equal("Completed", h.Sessions.Read(o.SessionId!)!.Status);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task TheWaitEndsAtTheWindowItIsGivenAndAWaitAgainReadsANoResponseDeviceAgain()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 1000;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        var ended = await h.WaitAsync(o.OperationId!, window: TimeSpan.FromMinutes(2));
        Assert.Equal(PivotsDeviceStates.NoResponse, ended.Devices.Single().State);
        Assert.Equal(40, h.Delays);
        Assert.Equal("Partial", Assert.Single(h.Finished()).Result);

        h.Admin.AnswerAfterPolls = 0;
        var again = await h.WaitAsync(o.OperationId!, window: Window);
        Assert.Equal(PivotsDeviceStates.Completed, again.Devices.Single().State);
        Assert.False(again.Partial);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task TheResultWindowIsFixedAtPrepare()
    {
        using var h = new PivotsHarness();
        var sendOnly = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(sendOnly with { ResultWindow = TimeSpan.FromMinutes(5) });
        Assert.All(o.Devices, d => Assert.Equal(PivotsDeviceStates.Sent, d.State));
        Assert.Equal(0, h.Admin.StatusReads);

        var waiting = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", window: Window);
        var waited = await h.ApplyAsync(waiting with { ResultWindow = PivotsChange.SendOnly });
        Assert.Equal(PivotsDeviceStates.Completed, waited.Devices.Single().State);
        Assert.True(h.Admin.StatusReads > 0);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task AnInServiceWaitStillClampsItsWindowAndWritesItsFinishedRecordOnce()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 1000;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", window: TimeSpan.FromSeconds(1));
        Assert.Contains(plan.Impact, l => l.Contains("waits up to 30 seconds", StringComparison.Ordinal));
        var o = await h.ApplyAsync(plan);
        Assert.Equal(10, h.Delays);
        Assert.Equal(PivotsDeviceStates.NoResponse, o.Devices.Single().State);
        Assert.Single(h.Finished());
        h.Admin.AnswerAfterPolls = 0;
        await h.WaitAsync(o.OperationId!);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task AfterARestartTheWaitReadsTheSavedSessionAndWritesTheMissingFinishedRecord()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Collection, "MCM00015", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        h.Restart();
        var done = await h.WaitAsync(o.OperationId!, window: Window, sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, done.Devices.Single(d => d.Name == "CLIENT01").State);
        Assert.Equal(PivotsDeviceStates.NoResponse, done.Devices.Single(d => d.Name == "CLIENT02").State);
        Assert.Equal(PivotsChangeKind.RunPivot, done.Change.Kind);
        Assert.Equal(o.SessionId, done.SessionId);
        Assert.Contains("come from the site", done.Message);
        Assert.Contains(h.Reports, r => !r.Final && r.Message.Contains("saved session", StringComparison.Ordinal));
        var finished = Assert.Single(h.Finished());
        Assert.Equal(h.Audit.Read(10).First().RunId, finished.RunId);
        Assert.Equal("Partial", h.Sessions.Read(o.SessionId!)!.Status);
        Assert.Equal(1, h.Admin.Sends);

        h.Restart();
        await h.WaitAsync(o.OperationId!, window: Window);
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task AfterARestartAScriptRunIsReadFromTheSessionAndItsCompletedDeviceStays()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        await h.WaitAsync(o.OperationId!, window: Window);
        h.Restart();
        var again = await h.WaitAsync(o.OperationId!, window: Window);
        var d = again.Devices.Single();
        Assert.Equal(PivotsDeviceStates.Completed, d.State);
        Assert.Equal("none", d.Rows.Single()["Label"]);
        Assert.Equal(1, h.Site.Count("Invoke-CMScript:"));
        Assert.Single(h.Finished());
    }

    [Fact]
    public async Task AfterARestartARunWhoseScriptTheSiteRemovedGetsAPlainMessage()
    {
        using var h = new PivotsHarness();
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, "CLIENT01", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        h.Site.List("Scripts").Remove(h.Site.Script(StubPivotsSite.ScriptGuid));
        h.Restart();
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => h.WaitAsync(o.OperationId!, window: Window));
        Assert.Contains("no longer holds the script", ex.Message);
        Assert.DoesNotContain("Exception", ex.Message);
    }

    [Fact]
    public async Task ARunThatNoSavedSessionHoldsGetsAPlainMessageAndAnInvalidOperationIdReadsNothing()
    {
        using var h = new PivotsHarness();
        var none = await Assert.ThrowsAsync<PivotsServiceException>(() => h.WaitAsync("16784999", window: Window));
        Assert.Contains("No saved session", none.Message);
        var bad = await Assert.ThrowsAsync<PivotsServiceException>(() => h.WaitAsync("1 or 1 eq 1", window: Window));
        Assert.Contains("not valid", bad.Message);
        Assert.Empty(h.Admin.Requests);
    }

    [Fact]
    public async Task ASessionThatHoldsAnotherOperationIsNotUsedForTheWait()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, "CLIENT01", window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        var record = h.Sessions.Read(o.SessionId!)!;
        h.Sessions.Update(record with { OperationId = "16784999" });
        h.Restart();
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => h.WaitAsync(o.OperationId!, window: Window, sessionId: o.SessionId));
        Assert.Contains("No saved session", ex.Message);
        Assert.DoesNotContain(h.Admin.Requests, r => r.Method == "GET");
    }
}
