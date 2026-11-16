using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Imported definitions run with no ambient credentials and with curl limited to HTTPS; no log line names a query.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ImportedRunScopeTests
{
    private static AppDefinition Imported() => new() { Id = "contoso-agent", SourceOrigin = ApplicationOrigin.Imported };

    [Fact]
    public void TheScopeIsActiveOnlyForAnImportedDefinitionAndEndsWithTheRun()
    {
        Assert.False(ImportedRunScope.Active);
        using (ImportedRunScope.EnterFor(Imported())) Assert.True(ImportedRunScope.Active);
        Assert.False(ImportedRunScope.Active);
        Assert.Null(ImportedRunScope.EnterFor(new AppDefinition { Id = "shipped", SourceOrigin = ApplicationOrigin.Catalog }));
        Assert.False(ImportedRunScope.Active);
    }

    [Fact]
    public async Task TheScopeFollowsTheAsyncFlow()
    {
        using var scope = ImportedRunScope.EnterFor(Imported());
        await Task.Delay(1);
        Assert.True(await Task.Run(() => ImportedRunScope.Active));
    }

    [Fact]
    public void AnImportedDefinitionGetsNoGitHubTokenAndAnotherDefinitionStillDoes()
    {
        var savedProvider = GitHubAuth.StoredTokenProvider;
        var savedFirst = Environment.GetEnvironmentVariable("APP_PACKAGER_GITHUB_TOKEN");
        var savedSecond = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("APP_PACKAGER_GITHUB_TOKEN", "ghp_" + new string('a', 36));
            GitHubAuth.Reset();

            Assert.NotNull(GitHubAuth.CurlConfigFor("https://api.github.com/repos/contoso/agent/releases/latest"));
            using (ImportedRunScope.EnterFor(Imported()))
                Assert.Null(GitHubAuth.CurlConfigFor("https://api.github.com/repos/contoso/agent/releases/latest"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_PACKAGER_GITHUB_TOKEN", savedFirst);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", savedSecond);
            GitHubAuth.StoredTokenProvider = savedProvider;
            GitHubAuth.Reset();
        }
    }

    [Fact]
    public void CurlOfAnImportedDefinitionIsLimitedToHttpsAndItsRedirects()
    {
        var plain = CurlProcess.BuildStartInfo(["--silent"], "https://contoso.example/a", false).ArgumentList.ToList();
        Assert.DoesNotContain("--proto-redir", plain);

        using (ImportedRunScope.EnterFor(Imported()))
        {
            var scoped = CurlProcess.BuildStartInfo(["--silent"], "https://contoso.example/a", false).ArgumentList.ToList();
            var at = scoped.IndexOf("--proto");
            Assert.True(at >= 0);
            Assert.Equal("=https", scoped[at + 1]);
            var redirect = scoped.IndexOf("--proto-redir");
            Assert.True(redirect >= 0);
            Assert.Equal("=https", scoped[redirect + 1]);
        }
    }


    [Fact]
    public void CurlOfAnImportedDefinitionReadsNoConfigFile()
    {
        using (ImportedRunScope.EnterFor(Imported()))
            Assert.Equal("-q", CurlProcess.BuildStartInfo(["--silent"], "https://contoso.example/a", false).ArgumentList[0]);
        Assert.NotEqual("-q", CurlProcess.BuildStartInfo(["--silent"], "https://contoso.example/a", false).ArgumentList[0]);
    }

    [Fact]
    public void AHopOfAnImportedDefinitionCannotLeaveItsHostWithAValueThatIsNotAShortVersion()
    {
        var guard = typeof(ChainedHttpRegexScraper).GetMethod("GuardCrossHost", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        void Run(string template, string url, bool relative, string? current, string[] captures, string[] hosts, string? version = null, string? versionHost = null)
        {
            try { guard.Invoke(null, [template, url, relative, current, captures, hosts, version, versionHost]); }
            catch (System.Reflection.TargetInvocationException ex) { throw ex.InnerException!; }
        }

        Assert.Throws<InvalidOperationException>(() => Run("{capture}/p", "https://evil.example/p", true, "https://vendor.example/a", ["//evil.example"], ["vendor.example"]));
        Assert.Throws<InvalidOperationException>(() => Run("https://evil.example/?d={capture0}", "https://evil.example/?d=secret", false, "https://intranet.example/x", ["secret"], ["intranet.example"]));
        Run("https://evil.example/?d={capture0}", "https://evil.example/?d=1.2.3", false, "https://intranet.example/x", ["1.2.3"], ["intranet.example"]);
        Run("{capture}/page.html", "https://vendor.example/p/page.html", true, "https://vendor.example/a", ["/p"], ["vendor.example"]);
    }
    [Fact]
    public void NoAddressInAMessageKeepsItsQuery()
    {
        var text = "curl: (22) The requested URL returned error: 403 https://cdn.contoso.example/agent.msi?sig=ABCDEF&expires=1 and http://a.example/b?x=1#frag";

        var safe = SecretRedactor.WithoutQueries(text);

        Assert.DoesNotContain("ABCDEF", safe);
        Assert.DoesNotContain("expires", safe);
        Assert.Contains("https://cdn.contoso.example/agent.msi", safe);
        Assert.Contains("http://a.example/b", safe);
    }

    [Fact]
    public async Task TheFailureOfADownloadNamesTheAddressWithoutItsQuery()
    {
        var service = new DownloadService();
        var folder = Path.Combine(Path.GetTempPath(), "apd-dl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(
                "https://127.0.0.1:1/agent.msi?sig=QUERYSECRET123", Path.Combine(folder, "agent.msi"), retryCount: 0, retryDelaySec: 0));

            Assert.DoesNotContain("QUERYSECRET123", ex.Message);
            Assert.Contains("https://127.0.0.1:1/agent.msi", ex.Message);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
