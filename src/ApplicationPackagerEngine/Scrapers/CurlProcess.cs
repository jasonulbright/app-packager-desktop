using System.Diagnostics;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Starts curl.exe with the GitHub API Authorization header attached when the
/// target URL needs one. Every HTTP path in the engine goes through here so a
/// token applies to scrapes and downloads alike.
/// </summary>
public static class CurlProcess
{
    /// <summary>
    /// Starts curl with the given options and the URL. When the URL is a GitHub API
    /// call and a token is available, an extra config is piped over stdin; stdin stays
    /// closed otherwise so curl never waits on it. A URL that <see cref="ValidateUrl"/>
    /// refuses throws before any process starts.
    /// </summary>
    public static Process Start(IEnumerable<string> options, string url)
    {
        var authConfig = GitHubAuth.CurlConfigFor(url);
        var process = new Process { StartInfo = BuildStartInfo(options, url, authConfig is not null) };

        process.Start();

        if (authConfig is not null)
        {
            process.StandardInput.Write(authConfig);
            process.StandardInput.Close();
        }

        return process;
    }

    /// <summary>
    /// The start info for one curl call. Every value is a separate ArgumentList entry,
    /// so a quote or space inside a path or header stays inside its argument. The URL
    /// follows --url, so a value that starts with '-' is never read as an option, and
    /// --globoff keeps '[' and '{' in a URL from expanding into further requests.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(IEnumerable<string> options, string url, bool readConfigFromStdin)
    {
        ValidateUrl(url);
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = readConfigFromStdin,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var option in options) startInfo.ArgumentList.Add(option);
        startInfo.ArgumentList.Add("--globoff");
        if (readConfigFromStdin)
        {
            startInfo.ArgumentList.Add("-K");
            startInfo.ArgumentList.Add("-");
        }
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    /// <summary>
    /// Refuses a URL that is not an absolute http or https URL, or that carries a double
    /// quote, whitespace or a control character. Such a URL comes from a vendor page or
    /// feed, and each of those characters can end the argument on a command line.
    /// </summary>
    public static void ValidateUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("Refused download URL: the URL is empty.");
        foreach (var c in url)
        {
            if (c == '"' || char.IsWhiteSpace(c) || char.IsControl(c))
                throw new InvalidOperationException(
                    $"Refused download URL '{Printable(url)}': it contains a double quote, whitespace or a control character.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !url.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refused download URL '{Printable(url)}': it is not an absolute http or https URL.");
    }

    private static string Printable(string value) =>
        new(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());
}
