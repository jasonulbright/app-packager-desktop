using System.Diagnostics;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scrapes a web page via HTTP GET and extracts a version string
/// using a regex pattern. Covers ~80% of apps.
///
/// Uses powershell.exe Invoke-WebRequest instead of .NET HttpClient
/// because the self-contained .NET 10 app's HTTP stack has TLS/cert
/// issues on some Windows Server environments. IWR uses the OS HTTP
/// stack which handles certificates correctly everywhere.
/// </summary>
public sealed class HttpRegexScraper : IVersionScraper
{
    public string Type => "HttpRegex";

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        var content = await FetchViaIwrAsync(config.Url, ct);
        if (content is null) return null;

        var match = Regex.Match(content, config.Pattern);
        if (match.Success && match.Groups.Count > 1)
            return match.Groups[1].Value;

        return null;
    }

    private static async Task<string?> FetchViaIwrAsync(string url, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12; (Invoke-WebRequest -Uri '{url}' -UseBasicParsing -UserAgent 'ApplicationPackager/2.1').Content\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(15), ct));

        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }

        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }
}
