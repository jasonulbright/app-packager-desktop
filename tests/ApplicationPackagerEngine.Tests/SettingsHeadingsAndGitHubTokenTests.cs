using System.Text.Json;
using System.Xml.Linq;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Settings: the headings in Title Case (decision D-12), the section Post Packaging, and the GitHub token field.</summary>
public sealed class SettingsHeadingsAndGitHubTokenTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // Small words stay lowercase inside a name (D-12); product names keep their own spelling.
    private static readonly string[] SmallWords = ["and", "or", "in", "of", "to", "the", "a", "for", "on", "at", "with"];

    private static XDocument Page() => XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));

    private static List<string> Headings() => Page().Descendants(Presentation + "TextBlock")
        .Where(t => (string?)t.Attribute("Style") is "{StaticResource SectionHeader}" or "{StaticResource SubHeader}")
        .Select(t => (string?)t.Attribute("Text") ?? "")
        .Where(text => text.Length > 0 && !text.StartsWith('{'))
        .ToList();

    internal static string? TitleCaseProblem(string heading)
    {
        var words = heading.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i].TrimEnd(',', ':');
            if (word.Length == 0 || word == "-") continue;
            if (i > 0 && SmallWords.Contains(word, StringComparer.Ordinal)) continue;
            if (i > 0 && SmallWords.Contains(word, StringComparer.OrdinalIgnoreCase)) return $"'{word}' is a small word and stays lowercase";
            if (char.IsLetter(word[0]) && !char.IsUpper(word[0])) return $"'{word}' starts with a lowercase letter";
        }
        return null;
    }

    [Fact]
    public void EveryHeadingOfTheSettingsPageIsInTitleCase()
    {
        var problems = Headings().Select(h => (Heading: h, Problem: TitleCaseProblem(h))).Where(x => x.Problem is not null).Select(x => $"'{x.Heading}': {x.Problem}").ToList();

        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.True(Headings().Count > 20);
    }

    [Theory]
    [InlineData("Systems in Use", null)]
    [InlineData("Updates, Telemetry and Store Policy", null)]
    [InlineData("GitHub API", null)]
    [InlineData("M365 Apps - Channel", null)]
    [InlineData("Systems In Use", "small word")]
    [InlineData("Systems in use", "lowercase letter")]
    [InlineData("Post packaging", "lowercase letter")]
    [InlineData("Updates, telemetry and store policy", "lowercase letter")]
    public void TheTitleCaseRuleFollowsDecisionD12(string heading, string? problem)
    {
        var found = TitleCaseProblem(heading);

        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Fact]
    public void TheSectionAfterPackageIsCalledPostPackaging()
    {
        var headings = Headings();

        Assert.Contains("Post Packaging", headings);
        Assert.DoesNotContain("After Package", headings);
    }

    [Fact]
    public void TheGitHubTokenHasAPasswordFieldInTheGitHubApiSection()
    {
        var page = Page();
        var box = page.Descendants(Presentation + "PasswordBox").Single(e => ((string?)e.Attribute("Password"))?.Contains("GitHubToken") == true);

        Assert.Equal("pwdGitHubToken", (string?)box.Attributes().Single(a => a.Name.LocalName == "AutomationId").Value);
        Assert.Contains(page.Descendants(Presentation + "CheckBox"), c => ((string?)c.Attribute("IsChecked"))?.Contains("RemoveGitHubToken") == true);
    }

    // ---- the stored token ----

    [Fact]
    public void ATokenIsProtectedForTheWindowsUserAndComesBack()
    {
        var protectedText = GitHubTokenStore.Protect("  ghp_example1234567890  ");

        Assert.NotEmpty(protectedText);
        Assert.DoesNotContain("ghp_", protectedText);
        Assert.Equal("ghp_example1234567890", GitHubTokenStore.Unprotect(protectedText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void NothingOrAnUnreadableValueGivesNoToken(string? stored) => Assert.Equal("", GitHubTokenStore.Unprotect(stored));

    [Fact]
    public void BlankIsNeverStored() => Assert.Equal("", GitHubTokenStore.Protect("  "));

    [Fact]
    public void TheSettingsFileHoldsTheProtectedTokenAndNeverThePlaintext()
    {
        var settings = new AppSettings { GitHubTokenProtected = GitHubTokenStore.Protect("ghp_secretvalue") };

        var json = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("ghp_secretvalue", json);
        Assert.Contains("GitHubTokenProtected", json);
    }

    // ---- the order of the sources ----

    [Fact]
    public void TheVariablesComeBeforeTheSavedTokenAndTheSavedTokenBeforeTheCli()
    {
        Func<string, string?> NoEnv = _ => null;
        Func<string, string?> AppVariable = name => name == "APP_PACKAGER_GITHUB_TOKEN" ? "from-app" : null;
        Func<string, string?> GithubVariable = name => name == "GITHUB_TOKEN" ? "from-github" : null;

        Assert.Equal(("from-app", "APP_PACKAGER_GITHUB_TOKEN"), GitHubAuth.ResolveTokenWithSource(AppVariable, () => "from-cli", () => "from-settings"));
        Assert.Equal(("from-github", "GITHUB_TOKEN"), GitHubAuth.ResolveTokenWithSource(GithubVariable, () => "from-cli", () => "from-settings"));
        Assert.Equal(("from-settings", "Settings"), GitHubAuth.ResolveTokenWithSource(NoEnv, () => "from-cli", () => "from-settings"));
        Assert.Equal(("from-cli", "gh auth login"), GitHubAuth.ResolveTokenWithSource(NoEnv, () => "from-cli", () => ""));
        Assert.Equal(("from-cli", "gh auth login"), GitHubAuth.ResolveTokenWithSource(NoEnv, () => "from-cli", null));
        Assert.Equal((null, null), GitHubAuth.ResolveTokenWithSource(NoEnv, () => null, () => null));
    }

    [Theory]
    [InlineData("bad\"token")]
    [InlineData("bad\ntoken")]
    public void ASavedTokenThatCouldBreakTheCurlConfigIsRefusedAndEndsTheSearch(string stored)
    {
        var result = GitHubAuth.ResolveTokenWithSource(_ => null, () => "from-cli", () => stored);

        Assert.Equal((null, "Settings"), result);
    }
}
