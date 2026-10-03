using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

public sealed partial class WorkbenchStore
{
    public string GetProfileFolder(string applicationId, string profileId, bool create = false)
    {
        if (ApplicationIds.IsDefaultProfile(profileId))
            throw new InvalidOperationException("The default profile is virtual and has no storage folder.");
        ApplicationIds.AssertProfileId(profileId);
        var folder = Path.Combine(GetApplicationFolder(applicationId), "profiles", profileId);
        if (create) Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The virtual packager-default profile first, then every saved profile by folder name.</summary>
    public IReadOnlyList<WorkbenchProfileSummary> GetProfiles(string applicationId)
    {
        var active = GetApplicationDefinition(applicationId).ActiveProfileId;
        var results = new List<WorkbenchProfileSummary>
        {
            new(ApplicationIds.DefaultProfileId, applicationId, WorkbenchProfile.DefaultProfileName, 0,
                IsDefault: true,
                IsActive: string.IsNullOrWhiteSpace(active) || ApplicationIds.IsDefaultProfile(active),
                Path: null)
        };

        var profilesRoot = Path.Combine(GetApplicationFolder(applicationId), "profiles");
        if (!Directory.Exists(profilesRoot)) return results;
        foreach (var folder in Directory.GetDirectories(profilesRoot).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, "profile.json");
            if (WorkbenchJson.Read(path) is not JsonObject stored) continue;
            var profileId = WorkbenchJson.AsText(stored["ProfileId"]) ?? "";
            results.Add(new WorkbenchProfileSummary(
                profileId, applicationId, WorkbenchJson.AsText(stored["Name"]) ?? "",
                WorkbenchJson.AsInt(stored["Revision"]) ?? 0, IsDefault: false,
                IsActive: string.Equals(active, profileId, StringComparison.OrdinalIgnoreCase), path));
        }
        return results;
    }

