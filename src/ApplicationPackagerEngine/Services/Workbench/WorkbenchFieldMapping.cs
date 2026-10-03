using System.Text;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>One clause row of the typed detection editor.</summary>
public sealed record WorkbenchDetectionClause(string Group, string Type, string Subject, string Operator, string Expected);

/// <summary>The typed detection editor's fields, as the combos and boxes show them.</summary>
public sealed record WorkbenchDetectionRule
{
    public string Type { get; init; } = "Registry";
    public string Hive { get; init; } = "HKLM";
    public string View { get; init; } = "64-bit";
    public string Key { get; init; } = "";
    public string ValueName { get; init; } = "";
    public string Operator { get; init; } = "Exists";
    public string ExpectedValue { get; init; } = "";
    public string VersionBinding { get; init; } = "Follows staged version";
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FileProperty { get; init; } = "Exists";
    public string Logic { get; init; } = "And";
    public string ScriptText { get; init; } = "";
    public IReadOnlyList<WorkbenchDetectionClause> Clauses { get; init; } = [];

    /// <summary>The stored Detection.Rule object: Script, 32/64, FollowsStaged/Pinned and TwoGroup tokens.</summary>
    public JsonObject ToJson()
    {
        var rule = WorkbenchJson.NewObject();
        rule["Type"] = Type == "PowerShell" ? "Script" : Type;
        rule["Hive"] = Hive;
        rule["View"] = View == "32-bit" ? "32" : "64";
        rule["Key"] = Key;
        rule["ValueName"] = ValueName;
        rule["Operator"] = Operator;
        rule["ExpectedValue"] = ExpectedValue;
        rule["VersionBinding"] = VersionBinding == "Pinned" ? "Pinned" : "FollowsStaged";
        rule["FilePath"] = FilePath;
        rule["FileName"] = FileName;
        rule["FileProperty"] = FileProperty;
        rule["Logic"] = Logic switch { "Or" => "Or", "Two groups" => "TwoGroup", _ => "And" };
        rule["ScriptText"] = ScriptText;
        var clauses = new JsonArray();
        foreach (var c in Clauses)
        {
            var clause = WorkbenchJson.NewObject();
            clause["Group"] = c.Group;
            clause["Type"] = c.Type;
            clause["Subject"] = c.Subject;
            clause["Operator"] = c.Operator;
            clause["Expected"] = c.Expected;
            clauses.Add(clause);
        }
        rule["Clauses"] = clauses;
        return rule;
    }

    /// <summary>Reads a stored rule; every absent member takes the editor default.</summary>
    public static WorkbenchDetectionRule FromJson(JsonNode? rule)
    {
        string Text(string name, string fallback)
        {
            var member = WorkbenchJson.Get(rule, name);
            return member.Found && member.Value is not null ? member.Text ?? fallback : fallback;
        }
        var clauses = WorkbenchProfileValidator.Items(WorkbenchJson.Get(rule, "Clauses").Value)
            .OfType<JsonObject>()
            .Select(c => new WorkbenchDetectionClause(
                WorkbenchJson.AsText(c["Group"]) ?? "", WorkbenchJson.AsText(c["Type"]) ?? "",
                WorkbenchJson.AsText(c["Subject"]) ?? "", WorkbenchJson.AsText(c["Operator"]) ?? "",
                WorkbenchJson.AsText(c["Expected"]) ?? ""))
            .ToList();
        return new WorkbenchDetectionRule
        {
            Type = WorkbenchReview.DetectionTypeDisplay(Text("Type", "Registry")),
            Hive = Text("Hive", "HKLM"),
            View = Text("View", "64") == "32" ? "32-bit" : "64-bit",
            Key = Text("Key", ""),
            ValueName = Text("ValueName", ""),
            Operator = Text("Operator", "Exists"),
            ExpectedValue = Text("ExpectedValue", ""),
            VersionBinding = Text("VersionBinding", "FollowsStaged") == "Pinned" ? "Pinned" : "Follows staged version",
            FilePath = Text("FilePath", ""),
            FileName = Text("FileName", ""),
            FileProperty = Text("FileProperty", "Exists"),
            Logic = Text("Logic", "And") switch { "TwoGroup" => "Two groups", "Or" => "Or", _ => "And" },
            ScriptText = Text("ScriptText", ""),
            Clauses = clauses
        };
    }
}

