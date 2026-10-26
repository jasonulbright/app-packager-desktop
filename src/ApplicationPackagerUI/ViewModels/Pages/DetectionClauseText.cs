using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One line of the clause tree: a connector with its clauses, or one clause.</summary>
public sealed record DetectionClauseNode(string Text, IReadOnlyList<DetectionClauseNode> Children);

/// <summary>Plain text for detection clauses, and the key path forms an operator pastes.</summary>
public static class DetectionClauseText
{
    public const string Hklm = "HKLM";
    public const string Hkcu = "HKCU";

    private static readonly (string Prefix, string Hive)[] HivePrefixes =
    [
        (@"Registry::HKEY_LOCAL_MACHINE\", Hklm), (@"Registry::HKEY_CURRENT_USER\", Hkcu),
        (@"HKEY_LOCAL_MACHINE\", Hklm), (@"HKEY_CURRENT_USER\", Hkcu),
        (@"HKLM:\", Hklm), (@"HKCU:\", Hkcu),
        (@"HKLM\", Hklm), (@"HKCU\", Hkcu)
    ];

    private const string Wow6432Node = @"SOFTWARE\WOW6432Node\";

    public static string HiveName(StageDetection detection) => detection.IsCurrentUserHive ? Hkcu : Hklm;

    public static string ViewName(bool is64Bit) => is64Bit ? "64-bit" : "32-bit";

    /// <summary>The hive named at the start of a pasted key path, and the path below it; a relative path has no hive.</summary>
    public static (string? Hive, string Relative) SplitKeyPath(string? path)
    {
        var text = (path ?? "").Trim();
        foreach (var (prefix, hive) in HivePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (hive, text[prefix.Length..].TrimStart('\\'));
        }
        return (null, text.TrimStart('\\'));
    }

    /// <summary>
    /// The hive, view and relative key of an Add/Remove Programs entry. The 32-bit view reads
    /// WOW6432Node through registry redirection, so a 32-bit key is stated without that segment,
    /// as ConfigMgr states it.
    /// </summary>
    public static (string Hive, bool Is64Bit, string Relative) UninstallKey(InstalledApplication application)
    {
        var (hive, relative) = SplitKeyPath(application.KeyPath);
        var isUser = string.Equals(hive, Hkcu, StringComparison.Ordinal)
            || application.Source.StartsWith(Hkcu, StringComparison.OrdinalIgnoreCase);
        var is32 = !isUser && (application.Source.Contains("32-bit", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(Wow6432Node, StringComparison.OrdinalIgnoreCase));
        if (relative.StartsWith(Wow6432Node, StringComparison.OrdinalIgnoreCase))
            relative = relative[..@"SOFTWARE\".Length] + relative[Wow6432Node.Length..];
        return (isUser ? Hkcu : Hklm, !is32, relative);
    }

    public static string Describe(StageDetection detection) => detection.Type switch
    {
        "RegistryKeyValue" => $@"{HiveName(detection)}\{detection.RegistryKeyRelative} [{detection.ValueName}] {Operator(detection.Operator, DetectionEvaluator.RegistryDefaultOperator)} '{detection.ExpectedValue}' ({ViewName(detection.Is64Bit)} view)",
        "RegistryKey" => $@"{HiveName(detection)}\{detection.RegistryKeyRelative} exists ({ViewName(detection.Is64Bit)} view)",
        "File" when IsVersion(detection.PropertyType) =>
            $@"{detection.FilePath}\{detection.FileName} version {Operator(detection.Operator, DetectionEvaluator.FileDefaultOperator)} {detection.ExpectedValue}",
        "File" => $@"{detection.FilePath}\{detection.FileName} exists",
        "Script" => $"Script ({LineCount(detection.ScriptText)} lines)",
        "Compound" => $"Compound: {detection.Clauses?.Length ?? 0} clauses ({Connector(detection)})",
        _ => detection.Type
    };

    /// <summary>
    /// The clause tree of a detection. Two group sizes make (first run AND) OR (second run AND);
    /// otherwise the connector joins every clause.
    /// </summary>
    public static DetectionClauseNode Tree(StageDetection detection)
    {
        if (detection.Type != "Compound") return new DetectionClauseNode(Describe(detection), []);
        var clauses = detection.Clauses ?? [];
        if (detection.GroupSizes is [var first, var second] && first > 0 && second > 0 && first + second == clauses.Length)
        {
            return new DetectionClauseNode("Any of (Or)",
            [
                new DetectionClauseNode("All of (And)", clauses.Take(first).Select(Tree).ToList()),
                new DetectionClauseNode("All of (And)", clauses.Skip(first).Select(Tree).ToList())
            ]);
        }
        var text = Connector(detection) == "Or" ? "Any of (Or)" : "All of (And)";
        return new DetectionClauseNode(text, clauses.Select(Tree).ToList());
    }

    public static bool ContainsScript(StageDetection detection) =>
        detection.Type == "Script" || (detection.Clauses ?? []).Any(ContainsScript);

    public static int ClauseCount(StageDetection detection) =>
        detection.Type == "Compound" ? (detection.Clauses ?? []).Sum(ClauseCount) : 1;

    private static string Connector(StageDetection detection) =>
        string.Equals(detection.Connector, "Or", StringComparison.OrdinalIgnoreCase) ? "Or" : "And";

    private static bool IsVersion(string? propertyType) => string.Equals(propertyType, "Version", StringComparison.OrdinalIgnoreCase);

    private static string Operator(string? op, string fallback) => string.IsNullOrWhiteSpace(op) ? fallback : op.Trim();

    private static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
}
