using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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

/// <summary>How a script clause's run becomes a verdict.</summary>
public enum ScriptVerdictRule
{
    /// <summary>The deployment rule: a non-zero exit code is a failed evaluation; otherwise output means detected.</summary>
    ExitCodeAndOutput,

    /// <summary>Output means detected whatever the exit code; a non-zero exit code with output carries a warning.</summary>
    OutputOnly
}

/// <summary>One script clause run. <paramref name="ExitCode"/> is null when the process did not end on its own.</summary>
/// <param name="OutputTruncated">True when stdout or stderr went past the capture limit and was cut.</param>
public sealed record DetectionScriptRun(int? ExitCode, string StdOut, string StdErr, bool TimedOut, bool OutputTruncated = false);

/// <summary>The verdict of one clause, the plain reason for it, and the verdicts of its sub-clauses.</summary>
public sealed record DetectionTrace(
    StageDetection Clause,
    DetectionEvaluation Verdict,
    string Detail,
    IReadOnlyList<DetectionTrace> Children,
    DetectionScriptRun? Script = null);

public sealed class DetectionEvaluationContext
{
    public IRegistryProvider Registry { get; init; } = WindowsRegistryProvider.Instance;

    /// <summary>Runs a script clause body. Without one, a script clause is not evaluated.</summary>
    public Func<string, DetectionScriptRun>? RunScript { get; init; }

    public ScriptVerdictRule ScriptRule { get; init; } = ScriptVerdictRule.ExitCodeAndOutput;
}

/// <summary>
/// Evaluates a staged detection rule the way the deployment system will: the rule's own
/// hive and registry view, its property type, and the comparison operator it declares.
/// An operator this evaluator does not implement fails closed, and it fails closed before
/// any presence shortcut, so an unsupported rule cannot pass itself off as an absence.
/// </summary>
public static partial class DetectionEvaluator
{
    /// <summary>The operator a registry value rule takes when it declares none.</summary>
    public const string RegistryDefaultOperator = "IsEquals";

    /// <summary>
    /// The operator a file rule takes when it declares none. The MECM clause builder and
    /// the Graph rule mapping both default a file rule this way, so the evaluation has to
    /// agree with what actually gets deployed.
    /// </summary>
    public const string FileDefaultOperator = "GreaterEquals";

    private const string DefaultValueName = "DisplayVersion";

    /// <summary>Nesting beyond this depth is refused rather than recursed into.</summary>
    public const int MaxCompoundDepth = 16;

    private static readonly string[] OrderingOperators =
        ["IsEquals", "NotEquals", "GreaterThan", "GreaterEquals", "LessThan", "LessEquals"];

    private static readonly string[] StringOperators = ["BeginsWith", "EndsWith", "Contains"];

    private static readonly string[] StringTypes = ["String", "Existence"];

