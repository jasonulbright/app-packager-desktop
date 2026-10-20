using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Deployment templates as JSON files, one per template, named by the template id. The files use
/// the property names of the suite tool's templates, so either tool reads the other's files; a
/// collection named in a suite file is not read and never written. The four seeds are written
/// once, into a folder that holds no template file. A seed cannot be changed or deleted; a file
/// that cannot be read is skipped and named in <see cref="Message"/>. A templates folder that is
/// a junction or symbolic link is neither read nor written.
/// </summary>
public sealed partial class DeploymentTemplateStore : IDeploymentTemplateStore
{
    public const string LogArea = "Deployments";
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxDeadlineOffsetHours = 8760;
    public const int MaxNameLength = 128;

    public const string LinkMessage =
        "The Deployments templates folder is a junction or symbolic link. The templates were not read or changed.";

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static readonly IReadOnlyList<DeploymentTemplate> Seeds =
    [
        new("WorkstationPilot", "Workstation Pilot", "Available", "DisplaySoftwareCenterOnly", false, false, false, false, true, false, true, 0, true),
        new("WorkstationProduction", "Workstation Production", "Required", "DisplayAll", false, false, false, true, true, false, true, 72, true),
        new("ServerPilot", "Server Pilot", "Available", "DisplaySoftwareCenterOnly", false, false, false, false, true, false, true, 0, true),
        new("ServerProduction", "Server Production", "Required", "HideAll", false, false, false, true, true, false, true, 168, true)
    ];

    private readonly string? _folder;
    private readonly IAppLog? _log;
    private readonly object _gate = new();

    public DeploymentTemplateStore(string? folder = null, IAppLog? log = null)
    {
        _folder = folder;
        _log = log;
    }

    public static string DefaultFolder() => Path.Combine(AppDataPaths.Root, "Deployments", "Templates");

    public string? Message { get; private set; }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex NotIdCharacter();

