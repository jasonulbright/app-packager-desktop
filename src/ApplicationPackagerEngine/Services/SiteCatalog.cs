using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services;

/// <summary>One known site: its folder name (AB1 or AB1~2), its code, its label and its providers (site.json).</summary>
public sealed record SiteInfo(string FolderName, string SiteCode, string Label, IReadOnlyList<string> Providers, string LastUsedUtc)
{
    /// <summary>The first provider, the default one, or an empty text.</summary>
    public string DefaultProvider => Providers.Count > 0 ? Providers[0] : "";

    /// <summary>The text of the switcher list: "AB1", the label and the provider.</summary>
    public string DisplayName
    {
        get
        {
            var sb = new StringBuilder(FolderName);
            if (Label.Length > 0) sb.Append(" - ").Append(Label);
            if (DefaultProvider.Length > 0) sb.Append(" (").Append(DefaultProvider).Append(')');
            return sb.ToString();
        }
    }
}

/// <summary>What a site code that the user entered means for the known sites.</summary>
public enum SiteMatchKind
{
    /// <summary>No folder exists for the code. A new site is created.</summary>
    New,
    /// <summary>A folder exists and lists the provider (or no provider was entered). It is used as it is.</summary>
    Existing,
    /// <summary>A folder exists for the code and does not list the provider. The user decides: same site, other provider, or a different environment.</summary>
    NeedsAnswer,
}

/// <summary>The answer to a reused site code (B9 question 1).</summary>
public enum SiteReuseAnswer
{
    SameSiteOtherProvider,
    DifferentEnvironment,
    Cancel,
}

public sealed record SiteMatch(SiteMatchKind Kind, SiteInfo? Existing);

/// <summary>
/// The sites of the data root: reads and writes site.json and active-site.json (contract schemaVersion 1). Creates a folder only for
/// a site that the user adds or that the first start finds. Every write goes through a temporary file and a rename.
/// </summary>
public static class SiteCatalog
{
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string SitesFolder(string? dataRoot) => Path.Combine(dataRoot ?? SiteContext.DataRoot, "sites");

    /// <summary>Every site folder with a valid name, in folder order. A folder without a readable site.json is listed with its code.</summary>
    public static IReadOnlyList<SiteInfo> List(string? dataRoot = null)
    {
        var sites = SitesFolder(dataRoot);
        if (!Directory.Exists(sites)) return [];
        var result = new List<SiteInfo>();
        foreach (var dir in Directory.EnumerateDirectories(sites).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (!SiteDataPaths.TryParseFolderName(name, out var parsed) || parsed is null) continue;
            result.Add(Read(parsed, dir));
        }
        return result;
    }

    public static SiteInfo? Get(string folderName, string? dataRoot = null) =>
        SiteDataPaths.TryParseFolderName(folderName, out var parsed) && parsed is not null && Directory.Exists(SiteDataPaths.GetSiteFolder(parsed.FolderName, dataRoot: dataRoot ?? SiteContext.DataRoot))
            ? Read(parsed, SiteDataPaths.GetSiteFolder(parsed.FolderName, dataRoot: dataRoot ?? SiteContext.DataRoot))
            : null;

    private static SiteInfo Read(SiteFolderName parsed, string folder)
    {
        var json = SiteSettingsSplit.ReadObject(Path.Combine(folder, "site.json"));
        var label = json?["label"] is JsonValue l && l.TryGetValue<string>(out var ls) ? ls : "";
        var last = json?["lastUsedUtc"] is JsonValue u && u.TryGetValue<string>(out var us) ? us : "";
        var providers = new List<string>();
        if (json?["providers"] is JsonArray arr)
            foreach (var p in arr)
                if (p is JsonValue pv && pv.TryGetValue<string>(out var ps) && !string.IsNullOrWhiteSpace(ps)) providers.Add(ps.Trim());
        return new SiteInfo(parsed.FolderName, parsed.SiteCode, label, providers, last);
    }

