using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services;

/// <summary>What an item holds in the source and in the target, before anything is written.</summary>
public static class SiteImportState
{
    /// <summary>The source site has no value for the item. Nothing to copy.</summary>
    public const string NotInSource = "NotInSource";
    /// <summary>The target has no value. The value would be added.</summary>
    public const string New = "New";
    /// <summary>The target holds the same value. Nothing changes.</summary>
    public const string Same = "Same";
    /// <summary>The target holds another value. The user chooses keep or replace.</summary>
    public const string Conflict = "Conflict";
}

/// <summary>What the import does with an item. The plan uses the present forms, the run the past forms.</summary>
public static class SiteImportOutcome
{
    public const string Skipped = "Skipped";
    public const string NotInSource = "NotInSource";
    public const string AlreadyDone = "AlreadyDone";
    public const string Copy = "Copy";
    public const string Copied = "Copied";
    public const string Replace = "Replace";
    public const string Replaced = "Replaced";
    public const string Keep = "Keep";
    public const string Kept = "Kept";
    public const string Failed = "Failed";
}

/// <summary>The answer to a conflict (a target value that differs from the source value). Keep is the default.</summary>
public enum SiteImportChoice
{
    Keep,
    Replace,
}

/// <summary>One group of site settings that the import can copy. Its keys are the scope manifest entry named by <see cref="ManifestId"/>.</summary>
public sealed record SiteImportItem(string Id, string Label, string Group, bool DefaultOn, bool EnvironmentValue, IReadOnlyList<string> Keys, string Note)
{
    /// <summary>The scope manifest entry. Equal to <see cref="Id"/>.</summary>
    public string ManifestId => Id;
}

/// <summary>A site record or a secret that the import never copies, with the reason shown in the dialog.</summary>
public sealed record SiteImportNever(string ManifestId, string Label, string Reason);

/// <summary>One row of the preview and of the report.</summary>
public sealed record SiteImportRow(
    string Id, string Label, string Group, bool EnvironmentValue, bool Selected, string State, string Outcome,
    string SourceText, string TargetText, string Detail);

public sealed record SiteImportRequest(
    string SourceFolder,
    string TargetFolder,
    IReadOnlyCollection<string>? SelectedIds = null,
    IReadOnlyDictionary<string, SiteImportChoice>? Choices = null,
    string? DataRoot = null,
    Func<DateTime>? UtcNow = null,
    Action<string, string>? Hook = null,
    JsonObject? SourceSettings = null,
    string SourceSettingsSha256 = "",
    bool CreateTargetIfMissing = false,
    string? SourceLabel = null);

public sealed record SiteImportReport(
    bool DryRun,
    string Outcome,
    string Reason,
    string SourceFolder,
    string TargetFolder,
    string SourceSha256,
    string TargetSha256,
    IReadOnlyList<SiteImportRow> Rows,
    IReadOnlyList<string> NeverCopiedKeys,
    bool TargetChanged = false,
    string Warning = "")
{
    public const string Done = "Done";
    public const string NothingToDo = "NothingToDo";
    public const string Refused = "Refused";
    public const string FailedOutcome = "Failed";

    public int Count(string outcome) => Rows.Count(r => r.Outcome == outcome);

    /// <summary>The rows that a run writes or wrote (copy and replace).</summary>
    public int Written => Count(SiteImportOutcome.Copy) + Count(SiteImportOutcome.Replace) + Count(SiteImportOutcome.Copied) + Count(SiteImportOutcome.Replaced);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Site settings import ({(DryRun ? "preview" : "run")}) {SourceFolder} -> {TargetFolder}: {Outcome}");
        if (Reason.Length > 0) sb.AppendLine(Reason);
        foreach (var r in Rows)
            sb.AppendLine($"{r.Outcome,-12} {r.State,-11} {r.Id} | {r.SourceText} -> {r.TargetText}{(r.Detail.Length == 0 ? "" : " | " + r.Detail)}");
        return sb.ToString();
    }
}

