using System.Diagnostics;
using System.IO.Compression;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Extracts version by downloading the installer and reading file properties.
/// Used for apps with no version API — the installer itself is the only version source.
///
/// Pattern field specifies the version source:
///   "FileVersion"       — FileVersionInfo.FileVersion (EXE)
///   "ProductVersion"    — FileVersionInfo.ProductVersion (EXE/bootstrapper)
///   "MsiProductVersion" — MSI ProductVersion property via COM
///
/// Downloaded files are cached in %TEMP%\SRLPackr-FileVersionCache\ so that
/// Stage can reuse the already-downloaded installer instead of re-downloading.
///
/// With Hops set, the hops run first (ChainedHttpRegexScraper semantics) and
/// their "url" capture names the file to download, for a vendor whose page
/// links the current build but publishes no version string. Staging reads the
/// same URL through ResolveDownloadUrlAsync; DownloadUrlQuery must be
/// non-empty ("CHAINED") for staging to ask.
///
/// With ProbeFloor set, Url carries a {build} placeholder and the HeadProbe build
/// walk names the file instead, for a vendor whose only published fact is which
/// numbered setup the download host still serves.
/// </summary>
public sealed class FileVersionScraper : IVersionScraper
{
    public string Type => "FileVersion";

    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "SRLPackr-FileVersionCache");

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        var url = await ResolveDownloadUrlAsync(config, ct);
        if (url is null) return null;

        Directory.CreateDirectory(CacheDir);

        var fileName = ResolveFileName(url);
        var cachedPath = Path.Combine(CacheDir, fileName);

        // A fixed URL serves "latest" with no version in the path and a
        // hop-resolved build name repeats across point releases, so the cache
        // is revalidated on every check; only a newer release costs a download.
        await DownloadAsync(url, cachedPath, ct);
        if (!File.Exists(cachedPath)) return null;

        var version = config.Pattern.ToUpperInvariant() switch
        {
            "FILEVERSION" => ReadExeVersion(cachedPath, useFileVersion: true),
            "PRODUCTVERSION" => ReadExeVersion(cachedPath, useFileVersion: false),
            "MSIPRODUCTVERSION" => ReadMsiVersion(cachedPath),
            "MSIXMANIFEST" => ReadMsixManifestVersion(cachedPath),
            _ => ReadExeVersion(cachedPath, useFileVersion: false)
        };

        return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
    }

    /// <summary>
    /// The URL whose file carries the version: the configured URL, or with
    /// Hops the "url" capture of the crawl. Null when a hop fails to match.
    /// </summary>
    public static async Task<string?> ResolveDownloadUrlAsync(VersionScraperConfig config, CancellationToken ct = default,
        Func<string, CancellationToken, Task<bool>>? isPublished = null)
    {
        if (!string.IsNullOrWhiteSpace(config.ProbeFloor))
        {
            var build = await HeadProbeScraper.WalkBuildNumberAsync(config.Url, config.ProbeFloor, config.MaxVersionHops, isPublished, ct);
            return build is null ? null : HeadProbeScraper.BuildNumberUrl(config.Url, build.Value);
        }
        if (config.Hops is not { Count: > 0 })
            return string.IsNullOrWhiteSpace(config.Url) ? null : config.Url;

        var (_, url) = await ChainedHttpRegexScraper.CrawlAsync(config, ct);
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    /// <summary>True when the download URL is discovered (hops or a build probe) rather than fixed.</summary>
    public static bool DiscoversUrl(VersionScraperConfig config) =>
        config.Hops is { Count: > 0 } || !string.IsNullOrWhiteSpace(config.ProbeFloor);

    /// <summary>
    /// Returns the cached installer path if the file exists, or null.
    /// Called by StagingService to reuse the downloaded file.
    /// </summary>
    public static string? GetCachedPath(string url)
    {
        var fileName = ResolveFileName(url);
        var path = Path.Combine(CacheDir, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string ReadExeVersion(string exePath, bool useFileVersion)
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        var version = useFileVersion ? info.FileVersion : info.ProductVersion;

        if (string.IsNullOrWhiteSpace(version))
            version = useFileVersion ? info.ProductVersion : info.FileVersion;

        // An installer can ship a version resource whose string table is empty
        // while its fixed block carries the four product parts.
        if (string.IsNullOrWhiteSpace(version))
            version = FixedBlockVersion(
                info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart);

        if (string.IsNullOrWhiteSpace(version) && info.FileMajorPart > 0)
            version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";

        return version ?? "";
    }

    /// <summary>
    /// The four fixed-block version parts as a dotted string. An unset block
    /// reads as 0.0.0.0 and yields an empty string rather than a false version.
    /// </summary>
    public static string FixedBlockVersion(int major, int minor, int build, int revision)
    {
        var version = $"{major}.{minor}.{build}.{revision}";
        return version == "0.0.0.0" ? "" : version;
    }

    private static string ReadMsixManifestVersion(string msixPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(msixPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return "";

            using var reader = new StreamReader(entry.Open());
            var xml = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
            var ns = xml.Root?.GetDefaultNamespace();
            var identity = ns is not null
                ? xml.Root?.Element(ns + "Identity")
                : xml.Root?.Element("Identity");
            return identity?.Attribute("Version")?.Value ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadMsiVersion(string msiPath)
    {
        try
        {
            var service = new Services.MsiInspectionService();
            var props = service.ReadProperties(msiPath);
            return props.ProductVersion;
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            var pathFile = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(pathFile) && pathFile.Contains('.'))
                return pathFile;
        }
        catch { }

        var hash = Math.Abs(url.GetHashCode()).ToString("x8");
        return $"download-{hash}.tmp";
    }

    /// <summary>Outcome of refreshing a cached download against the server's copy.</summary>
    public enum CacheRefresh
    {
        /// <summary>The server returned a body; the cache now holds it.</summary>
        Replaced,
        /// <summary>The server reported no change; the cached file is current.</summary>
        Current,
        /// <summary>The request failed; the cached file is kept.</summary>
        Kept
    }

    /// <summary>
    /// curl arguments for one download. With <paramref name="ifModifiedSincePath"/>
    /// the request is conditional on that file's timestamp, so an unchanged
    /// release answers 304 and writes no output file. The remote timestamp is
    /// kept on the downloaded file so the next condition stays exact.
    /// </summary>
    public static IReadOnlyList<string> BuildDownloadArguments(string outFile, string? ifModifiedSincePath)
    {
        string[] condition = string.IsNullOrEmpty(ifModifiedSincePath) ? [] : ["-z", ifModifiedSincePath];
        return ["-L", "--fail", "--silent", "--show-error", "--max-time", "120", "-R", "-o", outFile, .. condition,
            "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"];
    }

    /// <summary>
    /// Settles a conditional refresh: a body replaces the cache, no side file
    /// means the cached release is current, and a failed request keeps the
    /// cache rather than leaving the scrape without a file to read.
    /// </summary>
    public static CacheRefresh ApplyRefresh(string cachePath, string refreshPath, bool requestSucceeded)
    {
        if (!requestSucceeded)
        {
            TryDelete(refreshPath);
            return CacheRefresh.Kept;
        }

        if (File.Exists(refreshPath) && new FileInfo(refreshPath).Length > 0)
        {
            File.Move(refreshPath, cachePath, overwrite: true);
            return CacheRefresh.Replaced;
        }

        TryDelete(refreshPath);
        return CacheRefresh.Current;
    }

    private static async Task DownloadAsync(string url, string outFile, CancellationToken ct)
    {
        // An empty cache file would otherwise be revalidated forever, since a
        // 304 keeps whatever is on disk.
        if (!File.Exists(outFile) || new FileInfo(outFile).Length == 0)
        {
            if (!await RunCurlAsync(BuildDownloadArguments(outFile, null), url, ct))
                TryDelete(outFile);
            return;
        }

        // The refresh lands in a side file: a failed request cannot truncate the
        // cache, and a 304 leaves no side file at all.
        var refreshPath = outFile + ".refresh";
        TryDelete(refreshPath);
        var succeeded = await RunCurlAsync(BuildDownloadArguments(refreshPath, outFile), url, ct);
        ApplyRefresh(outFile, refreshPath, succeeded);
    }

    private static async Task<bool> RunCurlAsync(IReadOnlyList<string> arguments, string url, CancellationToken ct)
    {
        using var process = CurlProcess.Start(arguments, url);

        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(130), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return false;
        }

        return process.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
