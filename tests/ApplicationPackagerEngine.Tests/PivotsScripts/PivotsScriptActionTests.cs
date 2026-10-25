using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>Submit, Approve, Deny and Remove of site scripts through the real session script on the stub site.</summary>
public sealed class PivotsScriptActionTests
{
    private const string Text = "param([string]$ServiceName = 'CcmExec', [int]$Count = 1)\nGet-Service -Name $ServiceName | ConvertTo-Json -Compress";

    private static async Task<PivotsChange> Prepare(PivotsHarness h, PivotsChangeKind kind, string guid = StubPivotsSite.ScriptGuid, string? comment = null) =>
        await h.Service.PrepareAsync(PivotsHarness.Request(kind, guid: guid, comment: comment), CancellationToken.None);

    [Fact]
    public async Task SubmitCreatesTheScriptAndStoresItsParameterDefinitions()
    {
        using var h = new PivotsHarness();
        var plan = await h.Service.PrepareAsync(PivotsHarness.Request(PivotsChangeKind.SubmitScript, submitName: "Service state", submitText: Text), CancellationToken.None);
        Assert.False(plan.Blocked, string.Join(" ", plan.Checks.Select(c => c.Message)));
        Assert.Equal(["ServiceName", "Count"], plan.SubmitParameters.Select(p => p.Name));
        Assert.Equal(["System.String", "System.Int32"], plan.SubmitParameters.Select(p => p.Type));
        var o = await h.ApplyAsync(plan);
        Assert.True(o.Succeeded, o.Message);
        Assert.NotNull(o.CreatedScriptGuid);
        Assert.Null(o.CleanupChange);
        Assert.Equal(SiteScriptStates.Waiting, o.ScriptAfter!.State);
        Assert.Equal(["ServiceName", "Count"], o.ScriptAfter.Parameters.Select(p => p.Name));
        Assert.Equal(Text, o.ScriptAfter.Text);
        Assert.Contains("New-CimSession:cm01.contoso.com:Dcom", h.Site.Log);
        Assert.Equal(["Started", "Finished"], h.Audit.Read(5).Select(r => r.Phase));
    }

    [Fact]
    public async Task ASubmitWhoseDefinitionsFailNamesTheCreatedScriptAndOffersToRemoveOnlyIt()
    {
        using var h = new PivotsHarness();
        h.Site.Fail("define");
        var plan = await h.Service.PrepareAsync(PivotsHarness.Request(PivotsChangeKind.SubmitScript, submitName: "Service state", submitText: Text), CancellationToken.None);
        var o = await h.ApplyAsync(plan);
        Assert.False(o.Succeeded);
        Assert.True(o.Partial);
        var guid = o.CreatedScriptGuid!;
        Assert.Contains(guid, o.Message);
        Assert.DoesNotContain("SECRET-DETAIL-1234", o.Message);
        Assert.Equal(PivotsChangeKind.RemoveScript, o.CleanupChange!.Kind);
        Assert.Equal(guid, o.CleanupChange.ScriptGuid);
        Assert.Equal("Partial", h.Audit.Read(5).Last().Result);
        var cleanup = await h.Service.PrepareAsync(o.CleanupChange, CancellationToken.None);
        var removed = await h.ApplyAsync(cleanup);
        Assert.True(removed.Succeeded, removed.Message);
        Assert.Null(h.Site.Script(guid));
        Assert.NotNull(h.Site.Script(StubPivotsSite.ScriptGuid));
    }

