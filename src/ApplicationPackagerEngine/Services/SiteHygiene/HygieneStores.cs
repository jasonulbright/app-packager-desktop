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

    public static T? Read<T>(string path, IAppLog? log, string what) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length == 0 || info.Length > MaxFileBytes) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log?.Warning(SiteHygieneService.LogArea, $"The {what} file could not be read ({ex.GetType().Name}); it is ignored.");
            return null;
        }
    }

    public static void Write<T>(string path, T value, bool isDefaultFolder, string what)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, isDefaultFolder)))
            throw new HygieneServiceException($"The {what} folder is a junction or symbolic link, so the {what} was not saved.");
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new HygieneServiceException($"The {what} file is a symbolic link or another reparse point, so it was not written.");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Options);
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
}

/// <summary>The suppressions in &lt;data root&gt;\SiteHygiene\suppressions.json, keyed by check ID and object key.</summary>
public sealed class HygieneSuppressionStore(string? path = null, IAppLog? log = null) : IHygieneSuppressionStore
{
    public const string FileName = "suppressions.json";

    /// <summary>The most characters a comment keeps.</summary>
    public const int MaxComment = 500;

    private readonly Lock _gate = new();

    public string Path => path ?? System.IO.Path.Combine(HygieneFolders.Root(), FileName);

    private sealed record Document(int SchemaVersion, List<Suppression?>? Suppressions);

    public IReadOnlyList<Suppression> Load()
    {
        lock (_gate) return LoadCore();
    }

    private List<Suppression> LoadCore() =>
        (HygieneJsonFile.Read<Document>(Path, log, "suppression")?.Suppressions ?? [])
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.CheckId) && !string.IsNullOrWhiteSpace(s.ObjectKey))
            .Select(s => s! with { Comment = s!.Comment ?? "" })
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .ToList();

    public void Add(Suppression suppression)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        if (string.IsNullOrWhiteSpace(suppression.CheckId) || string.IsNullOrWhiteSpace(suppression.ObjectKey))
            throw new HygieneServiceException("A suppression needs a check ID and an object key.");
        var comment = (suppression.Comment ?? "").Trim();
        if (comment.Length > MaxComment) comment = comment[..MaxComment];
        var clean = suppression with { CheckId = suppression.CheckId.Trim().ToUpperInvariant(), Comment = comment, CreatedUtc = DateTime.SpecifyKind(suppression.CreatedUtc, DateTimeKind.Utc) };
        lock (_gate)
        {
            var list = LoadCore().Where(s => !string.Equals(s.Key, clean.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(clean);
            HygieneJsonFile.Write(Path, new Document(1, list!), path is null, "suppression list");
        }
    }

    public void Remove(string checkId, string objectKey)
    {
        var key = HygieneFinding.KeyOf(checkId, objectKey);
        lock (_gate)
        {
            var list = LoadCore();
            var kept = list.Where(s => !string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == list.Count) return;
            HygieneJsonFile.Write(Path, new Document(1, kept!), path is null, "suppression list");
        }
    }
}

/// <summary>The last scan in &lt;data root&gt;\SiteHygiene\last-scan.json; the New column compares the next scan with its keys.</summary>
public sealed class HygieneScanStore(string? path = null, IAppLog? log = null) : IHygieneScanStore
{
    public const string FileName = "last-scan.json";

    public string Path => path ?? System.IO.Path.Combine(HygieneFolders.Root(), FileName);

    private sealed record StoredFinding(string CheckId, string Category, HygieneSeverity Severity, string ObjectType, string ObjectKey, string ObjectName,
        string Evidence, string Recommendation, HygieneFixKind FixKind, Dictionary<string, string>? FixParameters, string? FixText, string? Guidance);

    private sealed record StoredCheck(string CheckId, string Name, string Category, string State, int Findings, string? Reason, HygieneSeverity Severity);

    private sealed record Document(int SchemaVersion, DateTime ScannedUtc, string? SiteCode, List<string>? Areas, bool Truncated, string? Note, int QueryCount,
        List<StoredFinding?>? Findings, List<StoredCheck?>? Checks);

    public void Save(HygieneScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var doc = new Document(1, DateTime.SpecifyKind(result.ScannedUtc, DateTimeKind.Utc), result.SiteCode, result.Areas.ToList(), result.Truncated, result.Note, result.QueryCount,
            result.Findings.Select(f => (StoredFinding?)new StoredFinding(f.CheckId, f.Category, f.Severity, f.ObjectType, f.ObjectKey, f.ObjectName, f.Evidence, f.Recommendation,
                f.FixKind, f.FixParameters.ToDictionary(p => p.Key, p => p.Value), f.FixText, f.Guidance)).ToList(),
            result.Checks.Select(c => (StoredCheck?)new StoredCheck(c.CheckId, c.Name, c.Category, c.State, c.Findings, c.Reason, c.Severity)).ToList());
        HygieneJsonFile.Write(Path, doc, path is null, "last scan");
    }

    public HygieneScanResult? Load()
    {
        var doc = HygieneJsonFile.Read<Document>(Path, log, "last scan");
        if (doc is null || doc.Findings is null || doc.Checks is null) return null;
        var findings = doc.Findings.Where(f => f is not null && !string.IsNullOrEmpty(f.CheckId) && f.ObjectKey is not null)
            .Select(f => new HygieneFinding(f!.CheckId, f.Category ?? "", f.Severity, f.ObjectType ?? "", f.ObjectKey, f.ObjectName ?? "", f.Evidence ?? "", f.Recommendation ?? "",
                f.FixKind, f.FixParameters ?? new Dictionary<string, string>(), f.FixText, IsNew: false, IsSuppressed: false) { Guidance = f.Guidance ?? "" }).ToList();
        var checks = doc.Checks.Where(c => c is not null && !string.IsNullOrEmpty(c.CheckId))
            .Select(c => new HygieneCheckStatus(c!.CheckId, c.Name ?? "", c.Category ?? "", c.State ?? HygieneCheckStates.NotRun, c.Findings, c.Reason) { Severity = c.Severity }).ToList();
        return new HygieneScanResult(findings, checks, DateTime.SpecifyKind(doc.ScannedUtc, DateTimeKind.Utc), doc.Truncated, doc.Note)
        {
            Areas = doc.Areas ?? [],
            SiteCode = doc.SiteCode ?? "",
            QueryCount = 0,
            Stored = true
        };
    }

    public IReadOnlySet<string>? Keys() =>
        Load() is { } last ? last.Findings.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
}
