using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The requirement templates that the Application Settings grid shows as columns: the free disk space, the memory and the minimum build
/// of the operating system. A value is one requirement operation of the Workbench profile (Requirements.Operations) whose RuleId is the
/// condition id. An empty value removes the operation; a value adds it (Op Add, applies to every deployment type) or changes the Value of
/// the operation that is already there, so an AppliesTo list or an Op that a person set in the Workbench stays. Every other operation of the
/// profile is left as it is. The cell paths start with <see cref="Prefix"/>; the rest of the path is the condition id.
/// </summary>
public static class GridRequirementFields
{
    public const string Prefix = "Requirements.";
    public const string DiskSpace = Prefix + ConditionTemplateService.MinimumFreeDiskSpaceId;
    public const string Memory = Prefix + ConditionTemplateService.MinimumMemoryId;
    public const string OsBuild = Prefix + ConditionTemplateService.MinimumOsBuildId;

    public static readonly IReadOnlyList<string> Paths = [DiskSpace, Memory, OsBuild];

    /// <summary>The lowest build that a minimum can name: Windows 10 1507. A smaller number is a version name such as 1909, not a build.</summary>
    public const int LowestBuild = 10240;

    /// <summary>The highest build that a minimum can name (five digits).</summary>
    public const int HighestBuild = 99999;

    public static bool Handles(string path) => Paths.Contains(path);

    /// <summary>The condition id (and RuleId) of a cell path.</summary>
    public static string ConditionId(string path) => path[Prefix.Length..];

    /// <summary>Empty, or a whole number in the range of the setting: MB up to the largest 32-bit number (the Intune setting takes no more), a build from the first Windows 10 build.</summary>
    public static bool IsValid(string path, string value)
    {
        if (value.Length == 0) return true;
        if (!value.All(char.IsAsciiDigit) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
        return path == OsBuild ? number is >= LowestBuild and <= HighestBuild : number >= 1;
    }

    /// <summary>True for an operation that gives the rule: it has the RuleId and does not remove a rule.</summary>
    private static bool Gives(JsonNode? operation, string ruleId) =>
        operation is not null
        && string.Equals(WorkbenchJson.Get(operation, "RuleId").Text, ruleId, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(WorkbenchJson.Get(operation, "Op").Text, "Remove", StringComparison.OrdinalIgnoreCase);

    /// <summary>The operation that the cell shows: the first one that gives the rule; null when there is none.</summary>
    private static JsonNode? Find(JsonNode? operations, string ruleId) =>
        (operations is JsonArray array ? array.AsEnumerable() : operations is null ? [] : [operations]).FirstOrDefault(o => Gives(o, ruleId));

    /// <summary>The Value of the operation of a cell, as text; empty when the profile has none.</summary>
    public static string Text(JsonNode? profileData, string path)
    {
        var value = WorkbenchJson.Get(WorkbenchJson.Get(Find(WorkbenchJson.Get(profileData, "Requirements.Operations").Value, ConditionId(path)), "Rule").Value, "Value");
        return value.Found && value.Value is not null ? value.Text ?? "" : "";
    }

    /// <summary>Sets or removes the operation of a cell on a profile document.</summary>
    public static void Apply(WorkbenchProfile profile, string path, string value)
    {
        var ruleId = ConditionId(path);
        if (profile.Data["Requirements"] is not JsonObject requirements)
            profile.Data["Requirements"] = requirements = WorkbenchJson.NewObject();
        if (requirements["Operations"] is not JsonArray operations)
            requirements["Operations"] = operations = new JsonArray();

        if (value.Length == 0)
        {
            // Every operation that gives the rule goes; an operation that removes a rule of another level stays.
            for (var i = operations.Count - 1; i >= 0; i--)
                if (Gives(operations[i], ruleId)) operations.RemoveAt(i);
            return;
        }

        var number = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        if (Find(operations, ruleId) is JsonObject existing)
        {
            var rule = existing["Rule"] as JsonObject ?? WorkbenchJson.NewObject();
            rule["ConditionId"] = ruleId;
            rule["Value"] = number;
            existing["Rule"] = rule;
            return;
        }

        var added = WorkbenchJson.NewObject();
        added["Op"] = "Add";
        added["RuleId"] = ruleId;
        var newRule = WorkbenchJson.NewObject();
        newRule["ConditionId"] = ruleId;
        newRule["Value"] = number;
        added["Rule"] = newRule;
        added["AppliesTo"] = new JsonArray("*");
        operations.Add(added);
    }
}
