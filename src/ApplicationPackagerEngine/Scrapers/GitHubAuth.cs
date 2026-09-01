using System.Diagnostics;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Resolves a GitHub API token once per process and turns it into a curl
/// configuration fragment for requests to api.github.com.
///
/// Anonymous api.github.com requests share a 60/hour budget per source address,
/// which a full-catalog sweep exhausts. A token raises the budget to 5000/hour.
///
/// Resolution order: PACKR_GITHUB_TOKEN, GITHUB_TOKEN, then `gh auth token`.
/// No token resolves to null and every request stays anonymous.
/// </summary>
public static class GitHubAuth
{
    private const string ApiHost = "api.github.com";

    private static readonly Lazy<string?> Cached = new(
        () => ResolveToken(Environment.GetEnvironmentVariable, ReadGhCliToken),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The resolved token, or null when none is available. Resolved once per process.
    /// </summary>
    public static string? Token => Cached.Value;

    /// <summary>
    /// True when the URL targets the GitHub REST API. Release asset downloads live on
    /// other hosts that reject an Authorization header, so only the API host is tokened.
    /// </summary>
    public static bool AppliesTo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a curl config body carrying the Authorization header for the URL,
    /// or null when the URL is not a GitHub API call or no token is available.
    /// The header travels over stdin rather than the command line so the token
    /// does not appear in the process arguments.
    /// </summary>
    public static string? CurlConfigFor(string? url)
    {
        if (!AppliesTo(url)) return null;
        var token = Token;
        return string.IsNullOrEmpty(token)
            ? null
            : $"header = \"Authorization: Bearer {token}\"\n";
    }

    /// <summary>
    /// Token resolution with injected lookups. Returns the first non-empty of
    /// PACKR_GITHUB_TOKEN, GITHUB_TOKEN, or the gh CLI, else null.
    /// </summary>
    public static string? ResolveToken(Func<string, string?> readEnv, Func<string?> readGhCli)
    {
        foreach (var name in new[] { "PACKR_GITHUB_TOKEN", "GITHUB_TOKEN" })
        {
            var value = readEnv(name)?.Trim();
            if (!string.IsNullOrEmpty(value)) return Sanitize(value);
        }

        var cli = readGhCli()?.Trim();
        return string.IsNullOrEmpty(cli) ? null : Sanitize(cli);
    }

    /// <summary>
    /// A token containing a quote or newline would break out of the curl config
    /// line, so such a value is refused rather than embedded.
    /// </summary>
    private static string? Sanitize(string token)
        => token.AsSpan().IndexOfAny('"', '\r', '\n') >= 0 ? null : token;

    private static string? ReadGhCliToken()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "auth token",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            // gh CLI absent: anonymous is the documented fallback.
            return null;
        }
    }
}
