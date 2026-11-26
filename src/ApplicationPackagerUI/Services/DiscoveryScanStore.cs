using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>One result of Discover unmanaged software: the site that was read, the number of products that matched and the choice, when one was made.</summary>
public sealed record DiscoveryRunReport(string SiteCode, string ProviderMachine, int Matched, DiscoveryAction? Choice);

/// <summary>
/// What the setup checklist keeps for one site: the time, the number of matched products and the choice, or the fact that the user skipped the
/// scan. It holds a count of products only: no client, no client count and no inventory row.
/// </summary>
public sealed record DiscoveryScanRecord(DateTime RecordedUtc, int? Matched, string? Choice, bool Skipped)
{
    /// <summary>"1 product matched" or "N products matched"; empty for a skipped scan.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string MatchedText => Matched is { } n ? $"{n} {(n == 1 ? "product" : "products")} matched" : "";
}

/// <summary>
/// The scan records, one for each site key (<see cref="SiteIdentity.Key"/>), in the state folder of the active site. With no active site the
/// file is in the data root; a read of a site also looks there, so a scan that ran before the site layout started is not lost.
/// </summary>
public sealed class DiscoveryScanStore
{
    public const string FileName = "discovery-scan.json";

    private sealed class StoreFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, DiscoveryScanRecord> Sites { get; set; } = [];
    }

    private static string RootPath() => Path.Combine(AppDataPaths.Root, FileName);

    /// <summary>The file of the active site.</summary>
    public static string ActivePath => SiteContext.ActiveSiteFolder is { } folder
        ? Path.Combine(folder, SiteContext.Areas.State, FileName)
        : RootPath();

    /// <summary>The record of a site, or null when the site has none. A damaged or locked file reads as no record.</summary>
    public DiscoveryScanRecord? Find(string? siteCode, string? providerMachine)
    {
        var key = SiteIdentity.Key(siteCode, providerMachine);
        var path = ActivePath;
        if (Read(path).Sites.TryGetValue(key, out var record)) return record;
        return path != RootPath() && Read(RootPath()).Sites.TryGetValue(key, out var earlier) ? earlier : null;
    }

    /// <summary>Stores the result of a scan. It replaces an earlier result and an earlier skip.</summary>
    public void Record(DiscoveryRunReport report) =>
        Write(report.SiteCode, report.ProviderMachine, new DiscoveryScanRecord(DateTime.UtcNow, report.Matched, report.Choice?.ToString(), Skipped: false));

    /// <summary>Stores that the user skipped the scan of a site. A site that has a record keeps it.</summary>
    public void Skip(string? siteCode, string? providerMachine)
    {
        if (Find(siteCode, providerMachine) is not null) return;
        Write(siteCode, providerMachine, new DiscoveryScanRecord(DateTime.UtcNow, null, null, Skipped: true));
    }

    private static void Write(string? siteCode, string? providerMachine, DiscoveryScanRecord record)
    {
        var path = ActivePath;
        var file = Read(path);
        file.Sites[SiteIdentity.Key(siteCode, providerMachine)] = record;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WorkbenchJson.WriteText(path, JsonSerializer.Serialize(file, WorkbenchJson.FileOptions));
    }

    private static StoreFile Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new StoreFile();
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path), WorkbenchJson.FileOptions) ?? new StoreFile();
            file.Sites = (file.Sites ?? []).Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StoreFile();
        }
    }
}
