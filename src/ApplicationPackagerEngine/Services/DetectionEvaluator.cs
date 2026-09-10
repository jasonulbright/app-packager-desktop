using System.Diagnostics;
using ApplicationPackagerEngine.Models;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Verdict of one detection rule. Error is separate from NotDetected on purpose: the
/// post-uninstall clean check negates the result, and negating a read failure or an
/// unsupported rule would report a machine as clean without having looked.
/// </summary>
public enum DetectionEvaluation
{
    NotDetected,
    Detected,
    Error
}

/// <summary>
/// Evaluates a staged detection rule the way the deployment system will: the rule's own
/// hive and registry view, its property type, and the comparison operator it declares.
/// An operator this evaluator does not implement fails closed, and it fails closed before
/// any presence shortcut, so an unsupported rule cannot pass itself off as an absence.
/// </summary>
public static class DetectionEvaluator
{
    /// <summary>The operator a registry value rule takes when it declares none.</summary>
    public const string RegistryDefaultOperator = "IsEquals";

    /// <summary>
    /// The operator a file rule takes when it declares none. The MECM clause builder and
    /// the Graph rule mapping both default a file rule this way, so the evaluation has to
    /// agree with what actually gets deployed.
    /// </summary>
    public const string FileDefaultOperator = "GreaterEquals";

    private static readonly string[] OrderingOperators =
        ["IsEquals", "NotEquals", "GreaterThan", "GreaterEquals", "LessThan", "LessEquals"];

    private static readonly string[] StringOperators = ["BeginsWith", "EndsWith", "Contains"];

    /// <summary>Runs a detection script, returning its exit code and stdout.</summary>
    public delegate (int ExitCode, string StdOut) ScriptRunner(string scriptText);

