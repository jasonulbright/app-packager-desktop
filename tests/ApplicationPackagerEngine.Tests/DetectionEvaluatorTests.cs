using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The local runner reads the rule's own hive, so a per-user rule finds its key; a prefix
/// comparison matches a value that carries the expected prefix plus a suffix; and an
/// operator the runner does not implement is an error, not a greater-or-equal comparison.
/// An error stays distinct from a genuine not-detected, because the post-uninstall check
/// negates the verdict.
/// </summary>
public sealed class DetectionEvaluatorTests : IDisposable
{
    // A disposable key under the current user's own software hive. Real application keys
    // are never touched.
    private readonly string _keyPath = @"Software\PackrTests\" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false); }
        catch (Exception) { /* best effort */ }
        try { Registry.CurrentUser.DeleteSubKey(@"Software\PackrTests", throwOnMissingSubKey: false); }
        catch (Exception) { /* the parent stays if another run holds a key under it */ }
    }

    private void WriteValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath)!;
        key.SetValue(name, value);
    }

    private StageDetection UserRule(string expected, string op, string propertyType = "String") => new()
    {
        Type = "RegistryKeyValue",
        Hive = "CurrentUser",
        RegistryKeyRelative = _keyPath,
        ValueName = "DisplayVersion",
        ExpectedValue = expected,
        Operator = op,
        PropertyType = propertyType,
        Is64Bit = false
    };

    [Theory]
    [InlineData("7.23.0", "7.23", "BeginsWith", DetectionEvaluation.Detected)]
    [InlineData("17.11-1", "17.11", "BeginsWith", DetectionEvaluation.Detected)]
    [InlineData("7.2", "7.23", "BeginsWith", DetectionEvaluation.NotDetected)]
    [InlineData("1.0.0-beta", "beta", "EndsWith", DetectionEvaluation.Detected)]
    [InlineData("1.0.0-beta", "alpha", "EndsWith", DetectionEvaluation.NotDetected)]
    [InlineData("1.0.0-beta.2", "beta", "Contains", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "beta", "Contains", DetectionEvaluation.NotDetected)]
    public void StringComparisonsReadTheWholeValue(
        string actual, string expected, string op, DetectionEvaluation want) =>
        Assert.Equal(want, DetectionEvaluator.CompareValue(actual, new StageDetection
        {
            ExpectedValue = expected,
            Operator = op,
            PropertyType = "String"
        }));

    /// <summary>An equality comparison in place of the prefix rule fails an installed value that carries a suffix.</summary>
    [Fact]
    public void APrefixRuleIsNotAnEqualityComparison()
    {
        var det = new StageDetection { ExpectedValue = "7.23", Operator = "BeginsWith", PropertyType = "String" };

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.CompareValue("7.23.0", det));
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.CompareValue("7.23", det));
    }

    [Theory]
    [InlineData("2.0.0", "1.0.0", "GreaterThan", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "1.0.0", "GreaterThan", DetectionEvaluation.NotDetected)]
    [InlineData("1.0.0", "1.0.0", "GreaterEquals", DetectionEvaluation.Detected)]
    [InlineData("0.9.0", "1.0.0", "GreaterEquals", DetectionEvaluation.NotDetected)]
    [InlineData("0.9.0", "1.0.0", "LessThan", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "1.0.0", "LessEquals", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "1.0.0", "IsEquals", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "2.0.0", "NotEquals", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", "1.0.0", "NotEquals", DetectionEvaluation.NotDetected)]
    public void VersionComparisonsHonorTheDeclaredOperator(
        string actual, string expected, string op, DetectionEvaluation want) =>
        Assert.Equal(want, DetectionEvaluator.CompareValue(actual, new StageDetection
        {
            ExpectedValue = expected,
            Operator = op,
            PropertyType = "Version"
        }));

    /// <summary>A lower installed version is not detected rather than waved through.</summary>
    [Fact]
    public void ALowerInstalledVersionIsNotDetected() =>
        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.CompareValue("1.136.2",
            new StageDetection { ExpectedValue = "1.137.0", Operator = "GreaterEquals", PropertyType = "Version" }));

    [Theory]
    [InlineData("Matches")]
    [InlineData("OneOf")]
    [InlineData("NotBeginsWith")]
    public void AnUnsupportedOperatorIsAnErrorRatherThanAFallback(string op) =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.CompareValue("1.0.0",
            new StageDetection { ExpectedValue = "1.0.0", Operator = op, PropertyType = "Version" }));

    /// <summary>
    /// The operator is judged before the value is, so a rule nobody can evaluate does not
    /// pass itself off as an absence and then as a clean machine.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnsupportedOperatorIsAnErrorEvenWhenTheValueIsAbsent(string? actual) =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.CompareValue(actual,
            new StageDetection { ExpectedValue = "1.0.0", Operator = "Matches", PropertyType = "Version" }));

    [Fact]
    public void AnUnsupportedOperatorIsAnErrorEvenWhenTheRegistryKeyIsMissing() =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Evaluate(UserRule("1.0", "Matches")));

    /// <summary>
    /// A registry value rule with no operator is an equality comparison, which is what the
    /// MECM clause builder emits for it.
    /// </summary>
    [Theory]
    [InlineData("1.0.0", DetectionEvaluation.Detected)]
    [InlineData("1.0.1", DetectionEvaluation.NotDetected)]
    public void ARegistryRuleWithNoOperatorDefaultsToEquality(string actual, DetectionEvaluation want) =>
        Assert.Equal(want, DetectionEvaluator.CompareValue(actual,
            new StageDetection { ExpectedValue = "1.0.0", PropertyType = "Version" }));

    /// <summary>
    /// A file version rule with no operator is a greater-or-equal comparison. Applying the
    /// registry default here would fail a machine carrying a newer build.
    /// </summary>
    [Theory]
    [InlineData("2.0.0", DetectionEvaluation.Detected)]
    [InlineData("1.0.0", DetectionEvaluation.Detected)]
    [InlineData("0.9.0", DetectionEvaluation.NotDetected)]
    public void AFileVersionRuleWithNoOperatorDefaultsToGreaterOrEqual(
        string actual, DetectionEvaluation want) =>
        Assert.Equal(want, DetectionEvaluator.CompareValue(actual,
            new StageDetection { ExpectedValue = "1.0.0", PropertyType = "Version" },
            DetectionEvaluator.FileDefaultOperator));

    [Fact]
    public void TheTwoDefaultsAreTheOnesTheDeploymentSystemsEmit()
    {
        Assert.Equal("IsEquals", DetectionEvaluator.RegistryDefaultOperator);
        Assert.Equal("GreaterEquals", DetectionEvaluator.FileDefaultOperator);
    }

    [Theory]
    [InlineData("17.11-1", "17.11")]
    [InlineData("1.0", "not-a-version")]
    [InlineData("v2.1", "2.1")]
    public void AVersionComparisonAgainstAnUnparseableValueIsAnError(string actual, string expected) =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.CompareValue(actual, new StageDetection
        {
            ExpectedValue = expected,
            Operator = "GreaterEquals",
            PropertyType = "Version"
        }));

    [Fact]
    public void AnUnimplementedRuleTypeIsAnError() =>
        Assert.Equal(DetectionEvaluation.Error,
            DetectionEvaluator.Evaluate(new StageDetection { Type = "Msi" }));

    [Fact]
    public void AnAbsentValueIsNotDetectedRatherThanAnError() =>
        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.CompareValue(null,
            new StageDetection { ExpectedValue = "1.0", Operator = "IsEquals" }));

    /// <summary>A rule with no expected value asks only that the value exists.</summary>
    [Fact]
    public void AnExpectationlessRuleIsSatisfiedByThePresenceOfTheValue() =>
        Assert.Equal(DetectionEvaluation.Detected,
            DetectionEvaluator.CompareValue("anything", new StageDetection { Operator = "IsEquals" }));

    /// <summary>The per-user rule the VS Code user definition stages.</summary>
    [Fact]
    public void APerUserRuleReadsTheCurrentUserHive()
    {
        WriteValue("DisplayVersion", "1.137.0");

        var det = UserRule("1.137.0", "GreaterEquals", "Version");

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(det));
    }

    [Fact]
    public void APerUserRuleUnderTheSixtyFourBitViewAlsoReadsIt()
    {
        WriteValue("DisplayVersion", "1.137.0");

        var det = UserRule("1.137.0", "GreaterEquals", "Version");
        det.Is64Bit = true;

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(det));
    }

    /// <summary>The same rule pointed at the machine hive finds nothing, so a runner fixed to HKLM fails a per-user rule.</summary>
    [Fact]
    public void TheSameKeyIsAbsentFromTheMachineHive()
    {
        WriteValue("DisplayVersion", "1.137.0");

        var det = UserRule("1.137.0", "GreaterEquals", "Version");
        det.Hive = "LocalMachine";

        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.Evaluate(det));
    }

    [Fact]
    public void APrefixRuleAgainstARealPerUserValueMatchesTheSuffixedReading()
    {
        WriteValue("DisplayVersion", "7.23.0");

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(UserRule("7.23", "BeginsWith")));
    }

    [Fact]
    public void AMissingKeyIsNotDetected() =>
        Assert.Equal(DetectionEvaluation.NotDetected,
            DetectionEvaluator.Evaluate(UserRule("1.0", "IsEquals")));

    [Fact]
    public void AKeyExistenceRuleReadsItsOwnHive()
    {
        WriteValue("Anything", "1");

        var det = new StageDetection
        {
            Type = "RegistryKey",
            Hive = "CurrentUser",
            RegistryKeyRelative = _keyPath,
            Is64Bit = false
        };

        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(det));

        det.Hive = "LocalMachine";
        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.Evaluate(det));
    }

    /// <summary>
    /// Reading the attributes tells a missing path from an unreadable one, but a directory
    /// carrying the file's name has attributes too and is not the file.
    /// </summary>
    [Fact]
    public void ADirectoryNamedLikeTheFileDoesNotSatisfyAFileRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "packr-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "setup.exe"));
        try
        {
            var det = new StageDetection
            {
                Type = "File",
                FilePath = root,
                FileName = "setup.exe",
                PropertyType = "Existence"
            };

            Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.Evaluate(det));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ARealFileSatisfiesAnExistenceRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "packr-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "setup.exe"), "not really an executable");
        try
        {
            var det = new StageDetection
            {
                Type = "File",
                FilePath = root,
                FileName = "setup.exe",
                PropertyType = "Existence"
            };

            Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(det));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnAbsentFileIsNotDetected()
    {
        var det = new StageDetection
        {
            Type = "File",
            FilePath = Path.Combine(Path.GetTempPath(), "packr-absent-" + Guid.NewGuid().ToString("N")),
            FileName = "setup.exe",
            PropertyType = "Existence"
        };

        Assert.Equal(DetectionEvaluation.NotDetected, DetectionEvaluator.Evaluate(det));
    }

    /// <summary>
    /// The engine's own build output carries a parseable FileVersion, so the file-reading
    /// path can be exercised without installing anything.
    /// </summary>
    [Theory]
    [InlineData("0.9.0.0", "GreaterEquals", DetectionEvaluation.Detected)]
    [InlineData("1.0.0.0", "GreaterEquals", DetectionEvaluation.Detected)]
    [InlineData("2.0.0.0", "GreaterEquals", DetectionEvaluation.NotDetected)]
    [InlineData("1.0.0.0", "IsEquals", DetectionEvaluation.Detected)]
    [InlineData("2.0.0.0", "IsEquals", DetectionEvaluation.NotDetected)]
    [InlineData("0.9.0.0", null, DetectionEvaluation.Detected)]
    [InlineData("2.0.0.0", null, DetectionEvaluation.NotDetected)]
    [InlineData("1.0.0.0", "Matches", DetectionEvaluation.Error)]
    public void AFileVersionRuleReadsTheFileItNames(
        string expected, string? op, DetectionEvaluation want)
    {
        var assembly = typeof(DetectionEvaluator).Assembly.Location;
        var det = new StageDetection
        {
            Type = "File",
            FilePath = Path.GetDirectoryName(assembly)!,
            FileName = Path.GetFileName(assembly),
            PropertyType = "Version",
            ExpectedValue = expected,
            Operator = op
        };

        Assert.Equal(want, DetectionEvaluator.Evaluate(det));
    }

    /// <summary>
    /// A shipped detection body reports absence by exiting 0 with no output, so a nonzero
    /// exit is the script itself having failed and not a verdict about the application.
    /// </summary>
    [Theory]
    [InlineData(0, "Installed", DetectionEvaluation.Detected)]
    [InlineData(0, "", DetectionEvaluation.NotDetected)]
    [InlineData(0, "   ", DetectionEvaluation.NotDetected)]
    [InlineData(1, "Installed", DetectionEvaluation.Error)]
    [InlineData(1, "", DetectionEvaluation.Error)]
    [InlineData(-1, "", DetectionEvaluation.Error)]
    public void AScriptRuleReadsExitCodeAndStdout(int exitCode, string stdout, DetectionEvaluation want)
    {
        var det = new StageDetection { Type = "Script", ScriptText = "exit 0", ScriptLanguage = "PowerShell" };

        Assert.Equal(want, DetectionEvaluator.Evaluate(det, _ => (exitCode, stdout)));
    }

    [Fact]
    public void AScriptRuleWithNoBodyIsAnError() =>
        Assert.Equal(DetectionEvaluation.Error,
            DetectionEvaluator.Evaluate(new StageDetection { Type = "Script" }, _ => (0, "Installed")));

    /// <summary>Running another language as PowerShell would judge a script that never ran.</summary>
    [Theory]
    [InlineData("VBScript")]
    [InlineData("JavaScript")]
    public void AScriptRuleInAnotherLanguageIsAnError(string language) =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Evaluate(
            new StageDetection { Type = "Script", ScriptText = "x", ScriptLanguage = language },
            _ => (0, "Installed")));

    /// <summary>A blank language is the PowerShell default the staging builder applies.</summary>
    [Fact]
    public void AScriptRuleWithNoLanguageIsReadAsPowerShell() =>
        Assert.Equal(DetectionEvaluation.Detected, DetectionEvaluator.Evaluate(
            new StageDetection { Type = "Script", ScriptText = "exit 0" }, _ => (0, "Installed")));

    /// <summary>
    /// This process launches nothing of its own, so a script rule with no supplied runner
    /// is unevaluated rather than quietly not-detected.
    /// </summary>
    [Fact]
    public void AScriptRuleWithNoRunnerIsAnErrorRatherThanNotDetected() =>
        Assert.Equal(DetectionEvaluation.Error, DetectionEvaluator.Evaluate(
            new StageDetection { Type = "Script", ScriptText = "exit 0", ScriptLanguage = "PowerShell" }));

    /// <summary>
    /// The clean phase negates the verdict. An error must not become a clean machine,
    /// which is what negating an error read as false produces.
    /// </summary>
    [Theory]
    [InlineData(DetectionEvaluation.NotDetected, true)]
    [InlineData(DetectionEvaluation.Detected, false)]
    [InlineData(DetectionEvaluation.Error, false)]
    public void OnlyAGenuineNotDetectedCountsAsClean(DetectionEvaluation verdict, bool clean) =>
        Assert.Equal(clean, verdict == DetectionEvaluation.NotDetected);

    [Fact]
    public void AnUnsupportedRuleCannotCertifyAMachineClean()
    {
        var det = new StageDetection { ExpectedValue = "1.0.0", Operator = "Matches", PropertyType = "Version" };

        var verdict = DetectionEvaluator.CompareValue("1.0.0", det);

        Assert.Equal(DetectionEvaluation.Error, verdict);
        Assert.NotEqual(DetectionEvaluation.NotDetected, verdict);
    }
}
