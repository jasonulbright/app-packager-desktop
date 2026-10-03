using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>bundle.json: the schema version, the application and one entry per payload file.</summary>
public sealed class WorkbenchBundleManifest
{
    public const string Complete = "Complete";
    public const string IncompleteSource = "IncompleteSource";

    public int SchemaVersion { get; set; }
    public string? ApplicationId { get; set; }
    public string? ExportedAt { get; set; }
    public bool SourcesIncluded { get; set; }
    public string? SourceStatus { get; set; }
    public List<WorkbenchBundleEntry> Entries { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One payload file: its path relative to the application folder, with backslash separators.</summary>
public sealed class WorkbenchBundleEntry
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed record WorkbenchBundleExport(string Path, string ApplicationId, bool SourcesIncluded, int EntryCount);

public sealed record WorkbenchBundleImport(string ApplicationId, int EntryCount, bool SourcesIncluded, string SourceStatus);

/// <summary>
/// Portable bundles: an application definition, its profiles and assets, and with
/// IncludeSources its BYO installer revisions, in one zip with a SHA-256 per entry.
/// Import never runs anything it extracts.
/// </summary>
public static class WorkbenchBundles
{
    public const int SchemaVersion = 1;
    public const string ManifestName = "bundle.json";
    public const string PayloadFolder = "application";

    private static readonly EnumerationOptions SkipReparsePoints = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false
    };

    private static readonly EnumerationOptions EveryEntry = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    /// <summary>
    /// Writes the bundle. Files and folders that are reparse points are left out, so a
    /// link inside the application folder never carries content from outside it; the
    /// sources folder goes in only with <paramref name="includeSources"/>, and the
    /// manifest records IncompleteSource when it was left out.
    /// </summary>
    public static WorkbenchBundleExport Export(WorkbenchStore store, string applicationId, string path, bool includeSources = false)
    {
        var applicationFolder = store.GetApplicationFolder(applicationId);
        if (!Directory.Exists(applicationFolder))
            throw new InvalidOperationException($"Application '{applicationId}' has nothing stored to export.");
        if (WorkbenchJson.IsReparsePoint(applicationFolder))
            throw new InvalidOperationException($"Application folder '{applicationFolder}' is a reparse point; refusing the export.");

        var root = Path.GetFullPath(applicationFolder).TrimEnd('\\');
        var files = new List<(string Full, string Relative)>();
        var sourcesSkipped = false;
        foreach (var file in Directory.EnumerateFiles(root, "*", SkipReparsePoints).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = file[(root.Length + 1)..];
            if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("sources\\", StringComparison.OrdinalIgnoreCase) && !includeSources)
            {
                sourcesSkipped = true;
                continue;
            }
            files.Add((file, relative));
        }
        if (!includeSources && Directory.Exists(Path.Combine(root, "sources"))) sourcesSkipped = true;

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new WorkbenchBundleManifest
        {
            SchemaVersion = SchemaVersion,
            ApplicationId = applicationId,
            ExportedAt = WorkbenchJson.NowRoundTrip(),
            SourcesIncluded = !sourcesSkipped,
            SourceStatus = sourcesSkipped ? WorkbenchBundleManifest.IncompleteSource : WorkbenchBundleManifest.Complete
        };
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var (full, relative) in files)
                {
                    var entry = zip.CreateEntry(PayloadFolder + "/" + relative.Replace('\\', '/'), CompressionLevel.Optimal);
                    using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = entry.Open();
                    // The hash is taken from the bytes written into the entry, so a file that changes during export cannot carry a hash of other content.
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long size = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                        size += read;
                    }
                    manifest.Entries.Add(new WorkbenchBundleEntry
                    {
                        RelativePath = relative,
                        Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                        Size = size
                    });
                }
                var manifestEntry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using var stream = manifestEntry.Open();
                JsonSerializer.Serialize(stream, manifest, WorkbenchJson.FileOptions);
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return new WorkbenchBundleExport(fullPath, applicationId, !sourcesSkipped, manifest.Entries.Count);
    }

    /// <summary>Reads bundle.json without extracting anything else.</summary>
    public static WorkbenchBundleManifest ReadManifest(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Bundle not found: {path}", path);
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(NormalizeEntryName(e.FullName), ManifestName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Bundle '{path}' has no bundle.json.");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<WorkbenchBundleManifest>(stream, WorkbenchJson.FileOptions)
               ?? throw new InvalidOperationException($"Bundle '{path}' has no bundle.json.");
    }

    /// <summary>
    /// The application id the bundle would import as, after validation: the given id, or the manifest's.
    /// </summary>
    public static string TargetApplicationId(WorkbenchBundleManifest manifest, string? applicationId)
    {
        var id = string.IsNullOrWhiteSpace(applicationId) ? manifest.ApplicationId : applicationId;
        if (!ApplicationIds.IsValid(id))
            throw new InvalidOperationException(
                $"Invalid ApplicationId '{id}'; expected catalog:<name>, custom:<name> or byo:<id>.");
        return id!;
    }

    /// <summary>
    /// Imports a bundle. The archive is extracted into a private temp folder; every
    /// entry name is checked before it is written, every manifest entry is hash-checked,
    /// and the extracted tree is walked so a file the manifest does not list refuses the
    /// import. The application folder is replaced only after every check passed, and
    /// an existing application is replaced only with <paramref name="force"/>.
    /// </summary>
    public static WorkbenchBundleImport Import(WorkbenchStore store, string path, string? applicationId = null, bool force = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Bundle not found: {path}", path);
        var staging = Path.Combine(Path.GetTempPath(), "apwb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Extract(path, staging);
            var manifestPath = Path.Combine(staging, ManifestName);
            WorkbenchBundleManifest? manifest;
            try
            {
                manifest = File.Exists(manifestPath)
                    ? JsonSerializer.Deserialize<WorkbenchBundleManifest>(File.ReadAllText(manifestPath), WorkbenchJson.FileOptions)
                    : null;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Bundle '{path}' has an unreadable bundle.json: {ex.Message}", ex);
            }
            if (manifest is null) throw new InvalidOperationException($"Bundle '{path}' has no bundle.json.");
            manifest.Entries ??= [];
            if (manifest.SchemaVersion > SchemaVersion)
                throw new InvalidOperationException(
                    $"Bundle schema {manifest.SchemaVersion} is newer than this build supports ({SchemaVersion}).");

            var payload = Path.GetFullPath(Path.Combine(staging, PayloadFolder)).TrimEnd('\\');
            var verified = new Dictionary<string, WorkbenchBundleEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest.Entries)
            {
                if (entry is null) continue;
                var relative = entry.RelativePath ?? "";
                var file = ResolveEntry(payload, relative);
                if (!File.Exists(file)) throw new InvalidOperationException($"Bundle entry '{relative}' is missing from the archive.");
                StageFileCopier.AssertNoReparsePoint(staging, file);
                if (!string.Equals(WorkbenchJson.FileSha256(file), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Bundle entry '{relative}' failed its hash check.");
                var key = file[(payload.Length + 1)..];
                if (!verified.TryAdd(key, entry))
                    throw new InvalidOperationException($"Bundle entry '{relative}' is listed twice.");
            }

            // Only what the manifest lists and hashed is imported; an extra file in the archive would otherwise arrive unverified.
            foreach (var file in Directory.EnumerateFiles(staging, "*", EveryEntry))
            {
                var full = Path.GetFullPath(file);
                if (string.Equals(full, Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase)) continue;
                var relative = full.StartsWith(payload + "\\", StringComparison.OrdinalIgnoreCase)
                    ? full[(payload.Length + 1)..]
                    : full[(Path.GetFullPath(staging).TrimEnd('\\').Length + 1)..];
                if (!full.StartsWith(payload + "\\", StringComparison.OrdinalIgnoreCase) || !verified.ContainsKey(relative))
                    throw new InvalidOperationException($"Bundle carries '{relative}', which its manifest does not list; refusing the import.");
            }
            foreach (var folder in Directory.EnumerateDirectories(staging, "*", EveryEntry))
            {
                if (WorkbenchJson.IsReparsePoint(folder))
                    throw new InvalidOperationException($"Bundle folder '{folder}' is a reparse point; refusing the import.");
            }

            var targetId = TargetApplicationId(manifest, applicationId);
            var target = store.GetApplicationFolder(targetId);
            if (WorkbenchJson.IsReparsePoint(store.ApplicationsRoot) || WorkbenchJson.IsReparsePoint(store.DataRoot))
                throw new InvalidOperationException($"The applications folder '{store.ApplicationsRoot}' resolves through a reparse point; refusing the import.");
            var exists = Directory.Exists(target) || File.Exists(target);
            if (exists && !force)
                throw new InvalidOperationException(
                    $"Application '{targetId}' already exists in this data root; re-run with -Force to replace it.");
            if (exists && WorkbenchJson.IsReparsePoint(target))
                throw new InvalidOperationException($"Application folder '{target}' is a reparse point; refusing to replace it.");

            Directory.CreateDirectory(store.ApplicationsRoot);
            var incoming = WorkbenchPaths.CombineUnder(store.ApplicationsRoot, ".import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(incoming);
            try
            {
                foreach (var (relative, entry) in verified)
                {
                    var written = StageFileCopier.Copy(incoming, Path.Combine(payload, relative), relative);
                    if (!string.Equals(WorkbenchJson.FileSha256(written), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Bundle entry '{relative}' changed while it was imported.");
                }
                Rebind(incoming, targetId, target);
                Swap(store, incoming, target, exists);
            }
            finally
            {
                if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
            }

            return new WorkbenchBundleImport(targetId, manifest.Entries.Count, manifest.SourcesIncluded,
                manifest.SourceStatus ?? (manifest.SourcesIncluded ? WorkbenchBundleManifest.Complete : WorkbenchBundleManifest.IncompleteSource));
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Extracts entry by entry. A name that is rooted, climbs out, names a stream or a
    /// device, or repeats another entry is refused before anything is written for it.
    /// </summary>
    private static void Extract(string path, string staging)
    {
        var root = Path.GetFullPath(staging).TrimEnd('\\');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeEntryName(entry.FullName);
            if (name.EndsWith('\\'))
            {
                if (name.Trim('\\').Length > 0) ResolveEntry(root, name.TrimEnd('\\'));
                continue;
            }
            var destination = ResolveEntry(root, name);
            if (!seen.Add(destination)) throw new InvalidOperationException($"Bundle entry '{entry.FullName}' appears twice in the archive.");
            var folder = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(folder);
            StageFileCopier.AssertNoReparsePoint(root, folder);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static string NormalizeEntryName(string name) => name.Replace('/', '\\');

    /// <summary>
    /// Resolves a bundle-relative name under <paramref name="root"/>, refusing a rooted
    /// name, a stream or drive colon, a device name, a dot-only or trailing-dot segment,
    /// and any name whose resolved full path leaves the root.
    /// </summary>
    private static string ResolveEntry(string root, string relative)
    {
        var name = NormalizeEntryName(relative ?? "");
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.StartsWith('\\'))
            throw new InvalidOperationException($"Bundle entry '{relative}' is not a relative path.");
        if (name.Contains(':'))
            throw new InvalidOperationException($"Bundle entry '{relative}' names a drive or an alternate data stream.");
        foreach (var segment in name.Split('\\'))
        {
            if (segment is "." or ".." || !ApplicationIds.IsPlainFolderName(segment) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"Bundle entry '{relative}' escapes the bundle.");
        }
        try { StageFileCopier.AssertPlainDestination(name); }
        catch (InvalidOperationException) { throw new InvalidOperationException($"Bundle entry '{relative}' uses a device name."); }

        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        var full = Path.GetFullPath(Path.Combine(rootFull, name));
        if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bundle entry '{relative}' escapes the bundle.");
        return full;
    }

    /// <summary>
    /// Points the imported records at the id they now live under: application.json,
    /// each profile.json, and each BYO revision's stored path.
    /// </summary>
    private static void Rebind(string folder, string applicationId, string finalFolder)
    {
        var definitionPath = Path.Combine(folder, "application.json");
        if (WorkbenchJson.Read(definitionPath) is JsonObject definition)
        {
            definition["ApplicationId"] = applicationId;
            if (definition["Sources"] is JsonArray sources)
            {
                foreach (var node in sources)
                {
                    if (node is not JsonObject source) continue;
                    var revision = WorkbenchJson.AsInt(source["Revision"]);
                    var fileName = WorkbenchJson.AsText(source["FileName"]);
                    source["Path"] = revision is null || string.IsNullOrWhiteSpace(fileName)
                        ? null
                        : Path.Combine(finalFolder, "sources", revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), fileName);
                }
            }
            WorkbenchJson.Write(definitionPath, definition);
        }

        var profiles = Path.Combine(folder, "profiles");
        if (!Directory.Exists(profiles)) return;
        foreach (var profileFolder in Directory.EnumerateDirectories(profiles, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var profilePath = Path.Combine(profileFolder, "profile.json");
            if (WorkbenchJson.Read(profilePath) is not JsonObject profile) continue;
            profile["ApplicationId"] = applicationId;
            WorkbenchJson.Write(profilePath, profile);
        }
    }

    /// <summary>Moves the verified folder into place; an application it replaces is moved aside first and removed after.</summary>
    private static void Swap(WorkbenchStore store, string incoming, string target, bool replace)
    {
        string? aside = null;
        if (replace)
        {
            aside = WorkbenchPaths.CombineUnder(store.ApplicationsRoot, ".replaced-" + Guid.NewGuid().ToString("N"));
            Directory.Move(target, aside);
        }
        try
        {
            Directory.Move(incoming, target);
        }
        catch
        {
            if (aside is not null && !Directory.Exists(target)) Directory.Move(aside, target);
            throw;
        }
        if (aside is not null)
        {
            try { Directory.Delete(aside, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