    private static readonly string[] IntegerTypes = ["Integer", "Int", "Int32", "Int64", "DWORD", "QWORD"];

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
        string? actual, StageDetection det, string? defaultOperator = null) =>
        Compare(actual, det, defaultOperator).Verdict;

    private static (DetectionEvaluation Verdict, string Reason) Compare(
        string? actual, StageDetection det, string? defaultOperator)
    {
        var fallback = defaultOperator ?? RegistryDefaultOperator;
        if (!IsSupportedOperator(det.Operator, fallback))
            return (DetectionEvaluation.Error, $"The operator '{det.Operator}' is not supported.");

        var op = string.IsNullOrWhiteSpace(det.Operator) ? fallback : det.Operator!.Trim();
        if (actual is null) return (DetectionEvaluation.NotDetected, "The value is empty.");
        // An existing empty value can satisfy NotEquals, LessThan and LessEquals, so it
        // counts as absent only for the operators it can never satisfy.
        if (actual.Length == 0 && !ComparesEmptyValue(op)) return (DetectionEvaluation.NotDetected, "The value is empty.");
        if (string.IsNullOrEmpty(det.ExpectedValue)) return (DetectionEvaluation.Detected, "The value exists.");

        var expected = det.ExpectedValue!;

        // Prefix, suffix and substring are string comparisons whatever the property type
        // says; there is no version reading of "starts with".
        switch (op.ToLowerInvariant())
        {
            case "beginswith":
                return Outcome(actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase));
            case "endswith":
                return Outcome(actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase));
            case "contains":
                return Outcome(actual.Contains(expected, StringComparison.OrdinalIgnoreCase));
        }

        var type = det.PropertyType?.Trim();
        if (string.Equals(type, "Version", StringComparison.OrdinalIgnoreCase))
        {
            // A version rule that cannot read either side as a version has not been
            // evaluated. Reporting "not detected" would hide a malformed rule.
            if (!Version.TryParse(actual, out var actualVersion) ||
                !Version.TryParse(expected, out var expectedVersion))
                return (DetectionEvaluation.Error, $"'{actual}' or '{expected}' is not a version.");

            return Ordered(PadVersion(actualVersion).CompareTo(PadVersion(expectedVersion)), op);
        }

        if (type is not null && IntegerTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            if (!Int128.TryParse(actual.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualNumber) ||
                !Int128.TryParse(expected.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedNumber))
                return (DetectionEvaluation.Error, $"'{actual}' or '{expected}' is not an integer.");

            return Ordered(actualNumber.CompareTo(expectedNumber), op);
        }

        if (!string.IsNullOrEmpty(type) && !StringTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return (DetectionEvaluation.Error, $"The property type '{type}' is not supported.");

        if (op.Equals("IsEquals", StringComparison.OrdinalIgnoreCase))
            return Outcome(string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
        if (op.Equals("NotEquals", StringComparison.OrdinalIgnoreCase))
            return Outcome(!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));

        return Ordered(string.Compare(actual, expected, StringComparison.OrdinalIgnoreCase), op);
    }

    private static bool ComparesEmptyValue(string op) =>
        op.Equals("NotEquals", StringComparison.OrdinalIgnoreCase)
        || op.Equals("LessThan", StringComparison.OrdinalIgnoreCase)
        || op.Equals("LessEquals", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="Version"/> ranks a missing component below zero, so 1.0 is less than
    /// 1.0.0; the deployment rule reads a missing component as zero.
    /// </summary>
    private static Version PadVersion(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static (DetectionEvaluation, string) Ordered(int comparison, string op) => op.ToLowerInvariant() switch
    {
        "isequals" => Outcome(comparison == 0),
        "notequals" => Outcome(comparison != 0),
        "greaterthan" => Outcome(comparison > 0),
        "greaterequals" => Outcome(comparison >= 0),
        "lessthan" => Outcome(comparison < 0),
        "lessequals" => Outcome(comparison <= 0),
        _ => (DetectionEvaluation.Error, $"The operator '{op}' is not supported.")
    };

    private static (DetectionEvaluation, string) Outcome(bool detected) =>
        detected ? (DetectionEvaluation.Detected, "match") : (DetectionEvaluation.NotDetected, "no match");

    /// <summary>
    /// Evaluates the rule against this machine. Script rules are evaluated only when a
    /// runner is supplied; this process does not launch one of its own.
    /// </summary>
    public static DetectionEvaluation Evaluate(StageDetection det, ScriptRunner? scriptRunner = null)
    {
        var context = new DetectionEvaluationContext
        {
            RunScript = scriptRunner is null
                ? null
                : text =>
                {
                    var (exitCode, stdout) = scriptRunner(text);
                    return new DetectionScriptRun(exitCode, stdout, "", false);
                }
        };
        try
        {
            return Trace(det, context).Verdict;
        }
        catch
        {
            return DetectionEvaluation.Error;
        }
    }

    /// <summary>
    /// Evaluates the rule with the context's registry and script runner and returns the
    /// verdict of every clause. Only cancellation from the script runner propagates; any
    /// other failure is the Error verdict of the clause that met it.
    /// </summary>
    public static DetectionTrace Trace(StageDetection det, DetectionEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(det);
        ArgumentNullException.ThrowIfNull(context);
        return TraceClause(det, context, 0);
    }

    private static DetectionTrace TraceClause(StageDetection det, DetectionEvaluationContext context, int depth)
    {
        try
        {
            return det.Type switch
            {
                "RegistryKeyValue" => TraceRegistryKeyValue(det, context),
                "RegistryKey" => TraceRegistryKey(det, context),
                "File" => TraceFile(det),
                "Script" => TraceScript(det, context),
                "Compound" => TraceCompound(det, context, depth),
                // An unimplemented rule type has not been evaluated, so it cannot answer
                // either question the runner asks of it.
                _ => Leaf(det, DetectionEvaluation.Error, $"The clause type '{det.Type}' is not supported.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Leaf(det, DetectionEvaluation.Error, FailureDetail(ex));
        }
    }

    /// <summary>
    /// Fixed text for a failure. An exception's own message can carry paths, machine
    /// names or account names, so only a refusal written for the user is shown as it is.
    /// </summary>
    private static string FailureDetail(Exception ex) => ex switch
    {
        DetectionScriptRefusedException => ex.Message,
        UnauthorizedAccessException or System.Security.SecurityException => "The clause could not be evaluated: access was denied.",
        _ => $"The clause could not be evaluated ({ex.GetType().Name})."
    };

    private static DetectionTrace Leaf(StageDetection det, DetectionEvaluation verdict, string detail,
        DetectionScriptRun? script = null) => new(det, verdict, detail, [], script);

    private static RegistryHive HiveOf(StageDetection det) =>
        det.IsCurrentUserHive ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private static RegistryView ViewOf(StageDetection det) =>
        det.Is64Bit ? RegistryView.Registry64 : RegistryView.Registry32;

    private static string HivePrefix(StageDetection det) => det.IsCurrentUserHive ? "HKCU" : "HKLM";

    private static string ViewText(StageDetection det) => det.Is64Bit ? "64-bit view" : "32-bit view";

    private static readonly (string Prefix, RegistryHive Hive)[] HivePrefixes =
    [
        ("HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine), ("HKLM", RegistryHive.LocalMachine),
        ("HKEY_CURRENT_USER", RegistryHive.CurrentUser), ("HKCU", RegistryHive.CurrentUser),
        ("HKEY_CLASSES_ROOT", RegistryHive.ClassesRoot), ("HKCR", RegistryHive.ClassesRoot),
        ("HKEY_USERS", RegistryHive.Users), ("HKU", RegistryHive.Users),
        ("HKEY_CURRENT_CONFIG", RegistryHive.CurrentConfig), ("HKCC", RegistryHive.CurrentConfig)
    ];

    /// <summary>
    /// The key path relative to the clause's hive. A leading separator makes the open
    /// fail, and a pasted hive prefix is read as a subkey name; both would report the key
    /// as absent, which a negative test then counts as a pass. A prefix naming another
    /// hive than the clause's is an error for the same reason.
    /// </summary>
    private static (string? Path, string? Error) RelativeKeyPath(StageDetection det)
    {
        var parts = (det.RegistryKeyRelative ?? "").Trim()
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0)
        {
            var first = parts[0];
            if (first.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase))
                first = first["Registry::".Length..];
            first = first.TrimEnd(':');
            var named = HivePrefixes.Where(p => p.Prefix.Equals(first, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0)
            {
                if (named[0].Hive != HiveOf(det))
                    return (null, $"The key path '{det.RegistryKeyRelative}' names another hive than the clause's {HivePrefix(det)}.");
                parts.RemoveAt(0);
            }
        }
        return parts.Count == 0 ? (null, "The clause has no registry key.") : (string.Join('\\', parts), null);
    }

    private static DetectionTrace TraceRegistryKeyValue(StageDetection det, DetectionEvaluationContext context)
    {
        var (path, pathError) = RelativeKeyPath(det);
        if (path is null) return Leaf(det, DetectionEvaluation.Error, pathError!);
        // Refused before the key is opened, so a missing key cannot mask a bad rule.
        if (!IsSupportedOperator(det.Operator, RegistryDefaultOperator))
            return Leaf(det, DetectionEvaluation.Error, $"The operator '{det.Operator}' is not supported.");

        var target = $@"{HivePrefix(det)}\{path}";
        using var key = context.Registry.OpenKey(HiveOf(det), ViewOf(det), path);
        if (key is null) return Leaf(det, DetectionEvaluation.NotDetected, $"Key not found: {target} ({ViewText(det)}).");

        var name = det.ValueName ?? DefaultValueName;
        var raw = key.GetValue(name);
        if (raw is null) return Leaf(det, DetectionEvaluation.NotDetected, $"Value '{name}' not found in {target} ({ViewText(det)}).");

        string actual;
        switch (raw)
        {
            case string s: actual = s; break;
            // REG_DWORD and REG_QWORD are unsigned; the registry API returns them as signed.
            case int i: actual = unchecked((uint)i).ToString(CultureInfo.InvariantCulture); break;
            case long l: actual = unchecked((ulong)l).ToString(CultureInfo.InvariantCulture); break;
            default:
                if (string.IsNullOrEmpty(det.ExpectedValue))
                    return Leaf(det, DetectionEvaluation.Detected, $"Value '{name}' exists in {target}.");
                return Leaf(det, DetectionEvaluation.Error,
                    $"The value type {key.GetValueKind(name)} of '{name}' is not supported for a comparison.");
        }

        var (verdict, reason) = Compare(actual, det, RegistryDefaultOperator);
        var op = string.IsNullOrWhiteSpace(det.Operator) ? RegistryDefaultOperator : det.Operator!.Trim();
        return Leaf(det, verdict, string.IsNullOrEmpty(det.ExpectedValue)
            ? $"Read '{actual}' from {target} [{name}]: {reason}"
            : $"Read '{actual}' from {target} [{name}]; {op} '{det.ExpectedValue}': {reason}");
    }

    private static DetectionTrace TraceRegistryKey(StageDetection det, DetectionEvaluationContext context)
    {
        var (path, pathError) = RelativeKeyPath(det);
        if (path is null) return Leaf(det, DetectionEvaluation.Error, pathError!);

        var target = $@"{HivePrefix(det)}\{path}";
        using var key = context.Registry.OpenKey(HiveOf(det), ViewOf(det), path);
        return key is null
            ? Leaf(det, DetectionEvaluation.NotDetected, $"Key not found: {target} ({ViewText(det)}).")
            : Leaf(det, DetectionEvaluation.Detected, $"Key exists: {target} ({ViewText(det)}).");
    }

    [GeneratedRegex(@"\s*\(.*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLabel();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"%(ProgramFiles|CommonProgramFiles)%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgramFilesVariable();

    /// <summary>
    /// The path a file rule probes. A 32-bit rule on a 64-bit Windows is evaluated as a
    /// 32-bit process would see it: the Windows system folder is redirected to SysWOW64 and
    /// %ProgramFiles% and %CommonProgramFiles% expand to their (x86) folders. This 64-bit
    /// process sees neither, so the path is rewritten instead.
    /// </summary>
    public static string ResolveFilePath(StageDetection det)
    {
        var raw = det.FilePath ?? "";
        var redirect = !det.Is64Bit && Environment.Is64BitOperatingSystem;
        if (redirect) raw = ProgramFilesVariable().Replace(raw, "%$1(x86)%");
        var folder = Environment.ExpandEnvironmentVariables(raw);
        if (redirect) folder = RedirectSystemFolder(folder);
        return Path.Combine(folder, det.FileName ?? "");
    }

    /// <summary>
    /// Only the real system folder is redirected; a folder named System32 elsewhere, such
    /// as under an application's own folder, is not.
    /// </summary>
    private static string RedirectSystemFolder(string folder)
    {
        var system = Path.TrimEndingDirectorySeparator(Environment.SystemDirectory);
        var windows = Path.GetDirectoryName(system);
        if (string.IsNullOrEmpty(windows)) return folder;
        if (!folder.StartsWith(system, StringComparison.OrdinalIgnoreCase)) return folder;
        var rest = folder[system.Length..];
        if (rest.Length > 0 && rest[0] is not ('\\' or '/')) return folder;
        return Path.Combine(windows, "SysWOW64") + rest;
    }

    /// <summary>
    /// The version string of a file without the build label Windows binaries append, for
    /// example "10.0.26100.1 (WinBuild.160101.0800)", without spaces, and with the comma
    /// separators of the "1, 2, 3, 4" form read as dots.
    /// </summary>
    public static string NormalizeFileVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return Whitespace().Replace(TrailingLabel().Replace(raw.Trim(), ""), "").Replace(',', '.');
    }

    private static DetectionTrace TraceFile(StageDetection det)
    {
        if (string.IsNullOrEmpty(det.FilePath) || string.IsNullOrEmpty(det.FileName))
            return Leaf(det, DetectionEvaluation.Error, "The clause has no file path or file name.");
        // Path.Combine drops the folder when the second part is rooted.
        if (Path.IsPathRooted(det.FileName))
            return Leaf(det, DetectionEvaluation.Error, $"The file name '{det.FileName}' is a path; the folder belongs in the file path.");

        var property = det.PropertyType?.Trim();
        var isVersion = string.Equals(property, "Version", StringComparison.OrdinalIgnoreCase);
        if (!isVersion && !string.IsNullOrEmpty(property)
            && !string.Equals(property, "Existence", StringComparison.OrdinalIgnoreCase))
            return Leaf(det, DetectionEvaluation.Error, $"The file property '{property}' is not supported.");

        var comparesValue = isVersion && !string.IsNullOrEmpty(det.ExpectedValue);

        // A file rule that compares a version defaults to greater-or-equal, matching what
        // the deployment systems emit. Validated before the file is probed.
        if (comparesValue && !IsSupportedOperator(det.Operator, FileDefaultOperator))
            return Leaf(det, DetectionEvaluation.Error, $"The operator '{det.Operator}' is not supported.");

        var filePath = ResolveFilePath(det);

        var (presence, presenceDetail) = FilePresence(filePath);
        if (presence != DetectionEvaluation.Detected) return Leaf(det, presence, presenceDetail);

        if (!comparesValue) return Leaf(det, DetectionEvaluation.Detected, $"File exists: {filePath}");

        // A version rule on a present file is a version comparison, not a presence check.
        var fileVersion = NormalizeFileVersion(FileVersionInfo.GetVersionInfo(filePath).FileVersion);
        if (fileVersion.Length == 0)
            return Leaf(det, DetectionEvaluation.Error, $"File exists but has no version information: {filePath}");

        var (verdict, reason) = Compare(fileVersion, det, FileDefaultOperator);
        var op = string.IsNullOrWhiteSpace(det.Operator) ? FileDefaultOperator : det.Operator!.Trim();
        return Leaf(det, verdict, $"File version '{fileVersion}' of {filePath}; {op} '{det.ExpectedValue}': {reason}");
    }

    /// <summary>
    /// Detected when the file is there, NotDetected when it provably is not, Error when
    /// the answer could not be obtained. File.Exists reports false for a path it cannot
    /// read, which is not the same as the file being absent; the attribute read separates
    /// those, and a directory carrying the file's name is not the file.
    /// </summary>
    private static (DetectionEvaluation, string) FilePresence(string filePath)
    {
        try
        {
            var attributes = File.GetAttributes(filePath);
            return attributes.HasFlag(FileAttributes.Directory)
                ? (DetectionEvaluation.NotDetected, $"A folder, not a file, is at {filePath}")
                : (DetectionEvaluation.Detected, $"File exists: {filePath}");
        }
        catch (FileNotFoundException) { return (DetectionEvaluation.NotDetected, $"File not found: {filePath}"); }
        catch (DirectoryNotFoundException) { return (DetectionEvaluation.NotDetected, $"File not found: {filePath}"); }
        catch (UnauthorizedAccessException) { return (DetectionEvaluation.Error, $"File could not be read: {filePath}: access was denied."); }
        catch (Exception ex) { return (DetectionEvaluation.Error, $"File could not be read: {filePath} ({ex.GetType().Name})."); }
    }

    private static DetectionTrace TraceScript(StageDetection det, DetectionEvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(det.ScriptText))
            return Leaf(det, DetectionEvaluation.Error, "The script is empty.");

        // Only PowerShell bodies are understood; running another language as PowerShell
        // would produce a verdict from a script that never ran as written.
        if (!string.IsNullOrWhiteSpace(det.ScriptLanguage)
            && !det.ScriptLanguage!.Trim().Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
            return Leaf(det, DetectionEvaluation.Error, $"The script language '{det.ScriptLanguage}' is not supported.");

        // This process launches nothing of its own. A caller that can run a script safely
        // supplies the runner; without one the rule is simply not evaluated here.
        if (context.RunScript is null)
            return Leaf(det, DetectionEvaluation.Error, "No script runner is available.");

        var run = context.RunScript(det.ScriptText!);
        if (run.TimedOut)
            return Leaf(det, DetectionEvaluation.Error, "The script timed out; its process tree was ended.", run);

        var hasOutput = !string.IsNullOrWhiteSpace(run.StdOut);
        var exit = run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

        if (context.ScriptRule == ScriptVerdictRule.ExitCodeAndOutput)
        {
            // A shipped detection body reports absence by exiting 0 with no output, so a
            // nonzero exit is the script having failed, not the application being absent.
            if (run.ExitCode != 0)
                return Leaf(det, DetectionEvaluation.Error, $"The script ended with exit code {exit}.", run);
            return hasOutput
                ? Leaf(det, DetectionEvaluation.Detected, "The script printed output: detected.", run)
                : Leaf(det, DetectionEvaluation.NotDetected, "The script printed no output: not detected.", run);
        }

        if (!hasOutput)
            return Leaf(det, DetectionEvaluation.NotDetected,
                $"The script printed no output: not detected (exit code {exit}).", run);
        if (run.ExitCode == 0)
            return Leaf(det, DetectionEvaluation.Detected, "The script printed output: detected (exit code 0).", run);
        return Leaf(det, DetectionEvaluation.Detected,
            $"The script printed output: detected. Warning: the script ended with exit code {exit}. " +
            "ConfigMgr treats a non-zero exit code as a failed detection, not as detected.", run);
    }

    private static DetectionTrace TraceCompound(StageDetection det, DetectionEvaluationContext context, int depth)
    {
        if (depth >= MaxCompoundDepth)
            return Leaf(det, DetectionEvaluation.Error, $"The compound is nested deeper than {MaxCompoundDepth} levels.");

        var clauses = det.Clauses ?? [];
        if (clauses.Length == 0)
            return Leaf(det, DetectionEvaluation.Error, "The compound has no clauses.");

        List<List<int>> groups;
        string shape;
        if (det.GroupSizes is { Length: > 0 } sizes)
        {
            if (sizes.Length != 2 || sizes.Any(s => s <= 0) || sizes.Sum() != clauses.Length)
                return Leaf(det, DetectionEvaluation.Error,
                    $"The group sizes '{string.Join(",", sizes)}' do not form two runs of the {clauses.Length} clauses.");
            groups = [Enumerable.Range(0, sizes[0]).ToList(), Enumerable.Range(sizes[0], sizes[1]).ToList()];
            shape = $"({sizes[0]} And) Or ({sizes[1]} And)";
        }
        else if (string.IsNullOrWhiteSpace(det.Connector) || det.Connector.Trim().Equals("And", StringComparison.OrdinalIgnoreCase))
        {
            groups = [Enumerable.Range(0, clauses.Length).ToList()];
            shape = "And";
        }
        else if (det.Connector.Trim().Equals("Or", StringComparison.OrdinalIgnoreCase))
        {
            groups = Enumerable.Range(0, clauses.Length).Select(i => new List<int> { i }).ToList();
            shape = "Or";
        }
        else
        {
            return Leaf(det, DetectionEvaluation.Error, $"The connector '{det.Connector}' is not supported.");
        }

        var children = clauses.Select(c => TraceClause(c, context, depth + 1)).ToList();
        var verdict = Or(groups.Select(g => And(g.Select(i => children[i].Verdict))));
        var detected = children.Count(c => c.Verdict == DetectionEvaluation.Detected);
        var errors = children.Count(c => c.Verdict == DetectionEvaluation.Error);
        var detail = $"{shape}: {detected} of {children.Count} clauses detected" +
                     (errors > 0 ? $", {errors} could not be evaluated" : "") + ".";
        return new DetectionTrace(det, verdict, detail, children);
    }

    private static DetectionEvaluation And(IEnumerable<DetectionEvaluation> verdicts)
    {
        var list = verdicts.ToList();
        if (list.Contains(DetectionEvaluation.NotDetected)) return DetectionEvaluation.NotDetected;
        return list.Contains(DetectionEvaluation.Error) ? DetectionEvaluation.Error : DetectionEvaluation.Detected;
    }

    private static DetectionEvaluation Or(IEnumerable<DetectionEvaluation> verdicts)
    {
        var list = verdicts.ToList();
        if (list.Contains(DetectionEvaluation.Detected)) return DetectionEvaluation.Detected;
        return list.Contains(DetectionEvaluation.Error) ? DetectionEvaluation.Error : DetectionEvaluation.NotDetected;
    }

    /// <summary>A one-line description of the clause. A script clause is described by its line count, never its text.</summary>
    public static string Describe(StageDetection det)
    {
        ArgumentNullException.ThrowIfNull(det);
        switch (det.Type)
        {
            case "RegistryKeyValue":
            {
                var name = det.ValueName ?? DefaultValueName;
                var target = $@"Registry value {HivePrefix(det)}\{det.RegistryKeyRelative} [{name}]";
                var op = string.IsNullOrWhiteSpace(det.Operator) ? RegistryDefaultOperator : det.Operator!.Trim();
                return string.IsNullOrEmpty(det.ExpectedValue)
                    ? $"{target} exists ({ViewText(det)})"
                    : $"{target} {op} '{det.ExpectedValue}' ({ViewText(det)})";
            }
            case "RegistryKey":
                return $@"Registry key {HivePrefix(det)}\{det.RegistryKeyRelative} ({ViewText(det)})";
            case "File":
            {
                var path = Path.Combine(det.FilePath ?? "", det.FileName ?? "");
                var view = det.Is64Bit ? "" : $" ({ViewText(det)})";
                if (string.Equals(det.PropertyType?.Trim(), "Version", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(det.ExpectedValue))
                {
                    var op = string.IsNullOrWhiteSpace(det.Operator) ? FileDefaultOperator : det.Operator!.Trim();
                    return $"File {path} Version {op} '{det.ExpectedValue}'{view}";
                }
                return $"File {path} exists{view}";
            }
            case "Script":
            {
                var lines = string.IsNullOrEmpty(det.ScriptText) ? 0 : det.ScriptText.Split('\n').Length;
                var language = string.IsNullOrWhiteSpace(det.ScriptLanguage) ? "PowerShell" : det.ScriptLanguage.Trim();
                return $"Script ({language}, {lines} line{(lines == 1 ? "" : "s")})";
            }
            case "Compound":
            {
                var count = det.Clauses?.Length ?? 0;
                var shape = det.GroupSizes is [var a, var b]
                    ? $"({a} And) Or ({b} And)"
                    : string.IsNullOrWhiteSpace(det.Connector) ? "And" : det.Connector.Trim();
                return $"Compound {shape} of {count} clauses";
            }
            default:
                return $"Unsupported clause type '{det.Type}'";
        }
    }
}
