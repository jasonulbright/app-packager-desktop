using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The append-only Pivots and Scripts audit log at &lt;data root&gt;\PivotsScripts\pivots-scripts-audit.jsonl:
/// one compact JSON object per line with the hash chain of <see cref="ChainedJsonlFile"/>. A
/// reservation keeps the file open from the probe to the last append of an action, so a program that
/// opens the file after the probe cannot make the Finished record fail.
/// </summary>
public sealed class PivotsAuditLog : IPivotsAuditLog
{
    public const string LogArea = "PivotsScripts";
    public const string FileName = "pivots-scripts-audit.jsonl";
    public const string SourceName = "Application Packager";

    /// <summary>Only the end of a larger log is read for the history.</summary>
    public const long MaxReadBytes = 16L * 1024 * 1024;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<string?> _path;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _clock;
    private readonly Func<string> _user;
    private readonly object _gate = new();

    public PivotsAuditLog(Func<string?>? path = null, IAppLog? log = null, Func<DateTime>? clock = null, Func<string>? user = null)
    {
        _path = path ?? (() => null);
        _log = log;
        _clock = clock ?? (() => DateTime.Now);
        _user = user ?? (() => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
    }

    public static string DefaultPath() => System.IO.Path.Combine(AppDataPaths.Root, PivotsFolders.FolderName, FileName);

    public string Path => _path() is { } custom && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : DefaultPath();

    private bool IsDefault => _path() is not { } custom || string.IsNullOrWhiteSpace(custom);

    public int SkippedLines { get; private set; }

    public bool OlderRecordsDropped { get; private set; }

    public AuditProbe Probe()
    {
        using var reservation = Reserve();
        return reservation.Probe;
    }

    public AuditWrite Append(PivotsAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var reservation = Reserve();
        return reservation.Probe.Writable ? reservation.Append(r) : new AuditWrite(false, reservation.Probe.Message);
    }

    /// <summary>Opens the log for writing and keeps it open until the reservation is disposed.</summary>
    public PivotsAuditReservation Reserve()
    {
        var path = Path;
        var (stream, message) = ChainedJsonlFile.Open(path, IsDefault, "The Pivots and Scripts audit log", FileName);
        if (stream is null) _log?.Warning(LogArea, "The Pivots and Scripts audit log could not be opened for writing.");
        return new PivotsAuditReservation(this, stream, new AuditProbe(stream is not null, stream is not null ? $"The Pivots and Scripts audit log can be written: {path}" : message));
    }

    internal AuditWrite Write(FileStream stream, PivotsAuditRecord record)
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

    private string Serialize(PivotsAuditRecord r)
    {
        var o = new JsonObject
        {
            ["Timestamp"] = (r.Timestamp == default ? _clock() : r.Timestamp).ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["User"] = string.IsNullOrEmpty(r.User) ? _user() : r.User,
            ["Event"] = r.Event,
            ["Phase"] = r.Phase,
            ["Result"] = string.IsNullOrEmpty(r.Result) ? null : r.Result,
            ["RunId"] = r.RunId,
            ["Target"] = r.Target,
            ["DeviceCount"] = r.DeviceCount,
            ["ScriptGuid"] = r.ScriptGuid,
            ["TextHash"] = r.TextHash,
            ["OperationId"] = r.OperationId,
            ["SessionId"] = r.SessionId,
            ["Message"] = r.Message,
            ["Source"] = string.IsNullOrEmpty(r.Source) ? SourceName : r.Source
        };
        return o.ToJsonString(WriteOptions);
    }

    /// <summary>A line read into a record; null when the line is not a JSON object of this log.</summary>
    internal static PivotsAuditRecord? Parse(string line)
    {
        if (JsonNode.Parse(line) is not JsonObject o) return null;
        string S(string name) => o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
            ? v.TryGetValue<string>(out var text) ? text : v.ToJsonString()
            : "";
        string? N(string name) => S(name) is { Length: > 0 } t ? t : null;
        var eventName = S("Event");
        if (eventName.Length == 0) return null;
        var time = DateTime.TryParseExact(S("Timestamp"), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : default;
        var count = int.TryParse(S("DeviceCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new PivotsAuditRecord(time, S("User"), eventName, S("Phase"), S("Result"), S("RunId"), S("Target"), count, N("ScriptGuid"), N("TextHash"), N("OperationId"), S("Message"))
        {
            Source = S("Source"),
            SessionId = N("SessionId")
        };
    }

    public IReadOnlyList<PivotsAuditRecord> Read(int max)
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
            throw new PivotsServiceException("The Pivots and Scripts audit log file is a symbolic link or another reparse point, so it was not read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning(LogArea, $"The Pivots and Scripts audit log could not be read ({ex.GetType().Name}).");
            throw new PivotsServiceException($"The Pivots and Scripts audit log could not be read: {path}. Check that the folder can be reached.");
        }
        var records = new List<PivotsAuditRecord>();
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
        if (SkippedLines > 0) _log?.Warning(LogArea, $"Skipped {SkippedLines} Pivots and Scripts audit log lines that could not be read.");
        return records;
    }

    /// <summary>The verification text of a record read from the log, as <see cref="AuditVerification.Text"/> gives it for a deployment record.</summary>
    public static string VerificationText(PivotsAuditRecord record)
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
public sealed class PivotsAuditReservation : IDisposable
{
    private readonly PivotsAuditLog? _owner;
    private readonly IPivotsAuditLog? _other;
    private readonly FileStream? _stream;

    internal PivotsAuditReservation(PivotsAuditLog owner, FileStream? stream, AuditProbe probe)
    {
        _owner = owner;
        _stream = stream;
        Probe = probe;
    }

    /// <summary>A reservation over a log that holds nothing: the probe and each append open the log.</summary>
    public PivotsAuditReservation(IPivotsAuditLog log)
    {
        _other = log ?? throw new ArgumentNullException(nameof(log));
        Probe = log.Probe();
    }

    public AuditProbe Probe { get; }

    public AuditWrite Append(PivotsAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (_other is not null) return _other.Append(r);
        return _stream is null ? new AuditWrite(false, Probe.Message) : _owner!.Write(_stream, r);
    }

    public void Dispose() => _stream?.Dispose();
}

/// <summary>The Pivots and Scripts folders under the per-user data root.</summary>
public static class PivotsFolders
{
    public const string FolderName = "PivotsScripts";

    public static string Root() => System.IO.Path.Combine(AppDataPaths.Root, FolderName);
}
