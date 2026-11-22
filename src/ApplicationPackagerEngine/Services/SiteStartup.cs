using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services;

/// <summary>What the start of the app decided about the site layout.</summary>
/// <param name="ActiveFolder">The active site folder name, or null when the single-site layout stays in use.</param>
/// <param name="Source">Where the choice came from: an environment variable name, active-site.json, "settings" or "none".</param>
/// <param name="Migration">The migration report of this start, or null when no migration ran.</param>
/// <param name="Notice">A text for the user when the migration did not finish; null otherwise.</param>
public sealed record SiteStartupResult(string? ActiveFolder, string Source, SiteMigrationReport? Migration, string? Notice);

/// <summary>
/// The first step of the app, before any store is read (Q-112d, B4 and B6). It runs the migration of the single-site data once
/// (copy, verify by SHA-256, keep the old files), chooses the active site and makes it the process site.
/// The order of choice is the contract's: SUITE_CM_SITECODE, APP_PACKAGER_SITE, active-site.json, then the site code of settings.json.
/// A failed migration leaves the single-site layout in use: the old files stay the live files.
/// </summary>
public static class SiteStartup
{
    /// <summary>The result of the latest <see cref="Initialize"/> in this process, for the notice that the window shows.</summary>
    public static SiteStartupResult? Last { get; private set; }

    public static SiteStartupResult Initialize(string? settingsRoot = null, string? legacyRoot = null, string? dataRoot = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        var result = InitializeCore(settingsRoot, legacyRoot, dataRoot, getEnvironmentVariable);
        Last = result;
        return result;
    }

    private static SiteStartupResult InitializeCore(string? settingsRoot = null, string? legacyRoot = null, string? dataRoot = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        settingsRoot ??= AppDataPaths.Root;
        legacyRoot ??= AppDataPaths.LegacyRoot;
        dataRoot ??= SiteDataPaths.DataRoot;
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        SiteContext.UseDataRoot(dataRoot);

        var settings = SiteSettingsSplit.ReadObject(Path.Combine(settingsRoot, "settings.json"));
        var saved = settings?["SiteCodeSaved"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
        var legacyCode = settings?["SiteCode"] is JsonValue cv && cv.TryGetValue<string>(out var s) ? s : null;
        var legacyProvider = settings?["ProviderMachine"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : null;
        var hasLegacySite = saved && SiteDataPaths.IsValidSiteCode(legacyCode);

        // 1. Migration, once per site code. A marker with a completion time means done.
        SiteMigrationReport? migration = null;
        string? notice = null;
        var migrationFailed = false;
        if (hasLegacySite)
        {
            var markerPath = Path.Combine(SiteDataPaths.GetSiteFolder(legacyCode!.ToUpperInvariant(), dataRoot: dataRoot), SiteDataMigration.MarkerFileName);
            if (!MarkerComplete(markerPath))
            {
                migration = SiteDataMigration.Run(new SiteMigrationOptions { Root = settingsRoot, LegacyRoot = legacyRoot, DataRoot = dataRoot });
                if (migration.Outcome == "Failed")
                {
                    migrationFailed = true;
                    notice = "The data for the site could not be copied into the per-site layout. The earlier data stays in use and nothing was changed. "
                             + migration.Items.FirstOrDefault(i => i.Status == SiteMigrationStatus.Failed)?.Detail;
                }
                else if (migration.Items.Any(i => i.Status is SiteMigrationStatus.Conflict or SiteMigrationStatus.Blocked))
                {
                    notice = "Some data was not copied into the per-site layout because a file differs or is in use. Both copies are kept.";
                }
            }
        }
        if (migrationFailed)
        {
            SiteContext.Deactivate();
            return new SiteStartupResult(null, "none", migration, notice);
        }

        // 2. The active site.
        var active = SiteDataPaths.GetActiveSite(dataRoot, getEnvironmentVariable);
        string? folder = null;
        var source = "none";
        if (active is not null)
        {
            if (Directory.Exists(SiteDataPaths.GetSiteFolder(active.FolderName, dataRoot: dataRoot)))
            {
                folder = active.FolderName;
                source = active.Source;
            }
            else if (active.Source is SiteDataPaths.SuiteSiteVariable or SiteDataPaths.AppSiteVariable)
            {
                // The launcher names a site that has no folder yet: create it with the launcher's provider.
                SiteDataPaths.TryParseFolderName(active.FolderName, out var parsed);
                var provider = getEnvironmentVariable("SUITE_CM_PROVIDER")?.Trim();
                var created = SiteCatalog.Create(parsed!.SiteCode, provider, dataRoot: dataRoot);
                folder = created.FolderName;
                source = active.Source;
            }
        }
        if (folder is null && hasLegacySite)
        {
            var name = legacyCode!.ToUpperInvariant();
            if (Directory.Exists(SiteDataPaths.GetSiteFolder(name, dataRoot: dataRoot)))
            {
                folder = name;
                source = "settings";
            }
        }
        if (folder is null)
        {
            SiteContext.Deactivate();
            return new SiteStartupResult(null, "none", migration, notice);
        }

        SiteContext.Activate(folder, dataRoot);
        if (source == "settings")
        {
            // Tools that start directly read active-site.json; the first start writes it.
            try { SiteCatalog.WriteActiveSite(folder, dataRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // The provider of the single site is listed in site.json when the migration wrote it without one.
            if (!string.IsNullOrWhiteSpace(legacyProvider))
            {
                try { SiteCatalog.AddProvider(folder, legacyProvider!, dataRoot); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return new SiteStartupResult(folder, source, migration, notice);
    }

    private static bool MarkerComplete(string path)
    {
        var marker = SiteSettingsSplit.ReadObject(path);
        return marker?["completedUtc"] is JsonValue v && v.TryGetValue<string>(out var done) && !string.IsNullOrEmpty(done);
    }
}