    /// <summary>Decides what a code and a provider mean for the known sites. Provider names compare without regard to case.</summary>
    public static SiteMatch Match(string siteCode, string? provider, string? dataRoot = null)
    {
        var code = siteCode.Trim().ToUpperInvariant();
        var existing = List(dataRoot).Where(s => string.Equals(s.SiteCode, code, StringComparison.Ordinal)).ToList();
        if (existing.Count == 0) return new SiteMatch(SiteMatchKind.New, null);
        var wanted = provider?.Trim() ?? "";
        if (wanted.Length == 0) return new SiteMatch(SiteMatchKind.Existing, existing[0]);
        var listed = existing.FirstOrDefault(s => s.Providers.Contains(wanted, StringComparer.OrdinalIgnoreCase));
        if (listed is not null) return new SiteMatch(SiteMatchKind.Existing, listed);
        // A site folder that lists no provider yet takes the first one without a question.
        var empty = existing.FirstOrDefault(s => s.Providers.Count == 0);
        return empty is not null ? new SiteMatch(SiteMatchKind.Existing, empty) : new SiteMatch(SiteMatchKind.NeedsAnswer, existing[0]);
    }

    /// <summary>
    /// Creates a site folder for a code with the first free name (AB1, AB1~2, ...) and writes site.json. Throws ArgumentException for an invalid code.
    /// </summary>
    public static SiteInfo Create(string siteCode, string? provider, string? label = null, string? dataRoot = null)
    {
        if (!SiteDataPaths.IsValidSiteCode(siteCode)) throw new ArgumentException($"Invalid site code: '{siteCode}'.", nameof(siteCode));
        var root = dataRoot ?? SiteContext.DataRoot;
        var name = SiteDataPaths.NextFreeFolderName(siteCode, List(root).Select(s => s.FolderName));
        var folder = SiteDataPaths.GetSiteFolder(name, dataRoot: root);
        Directory.CreateDirectory(folder);
        var now = Now();
        var node = new JsonObject
        {
            ["schemaVersion"] = SiteDataPaths.SchemaVersion,
            ["siteCode"] = siteCode.ToUpperInvariant(),
            ["providers"] = string.IsNullOrWhiteSpace(provider) ? new JsonArray() : new JsonArray(provider.Trim()),
            ["createdUtc"] = now,
            ["lastUsedUtc"] = now,
        };
        if (!string.IsNullOrWhiteSpace(label)) node["label"] = label.Trim();
        WriteJson(Path.Combine(folder, "site.json"), node);
        return Get(name, root)!;
    }

    /// <summary>Adds a provider to site.json when it is not listed. Keeps every other value. Returns the folder's site.</summary>
    public static SiteInfo AddProvider(string folderName, string provider, string? dataRoot = null)
    {
        var root = dataRoot ?? SiteContext.DataRoot;
        var folder = SiteDataPaths.GetSiteFolder(folderName, dataRoot: root);
        var path = Path.Combine(folder, "site.json");
        var node = SiteSettingsSplit.ReadObject(path);
        if (node is null)
        {
            // A folder without a readable site.json: write one for the folder's code.
            SiteDataPaths.TryParseFolderName(folderName, out var parsed);
            var now = Now();
            node = new JsonObject
            {
                ["schemaVersion"] = SiteDataPaths.SchemaVersion,
                ["siteCode"] = parsed!.SiteCode,
                ["providers"] = new JsonArray(),
                ["createdUtc"] = now,
                ["lastUsedUtc"] = now,
            };
        }
        var providers = node["providers"] as JsonArray ?? new JsonArray();
        var wanted = provider.Trim();
        if (wanted.Length > 0 && !providers.Any(p => p is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase)))
            providers.Add(wanted);
        node["providers"] = providers;
        WriteJson(path, node);
        return Get(folderName, root)!;
    }

    /// <summary>Sets lastUsedUtc to now.</summary>
    public static void Touch(string folderName, string? dataRoot = null)
    {
        var path = Path.Combine(SiteDataPaths.GetSiteFolder(folderName, dataRoot: dataRoot ?? SiteContext.DataRoot), "site.json");
        if (SiteSettingsSplit.ReadObject(path) is not { } node) return;
        node["lastUsedUtc"] = Now();
        WriteJson(path, node);
    }

    /// <summary>Writes active-site.json: the site that the user used last.</summary>
    public static void WriteActiveSite(string folderName, string? dataRoot = null)
    {
        if (!SiteDataPaths.IsValidFolderName(folderName)) throw new ArgumentException($"Invalid site folder name: '{folderName}'.", nameof(folderName));
        var root = dataRoot ?? SiteContext.DataRoot;
        Directory.CreateDirectory(root);
        WriteJson(Path.Combine(root, SiteDataPaths.ActiveSiteFileName), new JsonObject
        {
            ["schemaVersion"] = SiteDataPaths.SchemaVersion,
            ["siteCode"] = folderName,
            ["changedUtc"] = Now(),
        });
    }

    private static void WriteJson(string path, JsonObject node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
