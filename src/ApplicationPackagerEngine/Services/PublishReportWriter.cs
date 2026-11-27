using System.Globalization;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>The report of a Publish All run, as Markdown and JSON: what the run did for each application and system.</summary>
public static class PublishReportWriter
{
    public static string Folder(string logFolder) => Path.Combine(logFolder, "PublishReports");

    /// <summary>Writes both files and returns the path of the Markdown file.</summary>
    public static string Write(string folder, DateTime ended, string siteLine, string headline, bool cancelled,
        IReadOnlyList<(string Application, PublishRowResult Result)> rows)
    {
        Directory.CreateDirectory(folder);
        var stem = Path.Combine(folder, "publish-" + ended.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        var text = new StringBuilder();
        text.AppendLine("# Publish All report").AppendLine();
        text.AppendLine("- Ended: " + ended.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (siteLine.Length > 0) text.AppendLine("- " + siteLine);
        text.AppendLine("- Result: " + headline);
        if (cancelled) text.AppendLine("- The run was cancelled.");
        text.AppendLine().AppendLine("| Application | Version | System | Result | Message |").AppendLine("|---|---|---|---|---|");
        static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();
        foreach (var (application, result) in rows)
        {
            if (result.Destinations.Count == 0)
                text.AppendLine($"| {Cell(application)} | {Cell(result.Version)} | | Not run | |");
            foreach (var d in result.Destinations)
                text.AppendLine($"| {Cell(application)} | {Cell(result.Version)} | {d.Destination} | {Cell(d.Text)} | {Cell(d.Message)} |");
        }
        File.WriteAllText(stem + ".md", text.ToString(), new UTF8Encoding(false));
        var json = JsonSerializer.Serialize(new
        {
            Ended = ended,
            Site = siteLine,
            Result = headline,
            Cancelled = cancelled,
            Applications = rows.Select(r => new
            {
                r.Application,
                r.Result.Version,
                Systems = r.Result.Destinations.Select(d => new { d.Destination, Result = d.Text, d.Message, d.ObjectId })
            })
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(stem + ".json", json, new UTF8Encoding(false));
        return stem + ".md";
    }
}
