using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes result rows of the Pivots and Scripts page to a new CSV or JSON file in
/// &lt;data root&gt;\PivotsScripts\Reports. Every CSV cell is quoted, and a cell that a spreadsheet would
/// read as a formula (a leading = + - @, tab, carriage return or line feed) starts with an apostrophe.
/// JSON holds the values as strings exactly as the site returned them. A report of a partial run is
/// refused unless its note says so. The first CSV line and the JSON header name the view, the time,
/// the row count and the filter.
/// </summary>
public sealed class PivotsReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IPivotsReportWriter
{
    public const string LinkMessage = "The Pivots and Scripts reports folder is a junction or symbolic link. The report was not written.";
    public const string NoFilter = "No filter: every row of the view.";

    /// <summary>The most rows one report holds.</summary>
    public const int MaxRows = 500_000;

    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(PivotsFolders.Root(), "Reports");

    public string Folder => folder ?? DefaultFolder();

    private static string FilterText(PivotsReport report) => string.IsNullOrWhiteSpace(report.Filter) ? NoFilter : "Filter: " + report.Filter;

    public string WriteCsv(PivotsReport report)
    {
        Check(report);
        var now = _clock();
        var columns = report.Columns.Count;
        var text = new StringBuilder();
        var header = new string[columns];
        Array.Fill(header, "");
        header[0] = $"Pivots and Scripts: {report.View}. Generated {Stamp(now)}. Rows: {report.Rows.Count.ToString(CultureInfo.InvariantCulture)}. {FilterText(report)}";
        text.Append(string.Join(",", header.Select(ReportFile.Cell))).Append("\r\n");
        text.Append(string.Join(",", report.Columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in report.Rows) text.Append(string.Join(",", Cells(row, columns).Select(ReportFile.Cell))).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(report.Note))
        {
            var note = new string[columns];
            Array.Fill(note, "");
            note[0] = "Note: " + report.Note;
            text.Append(string.Join(",", note.Select(ReportFile.Cell))).Append("\r\n");
        }
        return Write(report, "csv", text.ToString(), now);
    }

    public string WriteJson(PivotsReport report)
    {
        Check(report);
        var now = _clock();
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, JsonOptions))
        {
            w.WriteStartObject();
            w.WriteString("View", report.View);
            w.WriteString("Generated", Stamp(now));
            w.WriteNumber("RowCount", report.Rows.Count);
            w.WriteString("Filter", FilterText(report));
            w.WriteBoolean("Partial", report.Partial);
            if (!string.IsNullOrWhiteSpace(report.Note)) w.WriteString("Note", report.Note);
            w.WriteStartArray("Rows");
            var names = UniqueNames(report.Columns);
            foreach (var row in report.Rows)
            {
                w.WriteStartObject();
                var i = 0;
                foreach (var value in Cells(row, names.Count)) w.WriteString(names[i++], value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Write(report, "json", Encoding.UTF8.GetString(buffer.ToArray()), now);
    }

    /// <summary>Column names made unique, as a JSON object needs: a repeated or empty name gets a number.</summary>
    internal static IReadOnlyList<string> UniqueNames(IReadOnlyList<string> columns)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            var name = string.IsNullOrWhiteSpace(column) ? "Column" : column;
            var candidate = name;
            for (var n = 2; !seen.Add(candidate); n++) candidate = name + " " + n.ToString(CultureInfo.InvariantCulture);
            names.Add(candidate);
        }
        return names;
    }

    private static string Stamp(DateTimeOffset now) => now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    private static void Check(PivotsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Columns.Count == 0) throw new PivotsServiceException("The report has no columns. Nothing was written.");
        if (report.Rows.Count > MaxRows)
            throw new PivotsServiceException($"The report has more than {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} rows. Filter the view and export it in parts.");
        if (report.Partial && string.IsNullOrWhiteSpace(report.Note))
            throw new PivotsServiceException("The results are partial, and the report does not say so. Nothing was written.");
    }

    private static IEnumerable<string> Cells(IReadOnlyList<string> row, int columns)
    {
        for (var i = 0; i < columns; i++) yield return i < row.Count ? row[i] ?? "" : "";
    }

    private static string Prefix(string view)
    {
        var name = new string((view ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return "pivots-" + (name.Length is > 0 and <= 40 ? name : "view");
    }

    private string Write(PivotsReport report, string extension, string content, DateTimeOffset now)
    {
        var target = Folder;
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null)))
            throw new PivotsServiceException(LinkMessage);
        try
        {
            return ReportFile.WriteNew(target, Prefix(report.View), extension, content, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PivotsServiceException("The report could not be written. Check that the reports folder can be written.");
        }
    }
}