/// <summary>
/// Import of settings from another site (Q-112e). It copies the site-scoped settings of a source site into the settings of a
/// target site and never moves anything: the source files are only read and are not changed. The items are the scope manifest
/// entries of the contract that are site-scoped and copyable. Values that are specific to one site (the SMS Provider, the SQL
/// server and database, the share root, the WSUS server) are off by default and copied only when the user ticks them. The site
/// code is never copied: it is the key of the target folder. Every site record (results, states, ring plans, audit) and every
/// secret is never copied. A target value that differs from the source value is a conflict; the default is to keep it.
/// A write goes to a temporary file, is verified by its SHA-256, and replaces the target file only after that. Running the same
/// import again changes nothing. A run that stopped before the replace left the target as it was and can run again.
/// </summary>
public static class SiteSettingsImport
{
    public const string LogFileName = "imported-from.json";
    public const string BackupInfix = ".before-import-";
    /// <summary>The number of settings backups that a replace keeps.</summary>
    public const int MaxBackups = 5;

    /// <summary>The copyable site-scoped entries of the scope manifest, in the order of the dialog.</summary>
    public static readonly IReadOnlyList<SiteImportItem> Items =
    [
        new("apd.content-distribution", "Content distribution (DP group, test collection)", GroupNames, true, false, ["ContentDistribution"],
            "Names only. The next connect shows each name that the target site does not have."),
        new("apd.distribution-overrides", "Distribution overrides for single applications", GroupNames, true, false, ["DistributionOverrides"],
            "Names only."),
        new("apd.destinations", "One Click destination flags", GroupNames, true, false, ["OneClick.Destinations", "OneClick.DefaultDestinations"],
            "The ConfigMgr flag depends on the site."),
        new("apd.hygiene-options", "Site Hygiene options", GroupNames, true, false, ["SiteHygiene", "SiteHygieneLive"],
            "Areas, pause, thresholds. Not the findings or suppressions of the source site."),
        new("apd.share-root", "File share root", GroupEnvironment, false, true, ["FileShareRoot"],
            "Differs by environment."),
        new("apd.wsus", "WSUS server settings", GroupEnvironment, false, true, ["Wsus"],
            "Differs by environment."),
        new("apd.sql-server", "Discovery SQL Server", GroupEnvironment, false, true, ["DiscoverySqlServer"],
            "A server of the source site. Tick it only when both sites use the same server."),
        new("apd.sql-database", "Discovery SQL database", GroupEnvironment, false, true, ["DiscoverySqlDatabase"],
            "The database of the source site."),
        new("apd.provider", "SMS Provider (server FQDN)", GroupEnvironment, false, true, ["ProviderMachine"],
            "The server of the source site. The target site has its own."),
    ];

    public const string GroupNames = "Names and options of the site";
    public const string GroupEnvironment = "Values of one environment (off by default)";

    /// <summary>Scope manifest entries that describe the source site or are secrets. The import never copies them.</summary>
    public static readonly IReadOnlyList<SiteImportNever> NeverCopied =
    [
        new("apd.site-code", "Site code", "The target keeps its own code. The code is the name of its folder."),
        new("apd.app-state-current", "ConfigMgr version of each application", "It describes the source site."),
        new("apd.app-state-published", "Published versions", "They describe the source site."),
        new("apd.check-cm-result", "Check ConfigMgr result", "It describes the source site."),
        new("apd.inventory", "Application inventory", "It describes the source site."),
        new("apd.site-operations", "Operation history", "It describes the source site."),
        new("apd.deployments", "Ring plans, ring runs and deployment audit", "They hold collection names and IDs of the source site."),
        new("apd.hygiene-stores", "Site Hygiene findings, suppressions, metrics and audit", "They hold object IDs of the source site."),
        new("apd.dpcontent", "DP Content reports and audit", "They describe the source site."),
        new("apd.pivots", "Pivots and Scripts saved runs", "They describe the source site."),
        new("apd.maintenance", "Maintenance Windows reports and audit", "They describe the source site."),
        new("apd.collections", "Collections reports and audit", "They describe the source site."),
        new("apd.github-token", "GitHub token", "A secret. It is global and works in every site."),
        new("apd.intune-secret", "Intune client secret", "A secret. It is global."),
        new("apd.feed-credentials", "Feed credentials", "A secret. They are global."),
        new("apd.nvd-key", "NVD API key", "A secret. It is global."),
    ];

