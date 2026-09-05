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
        Assert.Equal("Authenticated - gh auth login, account octocat (5,000 requests/hour, 4,991 remaining)", status.StatusText());
    }

    [Fact]
    public void Anonymous_NamesTheTwoWaysToRaiseTheBudget()
    {
        var status = GitHubApiStatusService.Parse(AnonymousRateLimit, null, false, null);

        Assert.False(status.Authenticated);
        Assert.Null(status.Account);
        Assert.Equal("Anonymous (60 requests/hour per address, 57 remaining); set GITHUB_TOKEN or run gh auth login", status.StatusText());
    }

    [Fact]
    public void RejectedToken_SurfacesTheApiMessage()
    {
        var status = GitHubApiStatusService.Parse("""{"message":"Bad credentials","status":"401"}""", null, true, "GITHUB_TOKEN");

        Assert.Equal("Bad credentials", status.Error);
        Assert.Equal("Unavailable - Bad credentials", status.StatusText());
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
        var status = GitHubApiStatusService.Parse(AuthenticatedRateLimit, null, true, "PACKR_GITHUB_TOKEN");
        Assert.Equal("Authenticated - PACKR_GITHUB_TOKEN (5,000 requests/hour, 4,991 remaining)", status.StatusText());
    }

    [Fact]
    public void TokenSource_FollowsTheResolutionOrder()
    {
        static Func<string, string?> Env(params (string Name, string? Value)[] pairs) =>
            name => pairs.FirstOrDefault(p => p.Name == name).Value;

        Assert.Equal(("a", "PACKR_GITHUB_TOKEN"),
            GitHubAuth.ResolveTokenWithSource(Env(("PACKR_GITHUB_TOKEN", "a"), ("GITHUB_TOKEN", "b")), () => "c"));
        Assert.Equal(("b", "GITHUB_TOKEN"),
            GitHubAuth.ResolveTokenWithSource(Env(("GITHUB_TOKEN", "b")), () => "c"));
        Assert.Equal(("c", "gh auth login"),
            GitHubAuth.ResolveTokenWithSource(Env(), () => "c"));
        Assert.Equal((null, null),
            GitHubAuth.ResolveTokenWithSource(Env(), () => null));
    }
}
