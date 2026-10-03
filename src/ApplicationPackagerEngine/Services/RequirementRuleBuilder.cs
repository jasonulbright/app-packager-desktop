using System.Globalization;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services;

/// <summary>A requirement spec bound to its condition template and value.</summary>
public sealed class ResolvedRequirement
{
    public required ConditionTemplate Template { get; init; }
    /// <summary>Mapped value for a CommonValue rule.</summary>
    public string? Value { get; init; }
    /// <summary>Culture names for an OperatingSystemLanguage rule.</summary>
    public string[] Cultures { get; init; } = [];
    /// <summary>Parsed value for a Boolean rule.</summary>
    public bool BooleanValue { get; init; }
}

/// <summary>
/// Binds requirement specs to condition templates and emits the CM
/// PowerShell that resolves the global conditions and builds the rule
/// objects. Unknown condition ids and unmapped values throw before any
/// application is created, so a package run never produces a deployment
/// type missing rules the operator asked for.
/// </summary>
public static class RequirementRuleBuilder
{
    /// <summary>PowerShell helper the generated scripts call per condition.</summary>
    public const string GlobalConditionFunctionName = "Get-ApkgGlobalCondition";

    public static List<ResolvedRequirement> Resolve(
        IEnumerable<StageRequirement> specs, ConditionTemplateDocument doc)
    {
        var resolved = new List<ResolvedRequirement>();
        foreach (var spec in specs)
        {
            var conditionId = spec.ConditionId ?? "";
            var template = doc.Conditions.FirstOrDefault(
                c => string.Equals(c.Id, conditionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Requirement rule references unknown condition id '{conditionId}'.");

            if (string.IsNullOrWhiteSpace(template.GlobalConditionName))
                throw new InvalidOperationException(
                    $"Condition template '{template.Id}' has no GlobalConditionName.");

            switch (template.RuleType)
            {
                case "CommonValue":
                {
                    var value = ValueAsString(spec.Value);
                    if (template.Values is { Count: > 0 })
                    {
                        if (!template.Values.TryGetValue(value ?? "", out var mapped))
                            throw new InvalidOperationException(
                                $"Condition '{conditionId}' has no mapping for value '{value}' (known: {string.Join(", ", template.Values.Keys)}).");
                        value = mapped;
                    }
                    if (string.IsNullOrWhiteSpace(value))
                        throw new InvalidOperationException($"Condition '{conditionId}' requires a Value.");
                    resolved.Add(new ResolvedRequirement { Template = template, Value = value });
                    break;
                }
                case "OperatingSystemLanguage":
                {
                    var cultures = (spec.Cultures ?? [])
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Select(c => CultureInfo.GetCultureInfo(c).Name)
                        .ToArray();
                    if (cultures.Length == 0)
                        throw new InvalidOperationException(
                            $"Condition '{conditionId}' requires at least one culture code (e.g. de-DE).");
                    resolved.Add(new ResolvedRequirement { Template = template, Cultures = cultures });
                    break;
                }
                case "Boolean":
                {
                    resolved.Add(new ResolvedRequirement
                    {
                        Template = template,
                        BooleanValue = ValueAsBoolean(spec.Value, conditionId)
                    });
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Condition '{conditionId}' has unsupported RuleType '{template.RuleType}'.");
            }
        }
        return resolved;
    }

    /// <summary>
    /// The get-or-create helper. Matching is by name, so an existing site
    /// condition of any origin is attached rather than duplicated. A name
    /// can match several site conditions, so a template PlatformType
    /// narrows the match and a non-unique match throws. CM cmdlets can
    /// report success while the provider refuses the write, so a creation
    /// is verified by reading the condition back.
    /// </summary>
    public static string BuildGlobalConditionFunction() =>
        $$"""
        function {{GlobalConditionFunctionName}} {
            param(
                [string]$Name, [int]$PlatformType, [string]$Kind,
                [string]$DataType, [string]$Namespace, [string]$Class,
                [string]$Property, [string]$ScriptText, [string]$Description
            )
            $found = @(Get-CMGlobalCondition -Name $Name)
            if ($PlatformType -gt 0 -and $found.Count -gt 1) {
                $found = @($found | Where-Object { [int]$_.PlatformType -eq $PlatformType })
            }
            if ($found.Count -gt 1) {
                throw "Global condition name '$Name' matches $($found.Count) site conditions; add a PlatformType to the template or rename to a unique condition."
            }
            if ($found.Count -eq 1) {
                Write-Host "Global condition (existing)  : $Name"
                return $found[0]
            }
            switch ($Kind) {
                'BuiltIn' { throw "Built-in global condition '$Name' was not found on the site." }
                'Wql' {
                    New-CMGlobalConditionWqlQuery -Name $Name -DataType $DataType -Namespace $Namespace -Class $Class -Property $Property -Description $Description | Out-Null
                }
                'Script' {
                    New-CMGlobalConditionScript -Name $Name -DataType $DataType -ScriptLanguage PowerShell -ScriptText $ScriptText -Description $Description | Out-Null
                }
                default { throw "Condition template for '$Name' has unsupported Kind '$Kind'." }
            }
            $created = @(Get-CMGlobalCondition -Name $Name)
            if ($PlatformType -gt 0 -and $created.Count -gt 1) {
                $created = @($created | Where-Object { [int]$_.PlatformType -eq $PlatformType })
            }
            if ($created.Count -ne 1) {
                throw "Global condition '$Name' is not readable after creation; the provider refused the write."
            }
            Write-Host "Global condition (created)   : $Name"
            return $created[0]
        }
        """;

    /// <summary>True when any resolved rule binds a Script-kind condition.</summary>
    public static bool UsesScriptConditions(IEnumerable<IReadOnlyList<ResolvedRequirement>> resolved) =>
        resolved.Any(r => r.Any(i => IsScriptKind(i.Template)));

    private static bool IsScriptKind(ConditionTemplate template) =>
        string.Equals(template.Kind, "Script", StringComparison.Ordinal);

    /// <summary>
    /// Emits the global condition lookups and rule objects for one
    /// deployment type, and returns the PowerShell variable names holding
    /// the rules. A Script-kind condition resolves through the content-versioned
    /// resolver with the identity <paramref name="scriptIdentity"/> returns;
    /// null resolves the unsigned identity.
    /// </summary>
    public static List<string> AppendRules(
        StringBuilder sb, IReadOnlyList<ResolvedRequirement> resolved, string variablePrefix,
        Func<ConditionTemplate, ConditionScriptIdentity>? scriptIdentity = null)
    {
        var names = new List<string>();
        for (var i = 0; i < resolved.Count; i++)
        {
            var item = resolved[i];
            var template = item.Template;
            var gcVar = $"${variablePrefix}_gc{i}";
            var ruleVar = $"${variablePrefix}_req{i}";

            if (IsScriptKind(template))
            {
                var identity = scriptIdentity?.Invoke(template) ?? ConditionScriptIdentity.Resolve(template);
                sb.Append($"{gcVar} = {MecmScriptSigningPs.ScriptGlobalConditionFunction}");
                sb.Append($" -Name '{Esc(template.GlobalConditionName)}'");
                sb.Append($" -PlatformType {template.PlatformType ?? 0}");
                sb.Append($" -DataType '{Esc(template.DataType ?? "")}'");
                sb.Append($" -Description '{Esc(template.Description ?? "")}'");
                sb.Append($" -ScriptText '{Esc(identity.Text)}'");
                sb.Append($" -SignedBase64 '{(identity.Signed ? Convert.ToBase64String(identity.Bytes) : "")}'");
                sb.Append($" -ContentHash '{identity.ContentHash}'");
                sb.AppendLine($" -ExpectedThumbprint '{Esc(identity.Signed ? identity.Thumbprint : "")}'");
            }
            else
            {
                sb.Append($"{gcVar} = {GlobalConditionFunctionName}");
                sb.Append($" -Name '{Esc(template.GlobalConditionName)}'");
                sb.Append($" -PlatformType {template.PlatformType ?? 0}");
                sb.Append($" -Kind '{Esc(template.Kind)}'");
                sb.Append($" -DataType '{Esc(template.DataType ?? "")}'");
                sb.Append($" -Namespace '{Esc(template.Namespace ?? "")}'");
                sb.Append($" -Class '{Esc(template.Class ?? "")}'");
                sb.Append($" -Property '{Esc(template.Property ?? "")}'");
                sb.Append(" -ScriptText ''");
                sb.AppendLine($" -Description '{Esc(template.Description ?? "")}'");
            }

            switch (template.RuleType)
            {
                case "CommonValue":
                    sb.AppendLine($"{ruleVar} = New-CMRequirementRuleCommonValue -InputObject {gcVar} -RuleOperator IsEquals -Value1 '{Esc(item.Value!)}'");
                    break;
                case "OperatingSystemLanguage":
                    var cultures = string.Join(", ", item.Cultures.Select(
                        c => $"[System.Globalization.CultureInfo]::GetCultureInfo('{Esc(c)}')"));
                    sb.AppendLine($"{ruleVar} = New-CMRequirementRuleOperatingSystemLanguageValue -InputObject {gcVar} -RuleOperator OneOf -Culture @({cultures})");
                    break;
                case "Boolean":
                    sb.AppendLine($"{ruleVar} = New-CMRequirementRuleBooleanValue -InputObject {gcVar} -Value ${(item.BooleanValue ? "true" : "false")}");
                    break;
            }
            sb.AppendLine($"Write-Host \"Requirement rule             : {PowerShellLiteral.EscapeDoubleQuoted(template.Id)} ({PowerShellLiteral.EscapeDoubleQuoted(template.GlobalConditionName)})\"");
            names.Add(ruleVar);
        }
        return names;
    }

    private static string? ValueAsString(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.ToString(),
        _ => value.ToString()
    };

    /// <summary>
    /// Parses the string or JSON boolean form. A bare non-empty string is
    /// not implicitly true: an unparseable value throws.
    /// </summary>
    private static bool ValueAsBoolean(object? value, string conditionId)
    {
        switch (value)
        {
            case bool b: return b;
            case JsonElement { ValueKind: JsonValueKind.True }: return true;
            case JsonElement { ValueKind: JsonValueKind.False }: return false;
        }
        var text = ValueAsString(value);
        if (bool.TryParse(text, out var parsed)) return parsed;
        throw new InvalidOperationException(
            $"Condition '{conditionId}' requires a boolean Value (got '{text}').");
    }

    private static string Esc(string value) => PowerShellLiteral.EscapeSingleQuoted(value);
}
