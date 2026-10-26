using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Ring run files: one JSON file per run in the runs folder, closed runs in its closed subfolder,
/// with the property names of the suite tool's run files so both tools share a folder. A new file
/// is created with FileMode.CreateNew under the first free name of both folders, so no run file
/// is ever overwritten; a save writes a temporary file beside the run and replaces the run with it,
/// so a reader never sees half a file; a save keeps every property of the file it does not know.
/// <para>
/// A lock is the file &lt;run&gt;.lock created with FileMode.CreateNew, which fails while the file
/// exists, also on an SMB share; it names its owner. A lock left by a session that ended stays
/// until someone deletes it by hand: a lock older than <see cref="StaleLockAge"/> is reported as
/// stale, never removed. The lock only excludes sessions that use the same runs folder.
/// </para>
/// </summary>
public sealed class RingRunStore
{
    public const string LogArea = "Deployments";
    public const int MaxFileBytes = 1024 * 1024;
    public const string ClosedFolderName = "closed";
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";

    /// <summary>A lock file older than this is reported as stale: the session that made it probably ended.</summary>
    public static readonly TimeSpan StaleLockAge = TimeSpan.FromHours(1);

    public const string LinkMessage = "The ring runs folder is a junction or symbolic link. The runs were not read or changed.";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Func<string?> _folder;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _local;
    private readonly Func<string> _user;

    public RingRunStore(Func<string?>? folder = null, IAppLog? log = null, Func<DateTime>? utcNow = null, TimeZoneInfo? local = null, Func<string>? user = null)
    {
        _folder = folder ?? (() => null);
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _local = local ?? TimeZoneInfo.Local;
        // ConfigMgr records the Windows identity name of the session as the creator, so the run names the same account.
        _user = user ?? (() => System.Security.Principal.WindowsIdentity.GetCurrent().Name);
    }

    private RingRunStore(RingRunStore source, string folder, bool isDefault)
        : this(() => folder, source._log, source._utcNow, source._local, source._user)
    {
        _pinnedDefault = isDefault;
        BeforeSave = source.BeforeSave;
    }

    private readonly bool? _pinnedDefault;

    public static string DefaultFolder() => Path.Combine(AppDataPaths.Root, "Deployments", "Rings", "runs");

    public string Folder => _folder() is { } custom && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : DefaultFolder();

    private bool IsDefault => _pinnedDefault ?? (_folder() is not { } custom || string.IsNullOrWhiteSpace(custom));

    /// <summary>
    /// A store fixed to the folder the setting names now. One operation uses one pinned store, so a
    /// folder change in Options during a write cannot make a later save address another folder
    /// than the lock it holds protects.
    /// </summary>
    public RingRunStore Pinned() => new(this, Folder, IsDefault);

    public string ClosedFolder => Path.Combine(Folder, ClosedFolderName);

    /// <summary>Runs at the start of each save, inside its storage failure handling.</summary>
    internal Action<RingRun>? BeforeSave { get; init; }

    /// <summary>The account a new run file names as its creator.</summary>
    public string User => _user();