    public static SiteImportReport Plan(SiteImportRequest request) => Execute(request, dryRun: true);

    public static SiteImportReport Run(SiteImportRequest request) => Execute(request, dryRun: false);

    /// <summary>The ids that are on by default (the dialog ticks them first).</summary>
    public static IReadOnlyList<string> DefaultSelection => Items.Where(i => i.DefaultOn).Select(i => i.Id).ToList();

    private static SiteImportReport Refuse(SiteImportRequest r, bool dryRun, string reason) =>
        new(dryRun, SiteImportReport.Refused, reason, r.SourceFolder, r.TargetFolder, "", "", [], []);

    private static SiteImportReport Execute(SiteImportRequest request, bool dryRun)
    {
        var root = request.DataRoot ?? SiteContext.DataRoot;
        if (!SiteDataPaths.TryParseFolderName(request.SourceFolder, out var source) || source is null)
            return Refuse(request, dryRun, $"The source site '{request.SourceFolder}' is not a site folder name.");
        if (!SiteDataPaths.TryParseFolderName(request.TargetFolder, out var target) || target is null)
            return Refuse(request, dryRun, $"The target site '{request.TargetFolder}' is not a site folder name.");
        // A site file (Q-112h) is the source when SourceSettings is set: it may come from the same site code on another machine.
        var fromFile = request.SourceSettings is not null;
        if (!fromFile && string.Equals(source.FolderName, target.FolderName, StringComparison.OrdinalIgnoreCase))
            return Refuse(request, dryRun, "The source and the target are the same site. Choose another source site.");
        var sourceFolder = SiteDataPaths.GetSiteFolder(source.FolderName, dataRoot: root);
        var targetFolder = SiteDataPaths.GetSiteFolder(target.FolderName, dataRoot: root);
        if (!fromFile && !Directory.Exists(sourceFolder)) return Refuse(request, dryRun, $"The source site {source.FolderName} does not exist.");
        var targetMissing = !Directory.Exists(targetFolder);
        if (targetMissing && !(fromFile && request.CreateTargetIfMissing)) return Refuse(request, dryRun, $"The target site {target.FolderName} does not exist.");

        var sourcePath = SiteSettingsSplit.SettingsPathOf(sourceFolder);
        var targetPath = SiteSettingsSplit.SettingsPathOf(targetFolder);
        JsonObject sourceObject;
        string sourceHash;
        if (fromFile)
        {
            sourceObject = request.SourceSettings!;
            sourceHash = request.SourceSettingsSha256;
        }
        else if (!TryReadObject(sourcePath, out sourceObject, out sourceHash, out var sourceProblem))
            return Refuse(request, dryRun, $"The settings of site {source.FolderName} cannot be read ({sourceProblem}). Nothing was changed.");
        if (!TryReadObject(targetPath, out var targetObject, out var targetHash, out var targetProblem))
            return Refuse(request, dryRun, $"The settings of site {target.FolderName} cannot be read ({targetProblem}). Nothing was changed.");

        var selected = new HashSet<string>(request.SelectedIds ?? DefaultSelection, StringComparer.Ordinal);
        var rows = new List<SiteImportRow>();
        var writes = new List<(SiteImportItem Item, string Key, JsonNode Value, bool Replace)>();
        var neverKeys = new List<string>();

        foreach (var item in Items)
        {
            var isSelected = selected.Contains(item.Id);
            var sourceTexts = new List<string>();
            var targetTexts = new List<string>();
            var anyNew = false;
            var anyConflict = false;
            var anySource = false;
            var itemWrites = new List<(string Key, JsonNode Value, bool Replace)>();
            var choice = request.Choices is not null && request.Choices.TryGetValue(item.Id, out var c) ? c : SiteImportChoice.Keep;
            var dropped = new List<string>();

            foreach (var key in item.Keys)
            {
                var sourceValue = Sanitized(Get(sourceObject, key), key, dropped);
                if (IsEmpty(sourceValue)) continue;
                anySource = true;
                var targetValue = Get(targetObject, key);
                sourceTexts.Add(Summarize(sourceValue));
                if (IsEmpty(targetValue))
                {
                    anyNew = true;
                    targetTexts.Add("(not set)");
                    itemWrites.Add((key, sourceValue!, false));
                }
                else if (SiteDataMigration.Canonical(targetValue) == SiteDataMigration.Canonical(sourceValue))
                {
                    targetTexts.Add(Summarize(targetValue));
                }
                else
                {
                    anyConflict = true;
                    targetTexts.Add(Summarize(targetValue));
                    itemWrites.Add((key, sourceValue!, true));
                }
            }
            neverKeys.AddRange(dropped);

            var state = !anySource ? SiteImportState.NotInSource
                : anyConflict ? SiteImportState.Conflict
                : anyNew ? SiteImportState.New
                : SiteImportState.Same;
            string outcome;
            var detail = dropped.Count > 0 ? "Not copied (secret name): " + string.Join(", ", dropped) : "";
            if (!isSelected) outcome = SiteImportOutcome.Skipped;
            else if (state == SiteImportState.NotInSource) outcome = SiteImportOutcome.NotInSource;
            else if (state == SiteImportState.Same) outcome = SiteImportOutcome.AlreadyDone;
            else
            {
                // An item with a conflict is kept whole (the default) or replaced whole; the keys of one item never mix the values of two sites.
                if (state == SiteImportState.Conflict && choice == SiteImportChoice.Keep)
                    outcome = SiteImportOutcome.Keep;
                else
                {
                    foreach (var w in itemWrites) writes.Add((item, w.Key, w.Value, w.Replace));
                    outcome = state == SiteImportState.Conflict ? SiteImportOutcome.Replace : SiteImportOutcome.Copy;
                }
            }
            rows.Add(new SiteImportRow(item.Id, item.Label, item.Group, item.EnvironmentValue, isSelected, state, outcome,
                anySource ? string.Join("; ", sourceTexts) : "(not set)", targetTexts.Count > 0 ? string.Join("; ", targetTexts) : "(not set)", detail));
        }

        if (dryRun)
        {
            var pending = rows.Count(r => r.Outcome is SiteImportOutcome.Copy or SiteImportOutcome.Replace);
            return new SiteImportReport(true, pending > 0 ? SiteImportReport.Done : SiteImportReport.NothingToDo,
                pending > 0 ? "" : "Nothing to copy: the ticked items are already in the target, not set in the source, or kept.",
                source.FolderName, target.FolderName, sourceHash, targetHash, rows, neverKeys);
        }

        // ---- run: write the target settings once, verified, then the log ----
        var now = (request.UtcNow ?? (() => DateTime.UtcNow))();
        var replaced = writes.Any(w => w.Replace);
        List<SiteImportRow> finalRows;
        var targetHashAfter = targetHash;
        var warning = "";
        try
        {
            if (writes.Count > 0)
            {
                if (targetMissing)
                {
                    var created = SiteCatalog.Create(target.SiteCode, null, request.SourceLabel, root);
                    if (!string.Equals(created.FolderName, target.FolderName, StringComparison.Ordinal))
                        throw new IOException($"The site folder {target.FolderName} could not be created; {created.FolderName} was created instead. Run the import again.");
                }
                var updated = (JsonObject)targetObject.DeepClone();
                foreach (var (_, key, value, _) in writes) Set(updated, key, value.DeepClone());
                if (replaced && File.Exists(targetPath))
                    Backup(targetPath, targetHash, now);
                var bytes = new UTF8Encoding(false).GetBytes(updated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
                // The replace goes ahead only while the target file is still the file that the plan read: another save in between is not overwritten.
                WriteVerified(targetPath, bytes, request.Hook, expectedExistingHash: targetHash);
                targetHashAfter = Hash(targetPath);
                // Verify each written key against the source value, reading the file again.
                var check = SiteSettingsSplit.ReadObject(targetPath) ?? throw new IOException("The target settings cannot be read after the write.");
                foreach (var (_, key, value, _) in writes)
                    if (SiteDataMigration.Canonical(Get(check, key)) != SiteDataMigration.Canonical(value))
                        throw new IOException($"The value of {key} in the target does not match the source after the write.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var failed = rows.Select(r => r.Outcome is SiteImportOutcome.Copy or SiteImportOutcome.Replace ? r with { Outcome = SiteImportOutcome.Failed, Detail = ex.Message } : r).ToList();
            var changed = File.Exists(targetPath) && Hash(targetPath) != targetHash;
            return new SiteImportReport(false, SiteImportReport.FailedOutcome,
                ex.Message + (changed ? " The target settings file changed; check it and run the import again." : " The old target settings stay in use; run the import again."),
                source.FolderName, target.FolderName, sourceHash, targetHash, failed, neverKeys, changed);
        }

        finalRows = rows.Select(r => r with { Outcome = r.Outcome switch
        {
            SiteImportOutcome.Copy => SiteImportOutcome.Copied,
            SiteImportOutcome.Replace => SiteImportOutcome.Replaced,
            SiteImportOutcome.Keep => SiteImportOutcome.Kept,
            _ => r.Outcome,
        } }).ToList();

        // The settings are in place and verified. The provider entry of site.json and the log are best effort: a failure of either is a warning, never a failed import.
        try
        {
            if (writes.FirstOrDefault(w => w.Key == "ProviderMachine").Value is JsonValue pv && pv.TryGetValue<string>(out var provider) && !string.IsNullOrWhiteSpace(provider))
                SiteCatalog.AddProvider(target.FolderName, provider, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning += $"The SMS Provider could not be added to site.json ({ex.Message}). ";
        }
        try
        {
            var logPath = Path.Combine(targetFolder, LogFileName);
            var written = finalRows.Count(r => r.Outcome is SiteImportOutcome.Copied or SiteImportOutcome.Replaced);
            if (written > 0 || (!File.Exists(logPath) && finalRows.Any(r => r.Outcome == SiteImportOutcome.AlreadyDone)))
                WriteLog(logPath, source.FolderName, sourceHash, targetHashAfter, now, finalRows, fromFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning += $"The log {LogFileName} could not be written ({ex.Message}). ";
        }

        var did = finalRows.Count(r => r.Outcome is SiteImportOutcome.Copied or SiteImportOutcome.Replaced);
        return new SiteImportReport(false, did > 0 ? SiteImportReport.Done : SiteImportReport.NothingToDo,
            did > 0 ? "" : "Nothing changed: the ticked items are already in the target, not set in the source, or kept.",
            source.FolderName, target.FolderName, sourceHash, targetHashAfter, finalRows, neverKeys, targetHashAfter != targetHash, warning.Trim());
    }

    // ---- values ----

    internal static JsonNode? Get(JsonObject obj, string path)
    {
        JsonNode? node = obj;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node)) return null;
        }
        return node;
    }

    internal static void Set(JsonObject obj, string path, JsonNode value)
    {
        var parts = path.Split('.');
        var current = obj;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[parts[i]] = next;
            }
            current = next;
        }
        current[parts[^1]] = value;
    }

