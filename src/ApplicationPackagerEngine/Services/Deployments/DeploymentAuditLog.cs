using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The append-only deployment audit log: one compact JSON object per line. An append opens the
/// file, moves to its end and writes; nothing already in the file is ever written again. The
/// file is opened for writing with FileShare.Read, so a program that holds the file open without
/// sharing write access makes the probe and the append fail alike. A reservation keeps the file
/// open from the probe to the append.
/// <para>
/// Each line this product writes ends with a "Chain" property: the lowercase hex SHA-256 of the
/// hash of the line before it, a line feed, and the line's JSON text without the Chain property.
/// The hash of a line is its Chain value, or the SHA-256 of its trimmed text when it has none
/// (a line of the suite tool or a damaged line); the first line follows 64 zeros. A read marks
/// a record Verified only when its Chain matches. The chain detects accidental edits, inserted
/// or removed lines and casual forgery only: the hash uses no secret, so anyone who can write
/// the file can recompute every Chain after a change.
/// </para>
/// </summary>
public sealed class DeploymentAuditLog : IDeploymentAuditLog
{
    public const string LogArea = "Deployments";
    public const string FileName = "deployment-audit.jsonl";
    public const string SourceName = "Application Packager";

    /// <summary>Only the end of a larger log is read for the history.</summary>
    public const long MaxReadBytes = 16L * 1024 * 1024;

    private const int MaxRecordBytes = 64 * 1024;

    /// <summary>The fields the suite tool writes, in its order; the other fields follow.</summary>
    private static readonly string[] SuiteOrder =
    [
        "Timestamp", "User", "DeploymentType", "ApplicationName", "ApplicationVersion", "CollectionName", "CollectionID", "MemberCount",
        "DeployPurpose", "DeployAction", "DeadlineDateTime", "DeploymentID", "Result"
    ];

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    

    private readonly Func<string?> _path;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _clock;
    private readonly Func<string> _user;
    private readonly object _gate = new();

    public DeploymentAuditLog(Func<string?>? path = null, IAppLog? log = null, Func<DateTime>? clock = null, Func<string>? user = null)
    {
        _path = path ?? (() => null);
        _log = log;
        _clock = clock ?? (() => DateTime.Now);
        _user = user ?? (() => $@"{Environment.UserDomainName}\{Environment.UserName}");
    }

    public static string DefaultPath() => System.IO.Path.Combine(AppDataPaths.Root, "Deployments", FileName);

    public string Path => _path() is { } custom && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : DefaultPath();

    private bool IsDefault => _path() is not { } custom || string.IsNullOrWhiteSpace(custom);

    /// <summary>The number of lines the last <see cref="Read"/> skipped because they could not be read.</summary>
    public int SkippedLines { get; private set; }

    public AuditProbe Probe()
    {
        using var reservation = Reserve();
        return reservation.Probe;
    }

    public AuditWrite Append(DeploymentAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        using var reservation = Reserve();
        return reservation.Probe.Writable ? reservation.Append(r) : new AuditWrite(false, reservation.Probe.Message);
    }

    /// <summary>Opens the log for writing and keeps it open until the reservation is disposed.</summary>
    public IDeploymentAuditReservation Reserve()
    {
        var path = Path;
        var (stream, message) = Open(path);
        if (stream is null) _log?.Warning(LogArea, "The audit log could not be opened for writing.");
        return new Reservation(this, stream, new AuditProbe(stream is not null, stream is not null ? $"The audit log can be written: {path}" : message));
    }

