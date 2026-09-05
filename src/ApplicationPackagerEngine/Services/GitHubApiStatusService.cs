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
    string? Error)
{
    /// <summary>One-line Settings text. The anonymous form names the two ways to raise the budget.</summary>
    public string StatusText()
    {
        if (Error is not null)
            return $"Unavailable - {Error}";

        var quota = Limit is null ? "" : $" ({Limit:N0} requests/hour, {Remaining:N0} remaining)";
        if (Authenticated)
        {
            var account = string.IsNullOrEmpty(Account) ? "" : $", account {Account}";
            return $"Authenticated - {Source}{account}{quota}";
        }

        var perAddress = Limit is null ? "" : $" ({Limit:N0} requests/hour per address, {Remaining:N0} remaining)";
        return $"Anonymous{perAddress}; set GITHUB_TOKEN or run gh auth login";
    }
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
                return new GitHubApiStatus(authenticated, source, null, null, null, message.GetString());
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

    private static async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        using var process = CurlProcess.Start(
            $"-L --silent --show-error --max-time 20 -A \"packr\" \"{url}\"", url);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var exitTask = process.WaitForExitAsync(ct);
        var completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(25), ct));
        if (completed != exitTask)
        {
            try { process.Kill(true); } catch { }
            return null;
        }
        var stdout = await stdoutTask;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }
}
