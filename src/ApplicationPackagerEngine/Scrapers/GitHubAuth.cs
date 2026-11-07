using System.Diagnostics;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Resolves a GitHub API token once per process and turns it into a curl
/// configuration fragment for requests to api.github.com.
///
/// Anonymous api.github.com requests share a 60/hour budget per source address,
/// which a full-catalog sweep exhausts. A token raises the budget to 5000/hour.
///
/// Resolution order: APP_PACKAGER_GITHUB_TOKEN, GITHUB_TOKEN, then `gh auth token`.
/// No token resolves to null and every request stays anonymous.
/// </summary>
public static class GitHubAuth
{
    private const string ApiHost = "api.github.com";

    private static readonly Lazy<(string? Token, string? Source)> Cached = new(
        () => ResolveTokenWithSource(Environment.GetEnvironmentVariable, ReadGhCliToken),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The resolved token, or null when none is available. Resolved once per process.
    /// </summary>
    public static string? Token => Cached.Value.Token;

    /// <summary>
    /// Where the token came from: the variable name, "gh auth login", or null
    /// when requests are anonymous.
    /// </summary>
    public static string? TokenSource => Cached.Value.Source;

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
    /// APP_PACKAGER_GITHUB_TOKEN, GITHUB_TOKEN, or the gh CLI, else null.
    /// </summary>
    public static string? ResolveToken(Func<string, string?> readEnv, Func<string?> readGhCli)
        => ResolveTokenWithSource(readEnv, readGhCli).Token;

    /// <summary>
    /// Token resolution that also names the winning source, for the Settings
    /// status row. A source whose value is refused by Sanitize still ends the
    /// search: the operator set it, so falling through would hide the problem.
    /// </summary>
    public static (string? Token, string? Source) ResolveTokenWithSource(
        Func<string, string?> readEnv, Func<string?> readGhCli)
    {
        foreach (var name in new[] { "APP_PACKAGER_GITHUB_TOKEN", "GITHUB_TOKEN" })
        {
            var value = readEnv(name)?.Trim();
            if (!string.IsNullOrEmpty(value)) return (Sanitize(value), name);
        }

        var cli = readGhCli()?.Trim();
        return string.IsNullOrEmpty(cli) ? (null, null) : (Sanitize(cli), "gh auth login");
    }

    /// <summary>
    /// A token containing a quote or newline would break out of the curl config
    /// line, so such a value is refused rather than embedded.
    /// </summary>
    private static string? Sanitize(string token)
        => token.AsSpan().IndexOfAny('"', '\r', '\n') >= 0 ? null : token;

    private static string? ReadGhCliToken() => ReadCliToken("gh", "auth token", 10_000);

    /// <summary>
    /// Runs the CLI and returns its output when it ends with success inside the time limit. The output
    /// is read in the background, so a CLI that hangs cannot hold the caller past the limit.
    /// </summary>
    internal static string? ReadCliToken(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            // The process has ended; its pipes close with it, so the reads end at once.
            if (!stdout.Wait(TimeSpan.FromSeconds(5))) return null;
            _ = stderr;
            return process.ExitCode == 0 ? stdout.Result.Trim() : null;
        }
        catch
        {
            // gh CLI absent: anonymous is the documented fallback.
            return null;
        }
    }
}
