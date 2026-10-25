using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Ring plans as JSON files, one per plan, named by the plan name. The files use the property
/// names of the suite tool's plan files, so both tools read the same folder; the option fields a
/// plan leaves at their defaults are not written, as the suite tool's seeds do not. The two seeds
/// are written once, into a folder that holds no plan file, with empty collections. A seed is never
/// replaced or deleted. A file that cannot be used is skipped and named in <see cref="Message"/>.
/// A plans folder or plan file that is a junction or symbolic link is neither read nor written.
/// </summary>
public sealed partial class RingPlanStore
{
    public const string LogArea = "Deployments";
    public const int MaxFileBytes = 256 * 1024;

    public const string LinkMessage = "The ring plans folder is a junction or symbolic link. The plans were not read or changed.";

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static readonly IReadOnlyList<RingPlan> Seeds =
    [
        new("Workstation-Rings", "Four workstation rings. Set each CollectionID before use.", false, RingPlanRules.LocalTime,
        [
            SeedRing("QA", 0, 1, "DisplayAll", null), SeedRing("Pilot", 2, 5, "DisplayAll", 90), SeedRing("Prod 1", 7, 10, "DisplayAll", 95), SeedRing("Prod Final", 14, 17, "DisplayAll", null)
        ], true, "Workstation-Rings.json"),
        new("Server-Rings", "Two server rings; Prod waits for Promote. Set each CollectionID before use.", true, RingPlanRules.LocalTime,
        [
            SeedRing("Test", 0, 2, "HideAll", 95), SeedRing("Prod", 7, 9, "HideAll", null)
        ], true, "Server-Rings.json")
    ];

    private static RingDefinition SeedRing(string name, int available, int deadline, string notification, int? threshold) =>
        new(name, "", "", DeploymentPurposes.Required, available, deadline, notification, false, false, false, "", threshold, null, null, null);

    private readonly Func<string?> _folder;
    private readonly IAppLog? _log;
    private readonly object _gate = new();

    public RingPlanStore(Func<string?>? folder = null, IAppLog? log = null)
    {
        _folder = folder ?? (() => null);
        _log = log;
    }

    public static string DefaultFolder() => Path.Combine(AppDataPaths.Root, "Deployments", "Rings");

    public string Folder => _folder() is { } custom && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : DefaultFolder();

    private bool IsDefault => _folder() is not { } custom || string.IsNullOrWhiteSpace(custom);

