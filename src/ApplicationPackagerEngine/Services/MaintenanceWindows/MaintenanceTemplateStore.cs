using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The shipped templates, read from embedded resources, and the user templates, one JSON
/// file per template named by its id. A user file that cannot be read is skipped and
/// logged; a user file with the id of a shipped template is ignored. A templates folder
/// that is a junction or symbolic link is neither read nor written.
/// </summary>
public sealed partial class MaintenanceTemplateStore : IMaintenanceTemplateStore
{
    public const string LogArea = "MaintenanceWindows";

    public const string LinkMessage =
        "The Maintenance Windows templates folder is a junction or symbolic link. The template was not saved or deleted.";

    /// <summary>The date part of every shipped template's start.</summary>
    public static readonly DateTime PlaceholderDate = new(2000, 1, 1);

    private const string ResourcePrefix = "ApplicationPackagerEngine.Services.MaintenanceWindows.Templates.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Lazy<IReadOnlyList<MaintenanceTemplate>> Shipped = new(LoadShipped);

    private readonly string? _folder;
    private readonly IAppLog? _log;
    private readonly ScheduleProjector _validator = new(TimeZoneInfo.Utc);
    private readonly object _gate = new();

    public MaintenanceTemplateStore(string? folder = null, IAppLog? log = null)
    {
        _folder = folder;
        _log = log;
    }

    public static string DefaultFolder() => Path.Combine(MaintenanceWindowsFolders.Root(), "Templates");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdRegex();

    /// <summary>A file named CON.json or COM1.json opens the device, not a file.</summary>
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNameRegex();

