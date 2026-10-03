using System.Security.Cryptography;
using System.Text;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One copy of a fixed-name vendor download for every download root that shares a
/// cache root (<see cref="Workbench.WorkbenchPaths.GetSharedDownloadCacheRoot"/>). The
/// first request downloads; a later request is conditional on the cached file's time,
/// so an unchanged release costs one round trip. The cached file and every copy keep
/// the server's Last-Modified time, which is what the condition compares.
/// </summary>
public static class SharedDownloadCache
{
    /// <summary>
    /// The cache file name: 16 hex digits of the URL's SHA-256, then the file name.
    /// Two vendors can serve one file name, and one vendor can serve different
    /// content under one name per channel.
    /// </summary>
    public static string Key(string url, string fileName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return $"{Convert.ToHexString(digest, 0, 8).ToLowerInvariant()}-{fileName}";
    }

    /// <summary>curl arguments for the conditional refresh of a cached file into a side file.</summary>
    public static string RefreshArguments(string url, string cachedPath, string refreshPath, IReadOnlyList<string>? headers = null) =>
        $"-L --fail --silent --show-error{CurlHeaders.Arguments(headers)} -R -z \"{cachedPath}\" -o \"{refreshPath}\" \"{url}\"";

    /// <summary>
    /// Fills or refreshes the cache entry for <paramref name="url"/>, then copies it to
    /// <paramref name="outFile"/> with the cached file's time. Returns the cache path.
    /// A failed refresh keeps the cached file; a failed first download throws.
    /// </summary>
    public static async Task<string> FetchAsync(
        DownloadService downloads, string url, string outFile, string cacheRoot,
        IReadOnlyList<string>? headers = null, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheRoot);
        var cached = Path.Combine(cacheRoot, Key(url, Path.GetFileName(outFile)));

        if (!File.Exists(cached) || new FileInfo(cached).Length == 0)
        {
            onProgress?.Invoke($"Downloading                  : {url}");
            await downloads.DownloadAsync(url, cached, onProgress: onProgress, ct: ct, headers: headers, extraArguments: " -R");
        }
        else
        {
            onProgress?.Invoke("Cached download present; refreshing only if newer.");
            var refresh = cached + ".refresh";
            TryDelete(refresh);
            var (succeeded, error) = await RunCurlAsync(RefreshArguments(url, cached, refresh, headers), url, ct);
            var outcome = FileVersionScraper.ApplyRefresh(cached, refresh, succeeded);
            onProgress?.Invoke(outcome switch
            {
                FileVersionScraper.CacheRefresh.Replaced => "Cached download replaced by a newer release.",
                FileVersionScraper.CacheRefresh.Current => "Cached download is current.",
                _ => $"Cached download kept; refresh failed: {error}"
            });
        }

        CopyWithTimestamp(cached, outFile);
        return cached;
    }

    /// <summary>
    /// Copies a cached file and gives the copy the cached file's last-write time: a later
    /// If-Modified-Since condition reads the copy's time, not the time of the copy.
    /// </summary>
    public static void CopyWithTimestamp(string cachedPath, string outFile)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(cachedPath, outFile, overwrite: true);
        File.SetLastWriteTimeUtc(outFile, File.GetLastWriteTimeUtc(cachedPath));
    }

    private static async Task<(bool Succeeded, string Error)> RunCurlAsync(string arguments, string url, CancellationToken ct)
    {
        using var process = CurlProcess.Start(arguments, url);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            BoundedProcessRunner.Terminate(process);
            throw;
        }
        string error;
        try { error = (await stderr).Trim(); } catch (Exception) { error = ""; }
        return (process.ExitCode == 0, error);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
