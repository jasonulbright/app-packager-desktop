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

        // Redirect mode: extract version from the final redirect URL, not page content
        var isRedirectMode = config.Pattern.StartsWith("REDIR:");
        var pattern = isRedirectMode ? config.Pattern["REDIR:".Length..] : config.Pattern;

        var content = isRedirectMode
            ? await FetchRedirectUrlAsync(config.Url, ct)
            : await FetchViaIwrAsync(config.Url, ct);
        if (content is null) return null;

        var match = Regex.Match(content, pattern, RegexOptions.Multiline);
        if (match.Success && match.Groups.Count > 1)
            return match.Groups[1].Value;

        return null;
    }

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>
    /// Fetches the final redirect URL without downloading content.
    /// Uses curl -w to extract the effective URL after all redirects.
    /// </summary>
    private static async Task<string?> FetchRedirectUrlAsync(string url, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"-L --silent --max-time 25 --output NUL -w \"%{{url_effective}}\" -A \"{UserAgent}\" \"{url}\"",
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

    private static async Task<string?> FetchViaIwrAsync(string url, CancellationToken ct)
    {
        // Use curl.exe: handles 308 redirects, avoids PS5.1 IWR quirks, better WAF compatibility
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = $"-L --silent --show-error --max-time 25 -A \"{UserAgent}\" \"{url}\"",
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
