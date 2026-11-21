using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The Workbench profile fields that the Application Settings grid shows as columns: runtime and reboot behavior. A field keeps the semantics
/// of the Workbench: an empty value (Inherit) removes the key so the next level supplies the value, a value is a custom override. The grid
/// writes the profile of the application that carries the marker MigratedFrom with its definition identifier (the profile that the saved
/// conditions already use); a missing profile is created in the same shape, and it becomes the active profile only while the application is on
/// the default profile or on that profile, so a profile that the operator chose is never replaced.
/// </summary>
public static class GridProfileFields
{
    public const string EstimatedMinutes = "Timing.EstimatedMinutes";
    public const string MaximumMinutes = "Timing.MaximumMinutes";
    public const string InstallReboot = "Install.RebootPolicy";

    public const string Inherit = "Inherit";

    /// <summary>The words of the reboot policy that the stage accepts (see ProfileDeploymentSettings); the first one is Inherit.</summary>
    public static readonly IReadOnlyList<string> RebootChoices = [Inherit, "No reboot", "Soft reboot", "Hard reboot", "Force restart"];

    public static readonly IReadOnlyList<string> Paths = [EstimatedMinutes, MaximumMinutes, InstallReboot];

    /// <summary>The text of a stored field for the cell; empty when the profile does not set it.</summary>
    public static string Text(JsonNode? profileData, string path)
    {
        var member = WorkbenchJson.Get(profileData, path);
        return member.Found && member.Value is not null ? member.Text ?? "" : "";
    }

    /// <summary>Checks a value of a column: minutes are a whole number above zero or empty; the reboot policy is one of the choices.</summary>
    public static bool IsValid(string path, string value) => path switch
    {
        EstimatedMinutes or MaximumMinutes => value.Length == 0 || (int.TryParse(value, out var minutes) && minutes > 0),
        InstallReboot => RebootChoices.Contains(value),
        _ => false
    };

    /// <summary>Sets one field on a profile document; an empty value or Inherit removes the key.</summary>
    public static void Apply(WorkbenchProfile profile, string path, string value)
    {
        if (value.Length == 0 || value == Inherit) { profile.ClearField(path, FieldClearMode.Inherit); return; }
        if (path is EstimatedMinutes or MaximumMinutes) profile.Set(path, JsonValue.Create(int.Parse(value)));
        else profile.Set(path, JsonValue.Create(value));
    }

    /// <summary>The profile of the application that carries the grid fields (MigratedFrom is the definition identifier); null when there is none.</summary>
    public static WorkbenchProfile? Find(WorkbenchStore store, string applicationId, string definitionId)
    {
        foreach (var candidate in store.GetProfiles(applicationId))
        {
            if (candidate.IsDefault) continue;
            var stored = store.GetProfile(applicationId, candidate.ProfileId);
            if (string.Equals(stored.Get("MigratedFrom").Text, definitionId, StringComparison.OrdinalIgnoreCase)) return stored;
        }
        return null;
    }

    /// <summary>The stored fields of one application, and the name of the profile that is active instead of the one the grid writes (null when none).</summary>
    public sealed record State(IReadOnlyDictionary<string, string> Fields, string? ActiveElsewhere)
    {
        public static readonly State Empty = new(Paths.ToDictionary(p => p, _ => "", StringComparer.Ordinal), null);
    }

    /// <summary>The stored text of every grid field of one application (an absent field is empty), and whether another profile is active.</summary>
    public static State Read(WorkbenchStore store, string applicationId, string definitionId)
    {
        var profile = Find(store, applicationId, definitionId);
        var fields = Paths.ToDictionary(p => p, p => profile is null ? "" : Text(profile.Data, p), StringComparer.Ordinal);
        var active = store.GetApplicationDefinition(applicationId).ActiveProfileId;
        string? elsewhere = null;
        if (!string.IsNullOrWhiteSpace(active) && !ApplicationIds.IsDefaultProfile(active)
            && !string.Equals(active, profile?.ProfileId, StringComparison.OrdinalIgnoreCase))
            elsewhere = store.GetProfile(applicationId, active).Name;
        return new State(fields, elsewhere);
    }

    /// <summary>Writes the fields of one application. Returns the saved profile.</summary>
    public static WorkbenchProfile Write(WorkbenchStore store, string applicationId, string definitionId, IReadOnlyDictionary<string, string> fields)
    {
        var profile = Find(store, applicationId, definitionId)?.Clone();
        if (profile is null)
        {
            profile = WorkbenchProfile.CreateNew(applicationId, name: LegacyPreferenceMigration.MigratedProfileName);
            profile.Data["MigratedFrom"] = definitionId;
        }
        foreach (var (path, value) in fields) Apply(profile, path, value);
        var active = store.GetApplicationDefinition(applicationId).ActiveProfileId;
        var activate = string.IsNullOrWhiteSpace(active) || ApplicationIds.IsDefaultProfile(active)
            || string.Equals(active, profile.ProfileId, StringComparison.OrdinalIgnoreCase);
        return store.SaveProfile(profile, activate);
    }
}
