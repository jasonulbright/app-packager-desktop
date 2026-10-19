using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes drift and compliance rows to a new CSV or HTML file under the Collections reports
/// folder. Every CSV cell is quoted, and a cell that a spreadsheet would read as a formula
/// starts with an apostrophe; every HTML value is encoded.
/// </summary>
public sealed class ComplianceReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IComplianceReportWriter
{
    public const string LinkMessage =
        "The Collections reports folder is a junction or symbolic link. The report was not written.";

    private static readonly string[] DriftColumns = ["Baseline", "Baseline ID", "Catalog entry", "Deployed version", "Catalog version", "State"];

    private static readonly string[] ComplianceColumns =
        ["Baseline", "Collection", "Compliant", "Non-compliant", "Error", "Unknown", "Last evaluated (UTC)", "Stale"];

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(CollectionsFolders.Root(), "Reports");

    public string WriteCsv(IReadOnlyList<DriftRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Write("compliance-drift", "csv", Csv(DriftColumns, rows.Select(DriftValues)));
    }

    public string WriteCsv(IReadOnlyList<ComplianceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Write("compliance-status", "csv", Csv(ComplianceColumns, rows.Select(ComplianceValues)));
    }

    public string WriteHtml(IReadOnlyList<DriftRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Write("compliance-drift", "html", Html("Baseline drift", "Baselines", DriftColumns, rows.Select(DriftValues).ToList()));
    }

    public string WriteHtml(IReadOnlyList<ComplianceRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Write("compliance-status", "html", Html("Baseline compliance", "Deployments", ComplianceColumns, rows.Select(ComplianceValues).ToList()));
    }

    private static IEnumerable<string> DriftValues(DriftRow row) =>
    [
        row.BaselineName ?? "", row.BaselineId ?? "", row.CatalogId ?? "", row.DeployedVersion ?? "", row.CatalogVersion ?? "", row.State ?? ""
    ];

    private static IEnumerable<string> ComplianceValues(ComplianceRow row) =>
    [
        row.BaselineName ?? "",
        row.CollectionName ?? "",
        row.Compliant.ToString(CultureInfo.InvariantCulture),
        row.NonCompliant.ToString(CultureInfo.InvariantCulture),
        row.Error.ToString(CultureInfo.InvariantCulture),
        row.Unknown.ToString(CultureInfo.InvariantCulture),
        row.LastEvaluated is { } time ? time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
        row.Stale ? "Yes" : "No"
    ];

    private static string Csv(string[] columns, IEnumerable<IEnumerable<string>> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in rows) text.Append(string.Join(",", row.Select(ReportFile.Cell))).Append("\r\n");
        return text.ToString();
    }

    private string Html(string title, string noun, string[] columns, IReadOnlyList<IEnumerable<string>> rows)
    {
        var generated = _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>").Append(Encode(title)).Append("</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse;width:100%}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>").Append(Encode(title)).Append("</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(". ").Append(noun).Append(": ")
            .Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n<table><thead><tr>");
        foreach (var column in columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
        html.Append("</tr></thead><tbody>\r\n");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var value in row) html.Append("<td>").Append(Encode(value)).Append("</td>");
            html.Append("</tr>\r\n");
        }
        html.Append("</tbody></table></body></html>\r\n");
        return html.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private string Write(string prefix, string extension, string content)
    {
        var target = folder ?? DefaultFolder();
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null)))
            throw new ComplianceServiceException(LinkMessage);
        return ReportFile.WriteNew(target, prefix, extension, content, _clock());
    }
}
