using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes the detection history to a new CSV or HTML file. Both carry the entry's time,
/// kind, description, mode, result and summary, never the clause's script text. Every
/// CSV cell is quoted, and a cell that a spreadsheet would read as a formula starts with
/// an apostrophe; every HTML value is encoded.
/// </summary>
public sealed class DetectionReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IDetectionReportWriter
{
    private static readonly string[] Columns = ["Time", "Kind", "Description", "Mode", "Result", "Summary"];

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(DetectionTesterFolders.Root(), "Reports");

    public string WriteCsv(IReadOnlyList<DetectionHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var text = new StringBuilder();
        text.Append(string.Join(",", Columns.Select(Cell))).Append("\r\n");
        foreach (var entry in entries)
            text.Append(string.Join(",", Row(entry).Select(Cell))).Append("\r\n");
        return Write("csv", text.ToString());
    }

    public string WriteHtml(IReadOnlyList<DetectionHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var generated = _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>Detection test history</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse;width:100%}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>Detection test history</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(". Tests: ")
            .Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n<table><thead><tr>");
        foreach (var column in Columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
        html.Append("</tr></thead><tbody>\r\n");
        foreach (var entry in entries)
        {
            html.Append("<tr>");
            foreach (var value in Row(entry)) html.Append("<td>").Append(Encode(value)).Append("</td>");
            html.Append("</tr>\r\n");
        }
        html.Append("</tbody></table></body></html>\r\n");
        return Write("html", html.ToString());
    }

    private static IEnumerable<string> Row(DetectionHistoryEntry entry) =>
    [
        entry.Time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        entry.Kind ?? "",
        entry.Description ?? "",
        entry.Negative ? "Negative" : "Positive",
        entry.Passed ? "Passed" : "Failed",
        entry.Summary ?? ""
    ];

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private string Write(string extension, string content)
    {
        var target = folder ?? DefaultFolder();
        var container = DetectionTesterFolders.ContainerOf(target, folder is null);
        if (DetectionTesterFolders.HasLink(target, container))
            throw new DetectionFolderRefusedException(DetectionFolderRefusedException.ReportsMessage);
        Directory.CreateDirectory(target);
        var stamp = _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"detection-history-{stamp}.{extension}" : $"detection-history-{stamp}-{n}.{extension}";
            var path = Path.Combine(target, name);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, encoding);
                writer.Write(content);
                return path;
            }
            catch (IOException) when (File.Exists(path) && n < 1000)
            {
            }
        }
    }
}
