using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services;

/// <summary>A site file that passed every check. The settings hold only allowlisted keys and no secret name.</summary>
public sealed record SiteFileContent(
    string SiteCode, string Label, string CreatedUtc, string CreatedBy, JsonObject Settings, string SettingsSha256, IReadOnlyList<string> ItemIds);

public sealed record SiteFileReadResult(bool Ok, string Reason, SiteFileContent? Content)
{
    public static SiteFileReadResult Refuse(string reason) => new(false, reason, null);
}

public sealed record SiteFileExportResult(bool Ok, string Reason, string Path, string Sha256, int ItemCount, long Bytes);

/// <summary>
/// Export and import of a site file between machines (Q-112h). The file is one zip with three fixed entries: manifest.json (kind, format
/// version, site code, label, and the SHA-256 and size of the other entries), settings.json and scope.json. Settings.json holds only the
/// keys of the Q-112e import items (an allowlist, not a denylist): secrets, tokens, credentials, the site code value and every record
/// that describes a site (results, states, ring plans, audit) are never exported because no item names them. Import never extracts to
/// disk: it reads the three entries in memory with a size limit, refuses any other entry name (that covers a path with .., a drive, a
/// separator or an absolute path), checks the hash of each entry, the site code rules and the allowlist, and only then hands the
/// settings to <see cref="SiteSettingsImport"/>, which keeps an existing value unless the user chooses Replace.
/// </summary>
public static class SiteFile
{
    public const string Kind = "app-packager-site-file";
    public const int FormatVersion = 1;
    public const string ManifestName = "manifest.json";
    public const string SettingsName = "settings.json";
    public const string ScopeName = "scope.json";
    public const string Extension = ".zip";
    /// <summary>The largest file and the largest entry that import reads. A site file is a few kilobytes.</summary>
    public const long MaxFileBytes = 1_000_000;
    public const int MaxEntryBytes = 500_000;
    public const int MaxLabelLength = 80;

    private static readonly string[] EntryNames = [ManifestName, SettingsName, ScopeName];

    /// <summary>The proposed file name for a site: <c>site-AB1-20261123.zip</c>.</summary>
    public static string SuggestedFileName(string folderName, DateTime utcNow) =>
        $"site-{folderName.Replace('~', '-')}-{utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Extension}";

    // ---- export ----

