using System.Diagnostics;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Downloads files using curl.exe (fast, built into Windows 10+/Server 2019+).
/// Falls back to powershell.exe Invoke-WebRequest if curl is unavailable.
/// </summary>
public sealed class DownloadService
{
    public async Task DownloadAsync(
        string url, string outFile,
        int retryCount = 1, int retryDelaySec = 5,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var maxAttempts = retryCount + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            onProgress?.Invoke($"Downloading: {Path.GetFileName(outFile)} (attempt {attempt}/{maxAttempts})");

            using var process = CurlProcess.Start(
                $"-L --fail --silent --show-error -o \"{outFile}\" \"{url}\"", url);

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0)
            {
                onProgress?.Invoke($"Downloaded: {Path.GetFileName(outFile)}");
                return;
            }

            if (attempt < maxAttempts)
            {
                onProgress?.Invoke($"Download failed (attempt {attempt}): {stderr.Trim()}. Retrying in {retryDelaySec}s...");
                await Task.Delay(TimeSpan.FromSeconds(retryDelaySec), ct);
            }
        }

        throw new InvalidOperationException($"Download failed after {maxAttempts} attempts: {url}");
    }
}
