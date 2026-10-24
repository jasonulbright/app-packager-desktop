using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The append-only DP content audit log at &lt;data root&gt;\DpContent\dp-content-audit.jsonl: one
/// compact JSON object per line with the hash chain of <see cref="ChainedJsonlFile"/>. A reservation
/// keeps the file open from the probe to the last append of a run, so a program that opens the file
/// after the probe cannot make a later append fail.
/// </summary>
public sealed class DpContentAuditLog : IDpContentAuditLog
{
    public const string LogArea = "DpContent";
    public const string FileName = "dp-content-audit.jsonl";
    public const string SourceName = "Application Packager";

    /// <summary>Only the end of a larger log is read for the history.</summary>
    public const long MaxReadBytes = 16L * 1024 * 1024;

    /// <summary>The most pairs one record lists; a larger chunk is refused by the service before it writes.</summary>
    public const int MaxPairsPerRecord = 100;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<string?> _path;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _clock;
    private readonly Func<string> _user;
    private readonly object _gate = new();

    public DpContentAuditLog(Func<string?>? path = null, IAppLog? log = null, Func<DateTime>? clock = null, Func<string>? user = null)
    {
        _path = path ?? (() => null);
        _log = log;
        _clock = clock ?? (() => DateTime.Now);
        _user = user ?? (() => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
    }

    public static string DefaultPath() => System.IO.Path.Combine(AppDataPaths.Root, "DpContent", FileName);

    public string Path => _path() is { } custom && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : DefaultPath();

    private bool IsDefault => _path() is not { } custom || string.IsNullOrWhiteSpace(custom);

    /// <summary>The number of lines the last <see cref="Read"/> skipped because they could not be read.</summary>
    public int SkippedLines { get; private set; }

    /// <summary>True when the last <see cref="Read"/> read only the end of a log longer than <see cref="MaxReadBytes"/>.</summary>
    public bool OlderRecordsDropped { get; private set; }

    public AuditProbe Probe()
    {
        using var reservation = Reserve();
        return reservation.Probe;
    }

    public AuditWrite Append(DpContentAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var reservation = Reserve();
        return reservation.Probe.Writable ? reservation.Append(r) : new AuditWrite(false, reservation.Probe.Message);
    }

    /// <summary>Opens the log for writing and keeps it open until the reservation is disposed.</summary>
    public DpContentAuditReservation Reserve()
    {
        var path = Path;
        var (stream, message) = ChainedJsonlFile.Open(path, IsDefault, "The DP content audit log", FileName);
        if (stream is null) _log?.Warning(LogArea, "The DP content audit log could not be opened for writing.");
        return new DpContentAuditReservation(this, stream, new AuditProbe(stream is not null, stream is not null ? $"The DP content audit log can be written: {path}" : message));
    }

    internal AuditWrite Write(FileStream stream, DpContentAuditRecord record)
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

    private string Serialize(DpContentAuditRecord r)
    {
        if (r.Pairs.Count > MaxPairsPerRecord) throw new InvalidOperationException("Too many pairs for one record.");
        var pairs = new JsonArray();
        foreach (var p in r.Pairs)
            pairs.Add(new JsonObject
            {
                ["PackageID"] = p.PackageId,
                ["ContentName"] = p.ContentName,
                ["DPName"] = p.DpName,
                ["NALPath"] = p.NalPath,
                ["StateBefore"] = p.StateBefore is { } s ? JsonValue.Create(s) : null,
                ["Result"] = string.IsNullOrEmpty(p.Result) ? null : p.Result
            });
        var o = new JsonObject
        {
            ["Timestamp"] = (r.Timestamp == default ? _clock() : r.Timestamp).ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["User"] = string.IsNullOrEmpty(r.User) ? _user() : r.User,
            ["Event"] = r.Event,
            ["Phase"] = r.Phase,
            ["Result"] = string.IsNullOrEmpty(r.Result) ? null : r.Result,
            ["RunId"] = r.RunId,
            ["Chunk"] = r.Chunk,
            ["PairCount"] = r.Pairs.Count,
            ["Pairs"] = pairs,
            ["Message"] = r.Message,
            ["Source"] = string.IsNullOrEmpty(r.Source) ? SourceName : r.Source
        };
        return o.ToJsonString(WriteOptions);
    }

    /// <summary>A line read into a record; null when the line is not a JSON object of this log.</summary>
    internal static DpContentAuditRecord? Parse(string line)
    {
        if (JsonNode.Parse(line) is not JsonObject o) return null;
        string S(JsonObject from, string name) => from.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
            ? v.TryGetValue<string>(out var text) ? text : v.ToJsonString()
            : "";
        int? I(JsonObject from, string name) => int.TryParse(S(from, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        var eventName = S(o, "Event");
        if (eventName.Length == 0) return null;
        var pairs = new List<DpContentAuditPair>();
        if (o.FirstOrDefault(p => string.Equals(p.Key, "Pairs", StringComparison.OrdinalIgnoreCase)).Value is JsonArray list)
            foreach (var item in list.OfType<JsonObject>())
                pairs.Add(new DpContentAuditPair(S(item, "PackageID"), S(item, "ContentName"), S(item, "DPName"), S(item, "NALPath"), I(item, "StateBefore"), S(item, "Result")));
        var time = DateTime.TryParseExact(S(o, "Timestamp"), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : default;
        return new DpContentAuditRecord(time, S(o, "User"), eventName, S(o, "Phase"), S(o, "Result"), S(o, "RunId"), I(o, "Chunk") ?? 0, pairs, S(o, "Message"))
        {
            Source = S(o, "Source")
        };
    }

    public IReadOnlyList<DpContentAuditRecord> Read(int max)
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
            throw new DpContentServiceException("The DP content audit log file is a symbolic link or another reparse point, so it was not read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning(LogArea, $"The DP content audit log could not be read ({ex.GetType().Name}).");
            throw new DpContentServiceException($"The DP content audit log could not be read: {path}. Check that the folder can be reached.");
        }
        var records = new List<DpContentAuditRecord>();
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
        if (SkippedLines > 0) _log?.Warning(LogArea, $"Skipped {SkippedLines} DP content audit log lines that could not be read.");
        return records;
    }

    /// <summary>The verification text of a record read from the log, as <see cref="AuditVerification.Text"/> gives it for a deployment record.</summary>
    public static string VerificationText(DpContentAuditRecord record)
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
public sealed class DpContentAuditReservation : IDisposable
{
    private readonly DpContentAuditLog? _owner;
    private readonly IDpContentAuditLog? _other;
    private readonly FileStream? _stream;

    internal DpContentAuditReservation(DpContentAuditLog owner, FileStream? stream, AuditProbe probe)
    {
        _owner = owner;
        _stream = stream;
        Probe = probe;
    }

    /// <summary>A reservation over a log that holds nothing: the probe and each append open the log.</summary>
    public DpContentAuditReservation(IDpContentAuditLog log)
    {
        _other = log ?? throw new ArgumentNullException(nameof(log));
        Probe = log.Probe();
    }

    public AuditProbe Probe { get; }

    public AuditWrite Append(DpContentAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (_other is not null) return _other.Append(r);
        return _stream is null ? new AuditWrite(false, Probe.Message) : _owner!.Write(_stream, r);
    }

    public void Dispose() => _stream?.Dispose();
}
