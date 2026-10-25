using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>The Maintenance Windows folders under the per-user data root.</summary>
internal static class MaintenanceWindowsFolders
{
    public const string FolderName = "MaintenanceWindows";

    public static string Root() => Path.Combine(AppDataPaths.Root, FolderName);
}

/// <summary>
/// Writes maintenance window rows to a new CSV or HTML file. Every CSV cell is quoted, and
/// a cell that a spreadsheet would read as a formula starts with an apostrophe; every HTML
/// value is encoded.
/// </summary>
public sealed class MaintenanceReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IMaintenanceReportWriter
{
    public const string LinkMessage =
        "The Maintenance Windows reports folder is a junction or symbolic link. The report was not written.";

    private static readonly string[] Columns =
        ["Status", "Collection", "Collection ID", "Window", "Type", "Recurrence", "Schedule", "Duration", "UTC", "Next occurrence"];

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(MaintenanceWindowsFolders.Root(), "Reports");

    public string WriteCsv(IReadOnlyList<MaintenanceWindowInfo> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.Append(string.Join(",", Columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in rows)
            text.Append(string.Join(",", Values(row).Select(ReportFile.Cell))).Append("\r\n");
        return Write("csv", text.ToString());
    }

    public string WriteHtml(IReadOnlyList<MaintenanceWindowInfo> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var generated = _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>Maintenance windows</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse;width:100%}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top}")
            .Append("th{border-bottom:2px solid #333}")
            .Append(".disabled{color:#777}")
            .Append("</style></head><body>\r\n<h1>Maintenance windows</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(". Windows: ")
            .Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n<table><thead><tr>");
        foreach (var column in Columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
        html.Append("</tr></thead><tbody>\r\n");
        foreach (var row in rows)
        {
            html.Append(row.Enabled ? "<tr>" : "<tr class=\"disabled\">");
            foreach (var value in Values(row)) html.Append("<td>").Append(Encode(value)).Append("</td>");
            html.Append("</tr>\r\n");
        }
        html.Append("</tbody></table></body></html>\r\n");
        return Write("html", html.ToString());
    }

    /// <summary>The display text of a window type.</summary>
    public static string TypeText(string? windowType) => windowType switch
    {
        "Any" => "Any",
        "SoftwareUpdates" => "Software updates",
        "TaskSequences" => "Task sequences",
        _ => windowType ?? ""
    };

    /// <summary>The display text of a schedule kind.</summary>
    public static string KindText(MaintenanceScheduleKind kind) => kind switch
    {
        MaintenanceScheduleKind.OneTime => "One time",
        MaintenanceScheduleKind.Daily => "Daily",
        MaintenanceScheduleKind.Weekly => "Weekly",
        MaintenanceScheduleKind.MonthlyByDate => "Monthly by date",
        MaintenanceScheduleKind.MonthlyByWeekday => "Monthly by weekday",
        MaintenanceScheduleKind.PatchTuesday => "Patch Tuesday",
        _ => kind.ToString()
    };

    /// <summary>A duration as hours and minutes, for example "2h 30m" or "4h".</summary>
    public static string DurationText(TimeSpan duration)
    {
        var minutes = (int)Math.Round(duration.TotalMinutes);
        var text = (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h";
        return minutes % 60 == 0 ? text : text + " " + (minutes % 60).ToString(CultureInfo.InvariantCulture) + "m";
    }

    private static IEnumerable<string> Values(MaintenanceWindowInfo row) =>
    [
        row.Enabled ? "Enabled" : "Disabled",
        row.CollectionName ?? "",
        row.CollectionId ?? "",
        row.WindowName ?? "",
        TypeText(row.WindowType),
        row.Schedule is null ? "" : KindText(row.Schedule.Kind),
        row.ScheduleText ?? "",
        row.Schedule is null ? "" : DurationText(row.Schedule.Duration),
        row.Schedule?.IsUtc == true ? "Yes" : "No",
        row.NextOccurrence?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? ""
    ];

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private string Write(string extension, string content)
    {
        var target = folder ?? DefaultFolder();
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null)))
            throw new MaintenanceWindowException(LinkMessage);
        return ReportFile.WriteNew(target, "maintenance-windows", extension, content, _clock());
    }
}
