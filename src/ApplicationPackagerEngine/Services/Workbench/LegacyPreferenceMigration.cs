using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>The outcome of one legacy preference migration pass.</summary>
public sealed record LegacyMigrationResult(IReadOnlyList<WorkbenchProfile> Migrated, IReadOnlyList<string> Skipped)
{
    public int MigratedCount => Migrated.Count;
    public int SkippedCount => Skipped.Count;
}

/// <summary>
/// Migrates the per-definition DeploymentConditions and CommandOverrides maps of the
/// desktop settings into one profile named Migrated per application.
/// </summary>
public static class LegacyPreferenceMigration
{
    public const string MigratedProfileName = "Migrated";

    /// <summary>
    /// Reads <c>DeploymentConditions</c> and <c>CommandOverrides</c> (definition id to
    /// entry) from the settings document and writes one Migrated profile per key.
    /// A key maps to custom:&lt;id&gt; when discovery finds it under the custom definitions
    /// root and to catalog:&lt;id&gt; otherwise; catalog wins a name collision.
    /// Provenance is the stored MigratedFrom marker, never the profile name, so a
    /// renamed profile is still found. An application whose migrated profile already
    /// records the same legacy content digest is skipped. The migrated profile becomes
    /// active only while the application is on the default profile or already on it;
    /// an operator's own active choice is left alone. The settings document is read only.
    /// </summary>
    public static LegacyMigrationResult Run(WorkbenchStore store, JsonNode? settings, WorkbenchDiscoveryRoots? roots = null)
    {
        var migrated = new List<WorkbenchProfile>();
        var skipped = new List<string>();

        var originOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in store.DiscoverApplications(roots))
        {
            if (application.Origin == ApplicationOrigin.Byo) continue;
            var name = application.ApplicationId[(application.ApplicationId.IndexOf(':') + 1)..];
            if (originOf.TryGetValue(name, out var existingId)
                && existingId.StartsWith(ApplicationIds.CatalogPrefix + ":", StringComparison.OrdinalIgnoreCase))
                continue;
            originOf[name] = application.ApplicationId;
        }

