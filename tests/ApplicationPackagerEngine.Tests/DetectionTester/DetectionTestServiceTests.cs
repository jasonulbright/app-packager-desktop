using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

public sealed class DetectionTestServiceTests
{
    private const string Key = @"SOFTWARE\Contoso\Widget";

    private sealed class FakeScriptHost(Func<string, DetectionScriptRun> run) : IDetectionScriptHost
    {
        public readonly List<(string Script, TimeSpan Timeout)> Calls = [];

        public Task<DetectionScriptRun> RunAsync(string scriptText, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((scriptText, timeout));
            return Task.FromResult(run(scriptText));
        }
    }

    private static FakeRegistryProvider Registry() => new FakeRegistryProvider()
        .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Version", "2.0")
        .AddKey(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\OnlyIn32");

    private static StageDetection ValueRule(string expected) => new()
    {
        Type = "RegistryKeyValue",
        RegistryKeyRelative = Key,
        ValueName = "Version",
        ExpectedValue = expected,
        Operator = "IsEquals",
        PropertyType = "String"
    };

    private static DetectionTestRequest Request(StageDetection det, bool negative = false,
        RegistryViewChoice view = RegistryViewChoice.Registry64, TimeSpan timeout = default) =>
        new(det, negative, view, timeout);

    private static DetectionTestService Service(FakeRegistryProvider? registry = null, IDetectionScriptHost? host = null) =>
        new(registry ?? Registry(), host ?? new FakeScriptHost(_ => new DetectionScriptRun(0, "", "", false)));

    [Theory]
    [InlineData("2.0", false, true, true)]
    [InlineData("3.0", false, false, false)]
    [InlineData("3.0", true, false, true)]
    [InlineData("2.0", true, true, false)]
    public async Task NegativeModeTurnsNotDetectedIntoAPass(string expected, bool negative, bool detected, bool passed)
    {
        var result = await Service().RunAsync(Request(ValueRule(expected), negative), CancellationToken.None);

        Assert.Equal(detected, result.Detected);
        Assert.Equal(passed, result.Passed);
        Assert.Single(result.Clauses);
        Assert.Contains(passed ? "Passed" : "Failed", result.Summary);
    }

    [Fact]
    public async Task AnUnevaluatedClauseFailsInNegativeModeToo()
    {
        var det = ValueRule("2.0");
        det.Operator = "Matches";

        var result = await Service().RunAsync(Request(det, negative: true), CancellationToken.None);

        Assert.False(result.Detected);
        Assert.False(result.Passed);
        Assert.Contains("could not be evaluated", result.Summary);
    }

    [Fact]
    public async Task TheRequestViewSelectsTheRegistryViewOfASingleClause()
    {
        var det = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\OnlyIn32", Is64Bit = true };

        var in32 = await Service().RunAsync(Request(det, view: RegistryViewChoice.Registry32), CancellationToken.None);
        var in64 = await Service().RunAsync(Request(det, view: RegistryViewChoice.Registry64), CancellationToken.None);

        Assert.True(in32.Detected);
        Assert.False(in64.Detected);
        Assert.True(det.Is64Bit);
    }

    [Fact]
    public async Task ACompoundKeepsTheViewsOfItsClausesAndListsEachClause()
    {
        var det = new StageDetection
        {
            Type = "Compound",
            Connector = "And",
            Clauses =
            [
                new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\OnlyIn32", Is64Bit = false },
                ValueRule("2.0")
            ]
        };

        var result = await Service().RunAsync(Request(det, view: RegistryViewChoice.Registry64), CancellationToken.None);

        Assert.True(result.Detected);
        Assert.Equal(3, result.Clauses.Count);
        Assert.StartsWith("Compound And", result.Clauses[0].Description);
        Assert.StartsWith("  Registry key", result.Clauses[1].Description);
        Assert.All(result.Clauses, c => Assert.True(c.Detected));
    }

    [Fact]
    public async Task AScriptClauseUsesTheOutputRuleAndReportsTheRun()
    {
        var host = new FakeScriptHost(_ => new DetectionScriptRun(2, "Installed", "warn", false));
        var det = new StageDetection { Type = "Script", ScriptText = "Write-Output 'Installed'; exit 2" };

        var result = await Service(host: host).RunAsync(Request(det), CancellationToken.None);

        Assert.True(result.Detected);
        Assert.True(result.Passed);
        Assert.Equal("Installed", result.ScriptStdout);
        Assert.Equal("warn", result.ScriptStderr);
        Assert.Equal(2, result.ScriptExitCode);
        Assert.Equal("Windows PowerShell 5.1, current user, not SYSTEM", result.RunContext);
        Assert.Contains("ConfigMgr", result.Clauses[0].Detail);
    }

    [Fact]
    public async Task TheDefaultScriptTimeoutIsThirtySeconds()
    {
        var host = new FakeScriptHost(_ => new DetectionScriptRun(0, "", "", false));
        var det = new StageDetection { Type = "Script", ScriptText = "'x'" };

        await Service(host: host).RunAsync(Request(det), CancellationToken.None);
        await Service(host: host).RunAsync(Request(det, timeout: TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), host.Calls[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), host.Calls[1].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), DetectionTestService.DefaultScriptTimeout);
    }

