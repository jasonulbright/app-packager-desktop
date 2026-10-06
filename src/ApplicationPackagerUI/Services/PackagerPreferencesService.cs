using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Syncs settings between the C# app's settings.json and the PS1 packager JSON files:
/// - packager-preferences.json (CompanyName, read by AppPackagerCommon.psm1)
/// - citrix-workspace-switches.json (CWA install params, read by package-citrix*.ps1)
/// </summary>
public class PackagerPreferencesService(SettingsService settingsService, PackagerDiscoveryService discoveryService)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Writes CompanyName and the chosen side files (BeyondCompareKeyFile and
    /// the like, under the same key names) from app settings to
    /// packager-preferences.json in the Packagers folder (for PS1 scripts to read).
    /// </summary>
    public void SyncPackagerPreferences(string companyName, IReadOnlyDictionary<string, string>? sideFiles = null)
    {
        var root = discoveryService.ResolvePackagersRoot();
        if (root is null) return;

        var path = Path.Combine(root, "packager-preferences.json");
        try
        {
            Dictionary<string, object> prefs = [];
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path), JsonOptions);
                if (existing is not null) prefs = existing;
            }
            prefs["CompanyName"] = companyName;
            foreach (var (key, value) in sideFiles ?? new Dictionary<string, string>())
                prefs[key] = value;
            File.WriteAllText(path, JsonSerializer.Serialize(prefs, JsonOptions));
        }
        catch { }
    }

    /// <summary>
    /// Writes the Citrix Workspace switch settings to citrix-workspace-switches.json
    /// in the Packagers folder, in the nested shape the PS1 Citrix packagers read.
    /// A setting left unset is written as null, which the packagers skip.
    /// </summary>
    public void SyncCwaSettings(CitrixWorkspaceSwitches? switches)
    {
        var root = discoveryService.ResolvePackagersRoot();
        if (root is null) return;

        var path = Path.Combine(root, "citrix-workspace-switches.json");
        try
        {
            var json = JsonSerializer.Serialize(switches ?? new CitrixWorkspaceSwitches(), JsonOptions);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    /// <summary>
    /// On first launch, seeds app settings from existing PS1 preference files.
    /// Returns true if migration occurred.
    /// </summary>
    public bool MigrateFromPs1IfNeeded()
    {
        var settings = settingsService.Load();

        // Only migrate if CompanyName is empty (first launch indicator)
        if (!string.IsNullOrWhiteSpace(settings.CompanyName))
            return false;

        var root = discoveryService.ResolvePackagersRoot();
        if (root is null) return false;

        var migrated = false;

        var prefsPath = Path.Combine(root, "packager-preferences.json");
        if (File.Exists(prefsPath))
        {
            try
            {
                var prefs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(prefsPath), JsonOptions);
                if (prefs is not null && prefs.TryGetValue("CompanyName", out var cn))
                {
                    var val = cn.GetString();
                    if (!string.IsNullOrWhiteSpace(val) && val != "ReplaceMe")
                    {
                        settings.CompanyName = val;
                        migrated = true;
                    }
                }
            }
            catch { }
        }

        // AppPackager.preferences.json sits in the parent of the Packagers folder.
        var oldPrefsPath = Path.Combine(Path.GetDirectoryName(root) ?? root, "AppPackager.preferences.json");
        if (File.Exists(oldPrefsPath))
        {
            try
            {
                var oldPrefs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(oldPrefsPath), JsonOptions);
                if (oldPrefs is not null)
                {
                    if (oldPrefs.TryGetValue("SiteCode", out var sc))
                        settings.SiteCode = sc.GetString() ?? settings.SiteCode;
                    if (oldPrefs.TryGetValue("FileShareRoot", out var fsr))
                        settings.FileShareRoot = fsr.GetString() ?? settings.FileShareRoot;
                    // The PS1 default stays behind: the settings already hold the per-user staging folder.
                    if (oldPrefs.TryGetValue("DownloadRoot", out var dr) && dr.GetString() is { } importedRoot
                        && !ApplicationPackagerEngine.Services.AppDataPaths.IsLegacyStagingRoot(importedRoot))
                        settings.DownloadRoot = importedRoot;
                    if (oldPrefs.TryGetValue("EstimatedRuntimeMins", out var ert))
                        settings.EstimatedRuntimeMins = ert.TryGetInt32(out var v) ? v : settings.EstimatedRuntimeMins;
                    if (oldPrefs.TryGetValue("MaximumRuntimeMins", out var mrt))
                        settings.MaximumRuntimeMins = mrt.TryGetInt32(out var v2) ? v2 : settings.MaximumRuntimeMins;
                    if (oldPrefs.TryGetValue("M365Channel", out var mc))
                        settings.M365Channel = mc.GetString() ?? settings.M365Channel;
                    if (oldPrefs.TryGetValue("HiddenApplications", out var ha) && ha.ValueKind == JsonValueKind.Array)
                        settings.HiddenApplications = ha.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray();
                    migrated = true;
                }
            }
            catch { }
        }

        if (migrated)
            settingsService.Save(settings);

        return migrated;
    }
}
