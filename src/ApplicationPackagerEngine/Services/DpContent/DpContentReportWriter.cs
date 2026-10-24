using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes one view of the DP Content Manager page to a new CSV or HTML file in
/// &lt;data root&gt;\DpContent\Reports. Every CSV cell is quoted, and a cell that a spreadsheet would read
/// as a formula starts with an apostrophe; every HTML value and column name is encoded. A report of
/// a truncated load is refused unless its note says so; the note is a last CSV row and an HTML header line.
/// The first CSV line and the HTML header name the view, the time, the row count and the filter.
/// </summary>
public sealed class DpContentReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IDpContentReportWriter
{
    public const string LinkMessage = "The DP content reports folder is a junction or symbolic link. The report was not written.";
    public const string NoteLabel = "Note";

    /// <summary>The most rows one report holds.</summary>
    public const int MaxRows = 500_000;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(AppDataPaths.Root, "DpContent", "Reports");

    public string Folder => folder ?? DefaultFolder();

    public const string NoFilter = "No filter: every row of the view.";

    private static string Title(DpContentReport report) => "DP content: " + report.View;

    private static string FilterText(DpContentReport report) => string.IsNullOrWhiteSpace(report.Filter) ? NoFilter : "Filter: " + report.Filter;

    private string Generated() => _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>The first line of a CSV file: one cell with the view, the time, the row count and the filter; the column names follow on the second line.</summary>
    private string CsvHeader(DpContentReport report) =>
        $"{Title(report)}. Generated {Generated()}. Rows: {report.Rows.Count.ToString(CultureInfo.InvariantCulture)}. {FilterText(report)}";

    public string WriteCsv(DpContentReport report)
    {
        Check(report);
        var columns = report.Columns.Count;
        var text = new StringBuilder();
        var header = new string[columns];
        Array.Fill(header, "");
        header[0] = CsvHeader(report);
        text.Append(string.Join(",", header.Select(ReportFile.Cell))).Append("\r\n");
        text.Append(string.Join(",", report.Columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in report.Rows) text.Append(string.Join(",", Cells(row, columns).Select(ReportFile.Cell))).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(report.Note))
        {
            var note = new string[columns];
            Array.Fill(note, "");
            note[0] = NoteLabel + ": " + report.Note;
            text.Append(string.Join(",", note.Select(ReportFile.Cell))).Append("\r\n");
        }
        return Write(report, "csv", text.ToString());
    }

    public string WriteHtml(DpContentReport report)
    {
        Check(report);
        var columns = report.Columns.Count;
        var title = Title(report);
        var generated = Generated();
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>").Append(Encode(title)).Append("</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top;min-width:6em;overflow-wrap:anywhere}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>").Append(Encode(title)).Append("</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(". Rows: ")
            .Append(report.Rows.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n")
            .Append("<p class=\"filter\">").Append(Encode(FilterText(report))).Append("</p>\r\n");
        if (!string.IsNullOrWhiteSpace(report.Note)) html.Append("<p class=\"note\">").Append(Encode(report.Note)).Append("</p>\r\n");
        html.Append("<table><thead><tr>");
        foreach (var column in report.Columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
        html.Append("</tr></thead><tbody>\r\n");
        foreach (var row in report.Rows)
        {
            html.Append("<tr>");
            foreach (var value in Cells(row, columns)) html.Append("<td>").Append(Encode(value)).Append("</td>");
            html.Append("</tr>\r\n");
        }
        html.Append("</tbody></table>\r\n</body></html>\r\n");
        return Write(report, "html", html.ToString());
    }

    private static void Check(DpContentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Columns.Count == 0) throw new DpContentServiceException("The report has no columns. Nothing was written.");
        if (report.Rows.Count > MaxRows)
            throw new DpContentServiceException($"The report has more than {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} rows. Filter the view and export it in parts.");
        if (report.Truncated && string.IsNullOrWhiteSpace(report.Note))
            throw new DpContentServiceException("The list was cut when it was loaded, and the report does not say so. Nothing was written.");
    }

    /// <summary>The cells of a row padded or cut to the column count.</summary>
    private static IEnumerable<string> Cells(IReadOnlyList<string> row, int columns)
    {
        for (var i = 0; i < columns; i++) yield return i < row.Count ? row[i] ?? "" : "";
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Prefix(string view)
    {
        var name = new string((view ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return "dp-content-" + (name.Length is > 0 and <= 40 ? name : "view");
    }

    private string Write(DpContentReport report, string extension, string content)
    {
        var target = Folder;
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null)))
            throw new DpContentServiceException(LinkMessage);
        try
        {
            return ReportFile.WriteNew(target, Prefix(report.View), extension, content, _clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DpContentServiceException("The report could not be written. Check that the reports folder can be written.");
        }
    }
}