        var conditionApps = WorkbenchJson.Get(settings, "DeploymentConditions").Value as JsonObject;
        var commandApps = WorkbenchJson.Get(settings, "CommandOverrides").Value as JsonObject;

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { conditionApps, commandApps })
            if (source is not null)
                foreach (var property in source) keys.Add(property.Key);

        foreach (var key in keys)
        {
            var condition = conditionApps is not null && conditionApps.TryGetPropertyValue(key, out var c) ? c : null;
            var command = commandApps is not null && commandApps.TryGetPropertyValue(key, out var m) ? m : null;

            var legacy = WorkbenchJson.NewObject();
            legacy["Conditions"] = WorkbenchJson.Clone(condition);
            legacy["Commands"] = WorkbenchJson.Clone(command);
            var legacyDigest = WorkbenchJson.TextSha256(PowerShellJson.SerializeCanonical(legacy));

            if (!originOf.TryGetValue(key, out var applicationId))
            {
                try { applicationId = ApplicationIds.New(ApplicationOrigin.Catalog, name: key); }
                catch (ArgumentException) { continue; }
            }
            if (!ApplicationIds.IsValid(applicationId)) continue;

            WorkbenchProfile? existing = null;
            foreach (var candidate in store.GetProfiles(applicationId))
            {
                if (candidate.IsDefault) continue;
                var stored = store.GetProfile(applicationId, candidate.ProfileId);
                if (string.Equals(stored.Get("MigratedFrom").Text, key, StringComparison.OrdinalIgnoreCase))
                {
                    existing = stored;
                    break;
                }
            }
            if (existing is not null && string.Equals(existing.Get("MigratedDigest").Text, legacyDigest, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(applicationId);
                continue;
            }

            var profile = existing?.Clone() ?? WorkbenchProfile.CreateNew(applicationId, name: MigratedProfileName);
            profile.Data.Remove("IsDefault");
            profile.Data["MigratedFrom"] = key;
            profile.Data["MigratedDigest"] = legacyDigest;

            if (WorkbenchJson.IsTruthy(condition)) ApplyConditions(profile, condition!);
            if (WorkbenchJson.IsTruthy(command)) ApplyCommands(profile, command!);

            var active = store.GetApplicationDefinition(applicationId).ActiveProfileId;
            var activate = string.IsNullOrWhiteSpace(active) || ApplicationIds.IsDefaultProfile(active)
                || string.Equals(active, profile.ProfileId, StringComparison.OrdinalIgnoreCase);
            migrated.Add(store.SaveProfile(profile, activate));
        }
        return new LegacyMigrationResult(migrated, skipped);
    }

    private static void ApplyConditions(WorkbenchProfile profile, JsonNode condition)
    {
        var rules = new JsonArray();
        var architecture = WorkbenchJson.Get(condition, "Architecture").Text;
        if (IsOneOf(architecture, "x64", "ARM64"))
        {
            var rule = WorkbenchJson.NewObject();
            rule["ConditionId"] = "cpu-arch";
            rule["Value"] = architecture;
            rules.Add(Operation("cpu-arch", rule));
        }

        var languages = WorkbenchProfileValidator.Items(WorkbenchJson.Get(condition, "Languages").Value)
            .Select(WorkbenchJson.AsText)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .ToList();
        if (languages.Count > 0)
        {
            var rule = WorkbenchJson.NewObject();
            rule["ConditionId"] = "os-language";
            rule["Cultures"] = new JsonArray(languages.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            rules.Add(Operation("os-language", rule));
        }

        var network = WorkbenchJson.Get(condition, "Network").Text;
        if (IsOneOf(network, "VpnOnly") || IsOneOf(network, "OnSiteOnly"))
        {
            var rule = WorkbenchJson.NewObject();
            rule["ConditionId"] = "vpn-connected";
            rule["Value"] = IsOneOf(network, "VpnOnly");
            rules.Add(Operation("vpn-connected", rule));
        }
        if (rules.Count > 0)
        {
            var requirements = WorkbenchJson.NewObject();
            requirements["Operations"] = rules;
            profile.Data["Requirements"] = requirements;
        }

        var split = WorkbenchJson.Get(condition, "Split").Text;
        if (IsOneOf(split, "Architecture", "Language", "Network"))
        {
            var variantsSplit = WorkbenchJson.NewObject();
            variantsSplit["SchemaVersion"] = 1;
            variantsSplit["Split"] = split;
            if (IsOneOf(split, "Language") && languages.Count > 0)
                variantsSplit["Languages"] = new JsonArray(languages.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            var variants = WorkbenchJson.NewObject();
            variants["Split"] = variantsSplit;
            variants["Overrides"] = WorkbenchJson.NewObject();
            profile.Data["Variants"] = variants;
        }

        var installMode = WorkbenchJson.Get(condition, "InstallMode").Text;
        if (IsOneOf(installMode, "CurrentUser", "AllUsers")) profile.Data["InstallMode"] = installMode;

        var titleMode = WorkbenchJson.Get(condition, "TitleMode").Text;
        if (IsOneOf(titleMode, "IncludeVersion", "NoVersion"))
        {
            if (profile.Data["Application"] is not JsonObject) profile.Data["Application"] = WorkbenchJson.NewObject();
            profile.Data["Application"]!["TitleMode"] = titleMode;
        }
    }

    private static void ApplyCommands(WorkbenchProfile profile, JsonNode command)
    {
        var install = (WorkbenchJson.Get(command, "Install").Text ?? "").Trim();
        var uninstall = (WorkbenchJson.Get(command, "Uninstall").Text ?? "").Trim();
        foreach (var (section, value) in new[] { ("Install", install), ("Uninstall", uninstall) })
        {
            if (value.Length == 0) continue;
            if (profile.Data[section] is not JsonObject) profile.Data[section] = WorkbenchJson.NewObject();
            profile.Data[section]!["Command"] = value;
        }
    }

    private static JsonObject Operation(string ruleId, JsonObject rule)
    {
        var operation = WorkbenchJson.NewObject();
        operation["Op"] = "Add";
        operation["RuleId"] = ruleId;
        operation["Rule"] = rule;
        operation["AppliesTo"] = new JsonArray("*");
        return operation;
    }

    private static bool IsOneOf(string? value, params string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}
