using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Downloads files using curl.exe (built into Windows 10+/Server 2019+).
///
/// The transfer writes to a temporary sibling and is moved into place only once it has
/// completed. Staging skips the download when the installer is already present, so a
/// truncated file left at the destination is indistinguishable from a cached payload and
/// would be packaged as one.
/// </summary>
public sealed class DownloadService
{
    public async Task DownloadAsync(
        string url, string outFile,
        int retryCount = 1, int retryDelaySec = 5,
        Action<string>? onProgress = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? headers = null,
        IReadOnlyList<string>? extraArguments = null,
        long? maxBytes = null)
    {
        CurlProcess.ValidateUrl(url);
        var headerArguments = CurlHeaders.Arguments(headers).Concat(extraArguments ?? [])
            .Concat(maxBytes is { } limit ? ["--max-filesize", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []).ToList();
        var dir = Path.GetDirectoryName(outFile);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var maxAttempts = retryCount + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Downloading: {Path.GetFileName(outFile)} (attempt {attempt}/{maxAttempts})");

            // Unique per attempt, so a leftover from an earlier run is never appended to
            // and a concurrent download of the same payload cannot collide.
            var partFile = $"{outFile}.{Guid.NewGuid():N}.part";
            var completed = false;

            try
            {
                using var process = CurlProcess.Start(
                    ["-L", "--fail", "--silent", "--show-error", .. headerArguments, "-o", partFile], url);

                // Read without the caller's token: an abandoned read leaves the child
                // blocked on a full pipe, and terminating it is what ends the stream.
                var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
                try
                {
                    await WaitForExitAsync(process, partFile, maxBytes, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or DownloadTooLargeException)
                {
                    // A cancelled or oversized download has to stop writing. Letting the handle fall out
                    // of scope leaves curl running and the file still growing.
                    BoundedProcessRunner.Terminate(process);
                    // Terminating ends the stream; observing the reader keeps it from
                    // being left as an unobserved task.
                    _ = await SafeRead(stderrTask).ConfigureAwait(false);
                    throw;
                }

                var stderr = await SafeRead(stderrTask).ConfigureAwait(false);

                if (maxBytes is { } cap && (process.ExitCode == CurlMaxFileSizeExceeded
                        || (File.Exists(partFile) && new FileInfo(partFile).Length > cap)))
                    throw new DownloadTooLargeException(cap);

                if (process.ExitCode == 0 && File.Exists(partFile) && new FileInfo(partFile).Length > 0)
                {
                    Promote(partFile, outFile);
                    completed = true;
                    onProgress?.Invoke($"Downloaded: {Path.GetFileName(outFile)}");
                    return;
                }

                if (attempt < maxAttempts)
                {
                    onProgress?.Invoke($"Download failed (attempt {attempt}): {stderr.Trim()}. Retrying in {retryDelaySec}s...");
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySec), ct);
                }
            }
            finally
            {
                // Only this attempt's own incomplete file is removed. A destination that
                // was already there stays exactly as it was.
                if (!completed) DeleteQuietly(partFile);
            }
        }

        throw new InvalidOperationException($"Download failed after {maxAttempts} attempts: {url}");
    }

    /// <summary>The curl exit code for a transfer above --max-filesize.</summary>
    private const int CurlMaxFileSizeExceeded = 63;

    private static readonly TimeSpan SizePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Waits for curl to exit. With a size limit, the part file is also checked while curl runs, because a
    /// server that sends no length is not stopped by --max-filesize.
    /// </summary>
    internal static async Task WaitForExitAsync(System.Diagnostics.Process process, string partFile, long? maxBytes, CancellationToken ct)
    {
        if (maxBytes is not { } limit)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return;
        }
        var exited = process.WaitForExitAsync(ct);
        while (await Task.WhenAny(exited, Task.Delay(SizePollInterval, ct)).ConfigureAwait(false) != exited)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(partFile) && new FileInfo(partFile).Length > limit) throw new DownloadTooLargeException(limit);
        }
        await exited.ConfigureAwait(false);
    }

    /// <summary>Moves a finished transfer onto the destination, replacing what was there.</summary>
    private static void Promote(string partFile, string outFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(partFile, outFile, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void DeleteQuietly(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try { return await readTask.ConfigureAwait(false); }
        catch (Exception) { return ""; }
    }
}

/// <summary>A download that passed the size limit it was given.</summary>
public sealed class DownloadTooLargeException(long maxBytes)
    : InvalidOperationException($"The download is larger than the limit of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
