namespace ApplicationPackagerEngine.Services;

public enum MaintenanceScheduleKind
{
    OneTime,
    Daily,
    Weekly,
    MonthlyByDate,
    MonthlyByWeekday,
    PatchTuesday
}

/// <summary>
/// A maintenance window schedule in the terms of a ConfigMgr schedule token.
/// </summary>
/// <param name="Start">
/// The effective date and the start time of each occurrence, as wall-clock time: UTC when
/// <paramref name="IsUtc"/> is true, otherwise the local time of the client. The kind of
/// the value is ignored.
/// </param>
/// <param name="Duration">One minute up to 24 hours.</param>
/// <param name="Interval">
/// Days for <see cref="MaintenanceScheduleKind.Daily"/>, weeks for Weekly, months for the
/// monthly kinds and PatchTuesday; ignored for OneTime. One or more.
/// </param>
/// <param name="DayOfWeek">The day for Weekly and MonthlyByWeekday; Tuesday is implied for PatchTuesday.</param>
/// <param name="DayOfMonth">1 to 31 for MonthlyByDate, or 0 for the last day of the month. A month without that day has no occurrence, as on a ConfigMgr client.</param>
/// <param name="WeekOrder">1 to 4, or 5 for the last such weekday of the month, for MonthlyByWeekday.</param>
/// <param name="PatchTuesdayOffsetDays">
/// Days from the second Tuesday of the month for PatchTuesday (-7 to 21 can be stored). For
/// MonthlyByWeekday, ConfigMgr's offset: 0 to 7 days after the weekday. Ignored for other kinds.
/// </param>
/// <param name="End">
/// No occurrence starts after this time. A ConfigMgr schedule token has no end date, so a
/// write of a schedule with an end is refused.
/// </param>
public sealed record MaintenanceSchedule(
    MaintenanceScheduleKind Kind,
    DateTime Start,
    TimeSpan Duration,
    bool IsUtc,
    int Interval,
    DayOfWeek? DayOfWeek,
    int? DayOfMonth,
    int? WeekOrder,
    int PatchTuesdayOffsetDays,
    DateTime? End)
{
    /// <summary>The same schedule with its effective date set to <paramref name="date"/>, keeping the start time.</summary>
    public MaintenanceSchedule EffectiveOn(DateTime date) =>
        this with { Start = DateTime.SpecifyKind(date.Date + Start.TimeOfDay, DateTimeKind.Unspecified) };
}

public interface IScheduleProjector
{
    /// <summary>
    /// The start times of the next <paramref name="count"/> occurrences that are still open
    /// at <paramref name="from"/> (an occurrence that started earlier and has not ended is
    /// included), in the local time of the projector's time zone. <paramref name="from"/>
    /// is local time of that zone.
    /// </summary>
    IReadOnlyList<DateTime> NextOccurrences(MaintenanceSchedule schedule, DateTime from, int count);

    /// <summary>The schedule as plain text close to the wording of the ConfigMgr console.</summary>
    string Describe(MaintenanceSchedule schedule);

    /// <summary>Null when ConfigMgr can store the schedule; otherwise a plain reason.</summary>
    string? Validate(MaintenanceSchedule schedule);
}

/// <param name="FolderPath">The console folder path, for example "Servers\Production"; empty at the root.</param>
/// <param name="IsBuiltIn">True for a collection whose ID starts with SMS.</param>
public sealed record CollectionInfo(string CollectionId, string Name, string FolderPath, int MemberCount, bool IsBuiltIn);

/// <param name="WindowType">"Any", "SoftwareUpdates" or "TaskSequences".</param>
/// <param name="Schedule">
/// A schedule this model cannot hold (an hour or minute interval, several schedules in one
/// window) or one that <see cref="IScheduleProjector.Validate"/> refuses (a duration over 24
/// hours, a weekly interval of 5 to 7, a monthly interval of 13 to 15) has a zero duration,
/// the ConfigMgr text in <paramref name="ScheduleText"/> and no <paramref name="NextOccurrence"/>;
/// such a window can only be enabled or disabled here.
/// </param>
/// <param name="ScheduleText">The text of <see cref="IScheduleProjector.Describe"/> for the schedule ConfigMgr stores.</param>
/// <param name="NextOccurrence">The next start time in local time; null when no occurrence is left.</param>
/// <param name="Comment">The same text as <paramref name="Description"/> in a window the service read; writes ignore it.</param>
/// <param name="Description">
/// The Description of the window in ConfigMgr. New-CMMaintenanceWindow, and
/// Set-CMMaintenanceWindow with a schedule, write ConfigMgr's own schedule text there; a
/// different text is the window's own description. Create and Rename keep the own
/// description of the source window, and Update keeps the one of the window it changes.
/// Empty on a new window: ConfigMgr writes its text.
/// </param>
public sealed record MaintenanceWindowInfo(
    string CollectionId,
    string CollectionName,
    string WindowName,
    string ServiceWindowId,
    string WindowType,
    bool Enabled,
    MaintenanceSchedule Schedule,
    string ScheduleText,
    DateTime? NextOccurrence,
    string Comment,
    string Description = "");

