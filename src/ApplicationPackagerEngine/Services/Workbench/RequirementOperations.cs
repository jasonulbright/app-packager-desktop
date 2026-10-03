using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>Applies a profile's Add, Replace and Remove requirement operations, keyed by RuleId.</summary>
public static class RequirementOperations
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// AppliesTo '*' (or no AppliesTo) reaches the manifest's own requirements and
    /// every deployment type entry. A named list reaches only the entries whose
    /// NameSuffix it names, so an app-wide rule never gates a deliberate
    /// unconditional fallback by accident. A manifest without deployment type
    /// entries always takes the operation on its own requirements.
    /// </summary>
    public static void Apply(StageManifest manifest, JsonNode? operations)
    {
        var entries = manifest.DeploymentTypes ?? [];
        foreach (var operation in WorkbenchProfileValidator.Items(operations))
        {
            if (operation is null) continue;
            var appliesTo = WorkbenchProfileValidator.Items(WorkbenchJson.Get(operation, "AppliesTo").Value)
                .Select(WorkbenchJson.AsText).Where(a => a is not null).ToList();
            if (appliesTo.Count == 0) appliesTo.Add("*");
            var all = appliesTo.Contains("*");

            if (all || entries.Length == 0)
                manifest.Requirements = ApplyOne(manifest.Requirements, operation);
            foreach (var entry in entries)
            {
                if (!all && !appliesTo.Contains(entry.NameSuffix, StringComparer.OrdinalIgnoreCase)) continue;
                entry.Requirements = ApplyOne(entry.Requirements, operation);
            }
        }
    }

    /// <summary>The identity an operation matches a rule on: RuleId, else ConditionId.</summary>
    public static string RuleIdOf(StageRequirement rule) =>
        string.IsNullOrWhiteSpace(rule.RuleId) ? rule.ConditionId : rule.RuleId!;

    private static StageRequirement[] ApplyOne(StageRequirement[]? rules, JsonNode operation)
    {
        var op = WorkbenchJson.Get(operation, "Op").Text ?? "";
        var ruleId = WorkbenchJson.Get(operation, "RuleId").Text ?? "";
        var current = (rules ?? []).Where(r => r is not null).ToList();
        var kept = current.Where(r => !string.Equals(RuleIdOf(r), ruleId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (string.Equals(op, "Remove", StringComparison.OrdinalIgnoreCase)) return [.. kept];

        var ruleNode = WorkbenchJson.Get(operation, "Rule").Value;
        var rule = ruleNode is JsonObject
            ? ruleNode.Deserialize<StageRequirement>(Options) ?? new StageRequirement()
            : new StageRequirement();
        rule.RuleId = ruleId;
        return [.. kept, rule];
    }
}
