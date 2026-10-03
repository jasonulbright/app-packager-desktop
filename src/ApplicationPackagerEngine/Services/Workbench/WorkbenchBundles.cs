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

/// <summary>What one bundle import accepts before it extracts anything.</summary>
public sealed record WorkbenchBundleLimits(int MaxEntries, long MaxTotalBytes, long MaxManifestBytes)
{
    /// <summary>
    /// A bundle holds one definition, its profiles with their assets and the BYO installer
    /// revisions; the cap bounds the number of files one archive can make an import create.
    /// </summary>
    public const int DefaultMaxEntries = 10_000;

    /// <summary>
    /// Bounds the bytes one import writes to the temp folder and the store. An Intune Win32
    /// app's content is limited to 30 GB, so two revisions of the largest installer a
    /// publish accepts fit under this cap.
    /// </summary>
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>bundle.json is read into memory before any other check; a manifest at the entry cap stays far below this size.</summary>
    public const long DefaultMaxManifestBytes = 16L * 1024 * 1024;

    public static readonly WorkbenchBundleLimits Default = new(DefaultMaxEntries, DefaultMaxTotalBytes, DefaultMaxManifestBytes);
}

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
    /// The confirmation text for an import that replaces an existing application: what the
    /// import removes, and the bundle's source status when it carries no installers.
    /// </summary>
    public static string ReplacePrompt(string applicationId, WorkbenchBundleManifest manifest)
    {
        var text = $"Application '{applicationId}' already exists in this data root. Replace it with the bundle's definition, profiles and assets? " +
                   "Profiles, assets and installer revisions that are not in the bundle are removed, and so are the application's sealed build records.";
        var status = manifest.SourceStatus ?? (manifest.SourcesIncluded ? WorkbenchBundleManifest.Complete : WorkbenchBundleManifest.IncompleteSource);
        if (string.Equals(status, WorkbenchBundleManifest.IncompleteSource, StringComparison.OrdinalIgnoreCase))
            text += " Source status: IncompleteSource. The bundle carries no installers, so every stored installer revision is removed and the application needs Replace installer before it stages.";
        return text;
    }

    /// <summary>
    /// Imports a bundle. Before anything is extracted, every archive name is checked, a
    /// size-bounded bundle.json is read, and every archive entry must be listed in it with
    /// the same uncompressed size, within <see cref="WorkbenchBundleLimits.Default"/>. Only
    /// listed entries are extracted, each stopped at its listed size and hash-checked. The
    /// application.json must belong to the manifest's application and every profile must be
    /// stored under its own id. An existing application is replaced only with
    /// <paramref name="force"/>; its build records go with it.
    /// </summary>
    public static WorkbenchBundleImport Import(WorkbenchStore store, string path, string? applicationId = null, bool force = false) =>
        Import(store, path, applicationId, force, WorkbenchBundleLimits.Default);

    internal static WorkbenchBundleImport Import(WorkbenchStore store, string path, string? applicationId, bool force,
        WorkbenchBundleLimits limits, Action<string>? beforeSwap = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Bundle not found: {path}", path);
        var staging = Path.Combine(Path.GetTempPath(), "apwb-" + Guid.NewGuid().ToString("N"));
        var stagingRoot = Path.GetFullPath(staging).TrimEnd('\\');
        var payload = Path.Combine(stagingRoot, PayloadFolder);
        try
        {
            WorkbenchBundleManifest manifest;
            Dictionary<string, (WorkbenchBundleEntry Listed, ZipArchiveEntry Archived)> listed;
            using (var zip = ZipFile.OpenRead(path))
            {
                var archived = ArchiveFiles(zip, stagingRoot, limits);
                manifest = ReadManifest(path, archived, limits);
                listed = MatchEntries(manifest, archived, payload, limits);
                Directory.CreateDirectory(staging);
                foreach (var (key, (entry, archive)) in listed)
                    ExtractEntry(archive, ResolveEntry(payload, key), entry, stagingRoot);
            }

            var verified = listed.ToDictionary(e => e.Key, e => e.Value.Listed, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.EnumerateDirectories(staging, "*", EveryEntry))
            {
                if (WorkbenchJson.IsReparsePoint(folder))
                    throw new InvalidOperationException($"Bundle folder '{folder}' is a reparse point; refusing the import.");
            }

            var targetId = TargetApplicationId(manifest, applicationId);
            AssertApplicationFile(path, manifest, payload, verified);
            AssertProfiles(payload, verified);
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
                var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (relative, entry) in verified)
                {
                    var written = StageFileCopier.Copy(incoming, Path.Combine(payload, relative), relative);
                    if (!string.Equals(WorkbenchJson.FileSha256(written), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Bundle entry '{relative}' changed while it was imported.");
                    expected[relative] = entry.Sha256;
                }
                foreach (var (relative, hash) in Rebind(incoming, targetId, target)) expected[relative] = hash;
                beforeSwap?.Invoke(incoming);
                Swap(store, incoming, target, targetId, expected);
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
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Every file entry of the archive by its resolved name under the staging root. A name
    /// that is rooted, climbs out, names a stream or a device, or repeats another entry is
    /// refused here, before anything is written.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> ArchiveFiles(ZipArchive zip, string stagingRoot, WorkbenchBundleLimits limits)
    {
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeEntryName(entry.FullName);
            if (name.EndsWith('\\'))
            {
                if (name.Trim('\\').Length > 0) ResolveEntry(stagingRoot, name.TrimEnd('\\'));
                continue;
            }
            var destination = ResolveEntry(stagingRoot, name);
            if (!files.TryAdd(destination[(stagingRoot.Length + 1)..], entry))
                throw new InvalidOperationException($"Bundle entry '{entry.FullName}' appears twice in the archive.");
            if (files.Count > limits.MaxEntries + 1)
                throw new InvalidOperationException($"Bundle has more than {limits.MaxEntries} entries; refusing the import.");
        }
        return files;
    }

    /// <summary>Reads bundle.json from the archive, refusing one larger than the manifest limit before it is read.</summary>
    private static WorkbenchBundleManifest ReadManifest(string path, Dictionary<string, ZipArchiveEntry> archived, WorkbenchBundleLimits limits)
    {
        if (!archived.TryGetValue(ManifestName, out var entry)) throw new InvalidOperationException($"Bundle '{path}' has no bundle.json.");
        if (entry.Length > limits.MaxManifestBytes)
            throw new InvalidOperationException($"Bundle '{path}': bundle.json is larger than {limits.MaxManifestBytes} bytes; refusing the import.");
        byte[] bytes;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            CopyBounded(stream, buffer, limits.MaxManifestBytes,
                () => new InvalidOperationException($"Bundle '{path}': bundle.json is larger than {limits.MaxManifestBytes} bytes; refusing the import."));
            bytes = buffer.ToArray();
        }
        WorkbenchBundleManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<WorkbenchBundleManifest>(bytes, WorkbenchJson.FileOptions); }
        catch (JsonException ex) { throw new InvalidOperationException($"Bundle '{path}' has an unreadable bundle.json: {ex.Message}", ex); }
        if (manifest is null) throw new InvalidOperationException($"Bundle '{path}' has no bundle.json.");
        manifest.Entries ??= [];
        if (manifest.SchemaVersion > SchemaVersion)
            throw new InvalidOperationException(
                $"Bundle schema {manifest.SchemaVersion} is newer than this build supports ({SchemaVersion}).");
        return manifest;
    }

    /// <summary>
    /// Pairs every manifest entry with its archive entry, by payload-relative name. An
    /// archive entry the manifest does not list, an entry whose uncompressed length differs
    /// from its listed size, and a bundle over the entry or size limit are refused.
    /// </summary>
    private static Dictionary<string, (WorkbenchBundleEntry Listed, ZipArchiveEntry Archived)> MatchEntries(
        WorkbenchBundleManifest manifest, Dictionary<string, ZipArchiveEntry> archived, string payload, WorkbenchBundleLimits limits)
    {
        var entries = manifest.Entries.Where(e => e is not null).ToList();
        if (entries.Count > limits.MaxEntries)
            throw new InvalidOperationException($"Bundle has more than {limits.MaxEntries} entries; refusing the import.");
        var payloadRoot = Path.GetFullPath(payload).TrimEnd('\\');
        var listed = new Dictionary<string, (WorkbenchBundleEntry, ZipArchiveEntry)>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            var relative = entry.RelativePath ?? "";
            var key = ResolveEntry(payloadRoot, relative)[(payloadRoot.Length + 1)..];
            if (listed.ContainsKey(key)) throw new InvalidOperationException($"Bundle entry '{relative}' is listed twice.");
            if (!archived.TryGetValue(PayloadFolder + "\\" + key, out var archive))
                throw new InvalidOperationException($"Bundle entry '{relative}' is missing from the archive.");
            if (entry.Size < 0 || archive.Length != entry.Size)
                throw new InvalidOperationException(
                    $"Bundle entry '{relative}' is {archive.Length} bytes in the archive, which differs from the size its manifest lists ({entry.Size}); refusing the import.");
            total += entry.Size;
            if (total > limits.MaxTotalBytes)
                throw new InvalidOperationException($"Bundle holds more than {limits.MaxTotalBytes} bytes; refusing the import.");
            listed[key] = (entry, archive);
        }

        // Only what the manifest lists and hashed is imported; an extra entry in the archive would otherwise arrive unverified.
        foreach (var name in archived.Keys)
        {
            if (string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            var prefix = PayloadFolder + "\\";
            var underPayload = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            var relative = underPayload ? name[prefix.Length..] : name;
            if (!underPayload || !listed.ContainsKey(relative))
                throw new InvalidOperationException($"Bundle carries '{relative}', which its manifest does not list; refusing the import.");
        }
        return listed;
    }

    /// <summary>
    /// Writes one listed entry, hashing what it writes. Data past the listed size stops the
    /// extraction at once, so an entry whose recorded length is false never fills the disk.
    /// </summary>
    private static void ExtractEntry(ZipArchiveEntry archive, string destination, WorkbenchBundleEntry entry, string stagingRoot)
    {
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        StageFileCopier.AssertNoReparsePoint(stagingRoot, folder);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written;
        using (var input = archive.Open())
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            written = CopyBounded(input, output, entry.Size,
                () => new InvalidOperationException($"Bundle entry '{entry.RelativePath}' holds more data than the {entry.Size} bytes its manifest lists; refusing the import."),
                hash);
        }
        if (written != entry.Size
            || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), entry.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bundle entry '{entry.RelativePath}' failed its hash check.");
    }

    private static long CopyBounded(Stream input, Stream output, long limit, Func<Exception> overLimit, IncrementalHash? hash = null)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit) throw overLimit();
            hash?.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        return total;
    }

    /// <summary>
    /// The bundle's application.json must be a definition of the manifest's application:
    /// the same ApplicationId and, when it records one, the same origin. A bundle without one
    /// would replace a stored application with nothing.
    /// </summary>
    private static void AssertApplicationFile(string path, WorkbenchBundleManifest manifest, string payload,
        IReadOnlyDictionary<string, WorkbenchBundleEntry> verified)
    {
        var bundled = manifest.ApplicationId;
        if (!ApplicationIds.IsValid(bundled))
            throw new InvalidOperationException($"Bundle '{path}' names no valid application in its bundle.json; refusing the import.");
        if (!verified.ContainsKey("application.json"))
            throw new InvalidOperationException($"Bundle '{path}' carries no application.json for {bundled}; refusing the import.");
        JsonNode? node;
        try { node = WorkbenchJson.Read(Path.Combine(payload, "application.json")); }
        catch (JsonException ex) { throw new InvalidOperationException($"Bundle application.json is not readable: {ex.Message}; refusing the import.", ex); }
        if (node is not JsonObject definition)
            throw new InvalidOperationException("Bundle application.json is not an application definition; refusing the import.");
        var id = WorkbenchJson.Get(definition, "ApplicationId").Text;
        if (!string.Equals(id, bundled, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bundle application.json is for '{id}', not the bundle's application '{bundled}'; refusing the import.");
        var origin = WorkbenchJson.Get(definition, "Origin").Text;
        if (!string.IsNullOrWhiteSpace(origin) && !string.Equals(origin, ApplicationIds.OriginOf(bundled!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Bundle application.json records origin '{origin}', not the origin of '{bundled}'; refusing the import.");
    }

    /// <summary>
    /// Every bundled profile must carry its folder's name as its ProfileId, and none may be
    /// the default profile: a profile under another folder is listed but cannot be opened,
    /// and a stored default would show a second default.
    /// </summary>
    private static void AssertProfiles(string payload, IReadOnlyDictionary<string, WorkbenchBundleEntry> verified)
    {
        foreach (var relative in verified.Keys)
        {
            var segments = relative.Split('\\');
            if (segments.Length < 2 || !string.Equals(segments[0], "profiles", StringComparison.OrdinalIgnoreCase)) continue;
            var folder = segments[1];
            if (ApplicationIds.IsDefaultProfile(folder))
                throw new InvalidOperationException("Bundle carries a profile folder named default, the id of the profile every application has as shipped; refusing the import.");
            if (segments.Length != 3 || !string.Equals(segments[2], "profile.json", StringComparison.OrdinalIgnoreCase)) continue;
            JsonNode? node;
            try { node = WorkbenchJson.Read(Path.Combine(payload, relative)); }
            catch (JsonException ex) { throw new InvalidOperationException($"Bundle profile '{folder}' is not readable: {ex.Message}; refusing the import.", ex); }
            var profileId = node is JsonObject profile ? WorkbenchJson.Get(profile, "ProfileId").Text : null;
            if (!string.Equals(profileId, folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Bundle profile folder '{folder}' carries ProfileId '{profileId}'; a profile is stored under its own id, so the import is refused.");
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
    /// each profile.json, and each BYO revision's stored path. Returns the SHA-256 of each
    /// file it rewrote, taken from the text it wrote.
    /// </summary>
    private static Dictionary<string, string> Rebind(string folder, string applicationId, string finalFolder)
    {
        var rewritten = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Write(string relative, JsonNode node)
        {
            var text = node.ToJsonString(WorkbenchJson.FileOptions);
            WorkbenchJson.WriteText(Path.Combine(folder, relative), text);
            rewritten[relative] = WorkbenchJson.TextSha256(text);
        }

        if (WorkbenchJson.Read(Path.Combine(folder, "application.json")) is JsonObject definition)
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
            Write("application.json", definition);
        }

        var profiles = Path.Combine(folder, "profiles");
        if (!Directory.Exists(profiles)) return rewritten;
        foreach (var profileFolder in Directory.EnumerateDirectories(profiles, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var relative = Path.Combine("profiles", Path.GetFileName(profileFolder), "profile.json");
            if (WorkbenchJson.Read(Path.Combine(folder, relative)) is not JsonObject profile) continue;
            profile["ApplicationId"] = applicationId;
            Write(relative, profile);
        }
        return rewritten;
    }

    /// <summary>
    /// Moves the verified folder into place. An application it replaces, and that
    /// application's build records, are moved aside first and removed after; a build record
    /// left behind would count an imported profile with the same id and revision as current
    /// and reuse a stage the import never made. A folder holding open files cannot be
    /// renamed on NTFS, so the verified files cannot be held through the move: every file is
    /// hashed again after the move instead, and any difference from what was verified rolls
    /// the import back.
    /// </summary>
    private static void Swap(WorkbenchStore store, string incoming, string target, string applicationId,
        IReadOnlyDictionary<string, string> expected)
    {
        var builds = WorkbenchPaths.CombineUnder(Path.Combine(store.DataRoot, "builds"), ApplicationIds.ToKey(applicationId));
        string? aside = null;
        string? buildsAside = null;
        if (Directory.Exists(target))
        {
            aside = WorkbenchPaths.CombineUnder(store.ApplicationsRoot, ".replaced-" + Guid.NewGuid().ToString("N"));
            Directory.Move(target, aside);
        }
        try
        {
            if (Directory.Exists(builds) && !WorkbenchJson.IsReparsePoint(builds))
            {
                buildsAside = WorkbenchPaths.CombineUnder(Path.Combine(store.DataRoot, "builds"), ".replaced-" + Guid.NewGuid().ToString("N"));
                Directory.Move(builds, buildsAside);
            }
            Directory.Move(incoming, target);
            if (ChangedSinceVerification(target, expected) is { } changed)
            {
                var rejected = WorkbenchPaths.CombineUnder(store.ApplicationsRoot, ".rejected-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, rejected);
                TryDelete(rejected);
                throw new InvalidOperationException($"Bundle entry '{changed}' changed between its verification and the swap; the import was rolled back.");
            }
        }
        catch
        {
            if (aside is not null && !Directory.Exists(target)) Directory.Move(aside, target);
            if (buildsAside is not null && !Directory.Exists(builds)) Directory.Move(buildsAside, builds);
            throw;
        }
        if (aside is not null) TryDelete(aside);
        if (buildsAside is not null) TryDelete(buildsAside);
    }

    /// <summary>The first file under <paramref name="folder"/> that is missing, extra or hashes differently from <paramref name="expected"/>; null when none.</summary>
    private static string? ChangedSinceVerification(string folder, IReadOnlyDictionary<string, string> expected)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\');
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", EveryEntry))
        {
            var relative = file[(root.Length + 1)..];
            if (!expected.TryGetValue(relative, out var hash)) return relative;
            if (WorkbenchJson.IsReparsePoint(file) || !string.Equals(WorkbenchJson.FileSha256(file), hash, StringComparison.OrdinalIgnoreCase))
                return relative;
            seen++;
        }
        return seen == expected.Count ? null : expected.Keys.First(k => !File.Exists(Path.Combine(root, k)));
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
