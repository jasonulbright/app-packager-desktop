using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes result rows of the Pivots and Scripts page to a new CSV or JSON file in
/// &lt;data root&gt;\PivotsScripts\Reports. Every CSV cell is quoted, and a cell that a spreadsheet would
/// read as a formula (a leading = + - @, tab, carriage return or line feed) starts with an apostrophe.
/// JSON holds the values as strings exactly as the site returned them. A report of a partial run is
/// refused unless its note says so. The JSON header and the text file beside a CSV name the view, the
/// time, the row count and the filter.
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

    /// <summary>The text file beside a CSV report that names its view, time, row count, filter and note.</summary>
    public static string AboutPath(string csvPath) => Path.ChangeExtension(csvPath, ".about.txt");

    /// <summary>
    /// The CSV holds the column names and the rows only, so Import-Csv and a spreadsheet read it as a
    /// table. The view, the time, the row count, the filter and the note go to <see cref="AboutPath"/>;
    /// when that file cannot be written, the CSV is deleted and the write fails.
    /// </summary>
    public string WriteCsv(PivotsReport report)
    {
        Check(report);
        var now = _clock();
        var columns = report.Columns.Count;
        var text = new StringBuilder();
        text.Append(string.Join(",", report.Columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in report.Rows) text.Append(string.Join(",", Cells(row, columns).Select(ReportFile.Cell))).Append("\r\n");
        var path = Write(report, "csv", text.ToString(), now);
        var about = new StringBuilder()
            .Append($"Pivots and Scripts: {report.View}.").Append("\r\n")
            .Append($"Generated {Stamp(now)}.").Append("\r\n")
            .Append($"Rows: {report.Rows.Count.ToString(CultureInfo.InvariantCulture)}.").Append("\r\n")
            .Append(FilterText(report)).Append("\r\n")
            .Append($"Data file: {Path.GetFileName(path)}").Append("\r\n");
        if (!string.IsNullOrWhiteSpace(report.Note)) about.Append("Note: ").Append(report.Note).Append("\r\n");
        try
        {
            using var stream = new FileStream(AboutPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(about.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new PivotsServiceException("The report could not be written. Check that the reports folder can be written.");
        }
        return path;
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
