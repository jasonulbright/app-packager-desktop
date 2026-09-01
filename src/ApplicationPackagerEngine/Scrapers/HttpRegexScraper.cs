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
public class HttpRegexScraper : IVersionScraper
{
    public virtual string Type => "HttpRegex";

    public virtual async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
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
    public virtual async Task<(string? Version, string? DownloadUrl)> GetVersionAndDownloadUrlAsync(
        VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return (null, null);

        return await FetchVersionAndUrlAsync(config, ct);
    }

    private async Task<(string? Version, string? DownloadUrl)> FetchVersionAndUrlAsync(
        VersionScraperConfig config, CancellationToken ct)
    {
        // LAST_MODIFIED mode: extract version from HTTP Last-Modified header as date
        if (config.Pattern.Equals("LAST_MODIFIED", StringComparison.OrdinalIgnoreCase))
        {
            var dateVersion = await FetchLastModifiedVersionAsync(config.Url, ct);
            return (dateVersion, null);
        }

        var isRedirectMode = config.Pattern.StartsWith("REDIR:");
        var pattern = isRedirectMode ? config.Pattern["REDIR:".Length..] : config.Pattern;

        var ua = config.NoUserAgent ? null : UserAgent;
        var content = isRedirectMode
            ? await FetchRedirectUrlAsync(config.Url, ct, ua)
            : await FetchViaIwrAsync(config.Url, ct, ua);
        if (content is null) return (null, null);

        var version = ExtractVersion(content, pattern, config.SelectHighestVersion);

        string? downloadUrl = null;
        if (!string.IsNullOrEmpty(config.DownloadUrlQuery))
        {
            // {version} lets the URL pattern target the same release the version came from,
            // which matters once max-by-version selection can pick a later match.
            var urlPattern = version is not null
                ? config.DownloadUrlQuery.Replace("{version}", Regex.Escape(version))
                : config.DownloadUrlQuery;
            var urlMatch = Regex.Match(content, urlPattern, RegexOptions.Multiline);
            if (urlMatch.Success && urlMatch.Groups.Count > 1)
                downloadUrl = urlMatch.Groups[1].Value;
        }

        return (version, downloadUrl);
    }

    /// <summary>
    /// Extracts the version capture from content. With selectHighest, every match is
    /// collected and the highest by version comparison wins; otherwise the first match does.
    /// </summary>
    public static string? ExtractVersion(string content, string pattern, bool selectHighest)
    {
        var matches = Regex.Matches(content, pattern, RegexOptions.Multiline);
        if (matches.Count == 0) return null;

        if (!selectHighest)
        {
            var first = matches[0];
            return first.Groups.Count > 1 ? first.Groups[1].Value : null;
        }

        var candidates = matches
            .Where(m => m.Groups.Count > 1)
            .Select(m => m.Groups[1].Value)
            .ToList();

        return candidates.Count == 0 ? null : LooseVersion.SelectHighest(candidates);
    }

    protected const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

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

    protected static async Task<string?> FetchViaIwrAsync(string url, CancellationToken ct, string? userAgent = null)
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

    /// <summary>
    /// Performs a HEAD request and converts the Last-Modified header to a date-based version (YYYY.M.D).
    /// </summary>
    private static async Task<string?> FetchLastModifiedVersionAsync(string url, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"--head -L --silent --max-time 15 \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(20), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

        // Parse Last-Modified header: "Last-Modified: Thu, 26 Mar 2026 19:56:26 GMT"
        var match = Regex.Match(stdout, @"Last-Modified:\s*(.+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        if (DateTimeOffset.TryParse(match.Groups[1].Value.Trim(), out var dt))
            return $"{dt.Year}.{dt.Month}.{dt.Day}";

        return null;
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
