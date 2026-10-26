using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>A value and the text the page shows for it.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>The words the Maintenance Windows page uses for schedule and window values.</summary>
public static class MaintenanceWindowsText
{
    public const string Any = "Any";
    public const string SoftwareUpdates = "SoftwareUpdates";
    public const string TaskSequences = "TaskSequences";

    /// <summary>The recurrence text of a window whose schedule this page cannot hold.</summary>
    public const string OtherSchedule = "Other";

    public static IReadOnlyList<Choice<string>> WindowTypes { get; } =
    [
        new(Any, "All deployments"),
        new(SoftwareUpdates, "Software updates"),
        new(TaskSequences, "Task sequences")
    ];

    public static IReadOnlyList<Choice<MaintenanceScheduleKind>> Kinds { get; } =
    [
        new(MaintenanceScheduleKind.OneTime, "One time"),
        new(MaintenanceScheduleKind.Daily, "Daily"),
        new(MaintenanceScheduleKind.Weekly, "Weekly"),
        new(MaintenanceScheduleKind.MonthlyByDate, "Monthly by date"),
        new(MaintenanceScheduleKind.MonthlyByWeekday, "Monthly by weekday"),
        new(MaintenanceScheduleKind.PatchTuesday, "Patch Tuesday offset")
    ];

    public static string WindowType(string? value) =>
        WindowTypes.FirstOrDefault(t => string.Equals(t.Value, value, StringComparison.OrdinalIgnoreCase))?.Text ?? value ?? "";

    public static string Kind(MaintenanceScheduleKind kind) => Kinds.First(k => k.Value == kind).Text;

    public static string Duration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h {duration.Minutes}m");

    public static string Time(DateTime? value) =>
        value is { } time ? time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "None";

    public static string Occurrence(DateTime start, TimeSpan duration) =>
        start.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " to " +
        (start + duration).ToString(start.Date == (start + duration).Date ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);

    public static string Target(string collectionName, string windowName) => $"{collectionName}: {windowName}";
}
