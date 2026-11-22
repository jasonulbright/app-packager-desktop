using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>Where a migrated item ends. One row of the report.</summary>
public static class SiteMigrationStatus
{
    /// <summary>Dry run: the item would be copied.</summary>
    public const string Planned = "Planned";
    /// <summary>The item was copied and its SHA-256 matched.</summary>
    public const string Copied = "Copied";
    /// <summary>The destination exists with the same content. Nothing changed.</summary>
    public const string AlreadyDone = "AlreadyDone";
    /// <summary>The destination exists with different content. Both stay. Nothing was written.</summary>
    public const string Conflict = "Conflict";
    /// <summary>The destination holds what an earlier run wrote, and the source changed since. Nothing was written.</summary>
    public const string SourceChanged = "SourceChanged";
    /// <summary>The source is not on this machine. Nothing to do.</summary>
    public const string NotPresent = "NotPresent";
    /// <summary>A global item. It stays where it is.</summary>
    public const string GlobalStays = "GlobalStays";
    /// <summary>A secret. It is never copied.</summary>
    public const string SecretNotCopied = "SecretNotCopied";
    /// <summary>The item cannot be copied now (database in use, link, invalid file). Nothing was written.</summary>
    public const string Blocked = "Blocked";
    /// <summary>The copy did not match its source hash, or an I/O error occurred. The run stopped.</summary>
    public const string Failed = "Failed";
}

public sealed record SiteMigrationItem(
    string Id,
    string Scope,
    string Source,
    string Destination,
    string Sha256,
    string Status,
    string Detail);

