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
    public void AnUnexpectedFailureShowsItsTypeAndNotItsText()
    {
        var context = new DetectionEvaluationContext
        {
            Registry = new FakeRegistryProvider(),
            RunScript = _ => throw new InvalidOperationException(@"internal detail \\server\share"),
            ScriptRule = ScriptVerdictRule.OutputOnly
        };

        var trace = DetectionEvaluator.Trace(new StageDetection { Type = "Script", ScriptText = "'x'" }, context);

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Equal("The clause could not be evaluated (InvalidOperationException).", trace.Detail);
    }

    [Fact]
    public void ARefusedScriptShowsTheRefusalText()
    {
        var context = new DetectionEvaluationContext
        {
            Registry = new FakeRegistryProvider(),
            RunScript = _ => throw new DetectionScriptRefusedException(DetectionScriptRefusedException.NoContainment),
            ScriptRule = ScriptVerdictRule.OutputOnly
        };

        var trace = DetectionEvaluator.Trace(new StageDetection { Type = "Script", ScriptText = "'x'" }, context);

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Equal(DetectionScriptRefusedException.NoContainment, trace.Detail);
    }

    [Fact]
    public void AFileThatCannotBeProbedShowsTheFailureTypeAndNotItsText()
    {
        var det = new StageDetection { Type = "File", FilePath = @"C:\Bad|Folder", FileName = "x.exe", Is64Bit = true };

        var trace = DetectionEvaluator.Trace(det, Context(new FakeRegistryProvider()));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.StartsWith(@"File could not be read: C:\Bad|Folder\x.exe (", trace.Detail);
        Assert.EndsWith("Exception).", trace.Detail);
        Assert.DoesNotContain("syntax", trace.Detail, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("The clause could not be evaluated: access was denied.", trace.Detail);
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
    [InlineData("1, 2, 3, 4", "1.2.3.4")]
    [InlineData("5,1,0,0", "5.1.0.0")]
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
    [InlineData(@"C:\Program Files\Acme\System32", @"C:\Program Files\Acme\System32\agent.exe")]
    [InlineData(@"D:\Data\System32", @"D:\Data\System32\agent.exe")]
    [InlineData(@"C:\Windows\System32Backup\System32", @"C:\Windows\System32Backup\System32\agent.exe")]
    public void AThirtyTwoBitFileRuleKeepsASystem32FolderOutsideTheWindowsSystemFolder(string folder, string expected)
    {
        var det = new StageDetection { Type = "File", FilePath = folder, FileName = "agent.exe", Is64Bit = false };

        Assert.Equal(expected, DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"%windir%\System32")]
    [InlineData(@"%SystemRoot%\system32")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\")]
    public void AThirtyTwoBitFileRuleUnderTheWindowsSystemFolderReadsSysWow64(string folder)
    {
        var det = new StageDetection { Type = "File", FilePath = folder, FileName = "notepad.exe", Is64Bit = false };
        var expected = Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!, "SysWOW64", "notepad.exe");

        Assert.Equal(expected, DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
    }

    [Fact]
    public void AThirtyTwoBitFileRuleBelowTheWindowsSystemFolderKeepsTheRestOfThePath()
    {
        var det = new StageDetection { Type = "File", FilePath = @"%windir%\System32\drivers\etc", FileName = "hosts", Is64Bit = false };
        var expected = Path.Combine(Path.GetDirectoryName(Environment.SystemDirectory)!, "SysWOW64", "drivers", "etc", "hosts");

        Assert.Equal(expected, DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
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
        Assert.Contains("InvalidOperationException", trace.Detail);
        Assert.DoesNotContain("no host", trace.Detail);
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

    [Theory]
    [InlineData("2.0", "2.0.0", "GreaterEquals", DetectionEvaluation.Detected)]
    [InlineData("2.0", "2.0.0.0", "IsEquals", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "1.0", "GreaterThan", DetectionEvaluation.NotDetected)]
    [InlineData("1.0", "1.0.0", "LessThan", DetectionEvaluation.NotDetected)]
    [InlineData("2.0", "2.0.1", "GreaterEquals", DetectionEvaluation.NotDetected)]
    public void MissingVersionComponentsCountAsZero(string actual, string expected, string op, DetectionEvaluation want) =>
        Assert.Equal(want, DetectionEvaluator.CompareValue(actual,
            new StageDetection { ExpectedValue = expected, Operator = op, PropertyType = "Version" }));

    [Fact]
    public void AFileVersionWithFourComponentsEqualsAShorterExpectedVersion() =>
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.CompareValue("5.1.0.0",
            new StageDetection { ExpectedValue = "5.1", Operator = "IsEquals", PropertyType = "Version" },
            DetectionEvaluator.FileDefaultOperator));

    [Theory]
    [InlineData(@"\SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    [InlineData(@"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    [InlineData(@"\HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\")]
    [InlineData(@"hkey_local_machine\SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    public void AKeyPathWithASeparatorOrTheClauseHiveIsNormalized(string path)
    {
        var context = new DetectionEvaluationContext();

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Trace(
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = path }, context).Verdict);
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Trace(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = path,
            ValueName = "CurrentMajorVersionNumber",
            ExpectedValue = "6",
            Operator = "GreaterEquals",
            PropertyType = "Integer"
        }, context).Verdict);
    }

    [Theory]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "LocalMachine")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "LocalMachine")]
    [InlineData(@"HKLM\Software\Microsoft\Windows\CurrentVersion", "CurrentUser")]
    [InlineData(@"HKCR\.txt", "LocalMachine")]
    [InlineData(@"HKEY_USERS\.DEFAULT", "LocalMachine")]
    public void AKeyPathNamingAnotherHiveIsAnError(string path, string hive)
    {
        var registry = new FakeRegistryProvider();

        var key = DetectionEvaluator.Trace(
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = path, Hive = hive }, Context(registry));
        var value = DetectionEvaluator.Trace(
            new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = path, Hive = hive, ExpectedValue = "1" }, Context(registry));

        Assert.Equal(DetectionEvaluation.Error, key.Verdict);
        Assert.Contains("hive", key.Detail);
        Assert.Equal(DetectionEvaluation.Error, value.Verdict);
        Assert.Equal(0, registry.OpenCount);
    }

    [Fact]
    public void AKeyPathThatIsOnlyAHiveIsAnError() =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Trace(
            new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"HKLM\" }, Context(new FakeRegistryProvider())).Verdict);

    [Fact]
    public void ADwordAboveTwoToTheThirtyFirstIsReadUnsigned()
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Flags", unchecked((int)0x80000000), RegistryValueKind.DWord);

        var trace = DetectionEvaluator.Trace(
            Value("2147483647", "GreaterThan", "Integer", valueName: "Flags"), Context(registry));

        Assert.Equal(DetectionEvaluation.Detected, trace.Verdict);
        Assert.Contains("2147483648", trace.Detail);
    }

    [Fact]
    public void AQwordAboveTheSignedRangeIsReadUnsigned()
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Huge", -1L, RegistryValueKind.QWord);

        var trace = DetectionEvaluator.Trace(
            Value("9223372036854775807", "GreaterThan", "Integer", valueName: "Huge"), Context(registry));

        Assert.Equal(DetectionEvaluation.Detected, trace.Verdict);
        Assert.Contains("18446744073709551615", trace.Detail);
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Trace(
            Value("18446744073709551615", "IsEquals", "Integer", valueName: "Huge"), Context(registry)).Verdict);
    }

    [Theory]
    [InlineData("NotEquals", DetectionEvaluation.Detected)]
    [InlineData("IsEquals", DetectionEvaluation.NotDetected)]
    [InlineData("BeginsWith", DetectionEvaluation.NotDetected)]
    [InlineData("GreaterEquals", DetectionEvaluation.NotDetected)]
    public void AnEmptyValueIsAbsentOnlyForAPositiveMatch(string op, DetectionEvaluation want)
    {
        var registry = new FakeRegistryProvider()
            .Set(RegistryHive.LocalMachine, RegistryView.Registry64, Key, "Version", "");

        Assert.Equal(want, DetectionEvaluator.Trace(Value("2.0", op), Context(registry)).Verdict);
        Assert.Equal(want, DetectionEvaluator.CompareValue("", new StageDetection { ExpectedValue = "2.0", Operator = op, PropertyType = "String" }));
    }

    [Fact]
    public void AThirtyTwoBitFileRuleReadsTheThirtyTwoBitProgramFiles()
    {
        var det = new StageDetection { Type = "File", FilePath = @"%ProgramFiles%\Contoso", FileName = "app.exe", Is64Bit = false };
        var x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)")!;
        var native = Environment.GetEnvironmentVariable("ProgramW6432")!;

        Assert.Equal(Path.Combine(x86, "Contoso", "app.exe"), DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
        det.FilePath = @"%programfiles%\Contoso";
        Assert.Equal(Path.Combine(x86, "Contoso", "app.exe"), DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
        det.FilePath = @"%CommonProgramFiles%\Contoso";
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("CommonProgramFiles(x86)")!, "Contoso", "app.exe"),
            DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
        det.FilePath = @"%ProgramFiles(x86)%\Contoso";
        Assert.Equal(Path.Combine(x86, "Contoso", "app.exe"), DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
        det.FilePath = @"%ProgramFiles%\Contoso";
        det.Is64Bit = true;
        Assert.Equal(Path.Combine(native, "Contoso", "app.exe"), DetectionEvaluator.ResolveFilePath(det), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"\Windows\notepad.exe")]
    public void ARootedFileNameIsAnError(string fileName)
    {
        var trace = DetectionEvaluator.Trace(
            new StageDetection { Type = "File", FilePath = @"C:\Contoso", FileName = fileName }, Context(new FakeRegistryProvider()));

        Assert.Equal(DetectionEvaluation.Error, trace.Verdict);
        Assert.Contains("file name", trace.Detail);
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
