using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes one view of the Site Hygiene page to a new CSV or HTML file in &lt;data root&gt;\SiteHygiene\Reports.
/// Every CSV cell is quoted, and a cell that a spreadsheet would read as a formula starts with an apostrophe;
/// every HTML value is encoded. The header names the view, the scan time, the row count, the filter, the
/// truncation note and each check that did not run: at the top of the HTML file, and in a text file beside
/// the CSV file. A report of a truncated scan is refused unless its note says so.
/// </summary>
public sealed class HygieneReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IHygieneReportWriter
{
    public const string LinkMessage = "The Site Hygiene reports folder is a junction or symbolic link. The report was not written.";
    public const string NoFilter = "No filter: every row of the view.";
    public const int MaxRows = 500_000;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(HygieneFolders.Root(), "Reports");

    public string Folder => folder ?? DefaultFolder();

    private static string Title(HygieneReport report) => "Site Hygiene: " + report.View;

    private string Generated() => _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static IEnumerable<string> HeaderLines(HygieneReport report, string generated)
    {
        yield return $"{Title(report)}. Generated {generated}. Rows: {report.Rows.Count.ToString(CultureInfo.InvariantCulture)}.";
        if (report.ScannedUtc is { } scanned) yield return report.TimeLabel + " " + scanned.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".";
        yield return string.IsNullOrWhiteSpace(report.Filter) ? NoFilter : "Filter: " + report.Filter;
        if (!string.IsNullOrWhiteSpace(report.Note)) yield return "Note: " + report.Note;
        if (report.ChecksNotRun.Count > 0)
        {
            yield return $"Checks that did not run: {report.ChecksNotRun.Count.ToString(CultureInfo.InvariantCulture)}. They report no finding in this file.";
            foreach (var c in report.ChecksNotRun) yield return "Did not run: " + c;
        }
    }

    /// <summary>The text file beside a CSV report that holds its header lines.</summary>
    public static string AboutPath(string csvPath) => Path.ChangeExtension(csvPath, ".about.txt");

    /// <summary>
    /// The CSV holds the column names and the rows only, so Import-Csv and a spreadsheet read it as a table.
    /// The header lines go to <see cref="AboutPath"/>; when that file cannot be written, the CSV is deleted
    /// and the write fails.
    /// </summary>
    public string WriteCsv(HygieneReport report)
    {
        Check(report);
        var columns = report.Columns.Count;
        var generated = Generated();
        var text = new StringBuilder();
        string Row(IEnumerable<string> cells) => string.Join(",", cells.Select(ReportFile.Cell));
        text.Append(Row(report.Columns)).Append("\r\n");
        foreach (var row in report.Rows) text.Append(Row(Cells(row, columns))).Append("\r\n");
        var path = Write(report, "csv", text.ToString());
        var about = new StringBuilder();
        foreach (var line in HeaderLines(report, generated)) about.Append(line).Append("\r\n");
        about.Append("Data file: ").Append(Path.GetFileName(path)).Append("\r\n");
        try
        {
            using var stream = new FileStream(AboutPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(about.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new HygieneServiceException("The report could not be written. Check that the reports folder can be written.");
        }
        return path;
    }

    public string WriteHtml(HygieneReport report)
    {
        Check(report);
        var columns = report.Columns.Count;
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>").Append(Encode(Title(report))).Append("</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top;min-width:6em;overflow-wrap:anywhere;white-space:pre-wrap}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>").Append(Encode(Title(report))).Append("</h1>\r\n");
        foreach (var line in HeaderLines(report, Generated())) html.Append("<p>").Append(Encode(line)).Append("</p>\r\n");
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

    private static void Check(HygieneReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Columns.Count == 0) throw new HygieneServiceException("The report has no columns. Nothing was written.");
        if (report.Rows.Count > MaxRows) throw new HygieneServiceException($"The report has more than {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} rows. Filter the view and export it in parts.");
        if (report.Truncated && string.IsNullOrWhiteSpace(report.Note)) throw new HygieneServiceException("The scan was cut, and the report does not say so. Nothing was written.");
    }

    private static IEnumerable<string> Cells(IReadOnlyList<string> row, int columns)
    {
        for (var i = 0; i < columns; i++) yield return i < row.Count ? row[i] ?? "" : "";
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Prefix(string view)
    {
        var name = new string((view ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return "site-hygiene-" + (name.Length is > 0 and <= 40 ? name : "view");
    }

    private string Write(HygieneReport report, string extension, string content)
    {
        var target = Folder;
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null))) throw new HygieneServiceException(LinkMessage);
        try
        {
            return ReportFile.WriteNew(target, Prefix(report.View), extension, content, _clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HygieneServiceException("The report could not be written. Check that the reports folder can be written.");
        }
    }
}
