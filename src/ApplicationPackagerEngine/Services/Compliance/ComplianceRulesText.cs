using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>The drift tag "[ccm:&lt;Id&gt;@&lt;Version&gt;]" in a baseline description.</summary>
public static partial class DriftTag
{
    [GeneratedRegex(@"\[ccm:([^@\]]+)@([^\]]+)\]")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s*\[ccm:[^\]]*\]")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}(\.[0-9]{1,9})?$")]
    private static partial Regex VersionRegex();

    /// <summary>The longest baseline description ConfigMgr stores.</summary>
    public const int MaxDescriptionLength = 512;

    public static string Text(string id, string version) => $"[ccm:{id}@{version}]";

    /// <summary>
    /// The first tag of <paramref name="description"/>; null when it has none. <c>Valid</c> is
    /// false for a tag whose ID or version does not have the catalog form.
    /// </summary>
    public static (string Id, string Version, bool Valid)? Parse(string? description)
    {
        var match = TagRegex().Match(description ?? "");
        if (!match.Success) return null;
        var id = match.Groups[1].Value;
        var version = match.Groups[2].Value;
        return (id, version, IdRegex().IsMatch(id) && VersionRegex().IsMatch(version));
    }

    /// <summary>
    /// The description with every existing tag removed and the tag of <paramref name="id"/> and
    /// <paramref name="version"/> appended once, so a second run never adds a second tag.
    /// </summary>
    public static string Apply(string? existing, string id, string version)
    {
        var tag = Text(id, version);
        var plain = AnyTagRegex().Replace(existing ?? "", "").TrimEnd();
        return plain.Length > 0 ? plain + " " + tag : tag;
    }

    /// <summary>Current, Behind, Untagged or Unknown for a baseline description against the catalog.</summary>
    public static string State(string? description, CatalogEntry? entry)
    {
        var tag = Parse(description);
        if (tag is null) return "Untagged";
        if (!tag.Value.Valid || entry is null) return "Unknown";
        var compared = CompareVersions(tag.Value.Version, entry.Version);
        return compared switch
        {
            0 => "Current",
            < 0 => "Behind",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Compares two catalog versions part by part; a missing fourth part counts as zero. A part
    /// that is not a whole number up to <see cref="long.MaxValue"/> counts as larger than any number.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        static long[] Parts(string v) => v.Split('.')
            .Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue)
            .Concat([0L, 0L, 0L, 0L]).Take(4).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < 4; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }
}

/// <summary>What ConfigMgr stores for a compliance setting, as verified on a site.</summary>
public static class SettingRules
{
    public static readonly IReadOnlyList<string> RegistryDataTypes = ["String", "Integer", "DateTime", "FloatingPoint", "Version", "StringArray"];

    public static readonly IReadOnlyList<string> ScriptDataTypes = ["String", "Integer", "DateTime", "FloatingPoint", "Version", "Boolean"];

    public static readonly IReadOnlyList<string> Hives = ["LocalMachine", "CurrentUser", "ClassesRoot", "CurrentConfig", "Users"];

    public static readonly IReadOnlyList<string> Languages = ["PowerShell", "VBScript", "JScript"];

    public static readonly IReadOnlyList<string> Severities = ["None", "Informational", "Warning", "Critical", "CriticalWithEvent"];

    public static readonly IReadOnlyList<string> Operators =
        ["IsEquals", "NotEquals", "GreaterThan", "LessThan", "GreaterEquals", "LessEquals", "BeginsWith", "NotBeginsWith", "EndsWith", "NotEndsWith", "Contains", "NotContains", "OneOf", "NoneOf", "AllOf"];

    private static readonly string[] TextOperators = ["BeginsWith", "NotBeginsWith", "EndsWith", "NotEndsWith", "Contains", "NotContains"];

    private static readonly string[] OrderOperators = ["GreaterThan", "LessThan", "GreaterEquals", "LessEquals"];

    public const int MaxNameLength = 255;

    public const int MaxScriptLength = 256 * 1024;

    /// <summary>The expected values of a setting: one per line for a string array or a list operator.</summary>
    public static IReadOnlyList<string> Values(CiSetting setting) =>
        IsList(setting)
            ? (setting.ExpectedValue ?? "").Split('\n').Select(v => v.TrimEnd('\r')).Where(v => v.Length > 0).ToList()
            : [setting.ExpectedValue ?? ""];

    private static bool IsList(CiSetting setting) => setting.DataType == "StringArray" || setting.Operator is "OneOf" or "NoneOf" or "AllOf";

