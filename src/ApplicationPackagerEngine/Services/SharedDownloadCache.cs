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

    /// <summary>curl options for the conditional refresh of a cached file into a side file; the URL is added by <see cref="CurlProcess"/>.</summary>
    public static IReadOnlyList<string> RefreshArguments(string cachedPath, string refreshPath, IReadOnlyList<string>? headers = null, long? maxBytes = null) =>
        ["-L", "--fail", "--silent", "--show-error", .. CurlHeaders.Arguments(headers),
            .. maxBytes is { } limit ? ["--max-filesize", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)] : (string[])[],
            "-R", "-z", cachedPath, "-o", refreshPath];

    /// <summary>
    /// Fills or refreshes the cache entry for <paramref name="url"/>, then copies it to
    /// <paramref name="outFile"/> with the cached file's time. Returns the cache path.
    /// A failed first download throws. A failed refresh keeps the cached file when
    /// <paramref name="keepCachedOnFailedRefresh"/> is true and throws otherwise: a version
    /// read from the vendor's page names a release the cached file may not be.
    /// One exclusive lock per cache entry serializes concurrent stages of one URL.
    /// A <paramref name="maxBytes"/> limit bounds the first download, a refresh and the file copied out; a file above it throws <see cref="DownloadTooLargeException"/> and a cached copy is not returned in its place.
    /// </summary>
    public static async Task<string> FetchAsync(
        DownloadService downloads, string url, string outFile, string cacheRoot,
        IReadOnlyList<string>? headers = null, Action<string>? onProgress = null, CancellationToken ct = default,
        bool keepCachedOnFailedRefresh = true, long? maxBytes = null)
    {
        CurlProcess.ValidateUrl(url);
        Directory.CreateDirectory(cacheRoot);
        var cached = Path.Combine(cacheRoot, Key(url, Path.GetFileName(outFile)));

        await using var entryLock = await AcquireEntryLockAsync(cached, ct);

        if (!File.Exists(cached) || new FileInfo(cached).Length == 0)
        {
            onProgress?.Invoke($"Downloading                  : {url}");
            await downloads.DownloadAsync(url, cached, onProgress: onProgress, ct: ct, headers: headers, extraArguments: ["-R"], maxBytes: maxBytes);
        }
        else
        {
            onProgress?.Invoke("Cached download present; refreshing only if newer.");
            var refresh = $"{cached}.{Guid.NewGuid():N}.refresh";
            (bool succeeded, string error, int exitCode) result;
            try { result = await RunCurlAsync(RefreshArguments(cached, refresh, headers, maxBytes), url, ct, refresh, maxBytes); }
            catch (DownloadTooLargeException)
            {
                FileVersionScraper.ApplyRefresh(cached, refresh, requestSucceeded: false);
                throw;
            }
            var (succeeded, error, exitCode) = result;
            if (maxBytes is { } cap && (exitCode == CurlMaxFileSizeExceeded || (File.Exists(refresh) && new FileInfo(refresh).Length > cap)))
            {
                FileVersionScraper.ApplyRefresh(cached, refresh, requestSucceeded: false);
                throw new DownloadTooLargeException(cap);
            }
            var outcome = FileVersionScraper.ApplyRefresh(cached, refresh, succeeded);
            if (outcome == FileVersionScraper.CacheRefresh.Kept && !keepCachedOnFailedRefresh)
                throw new InvalidOperationException(
                    $"Refresh of the cached download failed for {url}: {error}. The cached copy is dated "
                    + $"{File.GetLastWriteTimeUtc(cached):yyyy-MM-dd HH:mm} UTC ({CachedAge(cached)}) and may not be the release "
                    + "the version source names, so it is not staged.");
            onProgress?.Invoke(outcome switch
            {
                FileVersionScraper.CacheRefresh.Replaced => "Cached download replaced by a newer release.",
                FileVersionScraper.CacheRefresh.Current => "Cached download is current.",
                _ => $"Cached download kept; refresh failed: {error}"
            });
        }

        if (maxBytes is { } limit && new FileInfo(cached).Length > limit) throw new DownloadTooLargeException(limit);
        CopyWithTimestamp(cached, outFile);
        return cached;
    }

    /// <summary>
    /// Replaces the cache entry for <paramref name="url"/> with a fresh full download, then copies it
    /// to <paramref name="outFile"/> and runs <paramref name="verify"/> on that copy. The purge, the
    /// download and the check run under one entry lock, so no other stage reads or refills the entry
    /// in between. The replacement is published to the shared cache only after the copy passes the check.
    /// </summary>
    public static async Task<string> RefetchAsync(
        DownloadService downloads, string url, string outFile, string cacheRoot, Action<string> verify,
        IReadOnlyList<string>? headers = null, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        CurlProcess.ValidateUrl(url);
        Directory.CreateDirectory(cacheRoot);
        var cached = Path.Combine(cacheRoot, Key(url, Path.GetFileName(outFile)));

        await using var entryLock = await AcquireEntryLockAsync(cached, ct);

        try { File.Delete(cached); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The shared download cache entry {cached} could not be removed: {ex.Message}", ex);
        }
        var fresh = $"{cached}.{Guid.NewGuid():N}.refetch";
        try
        {
            onProgress?.Invoke($"Downloading                  : {url}");
            await downloads.DownloadAsync(url, fresh, onProgress: onProgress, ct: ct, headers: headers, extraArguments: ["-R"]);
            CopyWithTimestamp(fresh, outFile);
            verify(outFile);

            File.Move(fresh, cached);
        }
        catch
        {
            foreach (var bad in new[] { fresh, outFile })
                try { File.Delete(bad); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return cached;
    }

    private static string CachedAge(string cached)
    {
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cached);
        return age.TotalDays >= 1 ? $"{(int)age.TotalDays} days old" : $"{Math.Max(0, (int)age.TotalHours)} hours old";
    }

    /// <summary>
    /// The longest wait for a cache entry another stage holds while that stage shows no
    /// download progress. A holder whose download keeps writing is waited for as long as it
    /// writes.
    /// </summary>
    public static readonly TimeSpan EntryLockStallWait = TimeSpan.FromMinutes(10);

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Opens <c>&lt;entry&gt;.lock</c> with no sharing and waits while another stage holds it.
    /// The lock file is deleted when the last holder closes it. A delete-pending lock file
    /// answers access denied for a moment, so that answer is retried a bounded number of times.
    /// Only a sharing or lock violation means another holder; any other I/O error, such as a
    /// cache share that went offline, fails at once. The holder counts as making progress
    /// while the entry or one of its download or refresh side files changes size or time;
    /// a holder with no progress for <paramref name="stallWait"/> fails the stage. Each
    /// failure names the entry.
    /// </summary>
    internal static async Task<FileStream> AcquireEntryLockAsync(string cached, CancellationToken ct, TimeSpan? stallWait = null,
        TimeProvider? clock = null)
    {
        var lockPath = cached + ".lock";
        var limit = stallWait ?? EntryLockStallWait;
        clock ??= TimeProvider.System;
        var progressSince = clock.GetTimestamp();
        var progress = ProgressMark(cached);
        var denied = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation)
            {
                var mark = ProgressMark(cached);
                if (mark != progress)
                {
                    progress = mark;
                    progressSince = clock.GetTimestamp();
                }
                else if (clock.GetElapsedTime(progressSince) >= limit)
                    throw new InvalidOperationException(
                        $"The shared download cache entry {cached} stayed locked by another stage with no download progress for {Describe(limit)}; this stage stopped waiting.", ex);
                await Task.Delay(100, ct);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"The shared download cache entry {cached} could not be locked: {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException) when (++denied < 100)
            {
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// The sizes and write times of the entry and of its side files (the download's
    /// <c>.part</c> and the refresh's <c>.refresh</c>), the lock file excluded; any change
    /// between two reads is progress. Each file is queried by path: a directory listing can
    /// report a stale size for a file another process still has open for writing.
    /// </summary>
    private static string ProgressMark(string cached)
    {
        try
        {
            var folder = Path.GetDirectoryName(cached)!;
            var name = Path.GetFileName(cached);
            if (!Directory.Exists(folder)) return "";
            var parts = Directory.EnumerateFiles(folder, name + "*")
                .Where(f => !f.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");
            return string.Join(";", parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{span.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minutes"
            : $"{span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} seconds";

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

    private const int CurlMaxFileSizeExceeded = 63;

    private static async Task<(bool Succeeded, string Error, int ExitCode)> RunCurlAsync(
        IReadOnlyList<string> arguments, string url, CancellationToken ct, string? outputFile = null, long? maxBytes = null)
    {
        using var process = CurlProcess.Start(arguments, url);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await DownloadService.WaitForExitAsync(process, outputFile ?? "", maxBytes, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or DownloadTooLargeException)
        {
            BoundedProcessRunner.Terminate(process);
            throw;
        }
        string error;
        try { error = (await stderr).Trim(); } catch (Exception) { error = ""; }
        return (process.ExitCode == 0, error, process.ExitCode);
    }
}
