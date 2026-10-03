using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Merges every precedence level into one field-by-field result.
/// Precedence, highest last: Global, Packager, Profile, Variant, Target, Run.
/// </summary>
public static class EffectiveSettingsResolver
{
    /// <summary>
    /// The 18-field table. Global paths name AppSettings properties; Packager paths
    /// name stage-manifest fields; Profile paths are dotted profile paths.
    /// </summary>
    public static readonly IReadOnlyList<WorkbenchField> FieldMap =
    [
        new("DisplayName", null, "AppName", "Application.DisplayName", true),
        new("Publisher", null, "Publisher", "Application.Publisher", true),
        new("Description", "Description", "Description", "Application.Description", false),
        new("TitleMode", "TitleMode", null, "Application.TitleMode", false),
        new("Icon", null, "Icon", "Application.Icon", true),
        new("PinnedVersion", null, "SoftwareVersion", "PinnedVersion", true),
        new("InstallMode", null, "InstallMode", "InstallMode", false),
        new("InstallCommand", null, "InstallCommandLine", "Install.Command", true),
        new("UninstallCommand", null, "UninstallCommandLine", "Uninstall.Command", true),
        new("InstallScriptMode", null, null, "Install.Mode", false),
        new("UninstallScriptMode", null, null, "Uninstall.Mode", false),
        new("EstimatedMinutes", "EstimatedRuntimeMins", null, "Timing.EstimatedMinutes", false),
        new("MaximumMinutes", "MaximumRuntimeMins", null, "Timing.MaximumMinutes", false),
        new("Context", null, "InstallationBehaviorType", "Execution.Context", false),
        new("LogonRequirement", null, "LogonRequirementType", "Execution.LogonRequirement", false),
        new("UserInteraction", null, "RequireUserInteraction", "Execution.UserInteraction", false),
        new("ScriptHost", null, null, "Execution.ScriptHost", false),
        new("Detection", null, "Detection", "Detection.Rule", true)
    ];

    /// <summary>
    /// Resolves every field. A Global value counts when it is not null; a Packager
    /// value counts when it is neither null nor empty text; at the Profile, Variant,
    /// Target and Run levels presence alone counts, so an explicit null or empty
    /// string is a value that wins while an absent key inherits. A field nothing
    /// supplies reports NeedsStaging when only a completed Stage can resolve it,
    /// otherwise Global with a null value.
    /// </summary>
    public static EffectiveSettings Resolve(
        JsonNode? globalDefaults = null,
        JsonNode? baseManifest = null,
        JsonNode? profile = null,
        string? variant = null,
        JsonNode? targetOverrides = null,
        JsonNode? runOverrides = null)
    {
        JsonNode? variantOverride = null;
        if (!string.IsNullOrWhiteSpace(variant) && profile is not null)
        {
            var member = WorkbenchJson.Get(profile, "Variants.Overrides." + variant);
            if (member.Found) variantOverride = member.Value;
        }

        var results = new List<EffectiveSetting>(FieldMap.Count);
        foreach (var entry in FieldMap)
        {
            JsonNode? value = null;
            SettingSource? source = null;

            if (globalDefaults is not null && entry.GlobalPath is not null)
            {
                var member = WorkbenchJson.Get(globalDefaults, entry.GlobalPath);
                if (member.Found && member.Value is not null && !IsJsonNull(member.Value))
                {
                    value = member.Value;
                    source = SettingSource.Global;
                }
            }
            if (baseManifest is not null && entry.PackagerPath is not null)
            {
                var member = WorkbenchJson.Get(baseManifest, entry.PackagerPath);
                if (member.Found && member.Value is not null && !IsJsonNull(member.Value)
                    && !string.IsNullOrEmpty(WorkbenchJson.AsText(member.Value)))
                {
                    value = member.Value;
                    source = SettingSource.Packager;
                }
            }
            if (profile is not null)
                Take(profile, entry.ProfilePath, SettingSource.Profile, ref value, ref source);
            if (WorkbenchJson.IsTruthy(variantOverride))
                Take(variantOverride, entry.Field, SettingSource.Variant, ref value, ref source);
            if (WorkbenchJson.IsTruthy(targetOverrides))
                Take(targetOverrides, entry.Field, SettingSource.Target, ref value, ref source);
            if (WorkbenchJson.IsTruthy(runOverrides))
                Take(runOverrides, entry.Field, SettingSource.Run, ref value, ref source);

            source ??= entry.NeedsStaging ? SettingSource.NeedsStaging : SettingSource.Global;
            results.Add(new EffectiveSetting(entry.Field, value?.DeepClone(), source.Value));
        }
        return new EffectiveSettings(results);
    }

    private static void Take(JsonNode? level, string path, SettingSource levelSource,
        ref JsonNode? value, ref SettingSource? source)
    {
        var member = WorkbenchJson.Get(level, path);
        if (!member.Found) return;
        value = member.Value;
        source = levelSource;
    }

    private static bool IsJsonNull(JsonNode node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Null;
}