    public string? Message { get; private set; }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _.()\-]{0,63}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();

    public static bool IsSeed(string? name) => Seeds.Any(s => string.Equals(s.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A plain reason when the name cannot be a plan file name; null when it can.</summary>
    public static string? NameProblem(string? name)
    {
        var value = name ?? "";
        if (value.Length == 0 || !NameRegex().IsMatch(value))
            return "Enter a plan name of 1 to 64 characters: letters, digits, spaces, dots, hyphens, underscores and parentheses, starting with a letter or digit.";
        if (value.EndsWith('.') || value.EndsWith(' ') || value.Contains("..", StringComparison.Ordinal))
            return "A plan name cannot end with a dot or a space or hold two dots in a row.";
        var stem = value.Split('.')[0].TrimEnd();
        if (DeviceNameRegex().IsMatch(stem)) return "A plan name cannot be a Windows device name such as CON, NUL or COM1.";
        return null;
    }

    private string CheckedFolder()
    {
        var folder = Folder;
        if (!Path.IsPathFullyQualified(folder)) throw new DeploymentServiceException("The ring plans folder must be a full path, such as C:\\Rings or \\\\server\\share\\Rings.", DeploymentFailureKind.Storage);
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, IsDefault))) throw new DeploymentServiceException(LinkMessage, DeploymentFailureKind.Storage);
        return folder;
    }

    public IReadOnlyList<RingPlan> List()
    {
        lock (_gate)
        {
            Message = null;
            string folder;
            string[] files;
            try
            {
                folder = CheckedFolder();
                if (!Directory.Exists(folder) || Directory.GetFiles(folder, "*.json").Length == 0) WriteSeeds(folder);
                files = Directory.GetFiles(folder, "*.json");
            }
            catch (DeploymentServiceException ex)
            {
                Message = ex.Message;
                return [];
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"The ring plans folder could not be read ({ex.GetType().Name}).");
                Message = "The ring plans folder could not be read. The log has the details.";
                return [];
            }
            var plans = new List<RingPlan>();
            var skipped = new List<string>();
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var leaf = Path.GetFileName(file);
                var (plan, problem) = Read(file);
                if (plan is null)
                {
                    skipped.Add($"{leaf}: {problem}");
                    continue;
                }
                if (plans.Any(p => string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add($"{leaf}: another file already holds a plan named {plan.Name}.");
                    continue;
                }
                plans.Add(plan);
            }
            if (skipped.Count > 0)
            {
                Message = (skipped.Count == 1 ? "1 plan file is not listed. " : $"{skipped.Count.ToString(CultureInfo.InvariantCulture)} plan files are not listed. ") + string.Join(" ", skipped);
                _log?.Warning(LogArea, $"Skipped {skipped.Count} ring plan files.");
            }
            return plans.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Validates and writes the plan as &lt;name&gt;.json; returns the plan as listed.</summary>
    /// <param name="expectedFileStamp">A replace is refused unless the plan file still has this stamp; null takes the stamp the plan carries, and no stamp skips the check.</param>
    public RingPlan Save(RingPlan plan, bool replace, string? expectedFileStamp = null)
    {
        var stamp = expectedFileStamp ?? (replace && plan?.FileStamp is { Length: > 0 } carried ? carried : null);
        ArgumentNullException.ThrowIfNull(plan);
        var name = (plan.Name ?? "").Trim();
        if (NameProblem(name) is { } nameProblem) throw new DeploymentServiceException(nameProblem);
        if (IsSeed(name)) throw new DeploymentServiceException($"Plan {name} is a seed plan and cannot be changed. Copy it under a new name.");
        var normalized = Normalize(plan with { Name = name, IsSeed = false });
        var errors = RingPlanRules.Validate(normalized);
        if (errors.Count > 0) throw new DeploymentServiceException("The plan cannot be saved: " + string.Join(" ", errors));
        var written = "";
        lock (_gate)
        {
            var folder = CheckedFolder();
            var path = Path.Combine(folder, name + ".json");
            try
            {
                Directory.CreateDirectory(folder);
                var existing = Directory.GetFiles(folder, "*.json")
                    .Where(f => !string.Equals(Path.GetFileName(f), name + ".json", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Read(f).Plan).Any(p => p is not null && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing || (!replace && File.Exists(path)))
                    throw new DeploymentServiceException($"A plan named {name} exists. Choose another name.");
                if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    throw new DeploymentServiceException("The plan file is a symbolic link or another reparse point, so it was not written.", DeploymentFailureKind.Storage);
                written = Write(path, normalized, replace, stamp, name);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"A ring plan could not be saved ({ex.GetType().Name}).");
                throw new DeploymentServiceException("The plan could not be saved. Check that the ring plans folder can be written.", DeploymentFailureKind.Storage);
            }
        }
        _log?.Info(LogArea, $"Saved a ring plan with {normalized.Rings.Count} rings.");
        return normalized with { FileName = name + ".json", FileStamp = written };
    }

    public void Delete(string name)
    {
        var value = (name ?? "").Trim();
        if (IsSeed(value)) throw new DeploymentServiceException($"Plan {value} is a seed plan and cannot be deleted.");
        lock (_gate)
        {
            var folder = CheckedFolder();
            try
            {
                var file = Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.json").FirstOrDefault(f => Read(f).Plan is { } p && string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (file is null) throw new DeploymentServiceException($"Plan {value} does not exist any more.", DeploymentFailureKind.Stale);
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    throw new DeploymentServiceException("The plan file is a symbolic link or another reparse point, so it was not deleted.", DeploymentFailureKind.Storage);
                using (PlanLock(file, value)) File.Delete(file);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"A ring plan could not be deleted ({ex.GetType().Name}).");
                throw new DeploymentServiceException("The plan could not be deleted. Check that the ring plans folder can be written.", DeploymentFailureKind.Storage);
            }
        }
        _log?.Info(LogArea, "Deleted a ring plan.");
    }

    private static RingPlan Normalize(RingPlan plan) => plan with
    {
        Description = plan.Description ?? "",
        Rings = (plan.Rings ?? []).Select(r => r with
        {
            Name = (r.Name ?? "").Trim(),
            CollectionId = (r.CollectionId ?? "").Trim().ToUpperInvariant(),
            CollectionName = (r.CollectionName ?? "").Trim(),
            DpGroup = (r.DpGroup ?? "").Trim(),
            DeadlineOffsetDays = r.Purpose == DeploymentPurposes.Required ? r.DeadlineOffsetDays : null
        }).ToList()
    };

    private void WriteSeeds(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var seed in Seeds)
        {
            var path = Path.Combine(folder, seed.FileName);
            if (File.Exists(path)) continue;
            try
            {
                Write(path, seed, false, null, seed.Name);
            }
            catch (DeploymentServiceException) when (File.Exists(path))
            {
            }
        }
        _log?.Info(LogArea, "Wrote the two seed ring plans.");
    }

    // ---- Read ---------------------------------------------------------------------------------

    /// <summary>A plan file read and validated; the plan is null and the problem plain text when it cannot be used.</summary>
    internal (RingPlan? Plan, string Problem) Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (null, "the file is a symbolic link or another reparse point.");
            if (info.Length > MaxFileBytes) return (null, $"the file is larger than {(MaxFileBytes / 1024).ToString(CultureInfo.InvariantCulture)} KB.");
            var bytes = File.ReadAllBytes(file);
            var stamp = RingRunStore.Fingerprint(bytes, File.GetLastWriteTimeUtc(file));
            if (JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) is not JsonObject o) return (null, "the file does not hold a plan.");
            var (plan, problem) = Parse(o);
            if (plan is null) return (null, problem);
            plan = plan with { FileName = Path.GetFileName(file), IsSeed = IsSeed(plan.Name), FileStamp = stamp };
            var errors = RingPlanRules.Validate(plan);
            if (errors.Count > 0) return (null, string.Join(" ", errors));
            return (plan, "");
        }
        catch (JsonException)
        {
            return (null, "the file is not valid JSON.");
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"A ring plan file could not be read ({ex.GetType().Name}).");
            return (null, "the file could not be read.");
        }
    }

    /// <summary>A plan from the JSON of a plan file; the option fields a file leaves out take the defaults of the suite tool.</summary>
    internal static (RingPlan? Plan, string Problem) Parse(JsonObject o)
    {
        var name = (Text(o, "Name") ?? "").Trim();
        if (name.Length == 0) return (null, "the plan has no name.");
        if (Node(o, "Rings") is not JsonArray array) return (null, "the plan has no rings.");
        if (array.Count > RingPlanRules.MaxRings)
            return (null, $"the plan has {array.Count.ToString(CultureInfo.InvariantCulture)} rings. A plan can have at most {RingPlanRules.MaxRings.ToString(CultureInfo.InvariantCulture)} rings.");
        var rings = new List<RingDefinition>();
        var index = 0;
        foreach (var node in array)
        {
            if (node is not JsonObject r) continue;
            index++;
            var (ring, problem) = ParseRing(r, index);
            if (ring is null) return (null, problem);
            rings.Add(ring);
        }
        if (!TryBool(o, "HoldLaterRings", false, out var hold)) return (null, "HoldLaterRings is not true or false.");
        return (new RingPlan(name, Text(o, "Description") ?? "", hold, Text(o, "TimeBasedOn") ?? RingPlanRules.LocalTime, rings, false), "");
    }

    internal static (RingDefinition? Ring, string Problem) ParseRing(JsonObject r, int index)
    {
        var name = (Text(r, "Name") ?? "").Trim();
        var label = RingPlanRules.Label(index, name);
        if (!TryDays(r, "AvailableOffsetDays", 0, out var available)) return (null, $"{label} available offset must be a whole number of days.");
        if (!TryDays(r, "DeadlineOffsetDays", null, out var deadline)) return (null, $"{label} deadline offset must be a whole number of days.");
        if (!TryDays(r, "SuccessThresholdPercent", null, out var threshold)) return (null, $"{label} success threshold must be a whole number from 0 to 100.");
        bool Flag(string field, bool missing, out bool value) => TryBool(r, field, missing, out value);
        if (!Flag("OverrideServiceWindow", false, out var over) || !Flag("RebootOutsideServiceWindow", false, out var reboot) || !Flag("AllowMeteredConnection", false, out var metered)
            || !Flag("AllowBoundaryFallback", true, out var fallback) || !Flag("AllowMicrosoftUpdate", false, out var microsoft) || !Flag("RequirePostRebootFullScan", true, out var scan)
            || !Flag("ShowTaskSequenceProgress", true, out var progress))
            return (null, $"{label} has an option that is not true or false.");
        var purpose = Text(r, "Purpose") ?? DeploymentPurposes.Required;
        return (new RingDefinition(name, (Text(r, "CollectionID") ?? "").Trim().ToUpperInvariant(), (Text(r, "CollectionName") ?? "").Trim(), purpose, available ?? 0,
            purpose == DeploymentPurposes.Required ? deadline : null, Text(r, "UserNotification") ?? "DisplayAll", over, reboot, metered, (Text(r, "DPGroup") ?? "").Trim(), threshold,
            Empty(Text(r, "FastNetworkOption")), Empty(Text(r, "SlowNetworkOption")), Empty(Text(r, "RerunBehavior")), fallback, microsoft, scan, progress,
            Empty(Text(r, "TaskSequenceAvailability")) ?? "Clients", Empty(Text(r, "TaskSequenceDeploymentOption"))), "");
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static JsonNode? Node(JsonObject o, string name) => o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    internal static string? Text(JsonObject o, string name) => Node(o, name) switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
        _ => null
    };

    /// <summary>A whole number of days (or percent) from a number or numeric text; a missing, null or empty value gives the default; a fraction fails.</summary>
    internal static bool TryDays(JsonObject o, string name, int? missing, out int? value)
    {
        value = missing;
        var node = Node(o, name);
        if (node is null) return true;
        if (node is not JsonValue v) return false;
        double number;
        if (v.TryGetValue<string>(out var s))
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
        }
        else if (v.GetValueKind() == JsonValueKind.Number) number = v.GetValue<double>();
        else return false;
        if (double.IsNaN(number) || double.IsInfinity(number) || number != Math.Floor(number) || number is > int.MaxValue or < int.MinValue) return false;
        value = (int)number;
        return true;
    }

    internal static bool TryBool(JsonObject o, string name, bool missing, out bool value)
    {
        value = missing;
        switch (Node(o, name))
        {
            case null: return true;
            case JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False: value = v.GetValue<bool>(); return true;
            case JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s.Trim(), out var b): value = b; return true;
            default: return false;
        }
    }

    // ---- Write --------------------------------------------------------------------------------

    internal static JsonObject RingJson(RingDefinition r)
    {
        var o = new JsonObject
        {
            ["Name"] = r.Name,
            ["CollectionID"] = r.CollectionId,
            ["CollectionName"] = r.CollectionName,
            ["Purpose"] = r.Purpose,
            ["AvailableOffsetDays"] = r.AvailableOffsetDays,
            ["DeadlineOffsetDays"] = r.DeadlineOffsetDays is { } d ? JsonValue.Create(d) : null,
            ["UserNotification"] = r.UserNotification,
            ["OverrideServiceWindow"] = r.OverrideServiceWindow,
            ["RebootOutsideServiceWindow"] = r.RebootOutsideServiceWindow,
            ["AllowMeteredConnection"] = r.AllowMetered,
            ["DPGroup"] = r.DpGroup,
            ["SuccessThresholdPercent"] = r.SuccessThresholdPercent is { } t ? JsonValue.Create(t) : null
        };
        if (!r.AllowBoundaryFallback) o["AllowBoundaryFallback"] = false;
        if (r.AllowMicrosoftUpdate) o["AllowMicrosoftUpdate"] = true;
        if (!r.RequirePostRebootFullScan) o["RequirePostRebootFullScan"] = false;
        if (!r.ShowTaskSequenceProgress) o["ShowTaskSequenceProgress"] = false;
        if (r.TaskSequenceAvailability != "Clients") o["TaskSequenceAvailability"] = r.TaskSequenceAvailability;
        if (r.FastNetworkOption is { } fast) o["FastNetworkOption"] = fast;
        if (r.SlowNetworkOption is { } slow) o["SlowNetworkOption"] = slow;
        if (r.RerunBehavior is { } rerun) o["RerunBehavior"] = rerun;
        if (r.TaskSequenceDeploymentOption is { } option) o["TaskSequenceDeploymentOption"] = option;
        return o;
    }

    /// <summary>The stamp of a plan file now; null when it cannot be read.</summary>
    private static string? StampOf(string path)
    {
        try
        {
            return RingRunStore.Fingerprint(File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return null;
        }
    }

    /// <summary>A stamp that matches no file: a plan whose file could not be read back must be opened again before a replace.</summary>
    private const string UnreadStamp = "unread";

    /// <summary>
    /// The lock file of one plan file, made with CreateNew and deleted when disposed: every write and
    /// delete of the plan holds it, so a stamp check and the move that follows are one step for all
    /// sessions that use the folder.
    /// </summary>
    private static FileStream PlanLock(string path, string name)
    {
        var lockPath = path + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            // The holder's DeleteOnClose can remove the file between the refused create and the check, so the error code decides too.
            catch (IOException ex) when (File.Exists(lockPath) || (ex.HResult & 0xFFFF) is ErrorFileExists or ErrorAlreadyExists)
            {
                throw new DeploymentServiceException($"Another session is saving or deleting plan {name} now, so nothing was changed. Try again; if no session is working on it, delete {lockPath} by hand.", DeploymentFailureKind.Stale);
            }
            // A lock file in delete-pending state refuses a new create with access denied until its last handle closes.
            catch (UnauthorizedAccessException) when (attempt < LockAttempts)
            {
                Thread.Sleep(LockRetryDelay);
            }
        }
    }

    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int LockAttempts = 5;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Writes a temporary file and moves it to the plan file under the plan's lock. A new plan moves
    /// without overwrite, so of two sessions that create the same name one is refused; a replace with
    /// a stamp is refused when the file changed since the stamp was read. Returns the stamp of the
    /// file this call wrote.
    /// </summary>
    private static string Write(string path, RingPlan plan, bool replace, string? stamp, string name)
    {
        var o = new JsonObject
        {
            ["Name"] = plan.Name,
            ["Description"] = plan.Description,
            ["HoldLaterRings"] = plan.HoldLaterRings,
            ["TimeBasedOn"] = plan.TimeBasedOn,
            ["Rings"] = new JsonArray(plan.Rings.Select(r => (JsonNode)RingJson(r)).ToArray())
        };
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Utf8Bom);
            using (PlanLock(path, name))
            {
                if (!replace)
                {
                    try
                    {
                        File.Move(temp, path, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        throw new DeploymentServiceException($"A plan named {name} exists. Choose another name.");
                    }
                }
                else
                {
                    if (stamp is not null)
                    {
                        if (!File.Exists(path)) throw new DeploymentServiceException($"Plan {name} does not exist any more. It was not saved; save it as a new plan.", DeploymentFailureKind.Stale);
                        if (StampOf(path) is not { } now || !string.Equals(now, stamp, StringComparison.Ordinal))
                            throw new DeploymentServiceException($"Plan {name} changed since it was opened (another session saved it), so it was not saved. Open it again, then edit it.", DeploymentFailureKind.Stale);
                    }
                    File.Move(temp, path, overwrite: true);
                }
                return StampOf(path) ?? UnreadStamp;
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
    internal static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
