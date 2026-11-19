using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>What a download host says about a file address.</summary>
public enum UrlState
{
    /// <summary>Status 200 or 206.</summary>
    Exists,
    /// <summary>Status 404 or 410: the host does not have the file at this address.</summary>
    Missing,
    /// <summary>Any other answer, above all 403, and no answer: the host says nothing about the file.</summary>
    Unknown
}

/// <summary>
/// For a vendor that serves one file under two addresses (Tableau: esdalt and tssoftware). The second address is tried only when the host
/// reports the first one as missing (404). A 403 is no answer about the file and never switches the address.
/// </summary>
public static class UrlAvailability
{
    public static UrlState Classify(string? status) => (status ?? "").Trim() switch
    {
        "200" or "206" => UrlState.Exists,
        "404" or "410" => UrlState.Missing,
        _ => UrlState.Unknown
    };

    /// <summary>A range request of the first byte, with the request headers of the definition.</summary>
    public static async Task<UrlState> ProbeAsync(string url, IReadOnlyList<string>? headers, CancellationToken ct = default)
    {
        try
        {
            using var process = CurlProcess.Start(
                ["-L", "--silent", "--max-time", "30", "--range", "0-0", .. CurlHeaders.Arguments(headers), "-o", "NUL", "-w", "%{http_code}"], url);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            if (!await RunChildProcesses.WaitOrKillAsync(process, TimeSpan.FromSeconds(40), ct)) return UrlState.Unknown;
            return Classify(await stdoutTask);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UrlState.Unknown;
        }
    }

    /// <summary>
    /// The first address that the host does not report as missing. An address that exists, or whose answer cannot be told, is the one to
    /// download from; the download then decides. Throws when every address is missing.
    /// </summary>
    public static async Task<string> SelectAsync(IReadOnlyList<string> urls, Func<string, Task<UrlState>> probe, Action<string>? onProgress = null)
    {
        var missing = new List<string>();
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            var state = await probe(url);
            if (state == UrlState.Missing)
            {
                onProgress?.Invoke($"Not on the host (404): {url}");
                missing.Add(url);
                continue;
            }
            if (state == UrlState.Unknown) onProgress?.Invoke($"The host cannot tell whether the file is there (no 404): {url}; downloading from it");
            return url;
        }
        throw new InvalidOperationException($"The installer is not on the host at any of its addresses (404): {string.Join(", ", missing)}");
    }
}
