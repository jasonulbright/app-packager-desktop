using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.OneClick;

/// <summary>The run a report describes. Scope holds a text only for the destinations the included rows use.</summary>
public sealed record OneClickReportRun
{
    public required DateTime Started { get; init; }
    public required DateTime Ended { get; init; }
    public string Operator { get; init; } = "";
    public string Computer { get; init; } = "";
    /// <summary>Report only, Stage, or Stage and Publish.</summary>
    public string Action { get; init; } = "";
    public bool Force { get; init; }
    public bool Canceled { get; init; }
    public IReadOnlyDictionary<string, string>? Scope { get; init; }
}

/// <summary>One application in a report: the plan row extended by the run.</summary>
public sealed record OneClickReportRow
{
    public string Key { get; init; } = "";
    public string Application { get; init; } = "";
    public string Version { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyDictionary<string, string> Results { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();
    /// <summary>The full message behind a short result, such as the text of a WSUS refusal.</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

/// <summary>One earlier run as the history list shows it.</summary>
public sealed record OneClickReportSummary(DateTime Started, string Action, int Applications, int Published, int Failed, string MarkdownPath, string JsonPath);

/// <summary>
/// One Markdown and one JSON file per run under &lt;Logs&gt;\OneClick:
/// the header table, Scope, Applications, Details (full refusal and failure
/// text) and Rollback lines for the destinations in the scope only.
/// </summary>
public static class OneClickReportWriter
{
    public const string FolderName = "OneClick";

    public static readonly IReadOnlyDictionary<string, string> RollbackText = new Dictionary<string, string>
    {
        [OneClickDestinations.ConfigMgr] = "retire the application named in the ConfigMgr column.",
        [OneClickDestinations.Wsus] = "expire the package ID in the WSUS column from WSUS Updates.",
        [OneClickDestinations.Intune] = "unassign the app ID in the Intune column in the Intune admin center."
    };

    public static string Folder(string logFolder) => Path.Combine(logFolder, FolderName);

    private static string Escape(string? text) => (text ?? "").Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");

    private static string Get(IReadOnlyDictionary<string, string> map, string key) => map.TryGetValue(key, out var v) ? v ?? "" : "";

    /// <returns>The Markdown and JSON paths.</returns>
    public static (string MarkdownPath, string JsonPath) Write(OneClickReportRun run, IReadOnlyList<OneClickReportRow> rows, string folder)
    {
        Directory.CreateDirectory(folder);
        var stamp = run.Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var mdPath = Path.Combine(folder, $"one-click-{stamp}.md");
        var jsonPath = Path.Combine(folder, $"one-click-{stamp}.json");

        File.WriteAllText(mdPath, Markdown(run, rows), new UTF8Encoding(false));
        File.WriteAllText(jsonPath, Json(run, rows).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return (mdPath, jsonPath);
    }

    public static string Markdown(OneClickReportRun run, IReadOnlyList<OneClickReportRow> rows)
    {
        int Count(string outcome) => rows.Count(r => r.Outcome == outcome);
        var lines = new List<string>
        {
            "# One Click run " + run.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            "",
            "| Field | Value |",
            "|---|---|",
            "| Started | " + run.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " |",
            "| Ended | " + run.Ended.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " |",
            "| Operator | " + Escape(run.Operator) + " |",
            "| Computer | " + Escape(run.Computer) + " |",
            "| Action | " + run.Action + (run.Force ? " (force)" : "") + " |"
        };
        if (run.Canceled) lines.Add("| Canceled | yes |");
        lines.Add("| Applications | " + rows.Count + " |");
        lines.Add("| Published | " + Count(OneClickOutcomes.Published) + " |");
        lines.Add("| Not supported | " + Count(OneClickOutcomes.NotSupported) + " |");
        lines.Add("| Skipped | " + Count(OneClickOutcomes.Skipped) + " |");
        lines.Add("| Failed | " + Count(OneClickOutcomes.Failed) + " |");
        lines.Add("");

        if (run.Scope is not null)
        {
            lines.Add("## Scope");
            lines.Add("");
            foreach (var d in OneClickDestinations.All)
            {
                var text = Get(run.Scope, d);
                if (text.Length > 0) lines.Add($"- {d}: {text}");
            }
            lines.Add("");
        }

        lines.Add("## Applications");
        lines.Add("");
        lines.Add("| Application | Version | Outcome | ConfigMgr | WSUS | Intune | Reason |");
        lines.Add("|---|---|---|---|---|---|---|");
        foreach (var row in rows)
        {
            lines.Add("| " + string.Join(" | ",
                Escape(row.Application), Escape(row.Version), Escape(row.Outcome),
                Escape(Get(row.Results, OneClickDestinations.ConfigMgr)), Escape(Get(row.Results, OneClickDestinations.Wsus)),
                Escape(Get(row.Results, OneClickDestinations.Intune)), Escape(row.Reason)) + " |");
        }
        lines.Add("");

        var details = rows.SelectMany(row => OneClickDestinations.All
                .Where(d => Get(row.Details, d).Length > 0)
                .Select(d => $"- {Escape(row.Application)}, {d}: {Escape(Get(row.Details, d))}"))
            .ToList();
        if (details.Count > 0)
        {
            lines.Add("## Details");
            lines.Add("");
            lines.AddRange(details);
            lines.Add("");
        }

        lines.Add("## Rollback");
        lines.Add("");
        foreach (var d in OneClickDestinations.All)
            if (run.Scope is null || Get(run.Scope, d).Length > 0)
                lines.Add($"- {d}: {RollbackText[d]}");
        lines.Add("");
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static JsonObject Json(OneClickReportRun run, IReadOnlyList<OneClickReportRow> rows)
    {
        static string Utc(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        JsonObject? scope = null;
        if (run.Scope is not null)
        {
            scope = new JsonObject();
            foreach (var d in OneClickDestinations.All)
                if (Get(run.Scope, d).Length > 0) scope[d] = Get(run.Scope, d);
        }
        var applications = new JsonArray();
        foreach (var row in rows)
        {
            var app = new JsonObject
            {
                ["Packager"] = row.Key,
                ["Application"] = row.Application,
                ["Version"] = row.Version,
                ["Outcome"] = row.Outcome,
                ["Reason"] = row.Reason
            };
            foreach (var d in OneClickDestinations.All)
                app[d] = new JsonObject { ["Result"] = Get(row.Results, d), ["Id"] = Get(row.Ids, d), ["Detail"] = Get(row.Details, d) };
            applications.Add(app);
        }
        return new JsonObject
        {
            ["Started"] = Utc(run.Started),
            ["Ended"] = Utc(run.Ended),
            ["Operator"] = run.Operator,
            ["Computer"] = run.Computer,
            ["Action"] = run.Action,
            ["Force"] = run.Force,
            ["Canceled"] = run.Canceled,
            ["Scope"] = scope,
            ["Applications"] = applications
        };
    }

    /// <summary>Earlier run reports, newest first. An unreadable file is left out.</summary>
    public static IReadOnlyList<OneClickReportSummary> List(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var items = new List<OneClickReportSummary>();
        foreach (var file in Directory.GetFiles(folder, "one-click-*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            try
            {
                var data = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8))!.AsObject();
                var apps = (data["Applications"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var started = DateTime.Parse((string?)data["Started"] ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime();
                items.Add(new OneClickReportSummary(started, (string?)data["Action"] ?? "", apps.Count,
                    apps.Count(a => (string?)a["Outcome"] == OneClickOutcomes.Published),
                    apps.Count(a => (string?)a["Outcome"] == OneClickOutcomes.Failed),
                    Path.ChangeExtension(file, ".md"), file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
            {
            }
        }
        return items;
    }
}
