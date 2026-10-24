using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One JSON file per run in &lt;data root&gt;\PivotsScripts\Sessions, named &lt;id&gt;.json. Save creates the
/// file and fails when it exists; Update replaces it through a temporary file in the same folder. A
/// folder or a file that is a junction or symbolic link is refused, and a file larger than
/// <see cref="MaxFileBytes"/> is not read.
/// </summary>
public sealed partial class PivotsSessionStore(string? folder = null, IAppLog? log = null) : IPivotsSessionStore
{
    public const string LogArea = "PivotsScripts";
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The raw responses one session file keeps; later responses are left out and the record says so.</summary>
    public const int MaxRawBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex IdRegex();

    public static string DefaultFolder() => Path.Combine(PivotsFolders.Root(), "Sessions");

    public string Folder => folder ?? DefaultFolder();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public void Save(PivotsSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = PathOf(record.Id);
        CheckFolder();
        try
        {
            Directory.CreateDirectory(Folder);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = Serialize(record);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warning(LogArea, $"A session file could not be written ({ex.GetType().Name}).");
            throw new PivotsServiceException("The session could not be saved before the run, so nothing was sent. Check that the sessions folder can be written: " + Folder);
        }
    }

    public void Update(PivotsSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = PathOf(record.Id);
        CheckFolder();
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            if (!File.Exists(path)) throw new PivotsServiceException("The session file of the run does not exist any more, so the results were not saved.");
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new PivotsServiceException("The session file is a symbolic link or another reparse point, so it was not written.");
            File.WriteAllBytes(temp, Serialize(record));
            File.Replace(temp, path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warning(LogArea, $"A session file could not be updated ({ex.GetType().Name}).");
            throw new PivotsServiceException("The results could not be saved in the session file. Check that the sessions folder can be written.");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The session files a List or a FindByOperation call read; a test counts the work of a call with it.</summary>
    internal int FilesRead { get; private set; }

    /// <summary>
    /// Reads the newest files first and stops at <paramref name="max"/> readable sessions; at most
    /// <paramref name="max"/> times two files are read, so damaged files cannot make a call read every file.
    /// </summary>
    public IReadOnlyList<PivotsSessionSummary> List(int max)
    {
        FilesRead = 0;
        var list = new List<PivotsSessionSummary>();
        foreach (var r in Newest(max))
        {
            list.Add(new PivotsSessionSummary(r.Id, r.CreatedUtc, r.Kind, r.TargetDescription, r.Devices.Count, r.Status, r.OperationId));
            if (list.Count >= max) break;
        }
        return list.OrderByDescending(s => s.CreatedUtc).ToList();
    }

    /// <summary>Reads the newest files first and stops at the first session of the operation.</summary>
    public PivotsSessionRecord? FindByOperation(string operationId, int max)
    {
        FilesRead = 0;
        var seen = 0;
        foreach (var r in Newest(max))
        {
            if (r.OperationId == operationId) return r;
            if (++seen >= max) break;
        }
        return null;
    }

    private IEnumerable<PivotsSessionRecord> Newest(int max)
    {
        if (max <= 0 || !Directory.Exists(Folder)) yield break;
        CheckFolder();
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(Folder).GetFiles("*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PivotsServiceException("The sessions folder could not be read: " + Folder);
        }
        foreach (var file in files.Where(f => IdRegex().IsMatch(Path.GetFileNameWithoutExtension(f.Name))).OrderByDescending(f => f.LastWriteTimeUtc).Take(max * 2))
        {
            FilesRead++;
            if (Read(Path.GetFileNameWithoutExtension(file.Name)) is { } r) yield return r;
        }
    }

    public PivotsSessionRecord? Read(string id)
    {
        if (!IdRegex().IsMatch(id ?? "")) return null;
        var path = Path.Combine(Folder, id + ".json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxFileBytes) return null;
            if (DataFolderLinks.HasLink(Folder, DataFolderLinks.ContainerOf(Folder, folder is null))) return null;
            var record = JsonSerializer.Deserialize<PivotsSessionRecord>(File.ReadAllBytes(path), Options);
            return record is not null && record.Id == id ? Migrated(record) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log?.Warning(LogArea, $"A session file could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>The record with the raw responses cut to <see cref="MaxRawBytes"/>, newest kept last.</summary>
    internal static PivotsSessionRecord Bounded(PivotsSessionRecord record)
    {
        var total = 0L;
        var kept = new List<PivotsRawRecord>();
        var cut = 0;
        foreach (var raw in record.Raw)
        {
            total += raw.Body.Length;
            if (total > MaxRawBytes) { cut++; continue; }
            kept.Add(raw);
        }
        if (cut == 0) return record;
        kept.Add(new PivotsRawRecord(DateTime.UtcNow, "Note", null, $"{cut} more raw responses were not kept: the session file keeps at most {MaxRawBytes / (1024 * 1024)} MB of raw responses."));
        return record with { Raw = kept };
    }

    private static byte[] Serialize(PivotsSessionRecord record) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Migrated(Bounded(record)), Options));

    /// <summary>
    /// The parameter names of a run, sorted and without duplicates. A session file holds no value and
    /// no hash or length of a value: a short or guessable value is recoverable from its unsalted hash.
    /// </summary>
    public static IReadOnlyList<string> NamesOf(IReadOnlyDictionary<string, string>? parameters) =>
        NamesOf(parameters?.Keys);

    private static List<string> NamesOf(IEnumerable<string>? names) =>
        (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A record in the current form: the names of a "Parameters" map of an earlier version are kept, its hashes are dropped.</summary>
    private static PivotsSessionRecord Migrated(PivotsSessionRecord record) => record with
    {
        ParameterNames = NamesOf(record.ParameterNames is { Count: > 0 } names ? names : record.LegacyParameters?.Keys),
        LegacyParameters = null
    };

    private string PathOf(string id)
    {
        if (!IdRegex().IsMatch(id ?? "")) throw new PivotsServiceException("The session ID is not valid, so the session was not saved.");
        return Path.Combine(Folder, id + ".json");
    }

    private void CheckFolder()
    {
        if (DataFolderLinks.HasLink(Folder, DataFolderLinks.ContainerOf(Folder, folder is null)))
            throw new PivotsServiceException("The sessions folder is a junction or symbolic link, so nothing was saved and nothing was sent.");
    }
}
