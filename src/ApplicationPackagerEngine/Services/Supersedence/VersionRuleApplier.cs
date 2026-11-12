using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

public enum ClauseResult
{
    /// <summary>The operator was changed to IsEquals.</summary>
    ExactApplied,
    /// <summary>Exact was wanted and the guard blocked it; the clause is left as it is.</summary>
    ExactBlocked,
    /// <summary>The block is accepted for this title and version; the clause stays Minimum.</summary>
    ExactAcceptedBlock
}

/// <summary>What happened to one version clause.</summary>
public sealed record ClauseOutcome(string Clause, ClauseResult Result, string? Detail);

/// <summary>The verdict of the Exact guard for one clause.</summary>
public sealed record ExactGuardVerdict(ClauseResult Result, string? Detail)
{
    public static ExactGuardVerdict Allowed { get; } = new(ClauseResult.ExactApplied, null);
}

/// <summary>
/// Writes the version rule into a detection. Only a version clause of type File or Registry value whose operator is GreaterEquals and
/// whose expected value is the packaged version changes, and only to IsEquals when the rule is Exact and the guard allows it.
/// The definition is not changed; the result is a copy.
/// </summary>
public static class VersionRuleApplier
{
    public const string ExactOperator = "IsEquals";
    public const string MinimumOperator = "GreaterEquals";

    /// <summary>The words of a clause for a log line or a message.</summary>
    public static string Describe(StageDetection clause) => clause.Type.Equals("File", StringComparison.OrdinalIgnoreCase)
        ? $"file {clause.FilePath}\\{clause.FileName}"
        : $"registry {clause.RegistryKeyRelative}\\{clause.ValueName}";

    /// <summary>True for the clause that rule 2 of the specification may change.</summary>
    public static bool IsVersionClause(StageDetection clause, string packagedVersion)
    {
        if (string.IsNullOrWhiteSpace(packagedVersion)) return false;
        if (!string.Equals(clause.ExpectedValue?.Trim(), packagedVersion.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (clause.Type.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            var propertyType = string.IsNullOrWhiteSpace(clause.PropertyType) ? "Version" : clause.PropertyType;
            var op = string.IsNullOrWhiteSpace(clause.Operator) ? MinimumOperator : clause.Operator;
            return propertyType.Equals("Version", StringComparison.OrdinalIgnoreCase)
                && op.Equals(MinimumOperator, StringComparison.OrdinalIgnoreCase);
        }
        if (clause.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase))
        {
            var isVersion = string.Equals(clause.PropertyType, "Version", StringComparison.OrdinalIgnoreCase)
                || (clause.ValueName ?? "").Contains("Version", StringComparison.OrdinalIgnoreCase);
            return isVersion && string.Equals(clause.Operator, MinimumOperator, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// A copy of <paramref name="detection"/> with the rule written. With the rule Minimum the copy equals the original and there
    /// are no outcomes. The guard is asked once for each clause that would change.
    /// </summary>
    public static (StageDetection Detection, IReadOnlyList<ClauseOutcome> Outcomes) Apply(
        StageDetection detection, string packagedVersion, VersionRule rule, Func<StageDetection, ExactGuardVerdict>? guard)
    {
        var copy = Clone(detection);
        var outcomes = new List<ClauseOutcome>();
        if (rule == VersionRule.Exact) Walk(copy, packagedVersion, guard, outcomes);
        return (copy, outcomes);
    }

    /// <summary>The manifest with the rule written into its detection and into the detection of each deployment type.</summary>
    public static (StageManifest Manifest, IReadOnlyList<ClauseOutcome> Outcomes) ApplyToManifest(
        StageManifest manifest, VersionRule rule, Func<StageDetection, ExactGuardVerdict>? guard)
    {
        var copy = JsonSerializer.Deserialize<StageManifest>(JsonSerializer.Serialize(manifest))!;
        var outcomes = new List<ClauseOutcome>();
        if (rule == VersionRule.Exact)
        {
            Walk(copy.Detection, manifest.SoftwareVersion, guard, outcomes);
            foreach (var type in copy.DeploymentTypes ?? [])
                if (type.Detection is { } own) Walk(own, manifest.SoftwareVersion, guard, outcomes);
        }
        return (copy, outcomes);
    }

    private static void Walk(StageDetection detection, string version, Func<StageDetection, ExactGuardVerdict>? guard, List<ClauseOutcome> outcomes)
    {
        if (detection.Type.Equals("Compound", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var clause in detection.Clauses ?? []) Walk(clause, version, guard, outcomes);
            return;
        }
        if (!IsVersionClause(detection, version)) return;
        var verdict = guard?.Invoke(detection)
            ?? new ExactGuardVerdict(ClauseResult.ExactBlocked, "No installed value is recorded for this version.");
        if (verdict.Result == ClauseResult.ExactApplied) detection.Operator = ExactOperator;
        outcomes.Add(new ClauseOutcome(Describe(detection), verdict.Result, verdict.Detail));
    }

    private static StageDetection Clone(StageDetection detection) =>
        JsonSerializer.Deserialize<StageDetection>(JsonSerializer.Serialize(detection))!;
}
