using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scrapes a web page via HTTP GET and extracts a version string
/// using a regex pattern. Covers ~80% of apps.
///
/// Uses powershell.exe Invoke-WebRequest via temp script file to avoid
/// argument escaping issues with URLs containing &amp; characters.
/// </summary>
public sealed class HttpRegexScraper : IVersionScraper
{
    public string Type => "HttpRegex";

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        var (version, _) = await FetchVersionAndUrlAsync(config, ct);
        return version;
    }

    /// <summary>
    /// Fetches content and extracts both version (Pattern) and optional download URL (DownloadUrlQuery)
    /// in a single request.
    /// </summary>
    public async Task<(string? Version, string? DownloadUrl)> GetVersionAndDownloadUrlAsync(
        VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return (null, null);

        return await FetchVersionAndUrlAsync(config, ct);
    }

    private async Task<(string? Version, string? DownloadUrl)> FetchVersionAndUrlAsync(
        VersionScraperConfig config, CancellationToken ct)
    {
        var isRedirectMode = config.Pattern.StartsWith("REDIR:");
        var pattern = isRedirectMode ? config.Pattern["REDIR:".Length..] : config.Pattern;

        var ua = config.NoUserAgent ? null : UserAgent;
        var content = isRedirectMode
            ? await FetchRedirectUrlAsync(config.Url, ct, ua)
            : await FetchViaIwrAsync(config.Url, ct, ua);
        if (content is null) return (null, null);

        string? version = null;
        var match = Regex.Match(content, pattern, RegexOptions.Multiline);
        if (match.Success && match.Groups.Count > 1)
            version = match.Groups[1].Value;

        string? downloadUrl = null;
        if (!string.IsNullOrEmpty(config.DownloadUrlQuery))
        {
            var urlMatch = Regex.Match(content, config.DownloadUrlQuery, RegexOptions.Multiline);
            if (urlMatch.Success && urlMatch.Groups.Count > 1)
                downloadUrl = urlMatch.Groups[1].Value;
        }

        return (version, downloadUrl);
    }

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// Fetches the final redirect URL without downloading content.
    /// Uses curl -w to extract the effective URL after all redirects.
    /// </summary>
    private static async Task<string?> FetchRedirectUrlAsync(string url, CancellationToken ct, string? userAgent = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"-L --silent --max-time 60 --max-redirs 10 --max-filesize 1 -w \"%{{url_effective}}\"{(userAgent is not null ? $" -A \"{userAgent}\"" : "")} \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(65), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        // Exit 0 = success, exit 63 = --max-filesize exceeded (expected — we only need the URL)
        return (process.ExitCode == 0 || process.ExitCode == 63) && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private static async Task<string?> FetchViaIwrAsync(string url, CancellationToken ct, string? userAgent = null)
    {
        // Use curl.exe: handles 308 redirects, avoids PS5.1 IWR quirks, better WAF compatibility
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"-L --silent --show-error --max-time 25{(userAgent is not null ? $" -A \"{userAgent}\"" : "")} \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(30), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private static async Task<string?> RunPowerShellScriptAsync(string scriptPath, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(30), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }
}
