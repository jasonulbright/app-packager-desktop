using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The split of settings.json into the global file and the files of one site (Q-112d, scope manifest of the contract).
/// A load reads the global file and overlays the site files: a site-scoped key in the global file is ignored while a site is
/// active, so data of one site is never visible under another. A save writes the site keys to the site files and the rest to the
/// global file. The key lists equal the migration's lists (SiteDataMigration.ExtractSiteSettings); a test checks both.
/// Secrets and tokens are not site keys: they stay in the global file and never reach a site file.
/// </summary>
public static class SiteSettingsSplit
{
    public const string SettingsFileName = "settings.json";
    public const string AppStateFileName = "app-state.json";

    /// <summary>Top-level keys that belong to a site. SiteCode and ProviderMachine are also mirrored into the global file as the last selection.</summary>
    public static readonly IReadOnlyList<string> SiteKeys =
    [
        "SiteCode", "ProviderMachine", "DiscoverySqlServer", "DiscoverySqlDatabase", "FileShareRoot",
        "ContentDistribution", "DistributionOverrides", "Wsus", "SiteHygiene", "SiteHygieneLive",
    ];

    /// <summary>Site keys that the global file keeps as the last selection of the user.</summary>
    public static readonly IReadOnlyList<string> MirroredKeys = ["SiteCode", "ProviderMachine"];

    /// <summary>Keys of OneClick that belong to a site (the destination flags). The rest of OneClick is global.</summary>
    public static readonly IReadOnlyList<string> OneClickSiteKeys = ["Destinations", "DefaultDestinations"];

    /// <summary>Keys of one AppStates entry that belong to a site. LatestVersion stays global.</summary>
    public static readonly IReadOnlyList<string> AppStateSiteKeys = ["CurrentVersion", "LastStaged", "Published", "NotSupported"];

    /// <summary>Keys the site settings file may hold beyond the settings model (written by the migration).</summary>
    public static readonly IReadOnlyList<string> ExtraSiteFileKeys = ["CveMonitorSiteCode"];

    public static string SettingsPathOf(string siteFolder) => Path.Combine(siteFolder, SettingsFileName);

    public static string AppStatePathOf(string siteFolder) => Path.Combine(siteFolder, SiteContext.Areas.State, AppStateFileName);

