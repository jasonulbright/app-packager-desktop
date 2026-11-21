using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The requirement rules of one Intune publish: the specs (from the manifest and from the saved
/// deployment conditions), the condition templates that give each spec its meaning, and the
/// rules whose drop the administrator accepted.
/// </summary>
public sealed class IntuneRequirementPlan
{
    /// <summary>The specs that the run applies beside the ones in the manifest (the saved deployment conditions).</summary>
    public IReadOnlyList<StageRequirement> Extra { get; init; } = [];
    public ConditionTemplateDocument Templates { get; init; } = ConditionTemplateService.Defaults();

    /// <summary>The <see cref="DropKey(StageManifest, string)"/> values of the rules that may be left out of the app.</summary>
    public IReadOnlySet<string> AcceptedDrops { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The signing policy of the run. When it signs or requires signed requirement scripts, the
    /// script of a script condition is signed like the ConfigMgr global condition.
    /// </summary>
    public SigningPolicy? SigningPolicy { get; init; }

    /// <summary>
    /// The key that names one rule of one application in the list of accepted drops: the
    /// identity tag of the application in Intune (the same one that finds the app again on the
    /// next publish), a bar and the condition id. It does not change with the version.
    /// </summary>
    public static string DropKey(StageManifest manifest, string conditionId) =>
        $"{IntunePublishService.IdentityTag(manifest)}|{conditionId}";

    /// <summary>The plan of a run: the specs beside the manifest, the templates (the built-in ones when null) and the accepted drops.</summary>
    public static IntuneRequirementPlan Of(
        IReadOnlyList<StageRequirement>? extra, ConditionTemplateDocument? templates, IEnumerable<string>? acceptedDrops,
        SigningPolicy? signingPolicy = null) => new()
    {
        Extra = [.. (extra ?? []).Where(r => r is not null)],
        Templates = templates ?? ConditionTemplateService.Defaults(),
        AcceptedDrops = new HashSet<string>(acceptedDrops ?? [], StringComparer.OrdinalIgnoreCase),
        SigningPolicy = signingPolicy
    };

    /// <summary>
    /// The specs of the application. Like the ConfigMgr publish, the saved conditions of the run
    /// replace the requirement rules of the manifest; without them the manifest's rules apply.
    /// </summary>
    public IReadOnlyList<StageRequirement> AllSpecs(StageManifest manifest) =>
        Extra.Count > 0 ? Extra : [.. (manifest.Requirements ?? []).Where(r => r is not null)];
}

/// <summary>A requirement script must be signed by the policy and the signature was not produced. It is not a gap that a drop can accept.</summary>
public sealed class IntuneRequirementSigningException(string message) : InvalidOperationException(message);

/// <summary>A property of the Win32 app resource that carries a requirement as a minimum (the Intune admin center shows it under Requirements).</summary>
public sealed record IntuneAppSetting(string Property, JsonNode Value);

/// <summary>
/// One requirement spec and what became of it: a requirement rule, a property of the app (a hardware minimum or the minimum
/// Windows release), or a gap.
/// </summary>
public sealed record IntuneRequirementTranslation(StageRequirement Spec, JsonObject? Rule, string? Gap, IntuneAppSetting? Setting = null)
{
    public bool Translated => Rule is not null || Setting is not null;
}

/// <summary>
/// Turns the ConfigMgr requirement specs of an application into Intune requirement rules. Graph
/// has no rule for a WMI value, an operating system language or a custom condition, so each
/// becomes a PowerShell requirement rule that prints True or False and passes when it prints
/// True. A spec that cannot be turned into such a script has a gap, and the publish does not
/// continue without it unless the administrator accepted the drop of that rule.
/// </summary>
public static class IntuneRequirementRules
{
    /// <param name="signingPolicy">When it is given and signs or requires signed requirement scripts, a script condition is signed; null leaves every script unsigned (the findings never sign).</param>
    /// <exception cref="IntuneRequirementSigningException">A required signature was not produced.</exception>
    public static IReadOnlyList<IntuneRequirementTranslation> Translate(
        IEnumerable<StageRequirement> specs, ConditionTemplateDocument templates, SigningPolicy? signingPolicy = null)
    {
        var result = new List<IntuneRequirementTranslation>();
        foreach (var spec in specs)
        {
            try
            {
                var (rule, setting) = Build(spec, templates, signingPolicy);
                result.Add(new(spec, rule, null, setting));
            }
            catch (IntuneRequirementSigningException)
            {
                throw;
            }
            // A culture code that does not exist throws ArgumentException (CultureNotFoundException); it is a gap like any other.
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                result.Add(new(spec, null, ex.Message));
            }
        }
        return result;
    }

    private static (JsonObject? Rule, IntuneAppSetting? Setting) Build(StageRequirement spec, ConditionTemplateDocument templates, SigningPolicy? signingPolicy)
    {
        var id = spec.ConditionId ?? "";
        var template = templates.Conditions.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The requirement names the condition '{id}', which is not in the condition templates.");

        // The same binding as the ConfigMgr publish: an unknown value or a missing value is a failure there too.
        var resolved = RequirementRuleBuilder.Resolve([spec], templates)[0];

        // A minimum that the Win32 app resource has a property for goes there. An unknown build number has no release
        // and becomes a rule, so the requirement keeps its exact meaning.
        string? minimumScript = null;
        if (!string.IsNullOrWhiteSpace(template.IntuneProperty))
        {
            var setting = AppSetting(template, resolved);
            if (setting is not null) return (null, setting);
            minimumScript = BuildMinimumScript(resolved);
        }

        var (script, expected) = template.RuleType switch
        {
            _ when minimumScript is not null => (minimumScript, true),
            "CommonValue" when string.Equals(template.Kind, "Wql", StringComparison.Ordinal) && resolved.Operator == RequirementRuleBuilder.DefaultOperator => (WqlScript(template, resolved.Value!), true),
            "OperatingSystemLanguage" when string.Equals(template.Kind, "BuiltIn", StringComparison.Ordinal) => (LanguageScript(resolved.Cultures), true),
            "Boolean" when string.Equals(template.Kind, "Script", StringComparison.Ordinal) => (ConditionTemplateService.ResolveScriptText(template), resolved.BooleanValue),
            _ => throw new InvalidOperationException(
                $"The condition '{template.Id}' ({template.Kind} condition with the rule type {template.RuleType}) has no Intune requirement rule.")
        };

        // A script condition follows the signing policy of the requirement category, as the ConfigMgr
        // global condition does. The scripts that this program writes itself (WMI value, language) are
        // not script conditions and stay unsigned.
        var content = Encoding.UTF8.GetBytes(script);
        var enforce = false;
        if (string.Equals(template.Kind, "Script", StringComparison.Ordinal)
            && SigningPolicy.Normalize(signingPolicy) is { } policy && (policy.SignRequirements || policy.RequireRequirements))
        {
            try
            {
                var identity = ConditionScriptIdentity.Resolve(template, policy);
                content = identity.Bytes;
                enforce = policy.RequireRequirements && identity.Signed;
            }
            catch (InvalidOperationException ex)
            {
                throw new IntuneRequirementSigningException(ex.Message);
            }
        }

        return (new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.win32LobAppPowerShellScriptRule",
            ["ruleType"] = "requirement",
            ["displayName"] = Clip(string.IsNullOrWhiteSpace(template.GlobalConditionName) ? template.Id : template.GlobalConditionName, 100),
            ["enforceSignatureCheck"] = enforce,
            ["runAs32Bit"] = false,
            ["runAsAccount"] = "system",
            ["scriptContent"] = Convert.ToBase64String(content),
            ["operationType"] = "string",
            ["operator"] = "equal",
            ["comparisonValue"] = expected ? "True" : "False"
        }, null);
    }

    /// <summary>
    /// The app property for a minimum, or null when the app has none for this value (a build that is not the build of a
    /// release). The app property is a minimum, so a template with another operator has no Intune form.
    /// </summary>
    private static IntuneAppSetting? AppSetting(ConditionTemplate template, ResolvedRequirement resolved)
    {
        if (!string.Equals(resolved.Operator, "GreaterEquals", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The condition '{template.Id}' uses the operator {resolved.Operator}, but the Intune setting {template.IntuneProperty} is a minimum, so only GreaterEquals has an Intune form.");
        var number = resolved.Number
            ?? throw new InvalidOperationException($"The condition '{template.Id}' has no whole number for the Intune setting {template.IntuneProperty}.");

        switch (template.IntuneProperty)
        {
            case "minimumFreeDiskSpaceInMB":
            case "minimumMemoryInMB":
                if (number > int.MaxValue)
                    throw new InvalidOperationException($"The value {number} of the condition '{template.Id}' is larger than the Intune setting {template.IntuneProperty} takes (a 32-bit whole number of MB).");
                return new IntuneAppSetting(template.IntuneProperty!, JsonValue.Create((int)number));
            case "minimumSupportedWindowsRelease":
                return IntuneWindowsReleases.OfBuild(number) is { } release ? new IntuneAppSetting(template.IntuneProperty!, JsonValue.Create(release)) : null;
            default:
                throw new InvalidOperationException($"The condition '{template.Id}' names the unknown Intune setting '{template.IntuneProperty}'.");
        }
    }

    /// <summary>True when the build number of the operating system is at least the required build (the rule that stands in for a build with no release name).</summary>
    private static string BuildMinimumScript(ResolvedRequirement resolved) => string.Join("\r\n",
        "$ErrorActionPreference = 'SilentlyContinue'",
        $"Write-Output ([int](Get-CimInstance -ClassName Win32_OperatingSystem).BuildNumber -ge {resolved.Number})");

    /// <summary>True when some row of the WMI class has the value; the ConfigMgr rule compares with IsEquals.</summary>
    private static string WqlScript(ConditionTemplate template, string value)
    {
        if (string.IsNullOrWhiteSpace(template.Namespace) || string.IsNullOrWhiteSpace(template.Class) || string.IsNullOrWhiteSpace(template.Property))
            throw new InvalidOperationException($"The condition '{template.Id}' names no WMI namespace, class and property.");
        return string.Join("\r\n",
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"$values = @(Get-CimInstance -Namespace {Quote(template.Namespace)} -ClassName {Quote(template.Class)} | ForEach-Object {{ [string]$_.{Identifier(template.Property)} }})",
            $"Write-Output ($values -contains {Quote(value)})");
    }

    /// <summary>True when the installed UI language of the operating system is one of the cultures.</summary>
    private static string LanguageScript(string[] cultures) => string.Join("\r\n",
        $"$cultures = @({string.Join(", ", cultures.Select(Quote))})",
        "$name = [System.Globalization.CultureInfo]::InstalledUICulture.Name",
        "Write-Output ($cultures -contains $name)");

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>A WMI property name goes into the script as code, so it must be a plain name.</summary>
    private static string Identifier(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? name
            : throw new InvalidOperationException($"The WMI property name '{name}' is not a plain name.");

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
