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

    public string WriteCsv(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance)
    {
        ArgumentNullException.ThrowIfNull(drift);
        ArgumentNullException.ThrowIfNull(compliance);
        var text = new StringBuilder();
        text.Append(ReportFile.Cell("Baseline drift")).Append("\r\n");
        text.Append(Csv(DriftColumns, drift.Select(DriftValues)));
        text.Append("\r\n").Append(ReportFile.Cell("Baseline compliance")).Append("\r\n");
        text.Append(Csv(ComplianceColumns, compliance.Select(ComplianceValues)));
        return Write("compliance", "csv", text.ToString());
    }

    public string WriteHtml(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance)
    {
        ArgumentNullException.ThrowIfNull(drift);
        ArgumentNullException.ThrowIfNull(compliance);
        return Write("compliance", "html", Html(
        [
            ("Baseline drift", "Baselines", DriftColumns, drift.Select(DriftValues).ToList()),
            ("Baseline compliance", "Deployments", ComplianceColumns, compliance.Select(ComplianceValues).ToList())
        ]));
    }

    private static IEnumerable<string> DriftValues(DriftRow row) =>
    [
        row.BaselineName ?? "", row.BaselineId ?? "", row.CatalogId ?? "", row.DeployedVersion ?? "", row.CatalogVersion ?? "", row.State ?? ""
    ];

    public const string UnknownCount = "unknown";
    public const string NeverEvaluated = "never";

    private static IEnumerable<string> ComplianceValues(ComplianceRow row) =>
    [
        row.BaselineName ?? "",
        row.CollectionName ?? "",
        Count(row, row.Compliant),
        Count(row, row.NonCompliant),
        Count(row, row.Error),
        Count(row, row.Unknown),
        row.LastEvaluated is { } time ? time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : NeverEvaluated,
        row.Stale ? "Yes" : "No"
    ];

    private static string Count(ComplianceRow row, int count) => row.CountsKnown ? count.ToString(CultureInfo.InvariantCulture) : UnknownCount;

    private static string Csv(string[] columns, IEnumerable<IEnumerable<string>> rows)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in rows) text.Append(string.Join(",", row.Select(ReportFile.Cell))).Append("\r\n");
        return text.ToString();
    }

    private string Html(IReadOnlyList<(string Title, string Noun, string[] Columns, IReadOnlyList<IEnumerable<string>> Rows)> sections)
    {
        var generated = _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>Compliance report</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse;width:100%;margin-bottom:24px}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>Compliance report</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(".</p>\r\n");
        foreach (var (title, noun, columns, rows) in sections)
        {
            html.Append("<h2>").Append(Encode(title)).Append("</h2>\r\n<p>").Append(noun).Append(": ")
                .Append(rows.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n<table><thead><tr>");
            foreach (var column in columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
            html.Append("</tr></thead><tbody>\r\n");
            foreach (var row in rows)
            {
                html.Append("<tr>");
                foreach (var value in row) html.Append("<td>").Append(Encode(value)).Append("</td>");
                html.Append("</tr>\r\n");
            }
            html.Append("</tbody></table>\r\n");
        }
        html.Append("</body></html>\r\n");
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
