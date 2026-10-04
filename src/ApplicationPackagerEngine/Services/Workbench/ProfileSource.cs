using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Which source a grid Stage or Publish of a JSON definition builds from: the active
/// workbench profile, or the legacy per-definition DeploymentConditions and
/// CommandOverrides maps of the settings. One source applies, never both.
/// <list type="table">
/// <item>Non-default profile active (Migrated or an operator profile): a run snapshot of it; the maps do not apply.</item>
/// <item>Default profile active and a profile migrated from this definition exists: the operator chose the default
/// after migration; no snapshot and the maps do not apply.</item>
/// <item>Default profile active and no migrated profile (no store entry, migration not yet run, or the migrated
/// profile was deleted): no snapshot and the maps apply.</item>
/// <item>The store cannot be opened or read: <see cref="Failure"/> carries the reason and the run does not start,
/// because the active profile it would build from is unknown.</item>
/// </list>
/// </summary>
public sealed record ProfileSource(string ApplicationId, string? ActiveProfileId, bool LegacyMapsApply)
{
    /// <summary>Why the workbench store could not be read; null when it was read.</summary>
    public string? Failure { get; init; }

    /// <summary>Resolves the source for definition <paramref name="definitionId"/>; a null store is one that cannot be opened.</summary>
    public static ProfileSource Resolve(WorkbenchStore? store, string definitionId)
    {
        string applicationId;
        try { applicationId = ApplicationIds.New(ApplicationOrigin.Catalog, name: definitionId); }
        catch (ArgumentException) { return new ProfileSource("", null, true); }
        if (!ApplicationIds.IsValid(applicationId)) return new ProfileSource(applicationId, null, true);
        if (store is null)
            return new ProfileSource(applicationId, null, true) { Failure = "the workbench store could not be opened" };

        try
        {
            var active = store.GetApplicationDefinition(applicationId).ActiveProfileId;
            if (!string.IsNullOrWhiteSpace(active) && !ApplicationIds.IsDefaultProfile(active))
                return new ProfileSource(applicationId, active, false);
            return new ProfileSource(applicationId, null, !HasMigratedProfile(store, applicationId, definitionId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or JsonException)
        {
            return new ProfileSource(applicationId, null, true) { Failure = ex.Message };
        }
    }

    /// <summary>The refusal a run reports when <see cref="Failure"/> is set.</summary>
    public string RefusalMessage() =>
        $"The workbench store could not be read for {ApplicationId}: {Failure}. The run did not start; repair or restore the store, then run again.";

    /// <summary>True when a stored profile of the application carries the MigratedFrom marker for the legacy key.</summary>
    public static bool HasMigratedProfile(WorkbenchStore store, string applicationId, string legacyKey)
    {
        foreach (var candidate in store.GetProfiles(applicationId))
        {
            if (candidate.IsDefault) continue;
            var stored = store.GetProfile(applicationId, candidate.ProfileId);
            if (string.Equals(stored.Get("MigratedFrom").Text, legacyKey, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the legacy maps apply to a staged manifest: the source leaves them in
    /// force and the manifest is not the build of a non-default profile, which already
    /// carries that profile's conditions and commands.
    /// </summary>
    public static bool LegacyMapsApplyTo(StageManifest manifest, ProfileSource source) =>
        source.LegacyMapsApply
        && !(StageFinalizer.CarriesBuild(manifest) && !ApplicationIds.IsDefaultProfile(manifest.ProfileId ?? ApplicationIds.DefaultProfileId));
}
