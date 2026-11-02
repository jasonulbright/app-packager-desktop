using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.PivotsScripts.PivotsScriptsHarness;
using VM = ApplicationPackagerUI.ViewModels.Pages.PivotsScriptsViewModel;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The stored parameter names, the result rows of devices that did not answer and the app-closed session state, through the page and the real service.</summary>
public sealed class PivotsLabFix4Tests
{
    private const string Client01 = "CLIENT01";
    private const int Client01Id = 16777221;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ---- A session file holds the parameter names only -----------------------------------------------

    [Fact]
    public async Task TheSessionFileAndTheAuditLogHoldTheParameterNamesButNoValueHashOrLength()
    {
        using var h = new PivotsHarness();
        const string label = "PSL-Value-7731";
        var plan = await h.PrepareScriptAsync(RunTargetKind.Device, Client01, new Dictionary<string, string> { ["Label"] = label, ["Count"] = "21" }, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan with { RunConfirmed = true });
        Assert.True(o.Sent, o.Message);
        h.Restart();
        await h.WaitAsync(o.OperationId!, window: Window, sessionId: o.SessionId);
        foreach (var text in new[] { File.ReadAllText(Path.Combine(h.Sessions.Folder, o.SessionId + ".json")), File.ReadAllText(h.Audit.Path) })
        {
            foreach (var value in new[] { label, "21" })
            {
                Assert.DoesNotContain(Sha256Hex(value), text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain($"\"{value}\"", text, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("sha256:", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(label.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters", text, StringComparison.Ordinal);
        }
        Assert.Equal(["Count", "Label"], h.Sessions.Read(o.SessionId!)!.ParameterNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ASessionFileOfAnEarlierVersionThatHoldsHashesIsReadAndItsNextWriteDropsTheHashes()
    {
        using var h = new PivotsHarness();
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Sent, o.Message);
        var path = Path.Combine(h.Sessions.Folder, o.SessionId + ".json");
        var hash = "sha256:" + Sha256Hex("21");
        var json = File.ReadAllText(path);
        var start = json.IndexOf("\"ParameterNames\":", StringComparison.Ordinal);
        Assert.True(start >= 0, json);
        var end = json.IndexOf(']', start) + 1;
        File.WriteAllText(path, json[..start] + "\"Parameters\":{\"Count\":\"" + hash + "\",\"Label\":\"sha256:" + Sha256Hex("x") + "\"}" + json[end..]);

        var old = h.Sessions.Read(o.SessionId!);
        Assert.NotNull(old);
        Assert.Equal(["Count", "Label"], old.ParameterNames.Order(StringComparer.Ordinal));
        Assert.Single(h.Sessions.List(10), s => s.Id == o.SessionId);

        h.Restart();
        var waited = await h.WaitAsync(o.OperationId!, window: Window, sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, waited.Devices.Single().State);
        var rewritten = File.ReadAllText(path);
        Assert.DoesNotContain("sha256:", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha256Hex("21"), rewritten, StringComparison.Ordinal);
        Assert.Equal(["Count", "Label"], h.Sessions.Read(o.SessionId!)!.ParameterNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheHistoryDetailsOfAScriptRunNameNoHashOfAValue()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync(VM.HistoryTab);
        h.Audit.Records.Add(new PivotsAuditRecord(new DateTime(2026, 10, 24, 8, 0, 0), @"LAB\admin", "RunScript", "Started", "", "run-2", "Device WKS-001", 1, h.DiskScript.Guid, "abc", null, "Sending to 1 device."));
        await vm.RefreshHistoryCommand.ExecuteAsync(null);
        vm.SelectedHistory = vm.HistoryRows.Single();
        Assert.Contains("Parameter values are not stored.", vm.HistoryDetails);
        Assert.DoesNotContain("hash of each value", vm.HistoryDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("names", VM.ParameterValuesNotStored, StringComparison.Ordinal);
    }

    // ---- No result row for a device that has not answered ----------------------------------------------

    [Fact]
    public async Task BeforeAnyDeviceAnswersTheResultsListHasNoRowAndTheCountSaysZeroResultRows()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm);
        vm.QueryText = "OS";
        h.Service.WaitGate = new TaskCompletionSource();
        h.Service.ProgressState = id => id % 2 == 0 ? PivotsDeviceStates.Sent : PivotsDeviceStates.Running;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await WaitUntil(() => h.Service.WaitCalls == 1);
        await WaitUntil(() => vm.DeviceRows.Count == 6 && vm.DeviceRows.All(r => r.State is "Sent" or "Running"));
        Assert.Empty(vm.ResultRows);
        Assert.StartsWith("0 result rows, ", vm.ResultCount, StringComparison.Ordinal);
        Assert.Contains("0 of 6 devices answered", vm.WaitProgress);

        vm.StopWaitingCommand.Execute(null);
        await vm.WaitTask;
        Assert.False(vm.IsWaiting);
        Assert.Empty(vm.ResultRows);
        Assert.Equal(6, vm.DeviceRows.Count);
        Assert.StartsWith("0 result rows, ", vm.ResultCount, StringComparison.Ordinal);

        vm.ResultTabIndex = VM.ResultsView;
        await vm.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(VM.NoResultToExport, vm.StatusMessage);
        await vm.ExportJsonCommand.ExecuteAsync(null);
        Assert.Equal(VM.NoResultToExport, vm.StatusMessage);
        Assert.Empty(h.Reports.Written);
    }

    [Fact]
    public async Task ARunWithoutResultColumnsShowsAResultRowOnlyForTheDevicesThatAnsweredAndThePipelineCountsThem()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm);
        vm.QueryText = "OS";
        h.Service.FinalState = id => id % 2 == 0 ? PivotsDeviceStates.Failed : PivotsDeviceStates.NoResponse;
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal(6, vm.DeviceRows.Count);
        Assert.Equal(3, vm.ResultRows.Count);
        Assert.All(vm.ResultRows, r => Assert.Equal("Failed", r.FullCells[0]));
        Assert.StartsWith("3 result rows, ", vm.ResultCount, StringComparison.Ordinal);
        Assert.Equal(PivotsLabels.Count(3, "result"), vm.PipelineRows.Single().Results);
    }

    [Fact]
    public async Task AQueryThatMatchedNothingShowsNoResultRowAndNoRawXml()
    {
        var h = new PivotsScriptsHarness();
        var vm = await h.OpenAsync();
        await ResolveAsync(vm);
        vm.QueryText = "OS | where Caption == 'none'";
        h.Service.Shape = r => r with { Rows = [], Output = "", Message = "" };
        await vm.RunPivotCommand.ExecuteAsync(null);
        await vm.WaitTask;
        Assert.Equal(6, vm.DeviceRows.Count);
        Assert.All(vm.DeviceRows, d => Assert.Equal("Completed", d.State));
        Assert.Empty(vm.ResultRows);
        Assert.StartsWith("0 result rows, ", vm.ResultCount, StringComparison.Ordinal);
    }

    // ---- A run that the app close ended reads the same in the session list as in the audit log --------

    [Fact]
    public async Task ARunThatEndedBecauseTheAppClosedReadsAppClosedInTheSessionListAndAWaitAgainReplacesIt()
    {
        using var h = new PivotsHarness();
        h.Admin.AnswerAfterPolls = 100;
        var plan = await h.PreparePivotAsync(RunTargetKind.Device, Client01, window: PivotsChange.SendOnly);
        var o = await h.ApplyAsync(plan);
        using var cts = new CancellationTokenSource();
        string? atExit = null;
        h.OnDelay = n =>
        {
            if (n != 2) return;
            h.Service.EndWaitsAtExit();
            atExit = h.Sessions.Read(o.SessionId!)!.Status;
            cts.Cancel();
        };
        await h.WaitAsync(o.OperationId!, cts.Token, window: Window);
        Assert.Equal(PivotsScriptsService.AppClosedStatus, atExit);
        var session = h.Sessions.Read(o.SessionId!)!;
        Assert.Equal(PivotsScriptsService.AppClosedStatus, session.Status);
        Assert.Equal(PivotsScriptsService.ExitDuringWaitMessage, session.Message);
        Assert.Equal(PivotsScriptsService.AppClosedStatus, h.Sessions.List(10).Single(s => s.Id == o.SessionId).Status);
        Assert.True(new SessionGridRow(h.Sessions.List(10).Single(s => s.Id == o.SessionId)).CanWaitAgain);
        Assert.Equal(PivotsScriptsService.ExitDuringWaitMessage, Assert.Single(h.Finished()).Message);

        h.OnDelay = null;
        h.Admin.AnswerAfterPolls = 0;
        h.Restart();
        var again = await h.WaitAsync(o.OperationId!, window: Window, sessionId: o.SessionId);
        Assert.Equal(PivotsDeviceStates.Completed, again.Devices.Single(d => d.ResourceId == Client01Id).State);
        Assert.NotEqual(PivotsScriptsService.AppClosedStatus, h.Sessions.Read(o.SessionId!)!.Status);
    }
}
