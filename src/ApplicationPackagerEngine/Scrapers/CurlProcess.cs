using System.Diagnostics;

using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.VendorImport;

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
    /// <summary>Starts curl with a configuration for stdin that the caller built (the header of a feed credential). The secret never goes on the command line.</summary>
    public static Process StartWithConfig(IEnumerable<string> options, string url, string stdinConfig)
    {
        var process = new Process { StartInfo = BuildStartInfo(options, url, readConfigFromStdin: true) };
        process.Start();
        RunChildProcesses.Track(process);
        process.StandardInput.Write(stdinConfig);
        process.StandardInput.Close();
        return process;
    }

    /// <summary>
    /// A request of an imported definition to a host that has a key or Basic entry (version checks, page fetches, checksum files). curl would follow
    /// a redirect by itself and send a custom header to every target, so the program follows the redirects of the bound host itself, with the
    /// credential, in short probes; the real request then goes to the last address of the bound host with no <c>-L</c> (a redirect that appears now
    /// is not followed), or, when the chain left the bound host, to the first address outside it with no credential.
    /// </summary>
    private static Process StartCredentialed(List<string> options, Uri start, FeedCredential credential)
    {
        var current = start;
        // The limit of the caller (--max-redirs) bounds the chain; a chain that is longer is an error, not a request to an address that was never probed.
        var limit = MaxRedirects(options);
        var hops = 0;
        for (; credential.Entry.Matches(current); hops++)
        {
            var (status, location) = Probe(current, credential, options);
            if (status is not (301 or 302 or 303 or 307 or 308)) break;
            // An empty or unusable Location ends the chain at this address: it is never probed again.
            if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(current, location, out var next)
                || !next.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) break;
            if (hops >= limit) throw new InvalidOperationException($"More than {limit} redirects from {start.Host}; the request is refused.");
            current = next;
        }
        // Outside the bound host the rest is plain curl, which follows no further credential lookup (so two credentialed hosts cannot send each other around).
        if (!credential.Entry.Matches(current)) return StartPlain(options, current.AbsoluteUri);
        var request = options.Where(o => o is not ("-L" or "--location")).ToList();
        return StartWithConfig(request, current.AbsoluteUri, FeedPayloadCredentials.ConfigFor(credential));
    }

    private static int MaxRedirects(IReadOnlyList<string> options)
    {
        for (var i = 0; i + 1 < options.Count; i++)
            if (options[i] == "--max-redirs" && int.TryParse(options[i + 1], out var n) && n >= 0) return Math.Min(n, 10);
        return 5;
    }

    /// <summary>One request with the credential and no redirect: the status and the Location header. The body is not read.</summary>
    private static (int Status, string? Redirect) Probe(Uri address, FeedCredential credential, IReadOnlyList<string> options)
    {
        // Only the user agent and the headers of the caller go along; the output options of the real request do not.
        var kept = new List<string>();
        for (var i = 0; i < options.Count; i++)
            if (options[i] is "-A" or "--user-agent" or "-H" or "--header" && i + 1 < options.Count) { kept.Add(options[i]); kept.Add(options[++i]); }
        List<string> probe = ["--silent", "--max-redirs", "0", "--max-time", "10", "--max-filesize", "1", "-o", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            // %header{location}: with --max-filesize a redirect that has a body stops with exit 63 and %{redirect_url} stays empty, the header does not.
            "--write-out", "%{http_code}|%header{location}", .. kept];
        using var process = StartWithConfig(probe, address.AbsoluteUri, FeedPayloadCredentials.ConfigFor(credential));
        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(12000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return (0, null); }
        var output = outputTask.GetAwaiter().GetResult().Trim();
        var parts = output.Split('|', 2);
        return (int.TryParse(parts[0], out var code) ? code : 0, parts.Length > 1 ? parts[1] : null);
    }

    public static Process Start(IEnumerable<string> options, string url)
    {
        if (ImportedRunScope.Active && Uri.TryCreate(url, UriKind.Absolute, out var credentialed) && FeedPayloadCredentials.For(credentialed) is { } feedCredential)
            return StartCredentialed(options.ToList(), credentialed, feedCredential);
        return StartPlain(options, url);
    }

    private static Process StartPlain(IEnumerable<string> options, string url)
    {
        var authConfig = GitHubAuth.CurlConfigFor(url);
        var process = new Process { StartInfo = BuildStartInfo(options, url, authConfig is not null) };

        process.Start();
        RunChildProcesses.Track(process);

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
        // The curl config files of the user (_curlrc) can carry a credential: an imported definition reads none (-q must be the first argument).
        if (ImportedRunScope.Active) startInfo.ArgumentList.Add("-q");
        foreach (var option in options) startInfo.ArgumentList.Add(option);
        // A request that an imported definition causes cannot be downgraded to HTTP by a redirect after the static check of its address.
        if (ImportedRunScope.Active)
        {
            startInfo.ArgumentList.Add("--proto");
            startInfo.ArgumentList.Add("=https");
            startInfo.ArgumentList.Add("--proto-redir");
            startInfo.ArgumentList.Add("=https");
        }
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
    /// quote, whitespace, a control character or a trailing backslash. Such a URL comes
    /// from a vendor page or feed, and each of those can end the argument on a command
    /// line: a trailing backslash escapes the quote that closes a quoted argument.
    /// </summary>
    public static void ValidateUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("Refused download URL: the URL is empty.");
        if (url.EndsWith('\\'))
            throw new InvalidOperationException($"Refused download URL '{SecretRedactor.WithoutQueries(Printable(url))}': it ends with a backslash.");
        foreach (var c in url)
        {
            if (c == '"' || char.IsWhiteSpace(c) || char.IsControl(c))
                throw new InvalidOperationException(
                    $"Refused download URL '{SecretRedactor.WithoutQueries(Printable(url))}': it contains a double quote, whitespace or a control character.");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !url.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refused download URL '{SecretRedactor.WithoutQueries(Printable(url))}': it is not an absolute http or https URL.");
    }

    private static string Printable(string value) =>
        new(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());
}
