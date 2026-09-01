using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Token resolution order and the host gate. The environment and the gh CLI are
/// injected — no process launch, no network.
/// </summary>
public class GitHubAuthTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] entries)
        => name => entries.FirstOrDefault(e => e.Name == name).Value;

    [Fact]
    public void ResolveToken_PrefersPackrVariable()
    {
        var token = GitHubAuth.ResolveToken(
            Env(("PACKR_GITHUB_TOKEN", "packr-token"), ("GITHUB_TOKEN", "generic-token")),
            () => "gh-token");

        Assert.Equal("packr-token", token);
    }

    [Fact]
    public void ResolveToken_FallsBackToGithubTokenVariable()
    {
        var token = GitHubAuth.ResolveToken(
            Env(("GITHUB_TOKEN", "generic-token")),
            () => "gh-token");

        Assert.Equal("generic-token", token);
    }

    [Fact]
    public void ResolveToken_FallsBackToGhCli()
    {
        Assert.Equal("gh-token", GitHubAuth.ResolveToken(Env(), () => "gh-token"));
    }

    [Fact]
    public void ResolveToken_NoSourceReturnsNull()
    {
        Assert.Null(GitHubAuth.ResolveToken(Env(), () => null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveToken_BlankVariableIsSkipped(string blank)
    {
        Assert.Equal("gh-token", GitHubAuth.ResolveToken(
            Env(("PACKR_GITHUB_TOKEN", blank), ("GITHUB_TOKEN", blank)),
            () => "gh-token"));
    }

    [Fact]
    public void ResolveToken_TrimsSurroundingWhitespace()
    {
        Assert.Equal("abc123", GitHubAuth.ResolveToken(Env(("GITHUB_TOKEN", "  abc123\n")), () => null));
    }

    [Theory]
    [InlineData("bad\"token")]
    [InlineData("bad\ntoken")]
    [InlineData("bad\rtoken")]
    public void ResolveToken_RefusesTokenThatWouldEscapeTheCurlConfigLine(string token)
    {
        Assert.Null(GitHubAuth.ResolveToken(Env(("GITHUB_TOKEN", token)), () => null));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/x/y/releases/latest", true)]
    [InlineData("https://API.GitHub.com/rate_limit", true)]
    [InlineData("https://github.com/x/y/releases/download/v1/app.exe", false)]
    [InlineData("https://objects.githubusercontent.com/blob", false)]
    [InlineData("https://api.github.com.evil.test/repos", false)]
    [InlineData("https://downloads.example.test/app.msi", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void AppliesTo_OnlyMatchesTheApiHost(string? url, bool expected)
    {
        Assert.Equal(expected, GitHubAuth.AppliesTo(url));
    }

    [Fact]
    public void CurlConfigFor_NonApiHostReturnsNull()
    {
        Assert.Null(GitHubAuth.CurlConfigFor("https://github.com/x/y/releases/download/v1/app.exe"));
    }
}