    public IReadOnlyList<MaintenanceTemplate> All()
    {
        lock (_gate)
        {
            var result = Shipped.Value.ToList();
            result.AddRange(ReadUserTemplates().OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));
            return result;
        }
    }

    public void Save(MaintenanceTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.IsShipped) throw new MaintenanceWindowException("A shipped template cannot be changed. Save it as a new template.");
        var id = string.IsNullOrEmpty(t.Id) ? Guid.NewGuid().ToString("N") : t.Id;
        CheckUserId(id);
        if (string.IsNullOrWhiteSpace(t.Name)) throw new MaintenanceWindowException("Enter a name for the template.");
        if (t.Schedule is null) throw new MaintenanceWindowException("The template has no schedule.");
        if (!MaintenanceWindowTypes.IsKnown(t.WindowType))
            throw new MaintenanceWindowException("Select the window type: any, software updates or task sequences.");
        if (_validator.Validate(t.Schedule) is { } reason) throw new MaintenanceWindowException(reason);

        lock (_gate)
        {
            var path = Path.Combine(Folder(), id + ".json");
            try
            {
                WorkbenchJson.WriteText(path, JsonSerializer.Serialize(t with { Id = id, Name = t.Name.Trim(), Description = t.Description ?? "" }, JsonOptions));
            }
            catch (Exception ex) when (IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"A template could not be saved ({ex.GetType().Name}).");
                throw new MaintenanceWindowException("The template could not be saved. The log has the details.");
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
                throw new MaintenanceWindowException("The template could not be deleted. The log has the details.");
            }
        }
        _log?.Info(LogArea, "Deleted a user template.");
    }

    private static void CheckUserId(string id)
    {
        if (!IdRegex().IsMatch(id))
            throw new MaintenanceWindowException("The template id can contain only letters, digits, hyphens and underscores.");
        if (DeviceNameRegex().IsMatch(id))
            throw new MaintenanceWindowException("The template id cannot be a Windows device name such as CON, NUL or COM1.");
        if (Shipped.Value.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new MaintenanceWindowException("A shipped template cannot be changed or deleted.");
    }

    /// <summary>The templates folder; one that is a junction or symbolic link throws.</summary>
    private string Folder()
    {
        var folder = _folder ?? DefaultFolder();
        if (DataFolderLinks.HasLink(folder, DataFolderLinks.ContainerOf(folder, _folder is null)))
            throw new MaintenanceWindowException(LinkMessage);
        return folder;
    }

    private List<MaintenanceTemplate> ReadUserTemplates()
    {
        var result = new List<MaintenanceTemplate>();
        string folder;
        string[] files;
        try
        {
            folder = Folder();
            if (!Directory.Exists(folder)) return result;
            files = Directory.GetFiles(folder, "*.json");
        }
        catch (MaintenanceWindowException)
        {
            _log?.Warning(LogArea, "The templates folder is a junction or symbolic link; the user templates are not read.");
            return result;
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            _log?.Warning(LogArea, $"The templates folder could not be read ({ex.GetType().Name}).");
            return result;
        }

        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IdRegex().IsMatch(id) || Shipped.Value.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var template = JsonSerializer.Deserialize<MaintenanceTemplate>(File.ReadAllText(file), JsonOptions);
                if (template?.Schedule is null || string.IsNullOrWhiteSpace(template.Name)
                    || !string.Equals(template.Id, id, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("The template is incomplete.");
                result.Add(template with { IsShipped = false, Description = template.Description ?? "", WindowType = template.WindowType ?? "Any" });
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException || IsStorageFailure(ex))
            {
                _log?.Warning(LogArea, $"A user template file could not be read and is skipped ({ex.GetType().Name}).");
            }
        }
        return result;
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private sealed record ShippedTemplateFile(
        string? Name,
        string? Description,
        string? RecurrenceType,
        string? WindowType,
        int DurationHours,
        int DurationMinutes,
        int StartHour,
        int StartMinute,
        bool IsUtc,
        string? DayOfWeek,
        string? WeekOrder,
        int DayOfMonth,
        int RecurCount,
        int PatchTuesdayOffset);

    private static IReadOnlyList<MaintenanceTemplate> LoadShipped()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new List<MaintenanceTemplate>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var source = JsonSerializer.Deserialize<ShippedTemplateFile>(stream)
                ?? throw new InvalidOperationException($"The shipped template {name} is empty.");
            var id = name[ResourcePrefix.Length..^".json".Length];
            result.Add(new MaintenanceTemplate(id, source.Name ?? id, source.Description ?? "", Convert(source), WindowType(source.WindowType), true));
        }
        return result;
    }

    /// <summary>
    /// A non-negative PatchTuesdayOffset in a shipped template file makes the template a
    /// Patch Tuesday schedule whatever its RecurrenceType says.
    /// </summary>
    private static MaintenanceSchedule Convert(ShippedTemplateFile s)
    {
        var kind = s.PatchTuesdayOffset >= 0
            ? MaintenanceScheduleKind.PatchTuesday
            : Enum.Parse<MaintenanceScheduleKind>(s.RecurrenceType ?? nameof(MaintenanceScheduleKind.Daily), ignoreCase: true);
        DayOfWeek? day = kind is MaintenanceScheduleKind.Weekly or MaintenanceScheduleKind.MonthlyByWeekday
            ? Enum.Parse<DayOfWeek>(s.DayOfWeek ?? nameof(System.DayOfWeek.Sunday), ignoreCase: true)
            : null;
        int? week = kind == MaintenanceScheduleKind.MonthlyByWeekday
            ? (s.WeekOrder ?? "First").ToLowerInvariant() switch { "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "last" => 5, _ => 1 }
            : null;
        int? dayOfMonth = kind == MaintenanceScheduleKind.MonthlyByDate ? s.DayOfMonth : null;
        return new MaintenanceSchedule(
            kind,
            PlaceholderDate.AddHours(s.StartHour).AddMinutes(s.StartMinute),
            TimeSpan.FromMinutes(s.DurationHours * 60 + s.DurationMinutes),
            s.IsUtc,
            Math.Max(1, s.RecurCount),
            day,
            dayOfMonth,
            week,
            kind == MaintenanceScheduleKind.PatchTuesday ? s.PatchTuesdayOffset : 0,
            null);
    }

    private static string WindowType(string? source) => source switch
    {
        "SoftwareUpdatesOnly" => MaintenanceWindowTypes.SoftwareUpdates,
        "TaskSequencesOnly" => MaintenanceWindowTypes.TaskSequences,
        _ => MaintenanceWindowTypes.Any
    };
}

/// <summary>The values of <see cref="MaintenanceWindowInfo.WindowType"/> and their ConfigMgr forms.</summary>
public static class MaintenanceWindowTypes
{
    public const string Any = "Any";
    public const string SoftwareUpdates = "SoftwareUpdates";
    public const string TaskSequences = "TaskSequences";

    public static bool IsKnown(string? type) => type is Any or SoftwareUpdates or TaskSequences;

    /// <summary>The ServiceWindowType of SMS_ServiceWindow: 1 general, 4 software updates, 5 task sequences.</summary>
    public static int ToServiceWindowType(string type) => type switch
    {
        SoftwareUpdates => 4,
        TaskSequences => 5,
        _ => 1
    };

    public static string FromServiceWindowType(int value) => value switch
    {
        1 => Any,
        4 => SoftwareUpdates,
        5 => TaskSequences,
        _ => $"Unknown ({value})"
    };

    /// <summary>The -ApplyTo value of New-CMMaintenanceWindow and Set-CMMaintenanceWindow.</summary>
    public static string ToApplyTo(string type) => type switch
    {
        SoftwareUpdates => "SoftwareUpdatesOnly",
        TaskSequences => "TaskSequencesOnly",
        _ => "Any"
    };
}
