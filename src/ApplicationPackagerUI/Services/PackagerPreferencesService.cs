using System.IO;
using System.Text.Json;
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
    /// Writes CompanyName from app settings to packager-preferences.json
    /// in the Packagers folder (for PS1 scripts to read).
    /// </summary>
    public void SyncCompanyName(string companyName)
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
            File.WriteAllText(path, JsonSerializer.Serialize(prefs, JsonOptions));
        }
        catch { }
    }

    /// <summary>
    /// Writes CWA install settings to citrix-workspace-switches.json
    /// in the Packagers folder (for PS1 Citrix packager scripts to read).
    /// </summary>
    public void SyncCwaSettings(CwaInstallSettings cwa)
    {
        var root = discoveryService.ResolvePackagersRoot();
        if (root is null) return;

        var path = Path.Combine(root, "citrix-workspace-switches.json");
        try
        {
            var json = JsonSerializer.Serialize(cwa, JsonOptions);
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

        // Seed CompanyName from packager-preferences.json
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

        // Seed from old AppPackager.preferences.json (PS1 GUI prefs)
        // Look in the parent of Packagers folder (where start-apppackager.ps1 lives)
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
                    if (oldPrefs.TryGetValue("DownloadRoot", out var dr))
                        settings.DownloadRoot = dr.GetString() ?? settings.DownloadRoot;
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
