using System.Text.Json;
using System.Text.Json.Serialization;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>The data folder of the Site Hygiene page: &lt;data root&gt;\SiteHygiene.</summary>
public static class HygieneFolders
{
    public static string Root() => Path.Combine(AppDataPaths.Root, "SiteHygiene");
}

/// <summary>
/// One JSON document in a file: a write goes to a temporary file in the same folder and replaces the file
/// in one move, so a reader sees the old or the new document, never a part. A read returns null for a
/// missing, empty, larger than <see cref="MaxFileBytes"/>, linked or unreadable file.
/// </summary>
internal static class HygieneJsonFile
{
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static T? Read<T>(string path, IAppLog? log, string what, long maxBytes = MaxFileBytes) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length == 0) return null;
            if (info.Length > maxBytes)
            {
                log?.Warning(SiteHygieneService.LogArea, $"The {what} file is larger than a read takes; it is ignored.");
                return null;
            }
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log?.Warning(SiteHygieneService.LogArea, $"The {what} file could not be read ({ex.GetType().Name}); it is ignored.");
            return null;
        }
    }

    /// <summary>
    /// The document before a change: null for a missing or empty file. A file that exists but cannot be
    /// read throws, so a change never replaces a list it could not read.
    /// </summary>
    public static T? ReadForChange<T>(string path, IAppLog? log, string what) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new HygieneServiceException($"The {what} file is a symbolic link or another reparse point, so it was not changed: {path}");
            if (info.Length == 0) return null;
            if (info.Length > MaxFileBytes)
                throw new HygieneServiceException($"The {what} file is larger than {MaxFileBytes / (1024 * 1024)} MB, so it was not changed: {path}");
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log?.Warning(SiteHygieneService.LogArea, $"The {what} file could not be read before a change ({ex.GetType().Name}); it was not changed.");
            throw new HygieneServiceException($"The {what} file could not be read, so it was not changed. Close the program that uses it, or move the file away if it is damaged: {path}");
        }
    }

    public static void Write<T>(string path, T value, bool isDefaultFolder, string what, long maxBytes = MaxFileBytes)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, isDefaultFolder)))
            throw new HygieneServiceException($"The {what} folder is a junction or symbolic link, so the {what} was not saved.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.LongLength > maxBytes)
            throw new HygieneServiceException($"The {what} is larger than {Megabytes(maxBytes)} MB, so it was not saved. A larger file could not be read back.");
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new HygieneServiceException($"The {what} file is a symbolic link or another reparse point, so it was not written.");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new HygieneServiceException($"The {what} could not be saved. Check that the folder can be written: {folder}");
        }
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The suppressions in &lt;data root&gt;\SiteHygiene\suppressions.json, keyed by check ID and object key.</summary>
public sealed class HygieneSuppressionStore(string? path = null, IAppLog? log = null) : IHygieneSuppressionStore
{
    public const string FileName = "suppressions.json";

    /// <summary>The most characters a comment keeps.</summary>
    public const int MaxComment = 500;

    private readonly Lock _gate = new();

    public string Path => path ?? System.IO.Path.Combine(HygieneFolders.Root(), FileName);

    /// <summary>
    /// A suppression as stored; a missing SiteCode (a file of an earlier version) reads as every site, and a
    /// missing Provider as every provider of the site.
    /// </summary>
    private sealed record Stored(string? CheckId, string? ObjectKey, string? Comment, DateTime CreatedUtc, string? SiteCode, string? Provider = null);

    private sealed record Document(int SchemaVersion, List<Stored?>? Suppressions);

    private const string What = "suppression list";

    public IReadOnlyList<Suppression> Load()
    {
        lock (_gate) return Clean(HygieneJsonFile.Read<Document>(Path, log, What));
    }