    /// <summary>
    /// The text of a file, or null when it does not exist. A writer that replaces the file or holds it for a moment (another instance, the
    /// command-line build) is waited for up to two seconds before the IOException passes on.
    /// </summary>
    private static string? ReadTextWithRetry(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>Reads a JSON object file. Null when it does not exist or is not an object. Never throws for a damaged file.</summary>
    public static JsonObject? ReadObject(string path)
    {
        try
        {
            return ReadTextWithRetry(path) is { } text
                ? JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the file exists and cannot be read as a JSON object (damaged, locked or not an object).</summary>
    public static bool IsUnreadable(string path)
    {
        try
        {
            if (ReadTextWithRetry(path) is not { } text) return false;
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The settings of the active site: the global object with every site key replaced by the value of the site files.
    /// A site key that the site files lack is removed, so it reads as its default. Nothing of another site can remain.
    /// </summary>
    public static JsonObject Overlay(JsonObject global, JsonObject? site, JsonObject? appState, string siteCode, string? defaultProvider)
    {
        var result = (JsonObject)global.DeepClone();
        foreach (var key in SiteKeys) result.Remove(key);
        if (site is not null)
            foreach (var key in SiteKeys)
                if (site[key] is JsonNode value) result[key] = value.DeepClone();
        result["SiteCode"] = siteCode;
        if (!(result["ProviderMachine"] is JsonValue p && p.TryGetValue<string>(out var provider) && !string.IsNullOrWhiteSpace(provider)))
            result["ProviderMachine"] = defaultProvider ?? "";

        if (result["OneClick"] is JsonObject oneClick)
        {
            foreach (var key in OneClickSiteKeys) oneClick.Remove(key);
            if (site?["OneClick"] is JsonObject siteOneClick)
                foreach (var key in OneClickSiteKeys)
                    if (siteOneClick[key] is JsonNode value) oneClick[key] = value.DeepClone();
        }
        else if (site?["OneClick"] is JsonObject siteOnly)
        {
            var created = new JsonObject();
            foreach (var key in OneClickSiteKeys)
                if (siteOnly[key] is JsonNode value) created[key] = value.DeepClone();
            if (created.Count > 0) result["OneClick"] = created;
        }

        if (result["AppStates"] is JsonObject states)
        {
            foreach (var (_, state) in states)
                if (state is JsonObject entry)
                    foreach (var key in AppStateSiteKeys) entry.Remove(key);
        }
        if (appState is not null)
        {
            var states2 = result["AppStates"] as JsonObject ?? new JsonObject();
            foreach (var (id, node) in appState)
            {
                if (node is not JsonObject siteEntry) continue;
                var entry = states2[id] as JsonObject ?? new JsonObject();
                foreach (var key in AppStateSiteKeys)
                    if (siteEntry[key] is JsonNode value) entry[key] = value.DeepClone();
                states2[id] = entry;
            }
            result["AppStates"] = states2;
        }
        return result;
    }

    /// <summary>The three documents a save writes.</summary>
    public sealed record SplitResult(JsonObject Global, JsonObject Site, JsonObject? AppState);

    /// <summary>
    /// Splits the full settings of the active site. <paramref name="existingGlobal"/> supplies the site keys that earlier versions
    /// wrote there: they stay in the global file as they are (copy and keep), so a downgrade still finds them.
    /// <paramref name="existingSite"/> supplies keys of the site file that the settings model does not know (CveMonitorSiteCode).
    /// </summary>
    public static SplitResult Split(JsonObject full, JsonObject? existingGlobal, JsonObject? existingSite)
    {
        var site = existingSite is null ? new JsonObject() : (JsonObject)existingSite.DeepClone();
        foreach (var key in SiteKeys)
        {
            site.Remove(key);
            if (full[key] is JsonNode value) site[key] = value.DeepClone();
        }
        site.Remove("OneClick");
        if (full["OneClick"] is JsonObject oneClick)
        {
            var part = new JsonObject();
            foreach (var key in OneClickSiteKeys)
                if (oneClick[key] is JsonNode value) part[key] = value.DeepClone();
            if (part.Count > 0) site["OneClick"] = part;
        }

        JsonObject? appState = null;
        if (full["AppStates"] is JsonObject states)
        {
            foreach (var (id, node) in states)
            {
                if (node is not JsonObject entry) continue;
                var part = new JsonObject();
                foreach (var key in AppStateSiteKeys)
                    if (entry[key] is JsonNode value) part[key] = value.DeepClone();
                if (part.Count == 0) continue;
                appState ??= new JsonObject();
                appState[id] = part;
            }
        }

        var global = (JsonObject)full.DeepClone();
        foreach (var key in SiteKeys)
        {
            if (MirroredKeys.Contains(key)) continue;
            global.Remove(key);
            if (existingGlobal?[key] is JsonNode stale) global[key] = stale.DeepClone();
        }
        if (global["OneClick"] is JsonObject globalOneClick)
        {
            foreach (var key in OneClickSiteKeys)
            {
                globalOneClick.Remove(key);
                if (existingGlobal?["OneClick"] is JsonObject staleOneClick && staleOneClick[key] is JsonNode stale) globalOneClick[key] = stale.DeepClone();
            }
        }
        if (global["AppStates"] is JsonObject globalStates)
            foreach (var (_, node) in globalStates)
                if (node is JsonObject entry)
                    foreach (var key in AppStateSiteKeys) entry.Remove(key);
        return new SplitResult(global, site, appState);
    }
}