    /// <summary>
    /// Loads one saved profile, or the virtual default profile. The default profile
    /// is never stored and never editable: an empty override set at revision 0.
    /// </summary>
    public WorkbenchProfile GetProfile(string applicationId, string profileId)
    {
        if (ApplicationIds.IsDefaultProfile(profileId)) return WorkbenchProfile.CreateDefault(applicationId);

        var path = Path.Combine(GetProfileFolder(applicationId, profileId), "profile.json");
        if (WorkbenchJson.Read(path) is not JsonObject stored)
            throw new InvalidOperationException($"Profile '{profileId}' was not found for application '{applicationId}'.");
        var schema = WorkbenchJson.AsInt(stored["SchemaVersion"]) ?? 0;
        if (schema > WorkbenchProfile.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Profile schema {schema} is newer than this build supports ({WorkbenchProfile.CurrentSchemaVersion}): {path}");
        stored.Remove("IsDefault");
        return new WorkbenchProfile(stored);
    }

    /// <summary>
    /// Saves a profile and raises its revision by one. The incoming Revision is the
    /// revision the editor loaded: a stored revision that no longer matches means
    /// another writer saved in between, and the save is refused. The caller's object
    /// is not modified; the saved profile is returned.
    /// </summary>
    public WorkbenchProfile SaveProfile(WorkbenchProfile profile, bool setActive = false)
    {
        var data = WorkbenchJson.CloneObject(profile.Data);
        data.Remove("IsDefault");
        var applicationId = WorkbenchJson.AsText(data["ApplicationId"]);
        var profileId = WorkbenchJson.AsText(data["ProfileId"]);
        if (string.IsNullOrWhiteSpace(applicationId)) throw new InvalidOperationException("A profile requires an ApplicationId.");
        if (ApplicationIds.IsDefaultProfile(profileId))
            throw new InvalidOperationException("The packager default profile cannot be saved; use Copy-Profile to create an editable profile.");
        if (string.IsNullOrWhiteSpace(profileId))
        {
            profileId = Guid.NewGuid().ToString("N");
            data["ProfileId"] = profileId;
        }
        if (!WorkbenchJson.IsTruthy(data["SchemaVersion"])) data["SchemaVersion"] = WorkbenchProfile.CurrentSchemaVersion;
        var schema = WorkbenchJson.AsInt(data["SchemaVersion"]) ?? 0;
        if (schema > WorkbenchProfile.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Profile schema {schema} is newer than this build supports ({WorkbenchProfile.CurrentSchemaVersion}).");
        if (string.IsNullOrWhiteSpace(WorkbenchJson.AsText(data["Name"]))) throw new InvalidOperationException("A profile requires a Name.");

        var path = Path.Combine(GetProfileFolder(applicationId, profileId), "profile.json");
        var incoming = WorkbenchJson.AsInt(data["Revision"]) ?? 0;
        if (WorkbenchJson.Read(path) is JsonObject existing)
        {
            var storedRevision = WorkbenchJson.AsInt(existing["Revision"]) ?? 0;
            if (storedRevision != incoming)
                throw new InvalidOperationException(
                    $"Profile '{profileId}' changed on disk (stored revision {storedRevision}, this editor loaded {incoming}); reload before saving.");
        }
        else if (incoming > 0)
        {
            throw new InvalidOperationException(
                $"Profile '{profileId}' carries revision {incoming} but has no stored profile; save it as a new profile instead.");
        }

        WorkbenchProfileValidator.Validate(data);
        data["Revision"] = incoming + 1;
        data["SavedAt"] = WorkbenchJson.NowRoundTrip();
        WorkbenchJson.Write(path, data);

        if (setActive) SetActiveProfile(applicationId, profileId);
        return GetProfile(applicationId, profileId);
    }

    /// <summary>Records which profile a build uses for the application.</summary>
    public WorkbenchApplicationDefinition SetActiveProfile(string applicationId, string profileId)
    {
        var definition = GetApplicationDefinition(applicationId);
        definition.ActiveProfileId = profileId;
        return SaveApplicationDefinition(definition);
    }

    /// <summary>Save as: copies a profile and its managed assets under a new id, at revision 0 before the save.</summary>
    public WorkbenchProfile CopyProfile(string applicationId, string profileId, string newName, bool setActive = false)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("Save as requires a profile name.");
        var source = GetProfile(applicationId, profileId);
        var data = WorkbenchJson.CloneObject(source.Data);
        data.Remove("IsDefault");
        var newId = Guid.NewGuid().ToString("N");
        data["ProfileId"] = newId;
        data["Name"] = newName;
        data["Revision"] = 0;
        data["CopiedFrom"] = profileId;

        var target = GetProfileFolder(applicationId, newId, create: true);
        if (!ApplicationIds.IsDefaultProfile(profileId))
        {
            var sourceAssets = Path.Combine(GetProfileFolder(applicationId, profileId), "assets");
            if (Directory.Exists(sourceAssets)) CopyDirectory(sourceAssets, Path.Combine(target, "assets"));
        }
        return SaveProfile(new WorkbenchProfile(data), setActive);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var folder in Directory.GetDirectories(source))
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }
}

/// <summary>Profile validation, run before every save. Each rule has its own message.</summary>
public static class WorkbenchProfileValidator
{
    private static readonly string[] ScriptModes = ["Generated", "Extend", "Custom"];
    private static readonly string[] DetectionModes = ["Inherit", "Custom"];
    private static readonly string[] RequirementOps = ["Add", "Replace", "Remove"];

