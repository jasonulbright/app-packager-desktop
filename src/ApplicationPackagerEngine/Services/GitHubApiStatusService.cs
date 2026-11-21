using System.Text.Json;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Services;

/// <summary>What the GitHub REST API reports for this process's credentials.</summary>
public sealed record GitHubApiStatus(
    bool Authenticated,
    string? Source,
    string? Account,
    int? Limit,
    int? Remaining,
    string? Error,
    int? HttpStatus = null)
{
    /// <summary>
    /// The one status text of every place that shows the GitHub sign-in (Settings and the token box). It names the source:
    /// "Signed in by using GitHub CLI as octocat (5,000 requests per hour, 4,991 remaining)", "Token saved in settings as octocat ...",
    /// "Environment variable GITHUB_TOKEN as octocat ...", "Not authenticated: 60 requests per hour (57 remaining)".
    /// A token that GitHub refuses (401) or a request that GitHub blocks (403) is named as such.
    /// </summary>
    public string StatusText()
    {
        var phrase = GitHubAuth.SourcePhrase(Source);
        if (HttpStatus == 401)
            return $"Token rejected (401): GitHub refused the credential ({phrase}){ErrorSuffix()}. Replace the token, remove it, or sign in with the GitHub CLI again.";
        if (HttpStatus == 403)
            return $"GitHub refused the request (403){ErrorSuffix()}. {(Authenticated ? phrase + " is in use" : "No token is in use")}.";
        if (Error is not null)
            return $"Unavailable - {Error}";

        var quota = Limit is null ? "" : $" ({Limit:N0} requests per hour, {Remaining:N0} remaining)";
        if (Authenticated)
        {
            var who = string.IsNullOrEmpty(Account) ? "" : $" as {Account}";
            return Source == "gh auth login"
                ? $"Signed in by using GitHub CLI{who}{quota}"
                : $"{phrase}{who}{quota}";
        }

        return $"Not authenticated:{(Limit is null ? " 60 requests per hour" : $" {Limit:N0} requests per hour ({Remaining:N0} remaining)")}";
    }

    private string ErrorSuffix() => string.IsNullOrWhiteSpace(Error) ? "" : $" ({Error})";
}

/// <summary>
/// Reads the GitHub API quota for the credentials the scrapers use, so the
/// Settings page can show whether a catalog sweep will run into the 60/hour
/// anonymous budget. The rate-limit endpoint itself is not counted.
/// </summary>
public static class GitHubApiStatusService
{
    private const string RateLimitUrl = "https://api.github.com/rate_limit";
    private const string UserUrl = "https://api.github.com/user";

    /// <summary>The placeholder of the token box: what is in use now, and that a typed token overrides it.</summary>
    public static string TokenBoxHint(bool hasSavedToken, string? effectiveSource)
    {
        if (hasSavedToken) return "Saved in settings - leave empty to keep it";
        return string.IsNullOrEmpty(effectiveSource)
            ? "Not set - no token in use"
            : $"{GitHubAuth.SourcePhrase(effectiveSource)} in use - type a token to override it";
    }

    /// <summary>The note under the token box: which source wins. It never contradicts the status line, which comes from the same source.</summary>
    public static string TokenBoxNote(bool hasSavedToken, string? effectiveSource)
    {
        const string Order = "Order: a token saved here, then APP_PACKAGER_GITHUB_TOKEN, GITHUB_TOKEN, GH_TOKEN, then the GitHub CLI sign-in.";
        if (hasSavedToken)
            return $"A token is saved here (encrypted for the current Windows user). It wins over the environment variables and the GitHub CLI. Leave the box empty to keep it. {Order}";
        if (string.IsNullOrEmpty(effectiveSource))
            return $"No token is in use. Save one here, set GITHUB_TOKEN, or sign in with the GitHub CLI. {Order}";
        return $"{GitHubAuth.SourcePhrase(effectiveSource)} provides the token now. A token you save here wins over it. {Order}";
    }

    public static async Task<GitHubApiStatus> QueryAsync(CancellationToken ct = default)
    {
        var authenticated = !string.IsNullOrEmpty(GitHubAuth.Token);
        try
        {
            var rateLimit = await FetchAsync(RateLimitUrl, ct);
            var user = authenticated ? await FetchAsync(UserUrl, ct) : null;
            return Parse(rateLimit, user, authenticated, GitHubAuth.TokenSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GitHubApiStatus(authenticated, GitHubAuth.TokenSource, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Builds the status from the two response bodies. A missing rate-limit
    /// body reports the endpoint as unavailable; a missing user body leaves
    /// the account blank.
    /// </summary>
    public static GitHubApiStatus Parse(string? rateLimitJson, string? userJson, bool authenticated, string? source)
    {
        if (string.IsNullOrWhiteSpace(rateLimitJson))
            return new GitHubApiStatus(authenticated, source, null, null, null, "api.github.com did not answer");

        int? limit = null, remaining = null;
        try
        {
            using var doc = JsonDocument.Parse(rateLimitJson);
            if (doc.RootElement.TryGetProperty("message", out var message) && !doc.RootElement.TryGetProperty("resources", out _))
                return new GitHubApiStatus(authenticated, source, null, null, null, message.GetString(), ReadStatus(doc.RootElement));
            var core = doc.RootElement.GetProperty("resources").GetProperty("core");
            limit = core.GetProperty("limit").GetInt32();
            remaining = core.GetProperty("remaining").GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new GitHubApiStatus(authenticated, source, null, null, null, "unexpected rate_limit response");
        }

        string? account = null;
        if (!string.IsNullOrWhiteSpace(userJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(userJson);
                if (doc.RootElement.TryGetProperty("login", out var login))
                    account = login.GetString();
            }
            catch (JsonException) { }
        }

        return new GitHubApiStatus(authenticated, source, account, limit, remaining, null);
    }

    /// <summary>The HTTP status that the GitHub error body repeats in its "status" field (a string), or null.</summary>
    private static int? ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status)) return null;
        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var n)) return n;
        return status.ValueKind == JsonValueKind.String && int.TryParse(status.GetString(), out var m) ? m : null;
    }

    private static async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        using var process = CurlProcess.Start(
            ["-L", "--silent", "--show-error", "--max-time", "20", "-A", "app-packager-desktop"], url);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        if (!await RunChildProcesses.WaitOrKillAsync(process, TimeSpan.FromSeconds(25), ct))
        {
            return null;
        }
        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }
}
