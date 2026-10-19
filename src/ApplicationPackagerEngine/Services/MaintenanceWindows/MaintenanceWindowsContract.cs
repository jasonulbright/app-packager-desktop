namespace ApplicationPackagerEngine.Services;

public enum MaintenanceScheduleKind { OneTime, Daily, Weekly, MonthlyByDate, MonthlyByWeekday, PatchTuesday }

/// <summary>WeekOrder is 1 to 4, or 5 for the last week of the month.</summary>
public sealed record MaintenanceSchedule(MaintenanceScheduleKind Kind, DateTime Start, TimeSpan Duration, bool IsUtc,
    int Interval, DayOfWeek? DayOfWeek, int? DayOfMonth, int? WeekOrder, int PatchTuesdayOffsetDays, DateTime? End);

public interface IScheduleProjector
{
    IReadOnlyList<DateTime> NextOccurrences(MaintenanceSchedule schedule, DateTime from, int count);
    string Describe(MaintenanceSchedule schedule);
}

public sealed record CollectionInfo(string CollectionId, string Name, string FolderPath, int MemberCount, bool IsBuiltIn);

/// <summary>WindowType is Any, SoftwareUpdates or TaskSequences.</summary>
public sealed record MaintenanceWindowInfo(string CollectionId, string CollectionName, string WindowName, string ServiceWindowId,
    string WindowType, bool Enabled, MaintenanceSchedule Schedule, string ScheduleText, DateTime? NextOccurrence, string Comment);

public sealed record MaintenanceTemplate(string Id, string Name, string Description, MaintenanceSchedule Schedule, string WindowType, bool IsShipped);

public enum ChangeKind { Create, Update, Rename, SetEnabled, Remove, ApplyTemplate }

public sealed record MaintenanceChange(ChangeKind Kind, string CollectionId, string CollectionName, string WindowName, string? NewWindowName,
    MaintenanceWindowInfo? Before, MaintenanceWindowInfo? After);

/// <summary>Message is plain text and never carries raw exception text.</summary>
public sealed record MaintenanceOutcome(MaintenanceChange Change, bool Succeeded, string Message, MaintenanceChange? UndoChange);

public interface IMaintenanceWindowService
{
    /// <summary>Built-in collections are included and flagged.</summary>
    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct);

    Task<IReadOnlyList<MaintenanceWindowInfo>> ListWindowsAsync(CancellationToken ct);

    /// <summary>Reads the current state and fills Before.</summary>
    Task<MaintenanceChange> PrepareAsync(MaintenanceChange request, CancellationToken ct);

    Task<IReadOnlyList<MaintenanceOutcome>> ApplyAsync(IReadOnlyList<MaintenanceChange> changes, IProgress<MaintenanceOutcome> progress, CancellationToken ct);
}

public interface IMaintenanceTemplateStore
{
    IReadOnlyList<MaintenanceTemplate> All();
    void Save(MaintenanceTemplate t);
    void Delete(string id);
}

public interface IMaintenanceReportWriter
{
    string WriteCsv(IReadOnlyList<MaintenanceWindowInfo> rows);
    string WriteHtml(IReadOnlyList<MaintenanceWindowInfo> rows);
}
