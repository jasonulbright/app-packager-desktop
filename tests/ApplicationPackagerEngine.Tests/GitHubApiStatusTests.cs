using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Settings row that says whether a catalog sweep will run into the
/// anonymous GitHub budget, built from canned api.github.com bodies.
/// </summary>
public class GitHubApiStatusTests
{
    private const string AuthenticatedRateLimit =
        """{"resources":{"core":{"limit":5000,"used":9,"remaining":4991,"reset":1}},"rate":{"limit":5000,"remaining":4991}}""";

    private const string AnonymousRateLimit =
        """{"resources":{"core":{"limit":60,"used":3,"remaining":57,"reset":1}},"rate":{"limit":60,"remaining":57}}""";

    private const string User = """{"login":"octocat","id":1}""";

    [Fact]
    public void Authenticated_NamesSourceAccountAndQuota()
    {
        var status = GitHubApiStatusService.Parse(AuthenticatedRateLimit, User, true, "gh auth login");

        Assert.True(status.Authenticated);
        Assert.Equal("octocat", status.Account);
        Assert.Equal(5000, status.Limit);
        Assert.Equal(4991, status.Remaining);
        Assert.Equal("Signed in via GitHub CLI as octocat (5,000 requests per hour, 4,991 remaining)", status.StatusText());
    }

    [Fact]
    public void Anonymous_SaysNotAuthenticatedWithTheBudget()
    {
        var status = GitHubApiStatusService.Parse(AnonymousRateLimit, null, false, null);

        Assert.False(status.Authenticated);
        Assert.Null(status.Account);
        Assert.Equal("Not authenticated: 60 requests per hour (57 remaining)", status.StatusText());
    }

    [Fact]
    public void RejectedToken_SurfacesTheApiMessage()
    {
        var status = GitHubApiStatusService.Parse("""{"message":"Bad credentials","status":"401"}""", null, true, "GITHUB_TOKEN");

        Assert.Equal("Bad credentials", status.Error);
        Assert.Equal(401, status.HttpStatus);
        Assert.StartsWith("Token rejected (401): GitHub refused the credential (Environment variable GITHUB_TOKEN) (Bad credentials)", status.StatusText());
    }

    [Fact]
    public void Blocked_IsReportedAs403()
    {
        var status = GitHubApiStatusService.Parse("""{"message":"Resource not accessible","status":"403"}""", null, true, "Settings");
        Assert.Equal(403, status.HttpStatus);
        Assert.StartsWith("GitHub refused the request (403) (Resource not accessible). Token saved in settings is in use.", status.StatusText());
    }

    [Theory]
    [InlineData(true, "Settings", "Token saved in settings as octocat (5,000 requests per hour, 4,991 remaining)")]
    [InlineData(true, "GITHUB_TOKEN", "Environment variable GITHUB_TOKEN as octocat (5,000 requests per hour, 4,991 remaining)")]
    [InlineData(true, "gh auth login", "Signed in via GitHub CLI as octocat (5,000 requests per hour, 4,991 remaining)")]
    public void EverySourceIsNamed(bool auth, string source, string expected)
        => Assert.Equal(expected, GitHubApiStatusService.Parse(AuthenticatedRateLimit, User, auth, source).StatusText());

    [Fact]
    public void TheTokenBoxAgreesWithTheStatus()
    {
        Assert.Equal("GitHub CLI sign-in in use - type a token to override it", GitHubApiStatusService.TokenBoxHint(false, "gh auth login"));
        Assert.Equal("Saved in settings - leave empty to keep it", GitHubApiStatusService.TokenBoxHint(true, "Settings"));
        Assert.Equal("Not set - no token in use", GitHubApiStatusService.TokenBoxHint(false, null));
        Assert.Contains("GitHub CLI sign-in provides the token now. A token you save here wins over it.", GitHubApiStatusService.TokenBoxNote(false, "gh auth login"));
        Assert.Contains("wins over the environment variables and the GitHub CLI", GitHubApiStatusService.TokenBoxNote(true, "Settings"));
    }

    [Fact]
    public void NoAnswer_IsReportedNotGuessed()
    {
        Assert.Equal("Unavailable - api.github.com did not answer",
            GitHubApiStatusService.Parse(null, null, false, null).StatusText());
        Assert.Equal("Unavailable - unexpected rate_limit response",
            GitHubApiStatusService.Parse("<html>", null, false, null).StatusText());
    }

    [Fact]
    public void MissingUserBody_LeavesTheAccountBlank()
    {
        var status = GitHubApiStatusService.Parse(AuthenticatedRateLimit, null, true, "APP_PACKAGER_GITHUB_TOKEN");
        Assert.Equal("Environment variable APP_PACKAGER_GITHUB_TOKEN (5,000 requests per hour, 4,991 remaining)", status.StatusText());
    }

    [Fact]
    public void TokenSource_FollowsTheResolutionOrder()
    {
        static Func<string, string?> Env(params (string Name, string? Value)[] pairs) =>
            name => pairs.FirstOrDefault(p => p.Name == name).Value;

        Assert.Equal(("a", "APP_PACKAGER_GITHUB_TOKEN"),
            GitHubAuth.ResolveTokenWithSource(Env(("APP_PACKAGER_GITHUB_TOKEN", "a"), ("GITHUB_TOKEN", "b")), () => "c"));
        Assert.Equal(("b", "GITHUB_TOKEN"),
            GitHubAuth.ResolveTokenWithSource(Env(("GITHUB_TOKEN", "b")), () => "c"));
        Assert.Equal(("c", "gh auth login"),
            GitHubAuth.ResolveTokenWithSource(Env(), () => "c"));
        Assert.Equal((null, null),
            GitHubAuth.ResolveTokenWithSource(Env(), () => null));
    }
}