    private (FileStream? Stream, string Message) Open(string path)
    {
        if (!IsFullFilePath(path))
            return (null, $"The audit log path must be a full path to a file, such as C:\\Logs\\{FileName} or \\\\server\\share\\{FileName}.");
        var folder = System.IO.Path.GetDirectoryName(path)!;
        try
        {
            if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, IsDefault)))
                return (null, $"The audit log folder is a junction or symbolic link, so the log was not written: {folder}");
            Directory.CreateDirectory(folder);
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return (null, $"The audit log file is a symbolic link or another reparse point, so it was not written: {path}");
            return (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read), "");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, $"The audit log cannot be written: the file or folder is read-only or access is denied: {path}");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or DriveNotFoundException)
        {
            return (null, $"The audit log folder cannot be reached: {folder}");
        }
        catch (IOException ex) when (IsSharing(ex))
        {
            return (null, $"The audit log cannot be written now: another session of this product is making a change and holds it, or another program has it open: {path}. Try again when that session finishes, or close that program.");
        }
        catch (IOException)
        {
            return (null, $"The audit log cannot be written: {path}. Check that the folder can be reached and has free space.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (null, $"The audit log path cannot be used: {path}");
        }
    }

    private static bool IsSharing(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static bool IsFullFilePath(string path)
    {
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return false;
            var name = System.IO.Path.GetFileName(path);
            return name.Length > 0 && System.IO.Path.GetDirectoryName(path) is { Length: > 0 };
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private AuditWrite Write(FileStream stream, DeploymentAuditRecord record)
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
        if (Encoding.UTF8.GetByteCount(body) + ChainSuffixLength + 2 > MaxRecordBytes) return new AuditWrite(false, "The audit record is too large. Nothing was written.");
        lock (_gate)
        {
            try
            {
                var previous = PreviousLineHash(stream);
                var line = Encoding.UTF8.GetBytes(Chained(body, previous) + "\r\n");
                var length = stream.Seek(0, SeekOrigin.End);
                if (length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    var last = stream.ReadByte();
                    stream.Seek(0, SeekOrigin.End);
                    if (last != '\n') stream.Write("\r\n"u8);
                }
                stream.Write(line);
                stream.Flush(flushToDisk: true);
                return new AuditWrite(true, "The audit record was written.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.Warning(LogArea, $"An audit record could not be written ({ex.GetType().Name}).");
                return new AuditWrite(false, $"The audit record could not be written to {Path}. Check that the folder can be reached and has free space.");
            }
        }
    }

    // ---- Chain --------------------------------------------------------------------------------

    private const string ChainName = "Chain";
    private const string ChainPrefix = ",\"" + ChainName + "\":\"";

    /// <summary>The length of ,"Chain":"&lt;64 hex&gt;"} at the end of a chained line.</summary>
    private const int ChainSuffixLength = 76;

    /// <summary>The hash the first line of the log follows.</summary>
    internal static readonly string ChainStart = new('0', 64);

    /// <summary>The most bytes read back from the end of the log to find the line an append follows.</summary>
    private const int MaxTailBytes = 1024 * 1024;

    private static string Sha256(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The line without white space at its ends and without a byte order mark.</summary>
    private static string Normalized(string line) => line.Trim().Trim('﻿').Trim();

    /// <summary>The JSON object of a chained line without its Chain property, and the Chain value; null when the line carries no Chain.</summary>
    private static (string Body, string Chain)? SplitChain(string line)
    {
        if (line.Length <= ChainSuffixLength || !line.EndsWith("\"}", StringComparison.Ordinal)) return null;
        var start = line.Length - ChainSuffixLength;
        if (string.CompareOrdinal(line, start, ChainPrefix, 0, ChainPrefix.Length) != 0) return null;
        var chain = line.Substring(start + ChainPrefix.Length, 64);
        if (!chain.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) return null;
        return (line[..start] + "}", chain);
    }

    /// <summary>The hash the next line follows: the Chain value of a chained line, the SHA-256 of any other line.</summary>
    internal static string LineHash(string line)
    {
        var normalized = Normalized(line);
        return SplitChain(normalized) is { } split ? split.Chain : Sha256(normalized);
    }

    private static string ChainOf(string body, string previousHash) => Sha256(previousHash + "\n" + body);

    private static string Chained(string body, string previousHash) => body[..^1] + ChainPrefix + ChainOf(body, previousHash) + "\"}";

    /// <summary>True when the line carries a Chain that matches its own text and the hash of the line before it.</summary>
    internal static bool ChainMatches(string line, string previousHash) =>
        SplitChain(Normalized(line)) is { } split && string.Equals(ChainOf(split.Body, previousHash), split.Chain, StringComparison.Ordinal);

    /// <summary>
    /// The hash of the last line of the log, read from the held stream. A last line longer than the
    /// read window yields a hash no read reproduces, so the record that follows reads as unverified.
    /// </summary>
    private static string PreviousLineHash(FileStream stream)
    {
        var length = stream.Seek(0, SeekOrigin.End);
        if (length == 0) return ChainStart;
        var count = (int)Math.Min(length, MaxTailBytes);
        var buffer = new byte[count];
        stream.Seek(length - count, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer).TrimEnd();
        if (Normalized(text).Length == 0) return count == length ? ChainStart : Sha256(text);
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0 && count < length) return Sha256("unreadable\n" + text);
        return LineHash(text[(lastBreak + 1)..]);
    }

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private string Serialize(DeploymentAuditRecord r)
    {
        static JsonNode? Time(DateTime? value) => value is { } v ? JsonValue.Create(v.ToString(TimeFormat, CultureInfo.InvariantCulture)) : null;
        static JsonNode? UtcTime(DateTime? value) => value is { } v ? JsonValue.Create(v.ToString(TimeFormat, CultureInfo.InvariantCulture) + "Z") : null;
        static JsonNode? Id(string value) =>
            string.IsNullOrEmpty(value) ? null
            : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : JsonValue.Create(value);
        var o = new JsonObject
        {
            ["Timestamp"] = Time(r.Timestamp == default ? _clock() : r.Timestamp),
            ["User"] = string.IsNullOrEmpty(r.User) ? _user() : r.User,
            ["DeploymentType"] = r.DeploymentType,
            ["ApplicationName"] = r.ApplicationName,
            ["ApplicationVersion"] = r.ApplicationVersion,
            ["CollectionName"] = r.CollectionName,
            ["CollectionID"] = r.CollectionID,
            ["MemberCount"] = r.MemberCount is { } members ? JsonValue.Create(members) : null,
            ["DeployPurpose"] = r.DeployPurpose,
            ["DeployAction"] = r.DeployAction,
            ["DeadlineDateTime"] = Time(r.DeadlineDateTime),
            ["DeploymentID"] = Id(r.DeploymentID),
            ["Result"] = r.Result,
            ["Event"] = r.Event,
            ["ObjectId"] = r.ObjectId,
            ["AvailableDateTime"] = Time(r.AvailableDateTime),
            ["Message"] = r.Message,
            ["Source"] = string.IsNullOrEmpty(r.Source) ? SourceName : r.Source,
            ["DeploymentUniqueID"] = r.DeploymentUniqueID,
            ["ProgramName"] = r.ProgramName,
            ["TimeBasedOn"] = r.TimeBasedOn,
            ["DistributionPointGroups"] = r.DistributionPointGroups,
            ["Undoes"] = r.Undoes,
            ["ModelId"] = string.IsNullOrEmpty(r.ModelId) ? null : r.ModelId,
            ["CreatedUtc"] = UtcTime(r.CreatedUtc),
            ["UnclearDeploymentIds"] = string.IsNullOrEmpty(r.UnclearDeploymentIds) ? null : r.UnclearDeploymentIds
        };
        foreach (var (name, value) in r.Extra)
            if (!o.ContainsKey(name) && !string.Equals(name, ChainName, StringComparison.OrdinalIgnoreCase)) o[name] = value;
        return o.ToJsonString(WriteOptions);
    }

    private static readonly HashSet<string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        "Timestamp", "User", "DeploymentType", "ApplicationName", "ApplicationVersion", "CollectionName", "CollectionID", "MemberCount", "DeployPurpose",
        "DeployAction", "DeadlineDateTime", "DeploymentID", "Result", "Event", "ObjectId", "AvailableDateTime", "Message", "Source", "DeploymentUniqueID",
        "ProgramName", "TimeBasedOn", "DistributionPointGroups", "Undoes", "ModelId", "CreatedUtc", "UnclearDeploymentIds", ChainName
    };

    /// <summary>A line read into a record; null when the line is not a JSON object.</summary>
    internal static DeploymentAuditRecord? Parse(string line)
    {
        if (JsonNode.Parse(line) is not JsonObject o) return null;
        string S(string name) => o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is { } v ? NodeText(v) : "";
        DateTime? T(string name) => DateTime.TryParse(S(name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        var members = int.TryParse(S("MemberCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : (int?)null;
        var eventName = S("Event");
        return new DeploymentAuditRecord(T("Timestamp") ?? default, S("User"), S("DeploymentType"), S("ApplicationName"), S("ApplicationVersion"), S("CollectionName"),
            S("CollectionID"), members, S("DeployPurpose"), S("DeployAction"), T("DeadlineDateTime"), S("DeploymentID"), S("Result"))
        {
            Event = eventName.Length > 0 ? eventName : "Create",
            ObjectId = S("ObjectId"),
            AvailableDateTime = T("AvailableDateTime"),
            Message = S("Message"),
            Source = S("Source"),
            DeploymentUniqueID = S("DeploymentUniqueID"),
            ProgramName = S("ProgramName"),
            TimeBasedOn = S("TimeBasedOn"),
            DistributionPointGroups = S("DistributionPointGroups"),
            Undoes = S("Undoes"),
            ModelId = S("ModelId"),
            CreatedUtc = DeploymentService.ParseUtc(S("CreatedUtc")),
            UnclearDeploymentIds = S("UnclearDeploymentIds"),
            Extra =o.Where(p => !Named.Contains(p.Key)).Select(p => new KeyValuePair<string, string>(p.Key, p.Value is null ? "" : NodeText(p.Value))).ToList()
        };
    }

    private static string NodeText(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();

    public IReadOnlyList<DeploymentAuditRecord> Read(int max)
    {
        SkippedLines = 0;
        var path = Path;
        if (max <= 0 || !IsFullFilePath(path)) return [];
        byte[] bytes;
        var cut = false;
        try
        {
            if (!File.Exists(path)) return [];
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new DeploymentServiceException("The audit log file is a symbolic link or another reparse point, so it was not read.", DeploymentFailureKind.Storage);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaxReadBytes);
            stream.Seek(start, SeekOrigin.Begin);
            bytes = new byte[stream.Length - start];
            stream.ReadExactly(bytes);
            if (start > 0)
            {
                cut = true;
                var firstBreak = Array.IndexOf(bytes, (byte)'\n');
                bytes = firstBreak < 0 ? [] : bytes[(firstBreak + 1)..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning(LogArea, $"The audit log could not be read ({ex.GetType().Name}).");
            throw new DeploymentServiceException($"The audit log could not be read: {path}. Check that the folder can be reached.", DeploymentFailureKind.Storage);
        }

        var lines = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n').Select(Normalized).Where(l => l.Length > 0).ToArray();
        var records = new List<DeploymentAuditRecord>();
        for (var i = lines.Length - 1; i >= 0 && records.Count < max; i--)
        {
            var line = lines[i];
            try
            {
                // The line before the first line of a cut window is not read, so that line cannot be verified.
                var previous = i > 0 ? LineHash(lines[i - 1]) : cut ? null : ChainStart;
                if (Parse(line) is { } record)
                    records.Add(record with { Verified = previous is not null && ChainMatches(line, previous), HasChain = SplitChain(Normalized(line)) is not null });
                else SkippedLines++;
            }
            catch (JsonException)
            {
                SkippedLines++;
            }
        }
        records.Reverse();
        if (SkippedLines > 0) _log?.Warning(LogArea, $"Skipped {SkippedLines} audit log lines that could not be read.");
        return records;
    }

    private sealed class Reservation(DeploymentAuditLog owner, FileStream? stream, AuditProbe probe) : IDeploymentAuditReservation
    {
        public AuditProbe Probe { get; } = probe;

        public AuditWrite Append(DeploymentAuditRecord r)
        {
            ArgumentNullException.ThrowIfNull(r);
            return stream is null ? new AuditWrite(false, Probe.Message) : owner.Write(stream, r);
        }

        public void Dispose() => stream?.Dispose();
    }
}
