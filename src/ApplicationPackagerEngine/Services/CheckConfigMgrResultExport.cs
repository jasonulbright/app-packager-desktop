using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ApplicationPackagerEngine.Services;

/// <summary>One application of a Check ConfigMgr run, in the shape of the export file.</summary>
public sealed record CheckConfigMgrResultRow(
    string Application, string Vendor, string CMName, string InstalledSoftwareName, string Architecture,
    string ConfigMgrVersion, string LatestVersion, string Status, string Source, DateTime CheckedAtUtc);

/// <summary>
/// The result file of Check ConfigMgr. App Packager Desktop and the PowerShell App Packager write the
/// same format, so the Update rollout view of Deployment Helper reads either. Two files, overwritten per run:
/// CheckConfigMgrResult.csv and CheckConfigMgrResult.json in the app data folder.
/// </summary>
public static class CheckConfigMgrResultExport
{
    public const string UpdateAvailable = "UpdateAvailable";
    public const string UpToDate = "UpToDate";
    public const string NotFound = "NotFound";
    public const string SourceName = "APD";
    public const string FileBaseName = "CheckConfigMgrResult";

    public static readonly string[] Columns =
    [
        "Application", "Vendor", "CMName", "InstalledSoftwareName", "Architecture",
        "ConfigMgrVersion", "LatestVersion", "Status", "Source", "CheckedAtUtc"
    ];

    /// <summary>The folder of the fixed result file: the per-user app data folder.</summary>
    public static string DefaultFolder => AppDataPaths.LegacyRoot;

    public static string DefaultCsvPath => Path.Combine(DefaultFolder, FileBaseName + ".csv");

    /// <summary>
    /// Maps the status text of a grid row to a file status; null for a row without a usable answer
    /// (error, cancelled, version retrieved with no latest version).
    /// </summary>
    public static string? MapStatus(string? gridStatus)
    {
        if (string.IsNullOrEmpty(gridStatus)) return null;
        if (gridStatus.StartsWith("Update available", StringComparison.Ordinal)) return UpdateAvailable;
        return gridStatus switch
        {
            "Up to date" or "Current newer" => UpToDate,
            "Not found in ConfigMgr" => NotFound,
            _ => null
        };
    }

    public static string Format(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static IReadOnlyList<CheckConfigMgrResultRow> Filter(IEnumerable<CheckConfigMgrResultRow> rows, bool onlyUpdatesAvailable) =>
        rows.Where(r => !onlyUpdatesAvailable || r.Status == UpdateAvailable).ToList();

    /// <summary>UTF-8 with a byte order mark, CRLF line ends, every field quoted, a header row.</summary>
    public static string ToCsv(IEnumerable<CheckConfigMgrResultRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Columns.Select(Quote))).Append("\r\n");
        foreach (var r in rows)
        {
            string[] fields =
            [
                r.Application, r.Vendor, r.CMName, r.InstalledSoftwareName, r.Architecture,
                r.ConfigMgrVersion, r.LatestVersion, r.Status, r.Source, Format(r.CheckedAtUtc)
            ];
            sb.Append(string.Join(',', fields.Select(Quote))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>An array of objects with the names of the CSV columns.</summary>
    public static string ToJson(IEnumerable<CheckConfigMgrResultRow> rows) =>
        JsonSerializer.Serialize(rows.Select(r => new Dictionary<string, string>
        {
            ["Application"] = r.Application, ["Vendor"] = r.Vendor, ["CMName"] = r.CMName,
            ["InstalledSoftwareName"] = r.InstalledSoftwareName, ["Architecture"] = r.Architecture,
            ["ConfigMgrVersion"] = r.ConfigMgrVersion, ["LatestVersion"] = r.LatestVersion,
            ["Status"] = r.Status, ["Source"] = r.Source, ["CheckedAtUtc"] = Format(r.CheckedAtUtc)
        }), new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes one file; the format follows the extension (.json, else CSV). Returns the path.</summary>
    public static string Write(string path, IEnumerable<CheckConfigMgrResultRow> rows)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var text = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ToJson(rows) : ToCsv(rows);
        // Write beside the target and move over it, so a reader never sees a half-written file.
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(true));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Writes the fixed CSV and JSON twin in the app data folder, overwriting the previous run.</summary>
    public static string WriteDefault(IEnumerable<CheckConfigMgrResultRow> rows, string? folder = null)
    {
        var list = rows.ToList();
        var dir = folder ?? DefaultFolder;
        Write(Path.Combine(dir, FileBaseName + ".json"), list);
        return Write(Path.Combine(dir, FileBaseName + ".csv"), list);
    }

    private static string Quote(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
}