    public static SiteFileExportResult Export(string folderName, string destination, string? dataRoot = null, DateTime? utcNow = null, string createdBy = "Application Packager Desktop")
    {
        var root = dataRoot ?? SiteContext.DataRoot;
        if (!SiteDataPaths.TryParseFolderName(folderName, out var parsed) || parsed is null)
            return Fail($"'{folderName}' is not a site folder name.");
        var folder = SiteDataPaths.GetSiteFolder(parsed.FolderName, dataRoot: root);
        if (!Directory.Exists(folder)) return Fail($"The site {parsed.FolderName} does not exist.");
        if (string.IsNullOrWhiteSpace(destination)) return Fail("Choose where to save the site file.");
        var settingsPath = SiteSettingsSplit.SettingsPathOf(folder);
        JsonObject source = new();
        if (File.Exists(settingsPath))
        {
            if (SiteSettingsSplit.ReadObject(settingsPath) is not { } read) return Fail($"The settings of site {parsed.FolderName} cannot be read. Nothing was exported.");
            source = read;
        }

        // Allowlist: only the keys of the import items are copied into the file, each one cleaned of any property with a secret-like name.
        var settings = new JsonObject();
        var ids = new List<string>();
        foreach (var item in SiteSettingsImport.Items)
        {
            var any = false;
            foreach (var key in item.Keys)
            {
                var value = SiteSettingsImport.Sanitized(SiteSettingsImport.Get(source, key), key, []);
                if (SiteSettingsImport.IsEmpty(value)) continue;
                SiteSettingsImport.Set(settings, key, value!);
                any = true;
            }
            if (any) ids.Add(item.Id);
        }

        var info = SiteCatalog.Get(parsed.FolderName, root);
        var label = CleanLabel(info?.Label);
        var now = (utcNow ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var settingsBytes = Json(settings);
        var scopeBytes = Json(BuildScope(ids));
        var entries = new JsonArray
        {
            EntryInfo(SettingsName, settingsBytes),
            EntryInfo(ScopeName, scopeBytes),
        };
        var manifest = new JsonObject
        {
            ["kind"] = Kind,
            ["formatVersion"] = FormatVersion,
            ["schemaVersion"] = SiteDataPaths.SchemaVersion,
            ["siteCode"] = parsed.SiteCode,
            ["label"] = label,
            ["createdUtc"] = now,
            ["createdBy"] = createdBy,
            ["items"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
            ["entries"] = entries,
        };
        var manifestBytes = Json(manifest);

        var temp = destination + ".exporting";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(temp)) File.Delete(temp);
            var stamp = new DateTimeOffset(utcNow ?? DateTime.UtcNow, TimeSpan.Zero);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add(zip, ManifestName, manifestBytes, stamp);
                Add(zip, SettingsName, settingsBytes, stamp);
                Add(zip, ScopeName, scopeBytes, stamp);
            }
            // The file is read back with the import rules before it replaces the destination.
            var check = Read(temp);
            if (!check.Ok) throw new IOException("The new site file did not verify: " + check.Reason);
            File.Move(temp, destination, overwrite: true);
            return new SiteFileExportResult(true, "", destination, SiteDataMigration.Hash(destination), ids.Count, new FileInfo(destination).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return Fail(ex.Message);
        }

        static SiteFileExportResult Fail(string reason) => new(false, reason, "", "", 0, 0);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes, DateTimeOffset stamp)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = stamp < new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : stamp;
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private static JsonObject EntryInfo(string name, byte[] bytes) => new()
    {
        ["name"] = name,
        ["sha256"] = HashBytes(bytes),
        ["bytes"] = bytes.Length,
    };

