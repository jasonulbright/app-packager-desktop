using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

public sealed class DetectionEvaluatorTraceTests
{
    private const string Key = @"SOFTWARE\Contoso\Widget";

    private static DetectionEvaluationContext Context(FakeRegistryProvider registry,
        Func<string, DetectionScriptRun>? script = null,
        ScriptVerdictRule rule = ScriptVerdictRule.ExitCodeAndOutput) =>
        new() { Registry = registry, RunScript = script, ScriptRule = rule };

    private static StageDetection Value(string expected, string op = "IsEquals", string type = "String",
        bool is64 = true, string hive = "LocalMachine", string valueName = "Version") => new()
    {
        Type = "RegistryKeyValue",
        RegistryKeyRelative = Key,
        ValueName = valueName,
        ExpectedValue = expected,
        Operator = op,
        PropertyType = type,
        Is64Bit = is64,
        Hive = hive
    };

    [Fact]
    public void ARegistryValueRuleReadsTheViewItDeclares()
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry32, Key, "Version", "2.0");

        Assert.Equal(DetectionEvaluation.Detected,
            DetectionEvaluator.Trace(Value("2.0", is64: false), Context(registry)).Verdict);
        Assert.Equal(DetectionEvaluation.NotDetected,
            DetectionEvaluator.Trace(Value("2.0", is64: true), Context(registry)).Verdict);
    }

    [Fact]
    public void ARegistryValueRuleReadsTheHiveItDeclares()
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.CurrentUser, RegistryView.Registry64, Key, "Version", "2.0");

        Assert.Equal(DetectionEvaluation.Detected,
            DetectionEvaluator.Trace(Value("2.0", hive: "CurrentUser"), Context(registry)).Verdict);
        Assert.Equal(DetectionEvaluation.NotDetected,
            DetectionEvaluator.Trace(Value("2.0"), Context(registry)).Verdict);
    }

    [Fact]
    public void AKeyRuleReadsTheViewAndHiveItDeclares()
    {
        var registry = new FakeRegistryProvider().AddKey(RegistryHive.CurrentUser, RegistryView.Registry32, Key);
        var det = new StageDetection { Type = "RegistryKey", RegistryKeyRelative = Key, Hive = "CurrentUser", Is64Bit = false };

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Trace(det, Context(registry)).Verdict);
        det.Is64Bit = true;
        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.Trace(det, Context(registry)).Verdict);
        det.Is64Bit = false;
        det.Hive = "LocalMachine";
        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.Trace(det, Context(registry)).Verdict);
    }

    [Fact]
    public void AKeyThatCannotBeReadIsAnErrorWithItsReason()
    {
        var registry = new FakeRegistryProvider().Deny(RegistryHive.LocalMachine, RegistryView.Registry64, Key);

        var trace = DetectionEvaluator.Trace(Value("2.0"), Context(registry));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Contains("not allowed", trace.Detail);
        var key = DetectionEvaluator.Trace(
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = Key }, Context(registry));
        Assert.Equal(DetectionEvaluation.Error, key.Verdict);
    }

    [Theory]
    [InlineData("10", "9", "GreaterThan", DetectionEvaluation.Detected)]
    [InlineData("9", "10", "GreaterThan", DetectionEvaluation.NotDetected)]
    [InlineData("10", "10", "GreaterEquals", DetectionEvaluation.Detected)]
    [InlineData("10", "010", "IsEquals", DetectionEvaluation.Detected)]
    [InlineData("10", "11", "NotEquals", DetectionEvaluation.Detected)]
    [InlineData("8", "9", "LessThan", DetectionEvaluation.Detected)]
    [InlineData("9", "9", "LessEquals", DetectionEvaluation.Detected)]
    [InlineData("abc", "9", "GreaterThan", DetectionEvaluation.Error)]
    [InlineData("10", "nine", "IsEquals", DetectionEvaluation.Error)]
    public void AnIntegerRuleComparesNumbers(string actual, string expected, string op, DetectionEvaluation want) =>
        Assert.Equal(want, DetectionEvaluator.CompareValue(actual,
            new StageDetection { ExpectedValue = expected, Operator = op, PropertyType = "Integer" }));

    [Fact]
    public void ADwordValueIsReadAsItsDecimalNumber()
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Build", 26100, RegistryValueKind.DWord);

        var trace = DetectionEvaluator.Trace(
            Value("9999", "GreaterEquals", "Integer", valueName: "Build"), Context(registry));

        Assert.Equal(DetectionEvaluation.Detected, trace.Verdict);
        Assert.Contains("26100", trace.Detail);
    }

    [Fact]
    public void AQwordValueIsReadAsItsDecimalNumber()
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Big", 5000000000L, RegistryValueKind.QWord);

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Trace(
            Value("4999999999", "GreaterThan", "Integer", valueName: "Big"), Context(registry)).Verdict);
    }

    [Theory]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.Binary)]
    public void AComparisonOnAListOrBinaryValueIsAnError(RegistryValueKind kind)
    {
        object value = kind == RegistryValueKind.Binary ? new byte[] { 1, 2 } : new[] { "a", "b" };
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Version", value, kind);

        var trace = DetectionEvaluator.Trace(Value("a"), Context(registry));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Contains("not supported", trace.Detail);
    }

    [Fact]
    public void AMissingValueInAnExistingKeyIsNotDetectedWithAPlainDetail()
    {
        var registry = new FakeRegistryProvider().AddKey(RegistryHive.LocalMachine, RegistryView.Registry64, Key);

        var trace = DetectionEvaluator.Trace(Value("1.0"), Context(registry));

        Assert.Equal(DetectionEvaluation.NotDetected, trace.Verdict);
        Assert.Contains("not found", trace.Detail);
    }

    [Fact]
    public void AnUnsupportedOperatorIsAnErrorBeforeTheRegistryIsRead()
    {
        var registry = new FakeRegistryProvider();

        Assert.Equal(DetectionEvaluation.Error,
            DetectionEvaluator.Trace(Value("1.0", "Matches"), Context(registry)).Verdict);
        Assert.Equal(0, registry.OpenCount);
    }

    [Fact]
    public void AnUnsupportedPropertyTypeIsAnError() =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.CompareValue("1",
            new StageDetection { ExpectedValue = "1", Operator = "GreaterThan", PropertyType = "Float" }));

    private static FakeRegistryProvider TwoKeys() => new FakeRegistryProvider()
        .AddKey(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Present");

    private static StageDetection KeyClause(string path) =>
        new() { Type = "RegistryKey", RegistryKeyRelative = path };

    private static StageDetection ErrorClause() =>
        new() { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Present", ValueName = "v", ExpectedValue = "1", Operator = "Matches" };

    private static StageDetection Compound(string? connector, params StageDetection[] clauses) =>
        new() { Type = "Compound", Connector = connector, Clauses = clauses };

    [Theory]
    [InlineData("And", true, true, DetectionEvaluation.Detected)]
    [InlineData("And", true, false, DetectionEvaluation.NotDetected)]
    [InlineData("Or", false, true, DetectionEvaluation.Detected)]
    [InlineData("Or", false, false, DetectionEvaluation.NotDetected)]
    [InlineData(null, true, false, DetectionEvaluation.NotDetected)]
    public void ACompoundJoinsItsClauses(string? connector, bool first, bool second, DetectionEvaluation want)
    {
        var det = Compound(connector,
            KeyClause(first ? @"SOFTWARE\Present" : @"SOFTWARE\Absent"),
            KeyClause(second ? @"SOFTWARE\Present" : @"SOFTWARE\Absent"));

        var trace = DetectionEvaluator.Trace(det, Context(TwoKeys()));

        Assert.Equal(want, trace.Verdict);
        Assert.Equal(2, trace.Children.Count);
    }

    /// <summary>
    /// Three-valued: an And with a clause that is not detected is false whatever the
    /// unreadable clause holds, and an Or with a detected clause is true; otherwise the
    /// unreadable clause makes the compound unreadable.
    /// </summary>
    [Theory]
    [InlineData("And", true, DetectionEvaluation.Error)]
    [InlineData("And", false, DetectionEvaluation.NotDetected)]
    [InlineData("Or", true, DetectionEvaluation.Detected)]
    [InlineData("Or", false, DetectionEvaluation.Error)]
    public void ACompoundWithAnUnreadableClauseIsDecidedOnlyWhenTheOthersDecideIt(
        string connector, bool otherPresent, DetectionEvaluation want)
    {
        var det = Compound(connector, ErrorClause(), KeyClause(otherPresent ? @"SOFTWARE\Present" : @"SOFTWARE\Absent"));

        Assert.Equal(want, DetectionEvaluator.Trace(det, Context(TwoKeys())).Verdict);
    }

    [Theory]
    [InlineData(true, true, false, true, DetectionEvaluation.Detected)]
    [InlineData(false, true, true, true, DetectionEvaluation.Detected)]
    [InlineData(true, false, true, false, DetectionEvaluation.NotDetected)]
    public void GroupSizesFormTwoAndRunsJoinedByOr(bool a, bool b, bool c, bool d, DetectionEvaluation want)
    {
        string P(bool present) => present ? @"SOFTWARE\Present" : @"SOFTWARE\Absent";
        var det = Compound("And", KeyClause(P(a)), KeyClause(P(b)), KeyClause(P(c)), KeyClause(P(d)));
        det.GroupSizes = [2, 2];

        Assert.Equal(want, DetectionEvaluator.Trace(det, Context(TwoKeys())).Verdict);
    }

    [Theory]
    [InlineData(new[] { 1, 3 })]
    [InlineData(new[] { 0, 3 })]
    [InlineData(new[] { 1, 1, 1 })]
    public void MalformedGroupSizesAreAnError(int[] sizes)
    {
        var det = Compound("And", KeyClause(@"SOFTWARE\Present"), KeyClause(@"SOFTWARE\Present"), KeyClause(@"SOFTWARE\Present"));
        det.GroupSizes = sizes;

        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Trace(det, Context(TwoKeys())).Verdict);
    }

    [Fact]
    public void AnUnknownConnectorOrAnEmptyCompoundIsAnError()
    {
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Trace(
            Compound("Xor", KeyClause(@"SOFTWARE\Present")), Context(TwoKeys())).Verdict);
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Trace(
            Compound("And"), Context(TwoKeys())).Verdict);
    }

    [Fact]
    public void ACompoundNestedTooDeeplyIsAnError()
    {
        var det = KeyClause(@"SOFTWARE\Present");
        for (var i = 0; i < 40; i++) det = Compound("And", det);

        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Trace(det, Context(TwoKeys())).Verdict);
    }

    [Theory]
    [InlineData("10.0.26100.8457 (WinBuild.160101.0800)", "10.0.26100.8457")]
    [InlineData(" 1.2.3.4 ", "1.2.3.4")]
    [InlineData("1, 2, 3, 4", "1,2,3,4")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void AFileVersionLosesItsBuildLabelAndSpaces(string? raw, string want) =>
        Assert.Equal(want, DetectionEvaluator.NormalizeFileVersion(raw));

    [Fact]
    public void AFileVersionRuleReadsAWindowsBinaryWithABuildLabel()
    {
        var det = new StageDetection
        {
            Type = "File",
            FilePath = @"%SystemRoot%\System32",
            FileName = "notepad.exe",
            PropertyType = "Version",
            ExpectedValue = "6.0",
            Operator = "GreaterEquals"
        };

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Trace(det, Context(new FakeRegistryProvider())).Verdict);
    }

    [Fact]
    public void AThirtyTwoBitFileRuleReadsTheRedirectedSystemFolder()
    {
        var det = new StageDetection { Type = "File", FilePath = @"C:\Windows\System32", FileName = "notepad.exe", Is64Bit = false };

        Assert.Equal(@"C:\Windows\SysWOW64\notepad.exe", DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
        det.Is64Bit = true;
        Assert.Equal(@"C:\Windows\System32\notepad.exe", DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
        det.FilePath = @"C:\Windows\System32Extra";
        det.Is64Bit = false;
        Assert.Equal(@"C:\Windows\System32Extra\notepad.exe", DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
    }

    [Theory]
    [InlineData("Size")]
    [InlineData("DateModified")]
    public void AFilePropertyThisEvaluatorDoesNotReadIsAnError(string property)
    {
        var det = new StageDetection
        {
            Type = "File",
            FilePath = @"%SystemRoot%\System32",
            FileName = "notepad.exe",
            PropertyType = property,
            ExpectedValue = "1"
        };

        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Trace(det, Context(new FakeRegistryProvider())).Verdict);
    }

    private static StageDetection Script() => new() { Type = "Script", ScriptText = "Write-Output 'x'", ScriptLanguage = "PowerShell" };

    [Theory]
    [InlineData(0, "Installed", DetectionEvaluation.Detected)]
    [InlineData(0, "", DetectionEvaluation.NotDetected)]
    [InlineData(1, "Installed", DetectionEvaluation.Detected)]
    [InlineData(1, "", DetectionEvaluation.NotDetected)]
    public void TheOutputOnlyRuleReadsStdoutAlone(int exitCode, string stdout, DetectionEvaluation want)
    {
        var trace = DetectionEvaluator.Trace(Script(), Context(new FakeRegistryProvider(),
            _ => new DetectionScriptRun(exitCode, stdout, "", false), ScriptVerdictRule.OutputOnly));

        Assert.Equal(want, trace.Verdict);
        Assert.Equal(exitCode, trace.Script!.ExitCode);
    }

    [Fact]
    public void TheOutputOnlyRuleWarnsWhenTheExitCodeIsNonZeroAndStdoutHasText()
    {
        var trace = DetectionEvaluator.Trace(Script(), Context(new FakeRegistryProvider(),
            _ => new DetectionScriptRun(3, "Installed", "boom", false), ScriptVerdictRule.OutputOnly));

        Assert.Contains("ConfigMgr", trace.Detail);
        Assert.Contains("exit code 3", trace.Detail);
    }

    [Fact]
    public void TheExitCodeRuleStaysTheDefault()
    {
        var trace = DetectionEvaluator.Trace(Script(), Context(new FakeRegistryProvider(),
            _ => new DetectionScriptRun(1, "Installed", "", false)));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
    }

    [Theory]
    [InlineData(ScriptVerdictRule.OutputOnly)]
    [InlineData(ScriptVerdictRule.ExitCodeAndOutput)]
    public void AScriptThatTimesOutIsAnError(ScriptVerdictRule rule)
    {
        var trace = DetectionEvaluator.Trace(Script(), Context(new FakeRegistryProvider(),
            _ => new DetectionScriptRun(null, "partial", "", true), rule));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Contains("timed out", trace.Detail);
    }

    [Fact]
    public void ARunnerThatThrowsIsAnError()
    {
        var trace = DetectionEvaluator.Trace(Script(), Context(new FakeRegistryProvider(),
            _ => throw new InvalidOperationException("no host")));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Contains("no host", trace.Detail);
    }

    [Fact]
    public void CancellationFromTheRunnerIsNotSwallowed() =>
        Assert.Throws<OperationCanceledException>(() => DetectionEvaluator.Trace(Script(), Context(new FakeRegistryProvider(),
            _ => throw new OperationCanceledException())));

    [Fact]
    public void EachClauseDescribesItself()
    {
        Assert.Equal(@"Registry value HKLM\SOFTWARE\Contoso\Widget [Version] IsEquals '2.0' (32-bit view)",
            DetectionEvaluator.Describe(Value("2.0", is64: false)));
        Assert.Equal(@"Registry key HKCU\SOFTWARE\Contoso\Widget (64-bit view)",
            DetectionEvaluator.Describe(new StageDetection { Type = "RegistryKey", RegistryKeyRelative = Key, Hive = "CurrentUser" }));
        Assert.Equal(@"File %SystemRoot%\System32\notepad.exe Version GreaterEquals '6.0'",
            DetectionEvaluator.Describe(new StageDetection { Type = "File", FilePath = @"%SystemRoot%\System32", FileName = "notepad.exe", PropertyType = "Version", ExpectedValue = "6.0" }));
        Assert.Equal("File C:\\x\\a.exe exists",
            DetectionEvaluator.Describe(new StageDetection { Type = "File", FilePath = @"C:\x", FileName = "a.exe" }));
        Assert.Equal("Script (PowerShell, 1 line)", DetectionEvaluator.Describe(Script()));
        Assert.Equal("Compound Or of 2 clauses",
            DetectionEvaluator.Describe(Compound("Or", KeyClause("a"), KeyClause("b"))));
    }

    [Fact]
    public void TheExistingEntryPointNowEvaluatesACompound()
    {
        var det = Compound("Or",
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion" },
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\Absent-" + Guid.NewGuid().ToString("N") });

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(det));
    }
}
