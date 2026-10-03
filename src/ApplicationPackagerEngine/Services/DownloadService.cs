using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Downloads files using curl.exe (fast, built into Windows 10+/Server 2019+).
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
        IReadOnlyList<string>? headers = null)
    {
        var headerArguments = CurlHeaders.Arguments(headers);
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
                    $"-L --fail --silent --show-error{headerArguments} -o \"{partFile}\" \"{url}\"", url);

                // Read without the caller's token: an abandoned read leaves the child
                // blocked on a full pipe, and terminating it is what ends the stream.
                var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // A cancelled download has to stop writing. Letting the handle fall out
                    // of scope leaves curl running and the file still growing.
                    BoundedProcessRunner.Terminate(process);
                    // Terminating ends the stream; observing the reader keeps it from
                    // being left as an unobserved task.
                    _ = await SafeRead(stderrTask).ConfigureAwait(false);
                    throw;
                }

                var stderr = await SafeRead(stderrTask).ConfigureAwait(false);

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
