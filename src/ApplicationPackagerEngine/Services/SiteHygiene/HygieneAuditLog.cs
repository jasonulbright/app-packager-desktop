using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The append-only Site Hygiene audit log at &lt;data root&gt;\SiteHygiene\site-hygiene-audit.jsonl: one compact
/// JSON object per line with the hash chain of <see cref="ChainedJsonlFile"/>. A reservation keeps the file
/// open from the probe to the last append of a run, so a program that opens the file after the probe
/// cannot make a later append fail.
/// </summary>
public sealed class HygieneAuditLog : IHygieneAuditLog
{
    public const string LogArea = "SiteHygiene";
    public const string FileName = "site-hygiene-audit.jsonl";
    public const string SourceName = "Application Packager";

    /// <summary>Only the end of a larger log is read for the history.</summary>
    public const long MaxReadBytes = 16L * 1024 * 1024;

    /// <summary>The most objects one record lists; the service writes a larger run in several records.</summary>
    public const int MaxObjectsPerRecord = 100;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<string?> _path;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _clock;
    private readonly Func<string> _user;
    private readonly object _gate = new();

    public HygieneAuditLog(Func<string?>? path = null, IAppLog? log = null, Func<DateTime>? clock = null, Func<string>? user = null)
    {
        _path = path ?? (() => null);
        _log = log;
        _clock = clock ?? (() => DateTime.Now);
        _user = user ?? (() => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
    }

    public static string DefaultPath() => System.IO.Path.Combine(AppDataPaths.Root, "SiteHygiene", FileName);

    public string Path => _path() is { } custom && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : DefaultPath();

    private bool IsDefault => _path() is not { } custom || string.IsNullOrWhiteSpace(custom);

    public int SkippedLines { get; private set; }

    public bool OlderRecordsDropped { get; private set; }

    public AuditProbe Probe()
    {
        using var reservation = Reserve();
        return reservation.Probe;
    }

    public AuditWrite Append(HygieneAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var reservation = Reserve();
        return reservation.Probe.Writable ? reservation.Append(r) : new AuditWrite(false, reservation.Probe.Message);
    }

    /// <summary>Opens the log for writing and keeps it open until the reservation is disposed.</summary>
    public HygieneAuditReservation Reserve()
    {
        var path = Path;
        var (stream, message) = ChainedJsonlFile.Open(path, IsDefault, "The Site Hygiene audit log", FileName);
        if (stream is null) _log?.Warning(LogArea, "The Site Hygiene audit log could not be opened for writing.");
        return new HygieneAuditReservation(this, stream, new AuditProbe(stream is not null, stream is not null ? $"The Site Hygiene audit log can be written: {path}" : message));
    }

    internal AuditWrite Write(FileStream stream, HygieneAuditRecord record)
    {
        string body;
        try
        {
            body = Serialize(record);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _log?.Warning(LogArea, $"An audit record could not be written ({ex.GetType().Name}).");
            return new AuditWrite(false, "The audit record could not be built. Nothing was written.");
        }
        lock (_gate)
            return ChainedJsonlFile.Append(stream, body, Path, _log, LogArea);
    }

    private string Serialize(HygieneAuditRecord r)
    {
        if (r.Objects.Count > MaxObjectsPerRecord) throw new InvalidOperationException("Too many objects for one record.");
        var objects = new JsonArray();
        foreach (var o in r.Objects)
            objects.Add(new JsonObject
            {
                ["CheckId"] = o.CheckId,
                ["ObjectType"] = o.ObjectType,
                ["ObjectKey"] = o.ObjectKey,
                ["ObjectName"] = o.ObjectName,
                ["Result"] = string.IsNullOrEmpty(o.Result) ? null : o.Result
            });
        var node = new JsonObject
        {
            ["Timestamp"] = (r.Timestamp == default ? _clock() : r.Timestamp).ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["User"] = string.IsNullOrEmpty(r.User) ? _user() : r.User,
            ["Event"] = r.Event,
            ["Phase"] = r.Phase,
            ["Result"] = string.IsNullOrEmpty(r.Result) ? null : r.Result,
            ["RunId"] = r.RunId,
            ["Site"] = r.Site,
            ["ObjectCount"] = r.Objects.Count,
            ["Objects"] = objects,
            ["Message"] = r.Message,
            ["Source"] = string.IsNullOrEmpty(r.Source) ? SourceName : r.Source
        };
        return node.ToJsonString(WriteOptions);
    }

    /// <summary>A line read into a record; null when the line is not a JSON object of this log.</summary>
    internal static HygieneAuditRecord? Parse(string line)
    {
        if (JsonNode.Parse(line) is not JsonObject o) return null;
        static string S(JsonObject from, string name) => from.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
            ? v.TryGetValue<string>(out var text) ? text : v.ToJsonString()
            : "";
        var eventName = S(o, "Event");
        if (eventName.Length == 0) return null;
        var objects = new List<HygieneAuditObject>();
        if (o.FirstOrDefault(p => string.Equals(p.Key, "Objects", StringComparison.OrdinalIgnoreCase)).Value is JsonArray list)
            foreach (var item in list.OfType<JsonObject>())
                objects.Add(new HygieneAuditObject(S(item, "CheckId"), S(item, "ObjectType"), S(item, "ObjectKey"), S(item, "ObjectName"), S(item, "Result")));
        var time = DateTime.TryParseExact(S(o, "Timestamp"), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : default;
        return new HygieneAuditRecord(time, S(o, "User"), eventName, S(o, "Phase"), S(o, "Result"), S(o, "RunId"), objects, S(o, "Message"))
        {
            Source = S(o, "Source"),
            Site = S(o, "Site")
        };
    }

    public IReadOnlyList<HygieneAuditRecord> Read(int max)
    {
        SkippedLines = 0;
        OlderRecordsDropped = false;
        var path = Path;
        if (max <= 0 || !ChainedJsonlFile.IsFullFilePath(path)) return [];
        IReadOnlyList<ChainedJsonlFile.Line> lines;
        try
        {
            lines = ChainedJsonlFile.ReadLines(path, MaxReadBytes, out var cut);
            OlderRecordsDropped = cut;
        }
        catch (InvalidDataException)
        {
            throw new HygieneServiceException("The Site Hygiene audit log file is a symbolic link or another reparse point, so it was not read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning(LogArea, $"The Site Hygiene audit log could not be read ({ex.GetType().Name}).");
            throw new HygieneServiceException($"The Site Hygiene audit log could not be read: {path}. Check that the folder can be reached.");
        }
        var records = new List<HygieneAuditRecord>();
        for (var i = lines.Count - 1; i >= 0 && records.Count < max; i--)
        {
            var line = lines[i];
            try
            {
                if (Parse(line.Text) is { } record)
                    records.Add(record with { Verified = line.Verified, HasChain = line.HasChain, AfterChainStart = line.AfterChainStart, ChainBrokenAt = line.BrokenAt });
                else SkippedLines++;
            }
            catch (JsonException)
            {
                SkippedLines++;
            }
        }
        records.Reverse();
        if (SkippedLines > 0) _log?.Warning(LogArea, $"Skipped {SkippedLines} Site Hygiene audit log lines that could not be read.");
        return records;
    }

    /// <summary>The verification text of a record read from the log, as <see cref="AuditVerification.Text"/> gives it for a deployment record.</summary>
    public static string VerificationText(HygieneAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Verified switch
        {
            null => "",
            true => AuditVerification.Verified,
            false when record.ChainBrokenAt is { } line => AuditVerification.BrokenAt(line),
            false when record.HasChain => AuditVerification.NotVerified,
            false => AuditVerification.NoChain
        };
    }
}

/// <summary>A probe and the appends that follow it, with the log held open in between.</summary>
public sealed class HygieneAuditReservation : IDisposable
{
    private readonly HygieneAuditLog? _owner;
    private readonly IHygieneAuditLog? _other;
    private readonly FileStream? _stream;

    internal HygieneAuditReservation(HygieneAuditLog owner, FileStream? stream, AuditProbe probe)
    {
        _owner = owner;
        _stream = stream;
        Probe = probe;
    }

    /// <summary>A reservation over a log that holds nothing: the probe and each append open the log.</summary>
    public HygieneAuditReservation(IHygieneAuditLog log)
    {
        _other = log ?? throw new ArgumentNullException(nameof(log));
        Probe = log.Probe();
    }

    public AuditProbe Probe { get; }

    public AuditWrite Append(HygieneAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (_other is not null) return _other.Append(r);
        return _stream is null ? new AuditWrite(false, Probe.Message) : _owner!.Write(_stream, r);
    }

    public void Dispose() => _stream?.Dispose();
}