    [Fact]
    public async Task AScriptThatTimesOutFailsInBothModes()
    {
        var host = new FakeScriptHost(_ => new DetectionScriptRun(null, "", "", true));
        var det = new StageDetection { Type = "Script", ScriptText = "Start-Sleep 60" };

        var positive = await Service(host: host).RunAsync(Request(det), CancellationToken.None);
        var negative = await Service(host: host).RunAsync(Request(det, negative: true), CancellationToken.None);

        Assert.False(positive.Passed);
        Assert.False(negative.Passed);
        Assert.Contains("timed out", negative.Clauses[0].Detail);
        Assert.Null(negative.ScriptExitCode);
    }

    [Fact]
    public async Task AnEmptyScriptIsNotRun()
    {
        var host = new FakeScriptHost(_ => new DetectionScriptRun(0, "x", "", false));

        var result = await Service(host: host).RunAsync(Request(new StageDetection { Type = "Script", ScriptText = "  " }), CancellationToken.None);

        Assert.Empty(host.Calls);
        Assert.False(result.Passed);
        Assert.Contains("empty", result.Clauses[0].Detail);
    }

    [Fact]
    public async Task ARegistryOnlyTestStatesItsContextWithoutPowerShell()
    {
        var result = await Service().RunAsync(Request(ValueRule("2.0")), CancellationToken.None);

        Assert.Equal("This app, current user, not SYSTEM", result.RunContext);
        Assert.Null(result.ScriptStdout);
        Assert.Null(result.ScriptExitCode);
    }

    [Fact]
    public async Task SeveralScriptClausesReportEachRun()
    {
        var host = new FakeScriptHost(text => new DetectionScriptRun(0, text.Contains("one") ? "first" : "second", "", false));
        var det = new StageDetection
        {
            Type = "Compound",
            Connector = "Or",
            Clauses =
            [
                new StageDetection { Type = "Script", ScriptText = "'one'" },
                new StageDetection { Type = "Script", ScriptText = "'two'" }
            ]
        };

        var result = await Service(host: host).RunAsync(Request(det), CancellationToken.None);

        Assert.Contains("first", result.ScriptStdout);
        Assert.Contains("second", result.ScriptStdout);
        Assert.Equal(0, result.ScriptExitCode);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var det = new StageDetection { Type = "Script", ScriptText = "'x'" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().RunAsync(Request(det), cts.Token));
    }

    [Fact]
    public async Task ARealScriptRunsThroughWindowsPowerShell()
    {
        var service = new DetectionTestService();
        var det = new StageDetection { Type = "Script", ScriptText = "if (Test-Path $env:SystemRoot) { 'present' }" };

        var result = await service.RunAsync(Request(det), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal("present", result.ScriptStdout);
        Assert.Equal(0, result.ScriptExitCode);
    }

    [Fact]
    public async Task ARealRegistryClauseReadsThisComputer()
    {
        var service = new DetectionTestService();
        var det = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion" };

        var result = await service.RunAsync(Request(det), CancellationToken.None);

        Assert.True(result.Passed);
    }
}