    private static List<Suppression> Clean(Document? doc) =>
        (doc?.Suppressions ?? [])
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.CheckId) && !string.IsNullOrWhiteSpace(s.ObjectKey))
            .Select(s => new Suppression(s!.CheckId!, s.ObjectKey!, s.Comment ?? "", s.CreatedUtc) { SiteCode = Site(s.SiteCode), Provider = Provider(s.SiteCode, s.Provider) })
            .GroupBy(s => s.StoreKey, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .ToList();

    private static string Site(string? siteCode) => HygieneSite.Code(siteCode);

    /// <summary>An every-site suppression has no provider; a site suppression keeps the provider it recorded, or null.</summary>
    private static string? Provider(string? siteCode, string? provider) =>
        Site(siteCode).Length == 0 || provider is null ? null : HygieneSite.Provider(provider);

    private void Save(List<Suppression> list) =>
        HygieneJsonFile.Write(Path, new Document(2, list.Select(s => (Stored?)new Stored(s.CheckId, s.ObjectKey, s.Comment, s.CreatedUtc, s.SiteCode, s.Provider)).ToList()), path is null, What);

    public void Add(Suppression suppression) => AddRange([suppression]);

    public void AddRange(IReadOnlyList<Suppression> suppressions)
    {
        ArgumentNullException.ThrowIfNull(suppressions);
        var clean = new List<Suppression>();
        foreach (var suppression in suppressions)
        {
            ArgumentNullException.ThrowIfNull(suppression);
            if (string.IsNullOrWhiteSpace(suppression.CheckId) || string.IsNullOrWhiteSpace(suppression.ObjectKey))
                throw new HygieneServiceException("A suppression needs a check ID and an object key.");
            var comment = (suppression.Comment ?? "").Trim();
            if (comment.Length > MaxComment) comment = comment[..MaxComment];
            clean.Add(suppression with
            {
                CheckId = suppression.CheckId.Trim().ToUpperInvariant(), Comment = comment, CreatedUtc = DateTime.SpecifyKind(suppression.CreatedUtc, DateTimeKind.Utc),
                SiteCode = Site(suppression.SiteCode),
                Provider = Provider(suppression.SiteCode, suppression.Provider)
            });
        }
        if (clean.Count == 0) return;
        lock (_gate)
        {
            var added = clean.Select(s => s.StoreKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = Clean(HygieneJsonFile.ReadForChange<Document>(Path, log, What)).Where(s => !added.Contains(s.StoreKey)).ToList();
            list.AddRange(clean.GroupBy(s => s.StoreKey, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()));
            Save(list);
        }
    }

    public void Remove(string checkId, string objectKey) => RemoveRange("", [(checkId, objectKey)]);

    public void RemoveRange(string siteCode, IReadOnlyList<(string CheckId, string ObjectKey)> keys, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var site = Site(siteCode);
        var wanted = keys.Select(k => HygieneFinding.KeyOf(k.CheckId, k.ObjectKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return;
        lock (_gate)
        {
            var list = Clean(HygieneJsonFile.ReadForChange<Document>(Path, log, What));
            var kept = list.Where(s => !(wanted.Contains(s.Key) && s.AppliesTo(site, provider))).ToList();
            if (kept.Count == list.Count) return;
            Save(kept);
        }
    }
}

/// <summary>The last scan in &lt;data root&gt;\SiteHygiene\last-scan.json; the New column compares the next scan with its keys.</summary>
public sealed class HygieneScanStore(string? path = null, IAppLog? log = null) : IHygieneScanStore
{
    public const string FileName = "last-scan.json";

    /// <summary>The largest file Save writes; it equals the largest file a read takes, so a saved scan always loads.</summary>
    internal long MaxBytes { get; init; } = HygieneJsonFile.MaxFileBytes;

    public string Path => path ?? System.IO.Path.Combine(HygieneFolders.Root(), FileName);

    private sealed record StoredFinding(string CheckId, string Category, HygieneSeverity Severity, string ObjectType, string ObjectKey, string ObjectName,
        string Evidence, string Recommendation, HygieneFixKind FixKind, Dictionary<string, string>? FixParameters, string? FixText, string? Guidance, string? FixRefusal = null);

    private sealed record StoredCheck(string CheckId, string Name, string Category, string State, int Findings, string? Reason, HygieneSeverity Severity);

    /// <summary>A file of an earlier version has no Provider; its scan then belongs to no connection.</summary>
    private sealed record Document(int SchemaVersion, DateTime ScannedUtc, string? SiteCode, List<string>? Areas, bool Truncated, string? Note, int QueryCount,
        List<StoredFinding?>? Findings, List<StoredCheck?>? Checks, string? Provider = null);

    public void Save(HygieneScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var doc = new Document(1, DateTime.SpecifyKind(result.ScannedUtc, DateTimeKind.Utc), result.SiteCode, result.Areas.ToList(), result.Truncated, result.Note, result.QueryCount,
            result.Findings.Select(f => (StoredFinding?)new StoredFinding(f.CheckId, f.Category, f.Severity, f.ObjectType, f.ObjectKey, f.ObjectName, f.Evidence, f.Recommendation,
                f.FixKind, f.FixParameters.ToDictionary(p => p.Key, p => p.Value), f.FixText, f.Guidance, f.FixRefusal)).ToList(),
            result.Checks.Select(c => (StoredCheck?)new StoredCheck(c.CheckId, c.Name, c.Category, c.State, c.Findings, c.Reason, c.Severity)).ToList(),
            result.Provider is null ? null : HygieneSite.Provider(result.Provider));
        HygieneJsonFile.Write(Path, doc, path is null, "last scan", MaxBytes);
    }

    public HygieneScanResult? Load()
    {
        var doc = HygieneJsonFile.Read<Document>(Path, log, "last scan", MaxBytes);
        if (doc is null || doc.Findings is null || doc.Checks is null) return null;
        var findings = doc.Findings.Where(f => f is not null && !string.IsNullOrEmpty(f.CheckId) && f.ObjectKey is not null)
            .Select(f => new HygieneFinding(f!.CheckId, f.Category ?? "", f.Severity, f.ObjectType ?? "", f.ObjectKey, f.ObjectName ?? "", f.Evidence ?? "", f.Recommendation ?? "",
                f.FixKind, f.FixParameters ?? new Dictionary<string, string>(), f.FixText, IsNew: false, IsSuppressed: false) { Guidance = f.Guidance ?? "", FixRefusal = f.FixRefusal ?? "" }).ToList();
        var checks = doc.Checks.Where(c => c is not null && !string.IsNullOrEmpty(c.CheckId))
            .Select(c => new HygieneCheckStatus(c!.CheckId, c.Name ?? "", c.Category ?? "", c.State ?? HygieneCheckStates.NotRun, c.Findings, c.Reason) { Severity = c.Severity }).ToList();
        return new HygieneScanResult(findings, checks, DateTime.SpecifyKind(doc.ScannedUtc, DateTimeKind.Utc), doc.Truncated, doc.Note)
        {
            Areas = doc.Areas ?? [],
            SiteCode = doc.SiteCode ?? "",
            Provider = doc.Provider,
            QueryCount = 0,
            Stored = true
        };
    }

    public IReadOnlySet<string>? Keys() =>
        Load() is { } last ? last.Findings.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
}