    private DateTime LocalNow => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc), _local), DateTimeKind.Unspecified);

    private string CheckedFolder()
    {
        var folder = Folder;
        if (!Path.IsPathFullyQualified(folder))
            throw new DeploymentServiceException("The ring runs folder must be a full path, such as C:\\Rings\\runs or \\\\server\\share\\Rings\\runs.", DeploymentFailureKind.Storage);
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, IsDefault)))
            throw new DeploymentServiceException(LinkMessage, DeploymentFailureKind.Storage);
        return folder;
    }

    // ---- List and read ------------------------------------------------------------------------

    /// <summary>The runs of the open or the closed folder; a file that cannot be read is left out and named in <paramref name="message"/>.</summary>
    public IReadOnlyList<RingRun> List(bool closed, out string? message)
    {
        message = null;
        var folder = CheckedFolder();
        var dir = closed ? Path.Combine(folder, ClosedFolderName) : folder;
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return [];
            if (closed && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DeploymentServiceException(LinkMessage, DeploymentFailureKind.Storage);
            files = Directory.GetFiles(dir, "*.json");
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The ring runs folder could not be listed ({ex.GetType().Name}).");
            throw new DeploymentServiceException("The ring runs folder could not be read. Check that it can be reached.", DeploymentFailureKind.Storage);
        }
        var runs = new List<RingRun>();
        var skipped = new List<string>();
        foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var run = ReadFile(file, closed).Run;
                runs.Add(closed ? run : run with { Lock = LockInfo(file) });
            }
            catch (DeploymentServiceException ex)
            {
                skipped.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (skipped.Count > 0)
        {
            message = (skipped.Count == 1 ? "1 run file is not listed. " : $"{skipped.Count.ToString(CultureInfo.InvariantCulture)} run files are not listed. ") + string.Join(" ", skipped);
            _log?.Warning(LogArea, $"Skipped {skipped.Count} ring run files.");
        }
        return runs;
    }

    /// <summary>
    /// The deployment IDs the rings of every other run file hold, open and closed. <paramref name="unreadable"/>
    /// names the files that could not be read: their IDs are not known, so a caller that must exclude
    /// them cannot rely on the set.
    /// </summary>
    public IReadOnlySet<string> OtherRunDeploymentIds(string runId, out string? unreadable)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var open = List(false, out var openMessage);
        var closed = List(true, out var closedMessage);
        foreach (var run in open.Concat(closed).Where(r => !string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase)))
            foreach (var id in run.Rings.Select(r => r.DeploymentId).OfType<string>()) ids.Add(id);
        unreadable = string.Join(" ", new[] { openMessage, closedMessage }.Where(m => !string.IsNullOrEmpty(m)));
        if (unreadable.Length == 0) unreadable = null;
        return ids;
    }

    /// <summary>The open run with the RunId, or null; with <paramref name="includeClosed"/> also a closed run.</summary>
    public RingRun? Find(string runId, bool includeClosed = false)
    {
        var open = List(false, out _).FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase));
        if (open is not null || !includeClosed) return open;
        return List(true, out _).FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The run with the RunId and whether its file is in the closed folder; null when neither folder holds it.</summary>
    public (RingRun Run, bool InClosedFolder)? Locate(string runId)
    {
        var open = List(false, out _).FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase));
        if (open is not null) return (open, false);
        var closed = List(true, out _).FirstOrDefault(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase));
        return closed is null ? null : (closed, true);
    }

    /// <summary>A run file of the open folder, or of the closed folder, read now with its fingerprint.</summary>
    public RingRun Read(string fileName, bool closedFolder = false) => ReadFile(PathOf(fileName, closedFolder), closedFolder).Run;

    internal string OpenPath(string fileName) => PathOf(fileName, false);

    internal string PathOf(string fileName, bool closedFolder)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (name.Length == 0 || !string.Equals(name, fileName, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new DeploymentServiceException("The run file name is not valid.");
        var folder = CheckedFolder();
        if (closedFolder)
        {
            folder = Path.Combine(folder, ClosedFolderName);
            if (Directory.Exists(folder) && new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DeploymentServiceException(LinkMessage, DeploymentFailureKind.Storage);
        }
        return Path.Combine(folder, name);
    }

    internal sealed record RunFile(RingRun Run, JsonObject Json, string Path);

    internal RunFile ReadFile(string path, bool closed)
    {
        byte[] bytes;
        DateTime written;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new DeploymentServiceException("The run file does not exist any more. Refresh the runs.", DeploymentFailureKind.Stale);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DeploymentServiceException("the file is a symbolic link or another reparse point.", DeploymentFailureKind.Storage);
            if (info.Length > MaxFileBytes) throw new DeploymentServiceException($"the file is larger than {(MaxFileBytes / 1024).ToString(CultureInfo.InvariantCulture)} KB.", DeploymentFailureKind.Storage);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex) && ex is not DeploymentServiceException)
        {
            _log?.Warning(LogArea, $"A ring run file could not be read ({ex.GetType().Name}).");
            throw new DeploymentServiceException("the file could not be read.", DeploymentFailureKind.Storage);
        }
        JsonObject o;
        try
        {
            o = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as JsonObject ?? throw new DeploymentServiceException("the file is not a ring run.", DeploymentFailureKind.Storage);
        }
        catch (JsonException)
        {
            throw new DeploymentServiceException("the file is not valid JSON.", DeploymentFailureKind.Storage);
        }
        var run = Parse(o, System.IO.Path.GetFileName(path), closed) with { Fingerprint = Fingerprint(bytes, written) };
        return new RunFile(run, o, path);
    }

    /// <summary>The version stamp of a file: its last write time, its length and the SHA-256 of its bytes.</summary>
    internal static string Fingerprint(byte[] bytes, DateTime lastWriteUtc) =>
        $"{lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture)}:{bytes.Length.ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private RingRun Parse(JsonObject o, string fileName, bool closed)
    {
        var runId = RingPlanStore.Text(o, "RunId") ?? "";
        if (runId.Trim().Length == 0 || RingPlanStore.Node(o, "Rings") is not JsonArray array) throw new DeploymentServiceException("the file is not a ring run.", DeploymentFailureKind.Storage);
        if (RingPlanStore.Node(o, "Object") is not JsonObject obj) throw new DeploymentServiceException("the run names no object.", DeploymentFailureKind.Storage);
        var type = (RingPlanStore.Text(obj, "Type") ?? "").Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "application" => DeploymentObjectType.Application,
            "package" => DeploymentObjectType.Package,
            "tasksequence" => DeploymentObjectType.TaskSequence,
            "sug" or "softwareupdategroup" => DeploymentObjectType.SoftwareUpdateGroup,
            _ => throw new DeploymentServiceException("the run names an object type that is not known.", DeploymentFailureKind.Storage)
        };
        var timeBasedOn = RingPlanStore.Text(o, "TimeBasedOn") ?? RingPlanRules.LocalTime;
        var objectId = type == DeploymentObjectType.Application ? RingPlanStore.Text(obj, "CiId") ?? RingPlanStore.Text(obj, "ID") ?? "" : RingPlanStore.Text(obj, "ID") ?? "";
        var created = Utc(RingPlanStore.Text(o, "CreatedAtUtc")) ?? Local(RingPlanStore.Text(o, "CreatedAt")) ?? DateTime.MinValue;
        var rings = new List<RingRunRing>();
        var position = 0;
        foreach (var node in array)
        {
            if (node is not JsonObject r) continue;
            position++;
            rings.Add(ParseRing(r, position));
        }
        if (rings.Count == 0) throw new DeploymentServiceException("the run has no rings.", DeploymentFailureKind.Storage);
        if (rings.Select(r => r.Index).Distinct().Count() != rings.Count) throw new DeploymentServiceException("two rings of the run have the same index.", DeploymentFailureKind.Storage);
        RingPlanStore.TryBool(o, "HoldLaterRings", false, out var hold);
        var closedAt = RingPlanStore.Text(o, "ClosedAt");
        return new RingRun(runId.Trim(), fileName, RingPlanStore.Text(o, "PlanName") ?? "", hold, timeBasedOn, type, objectId.Trim(), RingPlanStore.Text(obj, "Name") ?? "",
            type == DeploymentObjectType.Package ? RingPlanStore.Text(obj, "ProgramName") ?? "" : "", RingPlanStore.Text(o, "CreatedBy") ?? "", created,
            rings.OrderBy(r => r.Index).ToList(), closed || !string.IsNullOrWhiteSpace(closedAt))
        {
            ModelId = type == DeploymentObjectType.Application ? RingPlanStore.Text(obj, "ModelId") ?? "" : "",
            Problems = [.. (rings.Count > RingPlanLimits.MaxRings ? [CapMessage(rings.Count)] : Array.Empty<string>()), .. rings.Where(r => r.Problem.Length > 0).Select(r => r.Problem)]
        };
    }

    private static readonly string[] KnownStatuses = [RingStatuses.Held, RingStatuses.Creating, RingStatuses.Created, RingStatuses.Removed, RingStatuses.Cancelled];

    /// <summary>
    /// One ring of a run file. A value that cannot be read never takes a default that widens what
    /// the ring does (no gate, plain options): the ring keeps a Problem that blocks Promote and Undo run.
    /// </summary>
    private static RingRunRing ParseRing(JsonObject r, int position)
    {
        if (!RingPlanStore.TryDays(r, "Index", position, out var index) || index is null or < 1)
            throw new DeploymentServiceException($"ring {position.ToString(CultureInfo.InvariantCulture)} of the file has an index that cannot be read.", DeploymentFailureKind.Storage);
        var name = RingPlanStore.Text(r, "Name") ?? "";
        var problems = new List<string>();
        var (definition, optionProblem) = RingPlanStore.ParseRing(r, index.Value);
        if (definition is null) problems.Add(optionProblem);
        if (!RingPlanStore.TryDays(r, "SuccessThresholdPercent", null, out var threshold) || threshold is < 0 or > 100)
        {
            if (definition is not null) problems.Add($"the success threshold '{RingPlanStore.Text(r, "SuccessThresholdPercent")}' is not a whole number from 0 to 100");
            threshold = null;
        }
        if (!RingPlanStore.TryDays(r, "ShiftedMinutes", 0, out var shifted)) problems.Add($"the shifted minutes '{RingPlanStore.Text(r, "ShiftedMinutes")}' are not a whole number");
        var status = RingPlanStore.Text(r, "Status") ?? RingStatuses.Held;
        if (!KnownStatuses.Contains(status, StringComparer.Ordinal)) problems.Add($"the status '{status}' is not Held, Creating, Created, Removed or Cancelled");
        var available = Wall(RingPlanStore.Text(r, "AvailableDateTime"))
            ?? throw new DeploymentServiceException($"ring {index.Value.ToString(CultureInfo.InvariantCulture)} has no available time that can be read.", DeploymentFailureKind.Storage);
        return new RingRunRing(index.Value, name, (RingPlanStore.Text(r, "CollectionID") ?? "").Trim().ToUpperInvariant(),
            RingPlanStore.Text(r, "CollectionName") ?? "", RingPlanStore.Text(r, "Purpose") ?? DeploymentPurposes.Required, available, Wall(RingPlanStore.Text(r, "DeadlineDateTime")),
            status, Wall(RingPlanStore.Text(r, "CreatingAt")), Empty(RingPlanStore.Text(r, "DeploymentID")), shifted ?? 0,
            (RingPlanStore.Text(r, "DPGroup") ?? "").Trim(), threshold)
        {
            Definition = definition,
            CreatingAtUtc = Utc(RingPlanStore.Text(r, "CreatingAtUtc")),
            CreatingBy = Empty(RingPlanStore.Text(r, "CreatingBy")),
            Problem = problems.Count == 0 ? "" : $"Unreadable ring {index.Value.ToString(CultureInfo.InvariantCulture)} '{name}': {string.Join("; ", problems.Select(p => Unlabelled(p, index.Value, name)))}."
        };
    }

    private static string Unlabelled(string problem, int index, string name)
    {
        var label = RingPlanRules.Label(index, name) + " ";
        var text = problem.StartsWith(label, StringComparison.Ordinal) ? problem[label.Length..] : problem;
        return text.TrimEnd('.');
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly string[] WallFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm"];

    /// <summary>A wall-clock time of the file: the text forms the suite tool reads, or a time with a Z that PowerShell 7 writes for a UTC value.</summary>
    internal static DateTime? Wall(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return null;
        if (value.EndsWith('Z')) value = value[..^1];
        return DateTime.TryParseExact(value, WallFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static DateTime? Utc(string? text) => Wall(text) is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;

    private DateTime? Local(string? text)
    {
        if (Wall(text) is not { } value) return null;
        if (_local.IsInvalidTime(value)) value = value.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), _local);
    }

    // ---- Locks --------------------------------------------------------------------------------

    /// <summary>The lock of an open run file, or null when none exists.</summary>
    public RingLockInfo? LockInfo(string runPath)
    {
        var path = runPath + ".lock";
        try
        {
            if (!File.Exists(path)) return null;
            var age = _utcNow() - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            return new RingLockInfo(path, OwnerText(path), age, age > StaleLockAge);
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            return new RingLockInfo(path, "an owner that cannot be read", TimeSpan.Zero, false);
        }
    }

    private static string OwnerText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(stream.Length, 512)];
            stream.ReadExactly(buffer);
            var text = new string(Encoding.UTF8.GetString(buffer).Where(c => !char.IsControl(c)).ToArray()).Trim();
            var token = text.IndexOf(", lock ", StringComparison.Ordinal);
            if (token >= 0) text = text[..token];
            return text.Length > 0 ? text : "an unknown session";
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            return "an unknown session";
        }
    }

    /// <summary>The plain refusal when another session holds the lock of the run.</summary>
    public static string LockedMessage(RingLockInfo info)
    {
        var minutes = (int)Math.Floor(info.Age.TotalMinutes);
        var age = minutes < 1 ? "less than a minute ago" : minutes == 1 ? "1 minute ago" : $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes ago";
        var text = $"Another session holds the run: the lock file {info.Path} names {info.Owner} and was last written {age}. Try again when that session finishes.";
        return info.Stale
            ? text + $" The lock file is older than {StaleLockAge.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes. Another session may still be running: check before you remove the lock."
            : text + " If no session is working on this run, delete that file by hand.";
    }

    /// <summary>Takes the lock of an open run; throws a plain refusal that names the lock file and its owner when another session holds it.</summary>
    public RingRunLock Lock(string fileName, bool closedFolder = false)
    {
        var runPath = PathOf(fileName, closedFolder);
        try
        {
            if (!File.Exists(runPath)) throw new DeploymentServiceException("The run file does not exist any more. Refresh the runs.", DeploymentFailureKind.Stale);
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            throw new DeploymentServiceException("The run file could not be read. Check that the runs folder can be reached.", DeploymentFailureKind.Storage);
        }
        return NewLock(runPath) ?? throw new DeploymentServiceException(LockedMessage(LockInfo(runPath) ?? new RingLockInfo(runPath + ".lock", "an unknown session", TimeSpan.Zero, false)));
    }

    /// <summary>The lock file of the run path made with CreateNew and its owner written; null when the lock file exists.</summary>
    private RingRunLock? NewLock(string runPath)
    {
        var path = runPath + ".lock";
        var token = Guid.NewGuid().ToString("N");
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        // A lock file that another session is deleting refuses the open with access denied (seen
        // over SMB on CM01), so a refusal while the file exists means the lock is held.
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && File.Exists(path))
        {
            return null;
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"A ring run lock could not be made ({ex.GetType().Name}).");
            throw new DeploymentServiceException($"The lock file {path} could not be made, so the run was not changed. Check that the runs folder can be written.", DeploymentFailureKind.Storage);
        }
        try
        {
            using (stream)
            {
                var owner = $"{_user()} on {Environment.MachineName} at {LocalNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}, process {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}, lock {token}";
                stream.Write(Encoding.UTF8.GetBytes(owner));
            }
        }
        catch
        {
            // The lock file this call made would otherwise block the run until someone deletes it by hand.
            try { File.Delete(path); } catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex)) { }
            throw;
        }
        return new RingRunLock(path, token);
    }

    // ---- Write --------------------------------------------------------------------------------

    /// <summary>
    /// Writes a new run file named &lt;plan&gt;_&lt;objectKey&gt;_&lt;yyyyMMdd-HHmmss&gt;.json, or with the
    /// first free suffix -2, -3 when an open or closed run has that name; never overwrites.
    /// </summary>
    public RingRun Create(RingRun run, string objectKey)
    {
        var (created, held) = CreateLocked(run, objectKey);
        held.Dispose();
        return created;
    }

    /// <summary>
    /// <see cref="Create"/> with the lock of the new file taken before the file exists, so no other
    /// session can change the run between its first write and the first ring's create. The caller
    /// disposes the lock.
    /// </summary>
    public (RingRun Run, RingRunLock Lock) CreateLocked(RingRun run, string objectKey)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Rings.Count > RingPlanLimits.MaxRings) throw new DeploymentServiceException(CapMessage(run.Rings.Count));
        var folder = CheckedFolder();
        var json = ToJson(run, null, objectKey);
        var bytes = Utf8.GetBytes(json.ToJsonString(WriteOptions));
        var stem = $"{Token(run.PlanName)}_{Token(objectKey)}_{LocalNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        try
        {
            Directory.CreateDirectory(folder);
            for (var n = 1; n < 1000; n++)
            {
                var name = n == 1 ? stem + ".json" : $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.json";
                var path = Path.Combine(folder, name);
                if (File.Exists(path) || File.Exists(Path.Combine(folder, ClosedFolderName, name))) continue;
                if (NewLock(path) is not { } held) continue;
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(bytes);
                }
                catch (IOException) when (File.Exists(path))
                {
                    held.Dispose();
                    continue;
                }
                catch
                {
                    held.Dispose();
                    throw;
                }
                _log?.Info(LogArea, $"Wrote a ring run file with {run.Rings.Count} rings.");
                try
                {
                    return (ReadFile(path, false).Run, held);
                }
                catch
                {
                    held.Dispose();
                    throw;
                }
            }
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"A ring run file could not be written ({ex.GetType().Name}).");
            throw new DeploymentServiceException("The run file could not be written. Check that the ring runs folder can be written.", DeploymentFailureKind.Storage);
        }
        throw new DeploymentServiceException("No free run file name was found. Close or move old runs.", DeploymentFailureKind.Storage);
    }

    internal static string CapMessage(int rings) =>
        $"The run has {rings.ToString(CultureInfo.InvariantCulture)} rings. A run can have at most {RingPlanLimits.MaxRings.ToString(CultureInfo.InvariantCulture)} rings.";

    /// <summary>The text of a name part of a run file: letters, digits, underscores and hyphens; anything else becomes an underscore.</summary>
    public static string Token(string? text)
    {
        var value = new string((text ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (value.Length > 64) value = value[..64];
        return string.IsNullOrWhiteSpace(value.Trim('_')) ? "unnamed" : value;
    }

    /// <summary>
    /// Saves the run over the file it was read from (the caller holds the lock and read the file
    /// under it) and returns it as read back. Properties the run does not model stay as the file held them.
    /// </summary>
    public RingRun Save(RingRun run, RingRunLock held, bool closedFolder = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(held);
        var path = PathOf(run.FileName, closedFolder);
        if (!string.Equals(held.Path, path + ".lock", StringComparison.OrdinalIgnoreCase))
            throw new DeploymentServiceException($"The lock this session holds is {held.Path}, not the lock of the run file {path}, so the run file was not saved.", DeploymentFailureKind.Storage);
        if (!held.IsHeld())
            throw new DeploymentServiceException($"The lock file {held.Path} is not held by this session any more (it was deleted, or another session made it), so the run file was not saved. Refresh the runs and check the run before you act on it again.", DeploymentFailureKind.Storage);
        held.Touch();
        var current = ReadFile(path, closedFolder);
        var json = ToJson(run, current.Json, null);
        try
        {
            BeforeSave?.Invoke(run);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new DeploymentServiceException("The run file is a symbolic link or another reparse point, so it was not written.", DeploymentFailureKind.Storage);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                File.WriteAllBytes(temp, Utf8.GetBytes(json.ToJsonString(WriteOptions)));
                File.Replace(temp, path, null);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"A ring run file could not be saved ({ex.GetType().Name}).");
            throw new DeploymentServiceException("The run file could not be saved. Check that the ring runs folder can be written.", DeploymentFailureKind.Storage);
        }
        return ReadFile(path, closedFolder).Run;
    }

    /// <summary>Stamps ClosedAt, saves and moves the run to the closed folder under a free name; returns the closed run.</summary>
    public RingRun Close(RingRun run, RingRunLock held)
    {
        var saved = Save(run with { IsClosed = true }, held);
        var source = OpenPath(saved.FileName);
        try
        {
            var closed = Path.Combine(CheckedFolder(), ClosedFolderName);
            Directory.CreateDirectory(closed);
            if (new DirectoryInfo(closed).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DeploymentServiceException(LinkMessage, DeploymentFailureKind.Storage);
            var stem = Path.GetFileNameWithoutExtension(saved.FileName);
            for (var n = 1; n < 1000; n++)
            {
                var name = n == 1 ? saved.FileName : $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.json";
                var target = Path.Combine(closed, name);
                if (File.Exists(target)) continue;
                try
                {
                    File.Move(source, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target) && File.Exists(source))
                {
                    continue;
                }
                _log?.Info(LogArea, "Closed a ring run.");
                return ReadFile(target, true).Run;
            }
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"A ring run could not be moved to the closed folder ({ex.GetType().Name}).");
            throw new DeploymentServiceException("The run is marked closed but could not be moved to the closed folder. Check that the ring runs folder can be written.", DeploymentFailureKind.Storage);
        }
        throw new DeploymentServiceException("No free name was found in the closed folder.", DeploymentFailureKind.Storage);
    }

    private JsonObject ToJson(RingRun run, JsonObject? original, string? objectKey)
    {
        var o = original?.DeepClone().AsObject() ?? new JsonObject();
        var localNow = LocalNow.ToString(DateFormat, CultureInfo.InvariantCulture);
        Set(o, "SchemaVersion", RingPlanStore.Node(o, "SchemaVersion")?.DeepClone() ?? JsonValue.Create(1));
        Set(o, "RunId", run.RunId);
        Set(o, "PlanName", run.PlanName);
        Set(o, "TimeBasedOn", run.TimeBasedOn);
        Set(o, "HoldLaterRings", run.HoldLaterRings);
        if (original is null)
        {
            var createdUtc = DateTime.SpecifyKind(run.CreatedUtc, DateTimeKind.Utc);
            Set(o, "CreatedAt", TimeZoneInfo.ConvertTimeFromUtc(createdUtc, _local).ToString(DateFormat, CultureInfo.InvariantCulture));
            Set(o, "CreatedAtUtc", createdUtc.ToString(DateFormat, CultureInfo.InvariantCulture) + "Z");
            Set(o, "CreatedBy", run.CreatedBy);
            Set(o, "ClosedAt", (JsonNode?)null);
            var obj = new JsonObject
            {
                ["Type"] = run.ObjectType == DeploymentObjectType.SoftwareUpdateGroup ? "SUG" : run.ObjectType.ToString(),
                ["ID"] = objectKey ?? run.ObjectId,
                ["Name"] = run.ObjectName,
                ["ProgramName"] = run.ObjectType == DeploymentObjectType.Package ? run.Program : null
            };
            if (run.ObjectType == DeploymentObjectType.Application)
            {
                obj["CiId"] = run.ObjectId;
                if (run.ModelId.Length > 0) obj["ModelId"] = run.ModelId;
            }
            o["Object"] = obj;
        }
        if (run.IsClosed && string.IsNullOrWhiteSpace(RingPlanStore.Text(o, "ClosedAt"))) Set(o, "ClosedAt", localNow);
        var before = (RingPlanStore.Node(o, "Rings") as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var rings = new JsonArray();
        foreach (var ring in run.Rings.OrderBy(r => r.Index))
        {
            var old = before.FirstOrDefault(b => RingPlanStore.TryDays(b, "Index", null, out var i) && i == ring.Index);
            var r = old?.DeepClone().AsObject() ?? new JsonObject();
            if (old is null)
            {
                Set(r, "Index", ring.Index);
                foreach (var (key, value) in RingPlanStore.RingJson(ring.Definition ?? Plain(ring))) Set(r, key, value?.DeepClone());
            }
            var previous = old is null ? null : RingPlanStore.Text(old, "Status");
            Set(r, "Name", ring.Name);
            Set(r, "CollectionID", ring.CollectionId);
            Set(r, "CollectionName", ring.CollectionName);
            Set(r, "AvailableDateTime", ring.AvailableTime.ToString(DateFormat, CultureInfo.InvariantCulture));
            Set(r, "DeadlineDateTime", ring.Deadline?.ToString(DateFormat, CultureInfo.InvariantCulture));
            Set(r, "Status", ring.Status);
            Set(r, "DeploymentID", ring.DeploymentId);
            if (old is null) Set(r, "AssignmentID", (JsonNode?)null);
            Set(r, "CreatingAt", ring.CreatingAt?.ToString(DateFormat, CultureInfo.InvariantCulture));
            Set(r, "CreatingAtUtc", ring.CreatingAtUtc is { } cu ? cu.ToString(DateFormat, CultureInfo.InvariantCulture) + "Z" : null);
            Set(r, "CreatingBy", ring.CreatingBy);
            if (old is null) { Set(r, "CreatedAt", (JsonNode?)null); Set(r, "RemovedAt", (JsonNode?)null); }
            if (previous != ring.Status)
            {
                if (ring.Status == RingStatuses.Created) Set(r, "CreatedAt", localNow);
                if (ring.Status == RingStatuses.Removed) Set(r, "RemovedAt", localNow);
                if (ring.Status == RingStatuses.Cancelled) Set(r, "CancelledAt", localNow);
            }
            Set(r, "ShiftedMinutes", ring.ShiftedMinutes);
            rings.Add(r);
        }
        Set(o, "Rings", rings);
        return o;
    }

    private static RingDefinition Plain(RingRunRing ring) =>
        new(ring.Name, ring.CollectionId, ring.CollectionName, ring.Purpose, 0, null, "DisplayAll", false, false, false, ring.DpGroup, ring.SuccessThresholdPercent, null, null, null);

    /// <summary>Sets a property under the spelling the file already uses, or adds it.</summary>
    private static void Set(JsonObject o, string name, JsonNode? value)
    {
        var key = o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
        o[key] = value;
    }

    private static void Set(JsonObject o, string name, string? value) => Set(o, name, value is null ? null : JsonValue.Create(value));
    private static void Set(JsonObject o, string name, int value) => Set(o, name, JsonValue.Create(value));
    private static void Set(JsonObject o, string name, bool value) => Set(o, name, JsonValue.Create(value));
}

/// <summary>
/// A lock file of a run that this session made. The holder refreshes its write time while it works,
/// so the stale age counts from the last sign of life; every save checks that the file still holds
/// this lock's token. Dispose deletes the file only while it holds the token.
/// </summary>
public sealed class RingRunLock : IDisposable
{
    private int _released;

    internal RingRunLock(string path, string token)
    {
        Path = path;
        Token = token;
    }

    /// <summary>The full path of the lock file.</summary>
    public string Path { get; }

    internal string Token { get; }

    /// <summary>Sets the lock file's write time to now; a lock file that is gone or cannot be touched is left as it is.</summary>
    public void Touch()
    {
        try
        {
            if (IsHeld()) File.SetLastWriteTimeUtc(Path, DateTime.UtcNow);
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
        }
    }

    /// <summary>True while the lock file exists and holds this lock's token.</summary>
    public bool IsHeld()
    {
        if (Volatile.Read(ref _released) == 1) return false;
        try
        {
            if (!File.Exists(Path)) return false;
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Contains(Token, StringComparison.Ordinal);
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!IsHeld())
        {
            Interlocked.Exchange(ref _released, 1);
            return;
        }
        if (Interlocked.Exchange(ref _released, 1) == 1) return;
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (RingPlanStore.IsStorageFailure(ex))
        {
        }
    }
}
