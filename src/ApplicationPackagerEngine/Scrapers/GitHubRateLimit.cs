using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Recognizes a GitHub API response that refuses a request because the rate limit
/// is used up: status 403 or 429 from api.github.com (or a response that carries
/// GitHub's request id header), with x-ratelimit-remaining 0, a Retry-After header,
/// or GitHub's rate-limit message. curl runs without --fail, so the refusal arrives
/// as an ordinary body that no version pattern matches; the header file is the only
/// place the status is visible.
/// </summary>
public static partial class GitHubRateLimit
{
    [GeneratedRegex(@"^HTTP/\S+\s+(?<status>\d{3})", RegexOptions.Multiline)]
    private static partial Regex StatusLine();

    /// <summary>curl options that write the response headers of every hop to <paramref name="headerFile"/>.</summary>
    public static string[] HeaderArguments(string headerFile) => ["-D", headerFile];

    /// <summary>A header file path for one request.</summary>
    public static string NewHeaderFile() => Path.Combine(Path.GetTempPath(), "apkg-hdr-" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>
    /// Reads and deletes the header file, and throws when the response is a GitHub
    /// rate-limit refusal. A missing or unreadable header file is no refusal.
    /// </summary>
    public static void ThrowIfRefused(string url, string headerFile, string? body)
    {
        string headers;
        try { headers = File.Exists(headerFile) ? File.ReadAllText(headerFile) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { headers = ""; }
        finally
        {
            try { File.Delete(headerFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (Refusal(url, headers, body) is { } message) throw new InvalidOperationException(message);
    }

    /// <summary>The refusal message, or null when the response is not a GitHub rate-limit refusal.</summary>
    public static string? Refusal(string url, string headerText, string? body)
    {
        var block = LastHeaderBlock(headerText);
        if (block.Count == 0 || block[0] is not { } statusLine) return null;
        var statusMatch = StatusLine().Match(statusLine);
        if (!statusMatch.Success) return null;
        var status = int.Parse(statusMatch.Groups["status"].Value, CultureInfo.InvariantCulture);
        if (status is not (403 or 429)) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var fromGitHub = GitHubAuth.AppliesTo(url) || headers.ContainsKey("x-github-request-id");
        if (!fromGitHub) return null;
        var limited = (headers.TryGetValue("x-ratelimit-remaining", out var remaining) && remaining == "0")
                      || headers.ContainsKey("retry-after")
                      || (body ?? "").Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        if (!limited) return null;

        var reset = "";
        if (headers.TryGetValue("x-ratelimit-reset", out var resetText)
            && long.TryParse(resetText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            reset = $"; it resets at {DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime():yyyy-MM-dd HH:mm}";
        var who = string.IsNullOrEmpty(GitHubAuth.TokenSource) ? "anonymous requests" : "the token from " + GitHubAuth.TokenSource;
        return $"GitHub API rate limit reached for {url} (HTTP {status}, {who}){reset}. " +
               "Set PACKR_GITHUB_TOKEN or GITHUB_TOKEN, or run gh auth login, to raise the limit; Settings shows the current budget.";
    }

    /// <summary>The lines of the last header block in a curl -D file: the final hop after redirects.</summary>
    private static List<string> LastHeaderBlock(string headerText)
    {
        var lines = headerText.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("HTTP/", StringComparison.Ordinal)) start = i;
        if (start < 0) return [];
        var block = new List<string>();
        for (var i = start; i < lines.Length && lines[i].Length > 0; i++) block.Add(lines[i]);
        return block;
    }
}