    /// <summary>Null when ConfigMgr can store the setting as described; otherwise a plain reason.</summary>
    public static string? Check(CiSetting? s)
    {
        if (s is null) return "Enter the setting.";
        if (string.IsNullOrWhiteSpace(s.Name)) return "Enter a setting name.";
        if (s.Name.Length > MaxNameLength || s.Name.Trim() != s.Name || s.Name.Any(char.IsControl)) return "The setting name must be 1 to 255 characters without control characters or outer spaces.";
        if (string.IsNullOrWhiteSpace(s.RuleName) || s.RuleName.Length > MaxNameLength || s.RuleName.Any(char.IsControl)) return "Enter a rule name of 1 to 255 characters.";
        if (!Severities.Contains(s.Severity)) return "Select the noncompliance severity.";
        if (!Operators.Contains(s.Operator)) return "Select the rule operator.";
        switch (s.Kind)
        {
            case "Registry":
                if (s.DataType == "Boolean") return "A registry setting cannot have the data type Boolean. ConfigMgr offers Boolean for script settings only.";
                if (!RegistryDataTypes.Contains(s.DataType)) return "Select the data type of the registry value.";
                if (!Hives.Contains(s.Hive)) return "Select the registry hive.";
                if (string.IsNullOrWhiteSpace(s.KeyName) || s.KeyName.Any(char.IsControl) || s.KeyName.StartsWith('\\') || s.KeyName.Contains(@"\\")) return "Enter the registry key path without the hive, for example SOFTWARE\\Contoso.";
                if (s.ValueName.Any(char.IsControl)) return "The value name contains a control character.";
                break;
            case "Script":
                if (s.DataType == "StringArray") return "A script setting cannot have the data type StringArray. ConfigMgr offers it for registry settings only.";
                if (!ScriptDataTypes.Contains(s.DataType)) return "Select the data type the script returns.";
                if (!Languages.Contains(s.DiscoveryLanguage)) return "Select the language of the discovery script.";
                if (string.IsNullOrWhiteSpace(s.DiscoveryScript)) return "Enter the discovery script.";
                if (s.DiscoveryScript.Length > MaxScriptLength || s.RemediationScript.Length > MaxScriptLength) return "A script is longer than 256 KB.";
                if (s.RemediationScript.Length > 0 && !Languages.Contains(s.RemediationLanguage)) return "Select the language of the remediation script.";
                if (s.Remediate && s.RemediationScript.Length == 0) return "Remediation needs a remediation script.";
                break;
            default:
                return "Select Registry or Script.";
        }
        if (s.DataType == "DateTime" && s.Remediate)
            return "ConfigMgr does not store a DateTime setting with remediation: the cmdlet reports success and stores nothing. Turn remediation off.";
        if (s.DataType == "StringArray" && s.Operator != "AllOf")
            return "ConfigMgr stores the rule of a StringArray setting as All of. Select All of.";
        if (s.DataType == "Boolean" && s.Operator is not ("IsEquals" or "NotEquals")) return "A Boolean setting takes Equals or Not equals.";
        if (s.DataType is "Boolean" or "Version" or "DateTime" or "Integer" or "FloatingPoint" && TextOperators.Contains(s.Operator))
            return $"The operator {s.Operator} needs the data type String.";
        if (s.DataType == "String" && OrderOperators.Contains(s.Operator))
            return $"The operator {s.Operator} needs a number, a version or a date.";
        var values = Values(s);
        if (values.Count == 0 || values.Any(v => v.Any(c => char.IsControl(c)))) return "Enter the expected value.";
        if (!IsList(s) && values.Count != 1) return "Enter one expected value.";
        foreach (var v in values)
            if (ValueProblem(s.DataType, v) is { } problem) return problem;
        if (s.Description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return "The description contains a control character.";
        return null;
    }

    private static string? ValueProblem(string type, string value) => type switch
    {
        "Integer" when !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) => "Enter a whole number as the expected value.",
        "FloatingPoint" when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => "Enter a number such as 1.5 as the expected value.",
        "Version" when !System.Version.TryParse(value, out _) => "Enter a version such as 1.2.3.4 as the expected value.",
        "Boolean" when value is not ("True" or "False") => "Enter True or False as the expected value.",
        "DateTime" when !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) => "Enter a date and time such as 2026-01-31T08:00:00 as the expected value.",
        _ => null
    };

    /// <summary>The ConfigMgr data type name as the cmdlets take it, from the SDM type a read returns.</summary>
    public static string DataTypeName(string sdmType) => sdmType switch
    {
        "Int64" => "Integer",
        "Double" => "FloatingPoint",
        _ => sdmType
    };

    public static string OperatorName(string sdmOperator) => sdmOperator switch
    {
        "Equals" => "IsEquals",
        _ => sdmOperator
    };

    public static string HiveName(string sdmHive) => sdmHive switch
    {
        "HKEY_LOCAL_MACHINE" => "LocalMachine",
        "HKEY_CURRENT_USER" => "CurrentUser",
        "HKEY_CLASSES_ROOT" => "ClassesRoot",
        "HKEY_CURRENT_CONFIG" => "CurrentConfig",
        "HKEY_USERS" => "Users",
        _ => sdmHive
    };

    /// <summary>True when only values the Set cmdlets change in place differ; the setting then keeps its ID.</summary>
    public static bool CanChangeInPlace(CiSetting before, CiSetting after) =>
        before.Kind == after.Kind && before.DataType == after.DataType && before.ExpectedValue == after.ExpectedValue
        && before.Operator == after.Operator && before.RuleName == after.RuleName && before.Remediate == after.Remediate
        && before.ReportNoncompliance == after.ReportNoncompliance && before.Severity == after.Severity
        && (after.Kind != "Registry" || before.RemediateDword == after.RemediateDword);

    /// <summary>True when the two describe the same stored setting.</summary>
    public static bool Same(CiSetting a, CiSetting b) =>
        a.Name == b.Name && a.Kind == b.Kind && a.DataType == b.DataType && a.ExpectedValue == b.ExpectedValue && a.Description == b.Description
        && a.Hive == b.Hive && a.KeyName == b.KeyName && a.ValueName == b.ValueName && a.Is64Bit == b.Is64Bit
        && a.DiscoveryLanguage == b.DiscoveryLanguage && Normalize(a.DiscoveryScript) == Normalize(b.DiscoveryScript)
        && a.RemediationLanguage == b.RemediationLanguage && Normalize(a.RemediationScript) == Normalize(b.RemediationScript)
        && a.RuleName == b.RuleName && a.Operator == b.Operator && a.Remediate == b.Remediate && a.ReportNoncompliance == b.ReportNoncompliance
        && a.Severity == b.Severity && a.RemediateDword == b.RemediateDword;

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