    public static void Validate(JsonObject profile)
    {
        if (profile["Timing"] is JsonObject timing)
            ValidateMinutes(timing["EstimatedMinutes"], timing["MaximumMinutes"],
                name => $"Timing.{name}",
                (estimated, maximum) => $"Timing.EstimatedMinutes ({estimated}) cannot exceed Timing.MaximumMinutes ({maximum}).");

        foreach (var section in new[] { "Install", "Uninstall" })
        {
            if (profile[section] is not JsonObject block) continue;
            if (block.TryGetPropertyValue("Mode", out var mode) && mode is not null && !IsOneOf(mode, ScriptModes))
                throw new InvalidOperationException($"{section}.Mode must be Generated, Extend or Custom (got '{WorkbenchJson.AsText(mode)}').");
        }

        if (profile["Detection"] is JsonObject detection && detection.TryGetPropertyValue("Mode", out var detectionMode) && detectionMode is not null)
        {
            if (!IsOneOf(detectionMode, DetectionModes))
                throw new InvalidOperationException($"Detection.Mode must be Inherit or Custom (got '{WorkbenchJson.AsText(detectionMode)}').");
            if (string.Equals(WorkbenchJson.AsText(detectionMode), "Custom", StringComparison.OrdinalIgnoreCase)
                && !WorkbenchJson.IsTruthy(detection["Rule"]))
                throw new InvalidOperationException("Detection.Mode Custom requires a Detection.Rule.");
        }

        if (profile["Requirements"] is JsonObject requirements && WorkbenchJson.IsTruthy(requirements["Operations"]))
        {
            foreach (var operation in Items(requirements["Operations"]))
            {
                if (operation is null) continue;
                var op = WorkbenchJson.Get(operation, "Op").Text ?? "";
                var ruleId = WorkbenchJson.Get(operation, "RuleId").Text;
                if (!RequirementOps.Contains(op, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Requirement operation Op must be Add, Replace or Remove (got '{op}').");
                if (string.IsNullOrWhiteSpace(ruleId)) throw new InvalidOperationException("Every requirement operation needs a RuleId.");
                if (!string.Equals(op, "Remove", StringComparison.OrdinalIgnoreCase) && !WorkbenchJson.IsTruthy(WorkbenchJson.Get(operation, "Rule").Value))
                    throw new InvalidOperationException($"Requirement operation '{op}' for rule '{ruleId}' needs a Rule.");
            }
        }

        foreach (var entry in Items(profile["SourceFiles"]))
        {
            if (entry is null) continue;
            var destination = WorkbenchJson.Get(entry, "Destination").Text;
            if (string.IsNullOrWhiteSpace(destination)) throw new InvalidOperationException("Every SourceFiles entry needs a Destination.");
            if (Path.IsPathRooted(destination))
                throw new InvalidOperationException($"SourceFiles destination '{destination}' must be relative to the content root.");
        }
    }

    /// <summary>
    /// Shared by profile timing and run overrides: each present value is a positive
    /// whole number, and the estimate does not exceed the maximum.
    /// </summary>
    internal static void ValidateMinutes(JsonNode? estimated, JsonNode? maximum,
        Func<string, string> label, Func<string, string, string> exceedsMessage)
    {
        foreach (var (name, value) in new[] { ("EstimatedMinutes", estimated), ("MaximumMinutes", maximum) })
        {
            if (value is null) continue;
            var text = WorkbenchJson.AsText(value);
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes < 1)
                throw new InvalidOperationException($"{label(name)} must be a positive whole number of minutes (got '{text}').");
        }
        if (estimated is not null && maximum is not null
            && WorkbenchJson.AsInt(estimated) > WorkbenchJson.AsInt(maximum))
            throw new InvalidOperationException(exceedsMessage(WorkbenchJson.AsText(estimated)!, WorkbenchJson.AsText(maximum)!));
    }

    /// <summary>The entries of a value that may be an array, a single item or null.</summary>
    internal static IEnumerable<JsonNode?> Items(JsonNode? node) => node switch
    {
        null => [],
        JsonArray array => array,
        _ => [node]
    };

    private static bool IsOneOf(JsonNode value, string[] allowed) =>
        allowed.Contains(WorkbenchJson.AsText(value), StringComparer.OrdinalIgnoreCase);
}