/// <param name="Schedule">The date part of Start is a placeholder; apply the template with <see cref="MaintenanceSchedule.EffectiveOn"/>.</param>
public sealed record MaintenanceTemplate(
    string Id,
    string Name,
    string Description,
    MaintenanceSchedule Schedule,
    string WindowType,
    bool IsShipped)
{
    public override string ToString() => Name;
}

public enum ChangeKind
{
    Create,
    Update,
    Rename,
    SetEnabled,
    Remove,
    ApplyTemplate
}

/// <summary>
/// One change to one window. What each kind reads:
/// Create and ApplyTemplate create <see cref="After"/> (its id is ignored).
/// Update sets the schedule, type, enabled state and comment of <see cref="After"/> on the
/// window <see cref="Before"/>, same name.
/// Rename creates <see cref="After"/> under <see cref="NewWindowName"/> and then removes
/// <see cref="Before"/>.
/// SetEnabled sets the enabled state of <see cref="After"/> on the window <see cref="Before"/>.
/// Remove removes the window <see cref="Before"/>.
/// <see cref="Before"/> identifies the window by <see cref="MaintenanceWindowInfo.ServiceWindowId"/>
/// and is the state captured by <see cref="IMaintenanceWindowService.PrepareAsync"/>.
/// For ApplyTemplate, a <see cref="Before"/> after preparation is the window that already
/// has the name: the change is skipped unless <see cref="NewWindowName"/> gives another name.
/// </summary>
public sealed record MaintenanceChange(
    ChangeKind Kind,
    string CollectionId,
    string CollectionName,
    string WindowName,
    string? NewWindowName,
    MaintenanceWindowInfo? Before,
    MaintenanceWindowInfo? After);

/// <param name="Message">Plain text for the operator; never raw exception text.</param>
/// <param name="UndoChange">The change that reverses this one; set on every outcome that changed ConfigMgr.</param>
public sealed record MaintenanceOutcome(MaintenanceChange Change, bool Succeeded, string Message, MaintenanceChange? UndoChange);

public interface IMaintenanceWindowService
{
    /// <summary>The device collections, built-in ones included and flagged.</summary>
    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct);

    Task<IReadOnlyList<MaintenanceWindowInfo>> ListWindowsAsync(CancellationToken ct);

    /// <summary>
    /// Reads the current state and returns the change with <see cref="MaintenanceChange.Before"/>
    /// filled. Throws <see cref="MaintenanceWindowException"/> with a plain message when the
    /// change cannot be made in the current state (collection missing or built in, name taken,
    /// window gone, schedule invalid).
    /// </summary>
    Task<MaintenanceChange> PrepareAsync(MaintenanceChange request, CancellationToken ct);

    /// <summary>
    /// Applies the changes in order and reports each outcome through <paramref name="progress"/>
    /// as soon as it is known. Each change checks the current state first. Cancellation stops
    /// between changes; the changes not started are returned as skipped outcomes.
    /// </summary>
    Task<IReadOnlyList<MaintenanceOutcome>> ApplyAsync(
        IReadOnlyList<MaintenanceChange> changes,
        IProgress<MaintenanceOutcome> progress,
        CancellationToken ct);
}

public interface IMaintenanceTemplateStore
{
    /// <summary>The shipped templates first, then the user templates by name.</summary>
    IReadOnlyList<MaintenanceTemplate> All();

    /// <summary>Saves a user template; a template with a shipped id or IsShipped set is refused.</summary>
    void Save(MaintenanceTemplate t);

    /// <summary>Deletes a user template; a shipped id is refused.</summary>
    void Delete(string id);
}

public interface IMaintenanceReportWriter
{
    /// <summary>Writes the rows to a new CSV file and returns its path.</summary>
    string WriteCsv(IReadOnlyList<MaintenanceWindowInfo> rows);

    /// <summary>Writes the rows to a new HTML file and returns its path.</summary>
    string WriteHtml(IReadOnlyList<MaintenanceWindowInfo> rows);
}

/// <summary>A maintenance window operation that could not run; the message is plain text for the operator.</summary>
public sealed class MaintenanceWindowException(string message) : Exception(message);