    /// <summary>True when this evaluator implements the rule's declared comparison.</summary>
    public static bool IsSupportedOperator(string? op, string defaultOperator)
    {
        var name = string.IsNullOrWhiteSpace(op) ? defaultOperator : op!.Trim();
        return OrderingOperators.Contains(name, StringComparer.OrdinalIgnoreCase)
            || StringOperators.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares a value read off the machine against the rule. A null or empty reading is
    /// an absent value, not an error; a rule carrying no expected value is satisfied by
    /// the value existing at all. The operator is validated first, so an unsupported rule
    /// is an error whether or not its target happens to be present.
    /// </summary>
    public static DetectionEvaluation CompareValue(
        string? actual, StageDetection det, string? defaultOperator = null)
    {
        var fallback = defaultOperator ?? RegistryDefaultOperator;
        if (!IsSupportedOperator(det.Operator, fallback)) return DetectionEvaluation.Error;

        if (string.IsNullOrEmpty(actual)) return DetectionEvaluation.NotDetected;
        if (string.IsNullOrEmpty(det.ExpectedValue)) return DetectionEvaluation.Detected;

        var op = string.IsNullOrWhiteSpace(det.Operator) ? fallback : det.Operator!.Trim();
        var expected = det.ExpectedValue!;

        // Prefix, suffix and substring are string comparisons whatever the property type
        // says; there is no version reading of "starts with".
        switch (op.ToLowerInvariant())
        {
            case "beginswith":
                return Verdict(actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase));
            case "endswith":
                return Verdict(actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase));
            case "contains":
                return Verdict(actual.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }

        if (det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true)
        {
            // A version rule that cannot read either side as a version has not been
            // evaluated. Reporting "not detected" would hide a malformed rule.
            if (!Version.TryParse(actual, out var actualVersion) ||
                !Version.TryParse(expected, out var expectedVersion))
                return DetectionEvaluation.Error;

            return CompareOrdered(actualVersion.CompareTo(expectedVersion), op);
        }

        if (op.Equals("IsEquals", StringComparison.OrdinalIgnoreCase))
            return Verdict(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
        if (op.Equals("NotEquals", StringComparison.OrdinalIgnoreCase))
            return Verdict(!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));

        return CompareOrdered(string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase), op);
    }

    private static DetectionEvaluation CompareOrdered(int comparison, string op) => op.ToLowerInvariant() switch
    {
        "isequals" => Verdict(comparison == 0),
        "notequals" => Verdict(comparison != 0),
        "greaterthan" => Verdict(comparison > 0),
        "greaterequals" => Verdict(comparison >= 0),
        "lessthan" => Verdict(comparison < 0),
        "lessequals" => Verdict(comparison <= 0),
        _ => DetectionEvaluation.Error
    };

    private static DetectionEvaluation Verdict(bool detected) =>
        detected ? DetectionEvaluation.Detected : DetectionEvaluation.NotDetected;

    /// <summary>
    /// Evaluates the rule against this machine. Script rules are evaluated only when a
    /// runner is supplied; this process does not launch one of its own.
    /// </summary>
    public static DetectionEvaluation Evaluate(StageDetection det, ScriptRunner? scriptRunner = null)
    {
        try
        {
            return det.Type switch
            {
                "RegistryKeyValue" => EvaluateRegistryKeyValue(det),
                "RegistryKey" => EvaluateRegistryKey(det),
                "File" => EvaluateFile(det),
                "Script" => EvaluateScript(det, scriptRunner),
                // An unimplemented rule type has not been evaluated, so it cannot answer
                // either question the runner asks of it.
                _ => DetectionEvaluation.Error
            };
        }
        catch
        {
            return DetectionEvaluation.Error;
        }
    }

    private static RegistryKey OpenBase(StageDetection det) =>
        RegistryKey.OpenBaseKey(
            det.IsCurrentUserHive ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            det.Is64Bit ? RegistryView.Registry64 : RegistryView.Registry32);

    private static DetectionEvaluation EvaluateRegistryKeyValue(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.RegistryKeyRelative)) return DetectionEvaluation.Error;
        // Refused before the key is opened, so a missing key cannot mask a bad rule.
        if (!IsSupportedOperator(det.Operator, RegistryDefaultOperator)) return DetectionEvaluation.Error;

        using var root = OpenBase(det);
        using var key = root.OpenSubKey(det.RegistryKeyRelative);
        if (key is null) return DetectionEvaluation.NotDetected;

        var value = key.GetValue(det.ValueName ?? "DisplayVersion")?.ToString();
        return CompareValue(value, det, RegistryDefaultOperator);
    }

    private static DetectionEvaluation EvaluateRegistryKey(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.RegistryKeyRelative)) return DetectionEvaluation.Error;

        using var root = OpenBase(det);
        using var key = root.OpenSubKey(det.RegistryKeyRelative);
        return key is null ? DetectionEvaluation.NotDetected : DetectionEvaluation.Detected;
    }

    private static DetectionEvaluation EvaluateFile(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.FilePath) || string.IsNullOrEmpty(det.FileName))
            return DetectionEvaluation.Error;

        var isVersion = det.PropertyType?.Equals("Version", StringComparison.OrdinalIgnoreCase) == true;
        var comparesValue = isVersion && !string.IsNullOrEmpty(det.ExpectedValue);

        // A file rule that compares a version defaults to greater-or-equal, matching what
        // the deployment systems emit. Validated before the file is probed.
        if (comparesValue && !IsSupportedOperator(det.Operator, FileDefaultOperator))
            return DetectionEvaluation.Error;

        var filePath = Path.Combine(
            Environment.ExpandEnvironmentVariables(det.FilePath),
            det.FileName);

        var presence = FilePresence(filePath);
        if (presence != DetectionEvaluation.Detected) return presence;

        if (!comparesValue) return DetectionEvaluation.Detected;

        // A version rule on a present file is a version comparison, not a presence check.
        var fileVersion = FileVersionInfo.GetVersionInfo(filePath).FileVersion;
        if (string.IsNullOrWhiteSpace(fileVersion)) return DetectionEvaluation.Error;

        return CompareValue(fileVersion, det, FileDefaultOperator);
    }

    /// <summary>
    /// Detected when the file is there, NotDetected when it provably is not, Error when
    /// the answer could not be obtained. File.Exists reports false for a path it cannot
    /// read, which is not the same as the file being absent; the attribute read separates
    /// those, and a directory carrying the file's name is not the file.
    /// </summary>
    private static DetectionEvaluation FilePresence(string filePath)
    {
        try
        {
            var attributes = File.GetAttributes(filePath);
            return attributes.HasFlag(FileAttributes.Directory)
                ? DetectionEvaluation.NotDetected
                : DetectionEvaluation.Detected;
        }
        catch (FileNotFoundException) { return DetectionEvaluation.NotDetected; }
        catch (DirectoryNotFoundException) { return DetectionEvaluation.NotDetected; }
        catch (Exception) { return DetectionEvaluation.Error; }
    }

    private static DetectionEvaluation EvaluateScript(StageDetection det, ScriptRunner? runner)
    {
        if (string.IsNullOrWhiteSpace(det.ScriptText)) return DetectionEvaluation.Error;

        // Only PowerShell bodies are understood; running another language as PowerShell
        // would produce a verdict from a script that never ran as written.
        if (!string.IsNullOrWhiteSpace(det.ScriptLanguage)
            && !det.ScriptLanguage!.Trim().Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
            return DetectionEvaluation.Error;

        // This process launches nothing of its own. A caller that can run a script safely
        // supplies the runner; without one the rule is simply not evaluated here.
        if (runner is null) return DetectionEvaluation.Error;

        var (exitCode, stdout) = runner(det.ScriptText!);

        // A shipped detection body reports absence by exiting 0 with no output, so a
        // nonzero exit is the script having failed, not the application being absent.
        if (exitCode != 0) return DetectionEvaluation.Error;

        return Verdict(!string.IsNullOrWhiteSpace(stdout));
    }
}