/// <summary>One row of the requirement operations grid.</summary>
public sealed record WorkbenchRequirementRow(string Op, string RuleId, string Summary, string Value, string AppliesTo)
{
    /// <summary>
    /// The stored operation. os-language takes comma-separated cultures; vpn-connected
    /// takes true or false; any other condition stores the value as text.
    /// </summary>
    public JsonObject ToJson()
    {
        var rule = WorkbenchJson.NewObject();
        rule["ConditionId"] = Summary;
        var value = Value.Trim();
        if (value.Length > 0)
        {
            if (string.Equals(Summary, "os-language", StringComparison.OrdinalIgnoreCase))
                rule["Cultures"] = new JsonArray(SplitList(value).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            else if (string.Equals(Summary, "vpn-connected", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out var flag))
                rule["Value"] = flag;
            else
                rule["Value"] = value;
        }
        var operation = WorkbenchJson.NewObject();
        operation["Op"] = Op;
        operation["RuleId"] = RuleId;
        operation["Rule"] = rule;
        operation["AppliesTo"] = new JsonArray(SplitList(AppliesTo).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return operation;
    }

    public static WorkbenchRequirementRow FromJson(JsonNode? operation)
    {
        var rule = WorkbenchJson.Get(operation, "Rule").Value;
        var summary = WorkbenchJson.Get(rule, "ConditionId").Text ?? "";
        var value = "";
        var plain = WorkbenchJson.Get(rule, "Value");
        if (plain.Found) value = plain.Text ?? "";
        else
            value = string.Join(", ", WorkbenchProfileValidator.Items(WorkbenchJson.Get(rule, "Cultures").Value)
                .Select(WorkbenchJson.AsText).Where(t => !string.IsNullOrEmpty(t)));
        var appliesTo = string.Join(", ", WorkbenchProfileValidator.Items(WorkbenchJson.Get(operation, "AppliesTo").Value)
            .Select(WorkbenchJson.AsText).Where(t => !string.IsNullOrEmpty(t)));
        return new WorkbenchRequirementRow(
            WorkbenchJson.Get(operation, "Op").Text ?? "", WorkbenchJson.Get(operation, "RuleId").Text ?? "",
            summary, value, appliesTo);
    }

    /// <summary>The grid rows for a stored Requirements.Operations value.</summary>
    public static IReadOnlyList<WorkbenchRequirementRow> ReadAll(JsonNode? operations) =>
        WorkbenchProfileValidator.Items(operations).Where(o => o is not null).Select(FromJson).ToList();

    /// <summary>A new Add operation for a condition template, applying to every deployment type.</summary>
    public static WorkbenchRequirementRow ForTemplate(string conditionId) => new("Add", conditionId, conditionId, "", "*");

    private static IEnumerable<string> SplitList(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0);
}

/// <summary>One row of the variant overrides grid, keyed by the variant suffix.</summary>
public sealed record WorkbenchVariantRow(string Suffix, string InstallCommand, string Detection, string Estimated, string Maximum);

/// <summary>Combo text to stored token conversions and editor text helpers.</summary>
public static class WorkbenchFieldMapping
{
    public static readonly IReadOnlyList<string> TitleModeOptions = ["Packager default", "Include version", "No version"];
    public static readonly IReadOnlyList<string> ScriptModeOptions = ["Generated", "Extend generated", "Custom"];
    public static readonly IReadOnlyList<string> RebootOptions = ["Inherit", "No reboot", "Soft reboot", "Hard reboot", "Force restart"];

    public static string TitleModeToDisplay(string? stored) => PackagerChildBridge.NormalizeTitleMode(stored) switch
    {
        "IncludeVersion" => "Include version",
        "NoVersion" => "No version",
        _ => "Packager default"
    };

    /// <summary>The stored token, or null for Packager default.</summary>
    public static string? DisplayToTitleMode(string? display) => display switch
    {
        "Include version" => "IncludeVersion",
        "No version" => "NoVersion",
        _ => null
    };

    public static string ScriptModeToDisplay(string? stored) => stored switch
    {
        "Extend" => "Extend generated",
        "Custom" => "Custom",
        _ => "Generated"
    };

    /// <summary>The stored token, or null for Generated, which is the inherited mode.</summary>
    public static string? DisplayToScriptMode(string? display) => display switch
    {
        "Extend generated" => "Extend",
        "Custom" => "Custom",
        _ => null
    };

    public static string ScriptNote(string? storedMode) => storedMode switch
    {
        "Extend" => "Extend generated runs the before hook, the generated wrapper, and the after hook as separate processes with explicit exit-code propagation, so an exit in one step cannot skip the rest.",
        "Custom" => "Custom replaces the generated wrapper. Changes to the packager's own wrapper at a later release no longer reach this profile and need review.",
        _ => "Generated keeps the packager's wrapper. Switch to Extend generated for hooks, or Custom to take ownership of the whole script."
    };

    /// <summary>Install for options: Packager default plus each mode the application declares.</summary>
    public static IReadOnlyList<string> InstallForOptions(IEnumerable<string>? supportedModes)
    {
        var modes = (supportedModes ?? []).ToList();
        var options = new List<string> { "Packager default" };
        if (modes.Contains("AllUsers", StringComparer.OrdinalIgnoreCase)) options.Add("System");
        if (modes.Contains("CurrentUser", StringComparer.OrdinalIgnoreCase)) options.Add("User");
        return options;
    }

    public static string InstallModeToDisplay(string? stored) => stored switch
    {
        "AllUsers" => "System",
        "CurrentUser" => "User",
        _ => "Packager default"
    };

    public static string? DisplayToInstallMode(string? display) => display switch
    {
        "System" => "AllUsers",
        "User" => "CurrentUser",
        _ => null
    };

    /// <summary>
    /// The split a stored Variants.Split names: the Split member of a split document, or
    /// the first entry of an array. None when there is no split.
    /// </summary>
    public static string VariantSplitToDisplay(JsonNode? stored)
    {
        string? split = stored switch
        {
            JsonObject document => WorkbenchJson.AsText(document["Split"]),
            JsonArray array => array.Count > 0 ? WorkbenchJson.AsText(array[0]) : null,
            _ => WorkbenchJson.AsText(stored)
        };
        return string.IsNullOrWhiteSpace(split) ? "None" : split;
    }

    /// <summary>The stored Variants.Split: a split document, or an empty array for None.</summary>
    public static JsonNode DisplayToVariantSplit(string? display)
    {
        if (string.IsNullOrWhiteSpace(display) || display == "None") return new JsonArray();
        var document = WorkbenchJson.NewObject();
        document["SchemaVersion"] = 1;
        document["Split"] = display;
        return document;
    }

    /// <summary>Gutter text for a script editor: one number per physical line, so it lines up with an unwrapped editor.</summary>
    public static string LineNumbers(string? text)
    {
        var count = Math.Max(1, (text ?? "").Split('\n').Length);
        var builder = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            if (i > 1) builder.Append(Environment.NewLine);
            builder.Append(i);
        }
        return builder.ToString();
    }
}