    private static JsonObject BuildScope(IReadOnlyList<string> ids)
    {
        var items = new JsonArray();
        foreach (var item in SiteSettingsImport.Items)
            items.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["keys"] = new JsonArray(item.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                ["environmentValue"] = item.EnvironmentValue,
                ["present"] = ids.Contains(item.Id),
            });
        var never = new JsonArray();
        foreach (var n in SiteSettingsImport.NeverCopied) never.Add(n.ManifestId);
        return new JsonObject { ["items"] = items, ["neverExported"] = never };
    }

    private static string CleanLabel(string? label)
    {
        var text = new string((label ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length > MaxLabelLength ? text[..MaxLabelLength] : text;
    }

    private static byte[] Json(JsonNode node) =>
        new UTF8Encoding(false).GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ---- import: read and validate ----

    /// <summary>Reads a site file in memory and checks every rule. Nothing is written. A refused file has a reason that names the rule.</summary>
    public static SiteFileReadResult Read(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return SiteFileReadResult.Refuse("The site file does not exist.");
            var length = new FileInfo(path).Length;
            if (length == 0) return SiteFileReadResult.Refuse("The site file is empty.");
            if (length > MaxFileBytes) return SiteFileReadResult.Refuse($"The file is larger than {MaxFileBytes / 1000} KB. A site file is a few kilobytes; this is not a site file.");

            Dictionary<string, byte[]> parts;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > EntryNames.Length)
                    return SiteFileReadResult.Refuse($"The file holds {zip.Entries.Count} entries; a site file holds exactly {EntryNames.Length}.");
                parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName;
                    if (name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.StartsWith('~'))
                        return SiteFileReadResult.Refuse($"The entry name '{Show(name)}' is a path, not a plain file name. The file was refused and nothing was written.");
                    if (!EntryNames.Contains(name, StringComparer.Ordinal))
                        return SiteFileReadResult.Refuse($"The entry '{Show(name)}' is not part of a site file. The file was refused.");
                    if (parts.ContainsKey(name))
                        return SiteFileReadResult.Refuse($"The entry '{name}' appears twice. The file was refused.");
                    if (entry.Length > MaxEntryBytes)
                        return SiteFileReadResult.Refuse($"The entry '{name}' is larger than {MaxEntryBytes / 1000} KB. The file was refused.");
                    // The declared size can lie: read with a hard limit.
                    using var es = entry.Open();
                    using var ms = new MemoryStream();
                    var buffer = new byte[8192];
                    int n;
                    while ((n = es.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (ms.Length + n > MaxEntryBytes)
                            return SiteFileReadResult.Refuse($"The entry '{name}' is larger than {MaxEntryBytes / 1000} KB when unpacked. The file was refused.");
                        ms.Write(buffer, 0, n);
                    }
                    parts[name] = ms.ToArray();
                }
            }
            foreach (var required in EntryNames)
                if (!parts.ContainsKey(required)) return SiteFileReadResult.Refuse($"The entry '{required}' is missing. This is not a complete site file.");

            if (ParseObject(parts[ManifestName]) is not { } manifest) return SiteFileReadResult.Refuse("manifest.json is not a JSON object.");
            if (Text(manifest, "kind") != Kind) return SiteFileReadResult.Refuse("manifest.json is not the manifest of a site file (kind).");
            if (manifest["formatVersion"] is not JsonValue fv || !fv.TryGetValue<int>(out var version))
                return SiteFileReadResult.Refuse("manifest.json has no format version.");
            if (version > FormatVersion) return SiteFileReadResult.Refuse($"The file has format version {version}. This version of the application reads version {FormatVersion}. Update the application.");
            if (version < 1) return SiteFileReadResult.Refuse($"The file has format version {version}, which is not valid.");

            var code = Text(manifest, "siteCode");
            if (!SiteDataPaths.IsValidSiteCode(code) || code != code.ToUpperInvariant())
                return SiteFileReadResult.Refuse(string.IsNullOrEmpty(code)
                    ? "manifest.json has no site code."
                    : $"The site code '{Show(code)}' in the file is not valid: use exactly three letters or digits, and not a reserved name.");

            // Hash and size of each entry.
            if (manifest["entries"] is not JsonArray list) return SiteFileReadResult.Refuse("manifest.json lists no entries.");
            var listed = new Dictionary<string, (string Hash, long Bytes)>(StringComparer.Ordinal);
            foreach (var node in list)
            {
                if (node is not JsonObject e) return SiteFileReadResult.Refuse("manifest.json has an entry that is not an object.");
                var name = Text(e, "name");
                if (name is not (SettingsName or ScopeName) || listed.ContainsKey(name))
                    return SiteFileReadResult.Refuse($"manifest.json lists the entry '{Show(name)}', which a site file does not have, or lists an entry twice.");
                listed[name] = (Text(e, "sha256").ToLowerInvariant(), e["bytes"] is JsonValue b && b.TryGetValue<long>(out var bytes) ? bytes : -1);
            }
            foreach (var name in new[] { SettingsName, ScopeName })
            {
                if (!listed.TryGetValue(name, out var expected)) return SiteFileReadResult.Refuse($"manifest.json has no hash for {name}.");
                if (expected.Hash.Length != 64 || !expected.Hash.All(Uri.IsHexDigit))
                    return SiteFileReadResult.Refuse($"The hash of {name} in manifest.json is not a SHA-256 value.");
                if (HashBytes(parts[name]) != expected.Hash)
                    return SiteFileReadResult.Refuse($"The SHA-256 of {name} does not match manifest.json. The file was changed or damaged and was refused.");
                if (expected.Bytes != parts[name].Length)
                    return SiteFileReadResult.Refuse($"The size of {name} does not match manifest.json. The file was refused.");
            }

            // Allowlist: the settings hold the keys of the import items and nothing else, and no secret-like name.
            if (ParseObject(parts[SettingsName]) is not { } raw) return SiteFileReadResult.Refuse("settings.json is not a JSON object.");
            var allowedTop = SiteSettingsImport.Items.SelectMany(i => i.Keys).Select(k => k.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
            var stray = raw.Select(p => p.Key).Where(k => !allowedTop.Contains(k)).ToList();
            if (stray.Count > 0) return SiteFileReadResult.Refuse($"settings.json holds a setting that a site file does not carry ({Show(stray[0])}). The file was refused.");
            var clean = new JsonObject();
            var ids = new List<string>();
            var dropped = new List<string>();
            foreach (var item in SiteSettingsImport.Items)
            {
                var any = false;
                foreach (var key in item.Keys)
                {
                    var value = SiteSettingsImport.Sanitized(SiteSettingsImport.Get(raw, key), key, dropped);
                    if (SiteSettingsImport.IsEmpty(value)) continue;
                    SiteSettingsImport.Set(clean, key, value!);
                    any = true;
                }
                if (any) ids.Add(item.Id);
            }
            if (dropped.Count > 0) return SiteFileReadResult.Refuse($"settings.json holds a value with a secret-like name ({Show(dropped[0])}). A site file never carries secrets; the file was refused.");
            // A sub-key that no item names (a key next to an allowed one) is also stray: the cleaned copy must equal the raw one.
            if (SiteDataMigration.Canonical(clean) != SiteDataMigration.Canonical(raw))
                return SiteFileReadResult.Refuse("settings.json holds a setting that a site file does not carry. The file was refused.");

            if (ParseObject(parts[ScopeName]) is not { } scope || scope["items"] is not JsonArray scopeItems)
                return SiteFileReadResult.Refuse("scope.json is not a scope manifest.");
            var known = SiteSettingsImport.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var node in scopeItems)
                if (node is not JsonObject so || !known.Contains(Text(so, "id")))
                    return SiteFileReadResult.Refuse("scope.json names an item that this version does not know. The file was refused.");

            return new SiteFileReadResult(true, "", new SiteFileContent(
                code, CleanLabel(Text(manifest, "label")), Text(manifest, "createdUtc"), Show(Text(manifest, "createdBy")), clean, listed[SettingsName].Hash, ids));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return SiteFileReadResult.Refuse(ex is InvalidDataException ? "The file is not a valid zip file." : $"The file cannot be read ({ex.Message}).");
        }
    }

    private static JsonObject? ParseObject(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Text from a file for a message: control characters out, length limited.</summary>
    private static string Show(string text)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return clean.Length > 60 ? clean[..57] + "..." : clean;
    }

    // ---- import: the target ----

    /// <summary>
    /// The target folder names for a file: each existing site folder with the same code first (merge), then the name of a new folder
    /// that import would create (a new site, or a different environment when the code is already in use).
    /// </summary>
    public static string NewFolderNameFor(SiteFileContent content, string? dataRoot = null) =>
        SiteDataPaths.NextFreeFolderName(content.SiteCode, SiteCatalog.List(dataRoot).Select(s => s.FolderName));

    /// <summary>The request that plans or runs the import of a read file into a target folder (an existing site, or the new folder name).</summary>
    public static SiteImportRequest ImportRequest(
        SiteFileContent content, string targetFolder, IReadOnlyCollection<string>? selectedIds = null,
        IReadOnlyDictionary<string, SiteImportChoice>? choices = null, string? dataRoot = null, Func<DateTime>? utcNow = null, Action<string, string>? hook = null) =>
        new(content.SiteCode, targetFolder, selectedIds, choices, dataRoot, utcNow, hook,
            content.Settings, content.SettingsSha256, CreateTargetIfMissing: true, SourceLabel: content.Label);
}