    [Theory]
    [InlineData("param([switch]$Detailed) 'x'", "SwitchParameter")]
    [InlineData("param([string]$Name = \"O'Brien\") 'x'", "single quote")]
    [InlineData("param([string]$a", "errors")]
    public async Task ASubmitThatBreaksTheRunScriptsRulesIsRefusedAtPrepare(string text, string message)
    {
        using var h = new PivotsHarness();
        var plan = await h.Service.PrepareAsync(PivotsHarness.Request(PivotsChangeKind.SubmitScript, submitName: "x", submitText: text), CancellationToken.None);
        Assert.True(plan.Blocked);
        Assert.Contains(plan.Checks, c => c.Message.Contains(message, StringComparison.Ordinal));
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, (await h.ApplyAsync(plan)).Message);
        Assert.Equal(0, h.Site.Count("New-CMScript:"));
    }

    [Fact]
    public async Task TheSiteRefusesAnAuthorsDecisionWhenItRequiresASecondApproverAndThePageSaysSo()
    {
        using var h = new PivotsHarness();
        h.Site.SetOption("TwoKey", 1);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var plan = await Prepare(h, PivotsChangeKind.ApproveScript, comment: "ok");
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.Author && c.Message.Contains("will refuse", StringComparison.Ordinal));
        var o = await h.ApplyAsync(plan);
        Assert.False(o.Succeeded);
        Assert.Contains("Script authors require additional script approver", o.Message);
        Assert.Contains("does not change that setting", o.Message);
        Assert.Equal(0, (int)h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"]!);
        Assert.Equal("Failed", h.Audit.Read(5).Last().Result);
    }

    [Fact]
    public async Task ApproveDenyAndRemoveChangeTheScriptAndReadItBack()
    {
        using var h = new PivotsHarness();
        h.Site.Script(StubPivotsSite.ScriptGuid)!["ApprovalState"] = 0;
        var approve = await h.ApplyAsync(await Prepare(h, PivotsChangeKind.ApproveScript, comment: "lab"));
        Assert.True(approve.Succeeded, approve.Message);
        Assert.Equal(SiteScriptStates.Approved, approve.ScriptAfter!.State);
        Assert.Contains("approved already", (await Prepare(h, PivotsChangeKind.ApproveScript)).Checks.Single(c => c.Result == DeploymentCheckResults.Fail).Message);
        var deny = await h.ApplyAsync(await Prepare(h, PivotsChangeKind.DenyScript, comment: "not now"));
        Assert.True(deny.Succeeded, deny.Message);
        Assert.Equal("not now", (string?)h.Site.Script(StubPivotsSite.ScriptGuid)!["Comment"]);
        var removePlan = await Prepare(h, PivotsChangeKind.RemoveScript);
        Assert.Contains(removePlan.Impact, l => l.Contains("run history", StringComparison.Ordinal));
        var remove = await h.ApplyAsync(removePlan);
        Assert.True(remove.Succeeded, remove.Message);
        Assert.Null(h.Site.Script(StubPivotsSite.ScriptGuid));
        Assert.Equal(StubPivotsSite.ScriptGuid, h.Audit.Read(20).Last().ScriptGuid);
        Assert.Contains("used already", (await h.ApplyAsync(removePlan)).Message);
        Assert.Null(await h.Service.LoadScriptAsync(StubPivotsSite.ScriptGuid, CancellationToken.None));
    }

    [Fact]
    public async Task AnActionOnAScriptWhoseTextChangedIsRefused()
    {
        using var h = new PivotsHarness();
        var plan = await Prepare(h, PivotsChangeKind.DenyScript);
        h.Site.Script(StubPivotsSite.ScriptGuid)!["Script"] = StubPivotsSite.Encoded("Get-Date");
        var o = await h.ApplyAsync(plan);
        Assert.StartsWith(PivotsScriptsService.SkippedPrefix, o.Message);
        Assert.Equal(0, h.Site.Count("Deny:"));
    }

    [Fact]
    public async Task CheckReportsSyntaxErrorsWithLineAndColumn()
    {
        using var h = new PivotsHarness();
        var bad = await h.Service.CheckScriptAsync("param([string]$a\nGet-Date", CancellationToken.None);
        Assert.False(bad.Valid);
        Assert.StartsWith("Line 1, column", bad.Errors[0]);
        var good = await h.Service.CheckScriptAsync(Text, CancellationToken.None);
        Assert.True(good.Valid);
        Assert.Empty(good.Problems);
        Assert.Equal("CcmExec", good.Parameters[0].Default);
    }

    [Fact]
    public async Task TestConnectionNamesTheSiteAndTheAdministrationService()
    {
        using var h = new PivotsHarness();
        var text = await h.Service.TestConnectionAsync(CancellationToken.None);
        Assert.Contains("connected to site MCM (version 5.00.9146.1000)", text);
        Assert.Contains("Administration service on cm01.contoso.com: answers", text);
    }
}
