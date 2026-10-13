using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scrapes a web page via HTTP GET with curl.exe and extracts a version string
/// using a regex pattern. Pattern LAST_MODIFIED reads the Last-Modified header as
/// a date version; a REDIR: prefix matches against the final redirect URL instead
/// of the page body.
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
        if (config.Pattern.Equals("LAST_MODIFIED", StringComparison.OrdinalIgnoreCase))
        {
            var dateVersion = await FetchLastModifiedVersionAsync(config.Url, ct);
            return (dateVersion, null);
        }

        var isRedirectMode = config.Pattern.StartsWith("REDIR:");
        var pattern = isRedirectMode ? config.Pattern["REDIR:".Length..] : config.Pattern;

        var ua = config.NoUserAgent ? null : UserAgent;
        var headers = CurlHeaders.Arguments(config.RequestHeaders);
        var content = isRedirectMode
            ? await FetchRedirectUrlAsync(config.Url, ct, ua, headers)
            : await FetchViaIwrAsync(config.Url, ct, ua, headers, config.FetchRetries, config.FetchMaxTimeSeconds, nameFailure: true);
        if (content is null) return (null, null);

        var version = string.IsNullOrWhiteSpace(config.ServedUrlTemplate) || isRedirectMode
            ? ExtractVersion(content, pattern, config.SelectHighestVersion)
            : await NewestServedVersionAsync(content, pattern, config.ServedUrlTemplate, ct, ua, headers);

        string? downloadUrl = null;
        if (!string.IsNullOrEmpty(config.DownloadUrlQuery))
        {
            // {version} lets the URL pattern target the same release the version came from,
            // which matters when max-by-version selection picks a later match.
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

    /// <summary>-A and the agent, or nothing when no agent is sent.</summary>
    protected static string[] UserAgentArguments(string? userAgent) => userAgent is null ? [] : ["-A", userAgent];

    protected const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// Fetches the final redirect URL without downloading content.
    /// Uses curl -w to extract the effective URL after all redirects.
    /// </summary>
    private static async Task<string?> FetchRedirectUrlAsync(string url, CancellationToken ct, string? userAgent = null, IReadOnlyList<string>? headers = null)
    {
        using var process = CurlProcess.Start(
            ["-L", "--silent", "--max-time", "60", "--max-redirs", "10", "--max-filesize", "1", "-w", "%{url_effective}", .. UserAgentArguments(userAgent), .. headers ?? []], url);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(65), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        // Exit 63 is --max-filesize exceeded, which is expected: only the effective URL is read.
        return (process.ExitCode == 0 || process.ExitCode == 63) && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private const int DefaultFetchMaxTimeSeconds = 25;
    private const int FetchRetryDelaySeconds = 5;

    /// <summary>
    /// The curl switches that bound one page fetch: the time limit of an attempt, and
    /// with retries the retry count and the pause between attempts. curl repeats only a
    /// timeout or a 5xx answer.
    /// </summary>
    public static IReadOnlyList<string> FetchLimitArguments(int retries, int maxTimeSeconds)
    {
        var arguments = new List<string> { "--max-time", (maxTimeSeconds > 0 ? maxTimeSeconds : DefaultFetchMaxTimeSeconds).ToString() };
        if (retries > 0)
            arguments.AddRange(["--retry", retries.ToString(), "--retry-delay", FetchRetryDelaySeconds.ToString()]);
        return arguments;
    }

    /// <summary>The time after which a page fetch with these limits is stopped: every attempt, every pause, and five seconds of margin.</summary>
    public static TimeSpan FetchBound(int retries, int maxTimeSeconds)
    {
        var attempt = maxTimeSeconds > 0 ? maxTimeSeconds : DefaultFetchMaxTimeSeconds;
        var count = Math.Max(retries, 0);
        return TimeSpan.FromSeconds(attempt * (count + 1) + FetchRetryDelaySeconds * count + 5);
    }

    /// <summary>
    /// The highest Pattern match whose installer URL answers. Throws when no listed
    /// version is served: an older staged version must not be read as the latest.
    /// </summary>
    private static async Task<string?> NewestServedVersionAsync(string content, string pattern, string urlTemplate,
        CancellationToken ct, string? userAgent, IReadOnlyList<string> headers)
    {
        var candidates = Regex.Matches(content, pattern, RegexOptions.Multiline)
            .Where(m => m.Groups.Count > 1)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0) return null;

        while (candidates.Count > 0)
        {
            var candidate = LooseVersion.SelectHighest(candidates);
            if (candidate is null) break;
            candidates.Remove(candidate);
            var status = await ServedStatusAsync(urlTemplate.Replace("{version}", candidate, StringComparison.OrdinalIgnoreCase), ct, userAgent, headers);
            if (status is "200" or "206") return candidate;
        }
        throw new InvalidOperationException($"No listed version has a downloadable installer at {urlTemplate}.");
    }

    /// <summary>The HTTP status of a one-byte range request, or the empty string when curl gets no answer.</summary>
    private static async Task<string> ServedStatusAsync(string url, CancellationToken ct, string? userAgent, IReadOnlyList<string> headers)
    {
        var sink = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        using var process = CurlProcess.Start(
            ["-L", "--silent", "--range", "0-0", "--output", sink, "--write-out", "%{http_code}", "--max-time", "60",
             .. UserAgentArguments(userAgent), .. headers], url);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(65), ct));
        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            ct.ThrowIfCancellationRequested();
            return "";
        }
        return (await stdoutTask).Trim();
    }

    /// <param name="nameFailure">A fetch that curl ends with an error throws and names the curl exit code; otherwise it returns null.</param>
    protected static async Task<string?> FetchViaIwrAsync(string url, CancellationToken ct, string? userAgent = null, IReadOnlyList<string>? headers = null,
        int retries = 0, int maxTimeSeconds = 0, bool nameFailure = false)
    {
        // curl.exe follows a 308 redirect, which Invoke-WebRequest under Windows PowerShell 5.1 does not.
        var headerFile = GitHubRateLimit.NewHeaderFile();
        using var process = CurlProcess.Start(
            ["-L", "--silent", "--show-error", .. FetchLimitArguments(retries, maxTimeSeconds), .. GitHubRateLimit.HeaderArguments(headerFile), .. UserAgentArguments(userAgent), .. headers ?? []], url);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(FetchBound(retries, maxTimeSeconds), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            try { File.Delete(headerFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }

        var stdout = await stdoutTask;
        GitHubRateLimit.ThrowIfRefused(url, headerFile, stdout);
        if (process.ExitCode != 0 && nameFailure)
            throw new InvalidOperationException($"Failed to fetch {url} (curl exit {process.ExitCode}).");
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    /// <summary>
    /// Performs a HEAD request and converts the Last-Modified header to a date-based version (YYYY.M.D).
    /// </summary>
    private static async Task<string?> FetchLastModifiedVersionAsync(string url, CancellationToken ct)
    {
        using var process = CurlProcess.Start(["--head", "-L", "--silent", "--max-time", "15"], url);

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
}
