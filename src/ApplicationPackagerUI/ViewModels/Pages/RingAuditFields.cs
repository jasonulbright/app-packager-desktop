using System.Globalization;
using System.Reflection;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The ring fields of an audit record: plan name, ring index, ring name and run ID; empty for a record of a single deployment.</summary>
public static class RingAuditFields
{
    public const string PlanName = "PlanName";
    public const string RingIndex = "RingIndex";
    public const string RingName = "RingName";
    public const string RunId = "RunId";

    // A record type that names a ring field carries it as a member; a line of the suite tool, or a type without the member, keeps it in Extra.
    private static readonly Dictionary<string, PropertyInfo?> Members = new[] { PlanName, RingIndex, RingName, RunId }
        .ToDictionary(n => n, n => typeof(DeploymentAuditRecord).GetProperty(n, BindingFlags.Public | BindingFlags.Instance), StringComparer.Ordinal);

    public static string Get(DeploymentAuditRecord record, string name)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Members.TryGetValue(name, out var member) && member?.GetValue(record) is { } value
            && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text)
            return text;
        foreach (var pair in record.Extra)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return "";
    }
}