    internal static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonObject o => o.Count == 0,
        JsonArray a => a.Count == 0,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        _ => false,
    };

    /// <summary>A clone of the value without any property whose name looks like a secret. A secret never reaches the target.</summary>
    internal static JsonNode? Sanitized(JsonNode? node, string key, List<string> dropped)
    {
        if (node is null) return null;
        var clone = node.DeepClone();
        // The key itself is checked as well: an item whose key looks like a secret is dropped whole.
        var last = key.Split('.')[^1];
        if (SiteDataMigration.SecretName.IsMatch(last)) { dropped.Add(key); return null; }
        SiteDataMigration.Sanitize(clone, key + ".", dropped);
        return clone;
    }

    private static string Summarize(JsonNode? node)
    {
        if (node is null) return "(not set)";
        string text;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) text = s;
        else if (node is JsonValue) text = node.ToJsonString();
        else text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return text.Length > 90 ? text[..87] + "..." : text;
    }

    // ---- files ----

    /// <summary>Reads a JSON object file once: its bytes give both the hash and the value. A missing file reads as an empty object.</summary>
    private static bool TryReadObject(string path, out JsonObject obj, out string hash, out string problem)
    {
        obj = new JsonObject();
        hash = "";
        problem = "";
        if (!File.Exists(path)) return true;
        try
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            hash = HashBytes(bytes);
            var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject parsed) { problem = "the file is not a JSON object"; return false; }
            obj = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Hash(string path) => SiteDataMigration.Hash(path);

    /// <summary>Copies the target settings aside before a replace. The copy is verified by its SHA-256.</summary>
    private static void Backup(string targetPath, string expectedHash, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var backup = targetPath + BackupInfix + stamp;
        for (var n = 2; File.Exists(backup); n++) backup = targetPath + BackupInfix + stamp + "-" + n;
        File.Copy(targetPath, backup);
        if (Hash(backup) != expectedHash)
        {
            File.Delete(backup);
            throw new IOException("The backup of the target settings did not match its source.");
        }
        // Keep the newest backups only.
        var all = Directory.GetFiles(Path.GetDirectoryName(targetPath)!, Path.GetFileName(targetPath) + BackupInfix + "*")
            .OrderByDescending(f => f, StringComparer.Ordinal).ToList();
        foreach (var stale in all.Skip(MaxBackups))
        {
            try { File.Delete(stale); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteVerified(string destination, byte[] content, Action<string, string>? hook, string? expectedExistingHash = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".importing";
        if (File.Exists(temp)) File.Delete(temp);
        File.WriteAllBytes(temp, content);
        var expected = HashBytes(content);
        hook?.Invoke("temp-written", destination);
        if (Hash(temp) != expected)
        {
            File.Delete(temp);
            throw new IOException($"The new settings file for {destination} did not verify.");
        }
        // A save of the app between the read and the replace changed the target: do not overwrite it.
        if (expectedExistingHash is not null && (File.Exists(destination) ? Hash(destination) : "") != expectedExistingHash)
        {
            File.Delete(temp);
            throw new IOException("The target settings changed while the import ran.");
        }
        File.Move(temp, destination, overwrite: true);
        if (Hash(destination) != expected)
            throw new IOException($"The settings file {destination} did not verify after the replace.");
    }

    private static void WriteLog(string logPath, string sourceFolder, string sourceHash, string targetHash, DateTime now, IReadOnlyList<SiteImportRow> rows, bool fromFile = false)
    {
        var items = new JsonArray();
        foreach (var r in rows)
            items.Add(new JsonObject { ["id"] = r.Id, ["outcome"] = r.Outcome, ["state"] = r.State });
        var log = new JsonObject
        {
            ["schemaVersion"] = SiteDataPaths.SchemaVersion,
            ["sourceSite"] = sourceFolder,
            ["sourceKind"] = fromFile ? "file" : "site",
            ["importedUtc"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["sourceSettingsSha256"] = sourceHash,
            ["targetSettingsSha256"] = targetHash,
            ["items"] = items,
        };
        var bytes = new UTF8Encoding(false).GetBytes(log.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
        WriteVerified(logPath, bytes, null);
    }
}
