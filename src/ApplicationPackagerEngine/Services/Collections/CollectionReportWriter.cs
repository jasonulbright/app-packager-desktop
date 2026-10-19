using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes collection rows to a new CSV or HTML file. Every CSV cell is quoted, and a cell that
/// a spreadsheet would read as a formula starts with an apostrophe; every HTML value is encoded.
/// </summary>
public sealed class CollectionReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : ICollectionReportWriter
{
    public const string LinkMessage =
        "The Collections reports folder is a junction or symbolic link. The report was not written.";

    private static readonly string[] Columns =
    [
        "Collection ID", "Name", "Folder", "Limiting collection", "Limiting collection ID", "Members", "Built-in", "Refresh",
        "Direct rules", "Query rules", "Include rules", "Exclude rules", "Comment"
    ];

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(CollectionsFolders.Root(), "Reports");

    public string WriteCsv(IReadOnlyList<CollectionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.Append(string.Join(",", Columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in rows)
            text.Append(string.Join(",", Values(row).Select(ReportFile.Cell))).Append("\r\n");
        return Write("csv", text.ToString());
    }

    public string WriteHtml(IReadOnlyList<CollectionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var generated = _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>Device collections</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse;width:100%}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>Device collections</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(". Collections: ")
            .Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n<table><thead><tr>");
        foreach (var column in Columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
        html.Append("</tr></thead><tbody>\r\n");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var value in Values(row)) html.Append("<td>").Append(Encode(value)).Append("</td>");
            html.Append("</tr>\r\n");
        }
        html.Append("</tbody></table></body></html>\r\n");
        return Write("html", html.ToString());
    }

    private static IEnumerable<string> Values(CollectionRow row) =>
    [
        row.CollectionId ?? "",
        row.Name ?? "",
        row.FolderPath ?? "",
        row.LimitingCollectionName ?? "",
        row.LimitingCollectionId ?? "",
        row.MemberCount.ToString(CultureInfo.InvariantCulture),
        row.IsBuiltIn ? "Yes" : "No",
        row.RefreshType ?? "",
        row.DirectRuleCount.ToString(CultureInfo.InvariantCulture),
        row.QueryRuleCount.ToString(CultureInfo.InvariantCulture),
        row.IncludeRuleCount.ToString(CultureInfo.InvariantCulture),
        row.ExcludeRuleCount.ToString(CultureInfo.InvariantCulture),
        row.Comment ?? ""
    ];

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private string Write(string extension, string content)
    {
        var target = folder ?? DefaultFolder();
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null)))
            throw new CollectionServiceException(LinkMessage);
        return ReportFile.WriteNew(target, "collections", extension, content, _clock());
    }
}