    private static bool IsSeed(string id) => Seeds.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<DeploymentTemplate> All()
    {
        lock (_gate)
        {
            Message = null;
            string folder;
            try
            {
                folder = Folder();
                if (!Directory.Exists(folder) || Directory.GetFiles(folder, "*.json").Length == 0) WriteSeeds(folder);
            }
            catch (DeploymentServiceException ex)
            {
                Message = ex.Message;
                return [];
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"The templates folder could not be read ({ex.GetType().Name}).");
                Message = "The templates folder could not be read. The log has the details.";
                return [];
            }

            var result = new List<DeploymentTemplate>();
            var skipped = new List<string>();
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json");
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                Message = "The templates folder could not be read. The log has the details.";
                _log?.Warning(LogArea, $"The templates folder could not be listed ({ex.GetType().Name}).");
                return [];
            }
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!IdRegex().IsMatch(id) || DeviceNameRegex().IsMatch(id) || Read(file, id) is not { } template)
                {
                    skipped.Add(Path.GetFileName(file));
                    continue;
                }
                result.Add(template);
            }
            if (skipped.Count > 0)
            {
                Message = skipped.Count == 1
                    ? $"1 template file could not be read and is not listed: {skipped[0]}."
                    : $"{skipped.Count.ToString(CultureInfo.InvariantCulture)} template files could not be read and are not listed: {string.Join(", ", skipped)}.";
                _log?.Warning(LogArea, $"Skipped {skipped.Count} template files that could not be read.");
            }
            return result.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public void Save(DeploymentTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var name = (t.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new DeploymentServiceException($"Enter a name for the template of at most {MaxNameLength} characters, on one line.");
        if (t.Purpose is not (DeploymentPurposes.Available or DeploymentPurposes.Required))
            throw new DeploymentServiceException("Select the purpose of the template: Available or Required.");
        if (!DeploymentScheduleRules.Notifications.Contains(t.UserNotification))
            throw new DeploymentServiceException("Select the user notification of the template.");
        if (t.DefaultDeadlineOffsetHours is < 0 or > MaxDeadlineOffsetHours)
            throw new DeploymentServiceException($"The deadline offset must be 0 to {MaxDeadlineOffsetHours} hours.");
        if (!string.IsNullOrEmpty(t.Id)) CheckUserId(t.Id);

        lock (_gate)
        {
            var folder = Folder();
            try
            {
                Directory.CreateDirectory(folder);
                var id = string.IsNullOrEmpty(t.Id) ? FreeId(folder, name) : t.Id;
                Write(Path.Combine(folder, id + ".json"), t with { Id = id, Name = name, IsSeeded = false });
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"A template could not be saved ({ex.GetType().Name}).");
                throw new DeploymentServiceException("The template could not be saved. The log has the details.");
            }
        }
        _log?.Info(LogArea, "Saved a user template.");
    }

    public void Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        CheckUserId(id);
        lock (_gate)
        {
            var path = Path.Combine(Folder(), id + ".json");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"A template could not be deleted ({ex.GetType().Name}).");
                throw new DeploymentServiceException("The template could not be deleted. The log has the details.");
            }
        }
        _log?.Info(LogArea, "Deleted a user template.");
    }

    private static void CheckUserId(string id)
    {
        if (!IdRegex().IsMatch(id))
            throw new DeploymentServiceException("The template id can contain only letters, digits, hyphens and underscores.");
        if (DeviceNameRegex().IsMatch(id))
            throw new DeploymentServiceException("The template id cannot be a Windows device name such as CON, NUL or COM1.");
        if (IsSeed(id))
            throw new DeploymentServiceException("A seeded template cannot be changed or deleted. Save it as a new template.");
    }

    /// <summary>The name without spaces and other characters a file name cannot hold, made unique in the folder.</summary>
    private static string FreeId(string folder, string name)
    {
        var stem = NotIdCharacter().Replace(name, "");
        if (stem.Length > 60) stem = stem[..60];
        if (stem.Length == 0 || DeviceNameRegex().IsMatch(stem)) stem = "Template";
        for (var n = 1; n < 1000; n++)
        {
            var id = n == 1 ? stem : $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}";
            if (!IsSeed(id) && !File.Exists(Path.Combine(folder, id + ".json"))) return id;
        }
        return Guid.NewGuid().ToString("N");
    }

    private string Folder()
    {
        var folder = _folder ?? DefaultFolder();
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, _folder is null)))
            throw new DeploymentServiceException(LinkMessage);
        return folder;
    }

    private void WriteSeeds(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var seed in Seeds)
        {
            var path = Path.Combine(folder, seed.Id + ".json");
            if (!File.Exists(path)) Write(path, seed);
        }
        _log?.Info(LogArea, "Wrote the four seeded templates.");
    }

    private DeploymentTemplate? Read(string file, string id)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxFileBytes) return null;
            if (JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) is not JsonObject o) return null;
            var name = Text(o, "Name")?.Trim();
            var purpose = Text(o, "DeployPurpose") ?? DeploymentPurposes.Available;
            var notification = Text(o, "UserNotification") ?? "DisplayAll";
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || purpose is not (DeploymentPurposes.Available or DeploymentPurposes.Required)
                || !DeploymentScheduleRules.Notifications.Contains(notification)) return null;
            var hours = o["DefaultDeadlineOffsetHours"] is JsonValue h && h.TryGetValue<int>(out var value) ? Math.Clamp(value, 0, MaxDeadlineOffsetHours) : 0;
            return new DeploymentTemplate(id, name, purpose, notification,
                string.Equals(Text(o, "TimeBasedOn"), "Utc", StringComparison.OrdinalIgnoreCase),
                Flag(o, "OverrideServiceWindow", false), Flag(o, "RebootOutsideServiceWindow", false), Flag(o, "AllowMeteredConnection", false),
                Flag(o, "AllowBoundaryFallback", true), Flag(o, "AllowMicrosoftUpdate", false), Flag(o, "RequirePostRebootFullScan", true), hours, IsSeed(id));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException || IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"A template file could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    private static string? Text(JsonObject o, string name) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonObject o, string name, bool missing) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v ? v.TryGetValue<bool>(out var b) && b : missing;

    /// <summary>Writes a temporary file beside the target and moves it over the target.</summary>
    private static void Write(string path, DeploymentTemplate t)
    {
        var o = new JsonObject
        {
            ["Name"] = t.Name,
            ["DeployPurpose"] = t.Purpose,
            ["UserNotification"] = t.UserNotification,
            ["TimeBasedOn"] = t.UtcBasis ? "Utc" : "LocalTime",
            ["OverrideServiceWindow"] = t.OverrideServiceWindow,
            ["RebootOutsideServiceWindow"] = t.RebootOutsideServiceWindow,
            ["AllowMeteredConnection"] = t.AllowMetered,
            ["AllowBoundaryFallback"] = t.AllowBoundaryFallback,
            ["AllowMicrosoftUpdate"] = t.AllowMicrosoftUpdate,
            ["RequirePostRebootFullScan"] = t.RequirePostRebootFullScan,
            ["DefaultDeadlineOffsetHours"] = t.DefaultDeadlineOffsetHours
        };
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The template file is a reparse point.");
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8Bom);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