public sealed record SiteMigrationReport(
    bool DryRun,
    string? SiteCode,
    string? SiteFolder,
    string Outcome,
    string Reason,
    IReadOnlyList<SiteMigrationItem> Items)
{
    public int Count(string status) => Items.Count(i => i.Status == status);

    /// <summary>True for a run (not a dry run) that wrote no file.</summary>
    public bool ChangedNothing => !DryRun && Count(SiteMigrationStatus.Copied) == 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Site data migration ({(DryRun ? "dry run" : "run")}): {Outcome}");
        if (SiteCode is not null) sb.AppendLine($"Site: {SiteCode}  Folder: {SiteFolder}");
        if (Reason.Length > 0) sb.AppendLine(Reason);
        foreach (var i in Items)
            sb.AppendLine($"{i.Status,-15} {i.Scope,-6} {i.Id} | {i.Source} -> {(i.Destination.Length == 0 ? "-" : i.Destination)} | {(i.Sha256.Length == 0 ? "-" : i.Sha256)}{(i.Detail.Length == 0 ? "" : " | " + i.Detail)}");
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Inputs of a migration. The defaults are the live per-user locations.</summary>
public sealed class SiteMigrationOptions
{
    /// <summary>AppDataPaths.Root: settings.json, Deployments, SiteHygiene, DpContent and the others.</summary>
    public string Root { get; init; } = AppDataPaths.Root;
    /// <summary>AppDataPaths.LegacyRoot: history.db and the Check ConfigMgr result.</summary>
    public string LegacyRoot { get; init; } = AppDataPaths.LegacyRoot;
    /// <summary>The data root of the site layout (SiteDataPaths.DataRoot).</summary>
    public string DataRoot { get; init; } = SiteDataPaths.DataRoot;
    /// <summary>Test hook. Called with a stage ("temp-written", "item-done") and the destination path. A throw simulates a crash.</summary>
    public Action<string, string>? Hook { get; init; }
    /// <summary>Time source for site.json and the marker.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// Migration of the single-site data of APD into the per-site layout (Q-112c). Copies, never moves: every old file
/// stays and keeps its content. A copy is accepted only when its SHA-256 equals the source. A function call starts it;
/// no start-up code calls it and no store reads the new paths yet (Q-112d). Secrets are never copied.
/// </summary>
public static class SiteDataMigration
{
    public const string MarkerFileName = "migration.json";
    public const string LegacyMarker = "legacy-single-site";

    /// <summary>Scope manifest ids that the migration copies, by group. Tests check them against scope-manifest.json.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SiteGroups = new Dictionary<string, string[]>
    {
        ["settings"] = ["apd.site-code", "apd.provider", "apd.sql-server", "apd.sql-database", "apd.share-root", "apd.content-distribution",
            "apd.distribution-overrides", "apd.destinations", "apd.wsus", "apd.hygiene-options", "apd.monitor-config"],
        ["app-state"] = ["apd.app-state-current", "apd.app-state-published"],
        ["check-cm-result"] = ["apd.check-cm-result"],
        ["history-db"] = ["apd.inventory", "apd.site-operations"],
        ["deployments"] = ["apd.deployments"],
        ["hygiene"] = ["apd.hygiene-stores"],
        ["dpcontent"] = ["apd.dpcontent"],
        ["pivots"] = ["apd.pivots"],
        ["maintenance"] = ["apd.maintenance"],
        ["collections"] = ["apd.collections"],
    };

    /// <summary>Global manifest ids. They stay where they are.</summary>
    public static readonly IReadOnlyList<string> GlobalIds =
    [
        "apd.intune-tenant", "apd.intune-client", "apd.app-state-latest", "apd.one-click", "apd.app-settings", "apd.packaging-options",
        "apd.ui", "apd.deployment-templates", "apd.cve-cache", "apd.templates", "apd.logs",
    ];

    /// <summary>Secret manifest ids. They are never copied.</summary>
    public static readonly IReadOnlyList<string> SecretIds = ["apd.github-token", "apd.intune-secret", "apd.feed-credentials", "apd.nvd-key"];

    private static readonly Regex SecretName = new("token|secret|password|passwd|apikey|api_key|credential|protected|webhook", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The report of what a run would do. Writes nothing and creates no folder.</summary>
    public static SiteMigrationReport Plan(SiteMigrationOptions? options = null) => Execute(options ?? new SiteMigrationOptions(), dryRun: true);

    /// <summary>Copies the site items, verifies each by SHA-256 and writes the marker. Safe to run again and after a crash.</summary>
    public static SiteMigrationReport Run(SiteMigrationOptions? options = null) => Execute(options ?? new SiteMigrationOptions(), dryRun: false);

    /// <summary>One thing to copy. Content is set for a generated file. Owned is set for a shared JSON settings file: the top-level keys this migration owns.</summary>
    private sealed record Candidate(string Id, string Source, string Destination, byte[]? Content, JsonObject? Owned = null);

    private static SiteMigrationReport Execute(SiteMigrationOptions o, bool dryRun)
    {
        var items = new List<SiteMigrationItem>();
        var settingsPath = Path.Combine(o.Root, "settings.json");
        JsonObject? settings = null;
        if (!File.Exists(settingsPath))
            return Refuse(dryRun, "NothingMigrated", $"No site code is configured: {settingsPath} does not exist.");
        try
        {
            settings = JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        if (settings is null)
            return Refuse(dryRun, "NothingMigrated", "No site code is configured: settings.json is not a valid JSON object.");
        var saved = settings["SiteCodeSaved"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
        var code = settings["SiteCode"] is JsonValue cv && cv.TryGetValue<string>(out var s) ? s : null;
        if (!saved)
            return Refuse(dryRun, "NothingMigrated", "No site code is configured: the site code was never saved in settings.json. Nothing is migrated until the user chooses a code.");
        if (!SiteDataPaths.IsValidSiteCode(code))
            return Refuse(dryRun, "NothingMigrated", $"No site code is configured: '{code}' is not a valid site code.");

        var siteCode = code!.ToUpperInvariant();
        var siteFolder = SiteDataPaths.GetSiteFolder(siteCode, dataRoot: o.DataRoot);
        var marker = ReadMarker(Path.Combine(siteFolder, MarkerFileName));
        var failed = false;

        // 1. site.json: identity. Written once. An existing valid file stays as it is.
        var siteJson = Path.Combine(siteFolder, "site.json");
        var provider = settings["ProviderMachine"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : "";
        try { items.Add(HandleSiteJson(o, dryRun, siteCode, siteFolder, siteJson, provider)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SiteMigrationVerifyException)
        {
            items.Add(new SiteMigrationItem("site.json", "site", "(identity)", siteJson, "", SiteMigrationStatus.Failed, ex.Message));
            return new SiteMigrationReport(dryRun, siteCode, siteFolder, "Failed", "", items);
        }

        // 2. Composite items built from settings.json and monitor-config.json (never a secret).
        var candidates = new List<Candidate>();
        var settingsExtract = ExtractSiteSettings(settings, Path.Combine(o.Root, "monitor-config.json"), out var dropped);
        candidates.Add(new Candidate("settings", settingsPath, Path.Combine(siteFolder, "settings.json"), Encoding.UTF8.GetBytes(ToStable(settingsExtract)), settingsExtract));
        var appState = ExtractAppState(settings);
        if (appState is not null)
            candidates.Add(new Candidate("app-state", settingsPath, Path.Combine(siteFolder, "state", "app-state.json"), Encoding.UTF8.GetBytes(ToStable(appState)), appState));

        // 3. Plain files and folders.
        AddFile(candidates, "check-cm-result", Path.Combine(o.LegacyRoot, "CheckConfigMgrResult.csv"), Path.Combine(siteFolder, "results", "CheckConfigMgrResult.csv"));
        AddFile(candidates, "check-cm-result", Path.Combine(o.LegacyRoot, "CheckConfigMgrResult.json"), Path.Combine(siteFolder, "results", "CheckConfigMgrResult.json"));
        AddFile(candidates, "history-db", Path.Combine(o.LegacyRoot, "history.db"), Path.Combine(siteFolder, "results", "history.db"));
        AddFolder(candidates, "deployments", Path.Combine(o.Root, "Deployments", "Rings"), Path.Combine(siteFolder, "deployments", "rings"), items);
        AddFile(candidates, "deployments", Path.Combine(o.Root, "Deployments", "deployment-audit.jsonl"), Path.Combine(siteFolder, "deployments", "audit.jsonl"));
        AddFolder(candidates, "hygiene", Path.Combine(o.Root, "SiteHygiene"), Path.Combine(siteFolder, "hygiene"), items);
        AddFolder(candidates, "dpcontent", Path.Combine(o.Root, "DpContent"), Path.Combine(siteFolder, "dpcontent"), items);
        AddFolder(candidates, "pivots", Path.Combine(o.Root, "PivotsScripts", "Sessions"), Path.Combine(siteFolder, "pivots", "sessions"), items, siteCode);
        AddFolder(candidates, "maintenance", Path.Combine(o.Root, "MaintenanceWindows", "Reports"), Path.Combine(siteFolder, "maintenance", "reports"), items);
        AddFolder(candidates, "collections", Path.Combine(o.Root, "Collections", "Reports"), Path.Combine(siteFolder, "collections", "reports"), items);

        // 4. Process. A failure stops the run: the old files stay in use and nothing later is copied.
        var done = new List<MarkerEntry>();
        foreach (var c in candidates)
        {
            if (failed) break;
            var item = ProcessCandidate(o, dryRun, c, marker, siteFolder);
            items.Add(item);
            if (item.Status == SiteMigrationStatus.Failed) failed = true;
            if (!dryRun && item.Status is SiteMigrationStatus.Copied or SiteMigrationStatus.AlreadyDone)
            {
                done.Add(new MarkerEntry(item.Id, item.Source, item.Destination, item.Sha256));
                if (item.Status == SiteMigrationStatus.Copied) WriteMarker(o, siteFolder, marker, done, complete: false);
            }
            if (!dryRun && item.Status == SiteMigrationStatus.Copied) o.Hook?.Invoke("item-done", c.Destination);
        }

        // 5. Global and secret entries: reported, never touched.
        foreach (var id in GlobalIds) items.Add(new SiteMigrationItem(id, "global", "(stays where it is)", "", "", SiteMigrationStatus.GlobalStays, "not copied"));
        foreach (var id in SecretIds) items.Add(new SiteMigrationItem(id, "secret", "(never copied)", "", "", SiteMigrationStatus.SecretNotCopied, "secrets are never copied into a site folder"));
        if (dropped.Count > 0)
            items.Add(new SiteMigrationItem("settings", "secret", "settings.json", "", "", SiteMigrationStatus.SecretNotCopied, "dropped from the site copy: " + string.Join(", ", dropped)));

        var changed = items.Any(i => i.Status == SiteMigrationStatus.Copied);
        string outcome;
        if (failed) outcome = "Failed";
        else if (dryRun) outcome = "DryRun";
        else if (changed) outcome = items.Any(i => i.Status is SiteMigrationStatus.Conflict or SiteMigrationStatus.Blocked) ? "MigratedWithConflicts" : "Migrated";
        else outcome = items.Any(i => i.Status is SiteMigrationStatus.Conflict or SiteMigrationStatus.Blocked or SiteMigrationStatus.SourceChanged) ? "NothingChangedWithConflicts" : "NothingChanged";

        if (!dryRun && !failed && !items.Any(i => i.Status is SiteMigrationStatus.Conflict or SiteMigrationStatus.Blocked))
            WriteMarker(o, siteFolder, marker, done, complete: true);

        return new SiteMigrationReport(dryRun, siteCode, siteFolder, outcome, "", items);
    }

    private static SiteMigrationReport Refuse(bool dryRun, string outcome, string reason) =>
        new(dryRun, null, null, outcome, reason, []);

    // ---- items ----

    private static void AddFile(List<Candidate> list, string id, string source, string destination)
    {
        if (File.Exists(source)) list.Add(new Candidate(id, source, destination, null));
    }

    private static void AddFolder(List<Candidate> list, string id, string sourceDir, string destDir, List<SiteMigrationItem> report, string? siteCode = null)
    {
        if (!Directory.Exists(sourceDir)) return;
        if (AppDataPaths.IsReparsePointDirectory(sourceDir))
        {
            report.Add(new SiteMigrationItem(id, "site", sourceDir, "", "", SiteMigrationStatus.Blocked, "the folder is a junction or symbolic link"));
            return;
        }
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (IsUnderReparsePoint(sourceDir, file))
            {
                report.Add(new SiteMigrationItem(id, "site", file, "", "", SiteMigrationStatus.Blocked, "the path crosses a junction or symbolic link"));
                continue;
            }
            if (id == "pivots" && SessionSite(file) is { } other && other != siteCode)
            {
                report.Add(new SiteMigrationItem(id, "site", file, "", "", SiteMigrationStatus.Blocked, $"the session names site {other}, not this site; run the migration for that site"));
                continue;
            }
            list.Add(new Candidate(id, file, Path.Combine(destDir, relative), null));
        }
    }

    /// <summary>The site code inside a Pivots session file when it is a valid code, else null.</summary>
    private static string? SessionSite(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o && o["SiteCode"] is JsonValue v && v.TryGetValue<string>(out var code)
                && SiteDataPaths.IsValidSiteCode(code)) return code.ToUpperInvariant();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsUnderReparsePoint(string root, string file)
    {
        var dir = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(dir) && dir.Length > root.Length)
        {
            if (AppDataPaths.IsReparsePointDirectory(dir)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0;
    }

    private static SiteMigrationItem HandleSiteJson(SiteMigrationOptions o, bool dryRun, string siteCode, string siteFolder, string path, string provider)
    {
        const string Id = "site.json";
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var ok = doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("siteCode", out var c) && c.ValueKind == JsonValueKind.String
                    && string.Equals(c.GetString(), siteCode, StringComparison.Ordinal);
                return new SiteMigrationItem(Id, "site", "(identity)", path, Hash(path), ok ? SiteMigrationStatus.AlreadyDone : SiteMigrationStatus.Conflict,
                    ok ? "exists" : "site.json exists for another site code; kept");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new SiteMigrationItem(Id, "site", "(identity)", path, "", SiteMigrationStatus.Conflict, "site.json exists and is not valid JSON; kept");
            }
        }
        var now = o.UtcNow().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var node = new JsonObject
        {
            ["schemaVersion"] = SiteDataPaths.SchemaVersion,
            ["siteCode"] = siteCode,
            ["providers"] = provider.Length > 0 ? new JsonArray(provider) : new JsonArray(),
            ["createdUtc"] = now,
            ["lastUsedUtc"] = now,
            ["migratedFrom"] = LegacyMarker,
        };
        var bytes = Encoding.UTF8.GetBytes(ToStable(node));
        if (dryRun) return new SiteMigrationItem(Id, "site", "(identity)", path, HashBytes(bytes), SiteMigrationStatus.Planned, "");
        WriteVerified(path, bytes, o.Hook);
        return new SiteMigrationItem(Id, "site", "(identity)", path, HashBytes(bytes), SiteMigrationStatus.Copied, "");
    }

    private static SiteMigrationItem ProcessCandidate(SiteMigrationOptions o, bool dryRun, Candidate c, Marker marker, string siteFolder)
    {
        var scope = "site";
        try
        {
            if (Path.GetFileName(c.Source).Equals("history.db", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var side in new[] { "-wal", "-journal" })
                {
                    var sidecar = c.Source + side;
                    if (File.Exists(sidecar) && new FileInfo(sidecar).Length > 0)
                        return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, "", SiteMigrationStatus.Blocked, $"the database is in use ({Path.GetFileName(sidecar)} is not empty); close the app and run again");
                }
            }
            var srcHash = c.Content is null ? Hash(c.Source) : HashBytes(c.Content);
            if (c.Owned is not null && File.Exists(c.Destination))
                return ProcessMerge(o, dryRun, c, marker, scope);
            if (File.Exists(c.Destination))
            {
                var destHash = Hash(c.Destination);
                if (destHash == srcHash) return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, srcHash, SiteMigrationStatus.AlreadyDone, "");
                var recorded = marker.Items.Any(m => string.Equals(m.Destination, c.Destination, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Source, c.Source, StringComparison.OrdinalIgnoreCase) && m.Sha256 == destHash);
                return recorded
                    ? new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, srcHash, SiteMigrationStatus.SourceChanged, "the source changed after the earlier copy; the copy is kept as it is")
                    : new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, srcHash, SiteMigrationStatus.Conflict, $"the destination exists with different content (SHA-256 {destHash}); both are kept");
            }
            if (Directory.Exists(c.Destination))
                return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, srcHash, SiteMigrationStatus.Conflict, "the destination is a folder; kept");
            if (dryRun) return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, srcHash, SiteMigrationStatus.Planned, "");

            if (c.Content is not null) WriteVerified(c.Destination, c.Content, o.Hook, srcHash);
            else CopyVerified(c.Source, c.Destination, srcHash, o.Hook);
            return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, srcHash, SiteMigrationStatus.Copied, "");
        }
        catch (SiteMigrationVerifyException ex)
        {
            return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, "", SiteMigrationStatus.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, "", SiteMigrationStatus.Failed, ex.Message);
        }
    }


    /// <summary>
    /// A shared JSON settings file (settings.json, statepp-state.json) that another app or an earlier run may have written.
    /// Each owned top-level key is added when absent, accepted when equal, and a conflict when different. Existing keys are kept.
    /// Nothing is written when any key conflicts.
    /// </summary>
    private static SiteMigrationItem ProcessMerge(SiteMigrationOptions o, bool dryRun, Candidate c, Marker marker, string scope)
    {
        var owned = c.Owned!;
        var ownedHash = HashBytes(c.Content!);
        JsonObject? existing;
        try { existing = JsonNode.Parse(File.ReadAllText(c.Destination)) as JsonObject; }
        catch (JsonException) { existing = null; }
        if (existing is null)
            return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, ownedHash, SiteMigrationStatus.Conflict, "the destination exists and is not a JSON object; kept");
        var conflicts = new List<string>();
        var toAdd = new List<string>();
        foreach (var (key, value) in owned)
        {
            if (!existing.ContainsKey(key)) toAdd.Add(key);
            else if (Canonical(existing[key]) != Canonical(value)) conflicts.Add(key);
        }
        if (conflicts.Count > 0)
        {
            var destHash = Hash(c.Destination);
            var recorded = marker.Items.Any(m => string.Equals(m.Destination, c.Destination, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Source, c.Source, StringComparison.OrdinalIgnoreCase) && m.Sha256 == destHash);
            return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, ownedHash,
                recorded ? SiteMigrationStatus.SourceChanged : SiteMigrationStatus.Conflict,
                (recorded ? "the source changed after the earlier copy; kept: " : "the destination has other values for: ") + string.Join(", ", conflicts) + "; both are kept");
        }
        if (toAdd.Count == 0)
            return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, Hash(c.Destination), SiteMigrationStatus.AlreadyDone, "");
        foreach (var key in toAdd) existing[key] = owned[key]!.DeepClone();
        var bytes = Encoding.UTF8.GetBytes(ToStable(existing));
        var hash = HashBytes(bytes);
        if (dryRun) return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, hash, SiteMigrationStatus.Planned, "adds keys: " + string.Join(", ", toAdd));
        WriteVerified(c.Destination, bytes, o.Hook, hash, overwrite: true);
        return new SiteMigrationItem(c.Id, scope, c.Source, c.Destination, hash, SiteMigrationStatus.Copied, "adds keys: " + string.Join(", ", toAdd));
    }

    /// <summary>Compact JSON with object keys in ordinal order, so two equal values compare equal.</summary>
    private static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case null: return "null";
            case JsonObject obj:
                return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}";
            case JsonArray arr:
                return "[" + string.Join(",", arr.Select(Canonical)) + "]";
            default:
                return node.ToJsonString();
        }
    }

    // ---- copy with verification ----

    private sealed class SiteMigrationVerifyException(string message) : Exception(message);

    private static void CopyVerified(string source, string destination, string expectedHash, Action<string, string>? hook)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".migrating";
        if (File.Exists(temp)) File.Delete(temp);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        FinishCopy(temp, destination, expectedHash, hook);
    }

    private static void WriteVerified(string destination, byte[] content, Action<string, string>? hook, string? expectedHash = null, bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".migrating";
        if (File.Exists(temp)) File.Delete(temp);
        File.WriteAllBytes(temp, content);
        FinishCopy(temp, destination, expectedHash ?? HashBytes(content), hook, overwrite);
    }

    private static void FinishCopy(string temp, string destination, string expectedHash, Action<string, string>? hook, bool overwrite = false)
    {
        hook?.Invoke("temp-written", destination);
        var actual = Hash(temp);
        if (actual != expectedHash)
        {
            File.Delete(temp);
            throw new SiteMigrationVerifyException($"The copy of {destination} did not match its source (expected {expectedHash}, got {actual}). The old files stay in use.");
        }
        File.Move(temp, destination, overwrite);
        if (Hash(destination) != expectedHash)
            throw new SiteMigrationVerifyException($"The copy {destination} did not verify after the move.");
    }

    // ---- extraction ----

    private static JsonObject ExtractSiteSettings(JsonObject settings, string monitorConfigPath, out List<string> dropped)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "SiteCode", "ProviderMachine", "DiscoverySqlServer", "DiscoverySqlDatabase", "FileShareRoot", "ContentDistribution", "DistributionOverrides", "Wsus", "SiteHygiene", "SiteHygieneLive" })
            if (settings[key] is JsonNode n) result[key] = n.DeepClone();
        if (settings["OneClick"] is JsonObject oc)
        {
            var one = new JsonObject();
            foreach (var key in new[] { "Destinations", "DefaultDestinations" })
                if (oc[key] is JsonNode n) one[key] = n.DeepClone();
            if (one.Count > 0) result["OneClick"] = one;
        }
        if (File.Exists(monitorConfigPath))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(monitorConfigPath)) is JsonObject mc && mc["SiteCode"] is JsonValue v && v.TryGetValue<string>(out var sc))
                    result["CveMonitorSiteCode"] = sc;
            }
            catch (JsonException) { }
        }
        dropped = [];
        Sanitize(result, "", dropped);
        return result;
    }

    private static JsonObject? ExtractAppState(JsonObject settings)
    {
        if (settings["AppStates"] is not JsonObject states || states.Count == 0) return null;
        var result = new JsonObject();
        foreach (var (id, node) in states)
        {
            if (node is not JsonObject app) continue;
            var o = new JsonObject();
            foreach (var key in new[] { "CurrentVersion", "LastStaged", "Published", "NotSupported" })
                if (app[key] is JsonNode n) o[key] = n.DeepClone();
            Sanitize(o, "", new List<string>());
            result[id] = o;
        }
        return result;
    }

    /// <summary>Removes every property whose name looks like a secret, at any depth. A secret never reaches a site folder.</summary>
    private static void Sanitize(JsonNode? node, string path, List<string> dropped)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in obj.Select(p => p.Key).ToList())
            {
                if (SecretName.IsMatch(name)) { obj.Remove(name); dropped.Add(path + name); continue; }
                Sanitize(obj[name], path + name + ".", dropped);
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr) Sanitize(child, path, dropped);
        }
    }

    private static string ToStable(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

    // ---- marker ----

    private sealed record MarkerEntry(string Id, string Source, string Destination, string Sha256);
    private sealed class Marker
    {
        public List<MarkerEntry> Items { get; set; } = [];
        public string? CompletedUtc { get; set; }
        public string? StartedUtc { get; set; }
    }

    private static Marker ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Marker();
            var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return m ?? new Marker();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new Marker(); }
    }

    /// <summary>
    /// The marker lists every copied file with its hash. It is the journal: written after each copy, so a crash leaves
    /// a true record. The file changes only when its item list changes, so a second run does not touch it.
    /// </summary>
    private static void WriteMarker(SiteMigrationOptions o, string siteFolder, Marker previous, List<MarkerEntry> done, bool complete)
    {
        var merged = previous.Items
            .Where(p => done.All(d => !string.Equals(d.Destination, p.Destination, StringComparison.OrdinalIgnoreCase)))
            .Concat(done)
            .OrderBy(e => e.Destination, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var path = Path.Combine(siteFolder, MarkerFileName);
        var now = o.UtcNow().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var started = previous.StartedUtc ?? now;
        var completed = previous.CompletedUtc ?? (complete ? now : null);
        var sameItems = previous.Items.Count == merged.Count
            && previous.Items.OrderBy(e => e.Destination, StringComparer.OrdinalIgnoreCase).SequenceEqual(merged);
        if (File.Exists(path) && sameItems && (previous.CompletedUtc is not null || !complete)) return;
        var node = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = LegacyMarker,
            ["startedUtc"] = started,
            ["completedUtc"] = completed,
            ["keepOldFilesUntil"] = "the release after the one that switches the stores (Q-112d); a later cleanup needs the owner's approval",
            ["items"] = new JsonArray(merged.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id,
                ["source"] = e.Source,
                ["destination"] = e.Destination,
                ["sha256"] = e.Sha256,
            }).ToArray()),
        };
        var temp = path + ".tmp";
        Directory.CreateDirectory(siteFolder);
        File.WriteAllText(temp, ToStable(node), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    // ---- hashing ----

    public static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
