namespace ApplicationPackagerEngine.Services;

/// <summary>
/// HTTP download with retry logic. Replaces curl.exe dependency from PS1 scripts.
/// </summary>
public sealed class DownloadService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", "ApplicationPackager/2.1" }
        }
    };

    /// <summary>
    /// Downloads a file with retry logic.
    /// </summary>
    public async Task DownloadAsync(
        string url, string outFile,
        int retryCount = 1, int retryDelaySec = 5,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Exception? lastEx = null;
        var maxAttempts = retryCount + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                onProgress?.Invoke($"Downloading: {Path.GetFileName(outFile)} (attempt {attempt}/{maxAttempts})");

                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream, ct);

                onProgress?.Invoke($"Downloaded: {Path.GetFileName(outFile)}");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
                if (attempt < maxAttempts)
                {
                    onProgress?.Invoke($"Download failed (attempt {attempt}): {ex.Message}. Retrying in {retryDelaySec}s...");
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySec), ct);
                }
            }
        }

        throw new InvalidOperationException($"Download failed after {maxAttempts} attempts: {url}", lastEx);
    }
}
