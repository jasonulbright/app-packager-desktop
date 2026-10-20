using System.Globalization;
using System.Net;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Writes audit records to a new CSV or HTML file. The columns are the union of the properties
/// of all records in the order they are first seen, so records of rings and of both tools keep
/// their fields. Every CSV cell is quoted, and a cell that a spreadsheet would read as a formula
/// starts with an apostrophe; every HTML value and column name is encoded.
/// </summary>
public sealed class DeploymentReportWriter(string? folder = null, Func<DateTimeOffset>? clock = null) : IDeploymentReportWriter
{
    public const string LinkMessage =
        "The Deployments reports folder is a junction or symbolic link. The report was not written.";

    private const string Prefix = "deployment-history";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public static string DefaultFolder() => Path.Combine(AppDataPaths.Root, "Deployments", "Reports");

    public string WriteCsv(IReadOnlyList<DeploymentAuditRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var (columns, values) = Table(rows);
        var text = new StringBuilder();
        text.Append(string.Join(",", columns.Select(ReportFile.Cell))).Append("\r\n");
        foreach (var row in values) text.Append(string.Join(",", row.Select(ReportFile.Cell))).Append("\r\n");
        return Write("csv", text.ToString());
    }

    public string WriteHtml(IReadOnlyList<DeploymentAuditRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var (columns, values) = Table(rows);
        var generated = _clock().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\r\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>Deployment history</title><style>")
            .Append("body{font-family:\"Segoe UI\",Arial,sans-serif;margin:20px}")
            .Append("table{border-collapse:collapse}")
            .Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #ccc;vertical-align:top;min-width:6em;overflow-wrap:anywhere}")
            .Append("th{border-bottom:2px solid #333}")
            .Append("</style></head><body>\r\n<h1>Deployment history</h1>\r\n")
            .Append("<p>Generated ").Append(Encode(generated)).Append(". Records: ")
            .Append(values.Count.ToString(CultureInfo.InvariantCulture)).Append(".</p>\r\n<table><thead><tr>");
        foreach (var column in columns) html.Append("<th>").Append(Encode(column)).Append("</th>");
        html.Append("</tr></thead><tbody>\r\n");
        foreach (var row in values)
        {
            html.Append("<tr>");
            foreach (var value in row) html.Append("<td>").Append(Encode(value)).Append("</td>");
            html.Append("</tr>\r\n");
        }
        html.Append("</tbody></table>\r\n</body></html>\r\n");
        return Write("html", html.ToString());
    }

    private static (List<string> Columns, List<List<string>> Rows) Table(IReadOnlyList<DeploymentAuditRecord> records)
    {
        var columns = new List<string>();
        var maps = new List<Dictionary<string, string>>();
        foreach (var record in records)
        {
            var map = Fields(record);
            foreach (var name in map.Keys)
                if (!columns.Contains(name)) columns.Add(name);
            maps.Add(map);
        }
        return (columns, maps.Select(m => columns.Select(c => m.TryGetValue(c, out var v) ? v : "").ToList()).ToList());
    }

    /// <summary>The properties a record carries with a value, in the order of the record type and then its other fields.</summary>
    private static Dictionary<string, string> Fields(DeploymentAuditRecord r)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) map[name] = value;
        }
        static string? Time(DateTime? value) => value is { } v && v != default ? v.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null;
        Add("Timestamp", Time(r.Timestamp));
        Add("User", r.User);
        Add("DeploymentType", r.DeploymentType);
        Add("ApplicationName", r.ApplicationName);
        Add("ApplicationVersion", r.ApplicationVersion);
        Add("CollectionName", r.CollectionName);
        Add("CollectionID", r.CollectionID);
        Add("MemberCount", r.MemberCount?.ToString(CultureInfo.InvariantCulture));
        Add("DeployPurpose", r.DeployPurpose);
        Add("DeployAction", r.DeployAction);
        Add("DeadlineDateTime", Time(r.DeadlineDateTime));
        Add("DeploymentID", r.DeploymentID);
        Add("Result", r.Result);
        Add("Event", r.Event);
        Add("ObjectId", r.ObjectId);
        Add("AvailableDateTime", Time(r.AvailableDateTime));
        Add("Message", r.Message);
        Add("Source", r.Source);
        Add("DeploymentUniqueID", r.DeploymentUniqueID);
        Add("ProgramName", r.ProgramName);
        Add("TimeBasedOn", r.TimeBasedOn);
        Add("DistributionPointGroups", r.DistributionPointGroups);
        Add("Undoes", r.Undoes);
        foreach (var (name, value) in r.Extra)
            if (!map.ContainsKey(name)) map[name] = value;
        return map;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private string Write(string extension, string content)
    {
        var target = folder ?? DefaultFolder();
        if (DataFolderLinks.HasLink(target, DataFolderLinks.ContainerOf(target, folder is null)))
            throw new DeploymentServiceException(LinkMessage);
        try
        {
            return ReportFile.WriteNew(target, Prefix, extension, content, _clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeploymentServiceException("The report could not be written. Check that the reports folder can be written.");
        }
    }
}
