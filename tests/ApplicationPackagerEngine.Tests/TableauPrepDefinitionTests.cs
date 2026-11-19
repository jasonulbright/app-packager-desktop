using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Tableau Prep Builder. The Prep release page names the release train only (2026.3); the page of Desktop and Reader lists the
/// point release (2026.3.1). The definition reads the train from the Prep page and the newest point release of that train from
/// the second page, and downloads from the esdalt/tableau_prep path that the Tableau download page uses. The pages here are small
/// fixtures of their shape; no request goes to Tableau.
/// </summary>
public sealed class TableauPrepDefinitionTests
{
    private const string PrepPage = "<h1>Tableau Prep release notes</h1><p>Current release: 2026.3</p><ul><li><a href=\"/p/2026.2\">2026.2</a></li><li>2026.1</li><li>2025.3</li></ul>";
    private const string TrainPage = "<h1>Release notes</h1><ul><li>2026.3.1</li><li>2026.2.3</li><li>2026.2.2</li><li>2026.1.4</li></ul>";

    private static (string? Version, string? Train) Read(string prepPage, string trainPage)
    {
        var hops = DefinitionFixture.Load("tableauprep.json").VersionScraper.Hops!;
        var first = ChainedHttpRegexScraper.SelectMatch(prepPage, hops[0], []);
        if (first is null) return (null, null);
        var train = first.Groups["capture"].Value;
        var second = ChainedHttpRegexScraper.SelectMatch(trainPage, hops[1], [train]);
        return (second?.Groups["version"].Value, train);
    }

    [Fact]
    public void ThePatchOfTheTrainComesFromThePageOfDesktopAndReader()
    {
        var (version, train) = Read(PrepPage, TrainPage);

        Assert.Equal("2026.3", train);
        Assert.Equal("2026.3.1", version);
    }

    [Fact]
    public void ANewerPatchOnThePageWinsOverAnOlderOne()
    {
        var (version, _) = Read(PrepPage, "<li>2026.3.1</li><li>2026.3.2</li><li>2026.3.10</li><li>2026.3</li>");
        Assert.Equal("2026.3.10", version);
    }

    [Fact]
    public void ThePatchOfAnotherTrainIsNeverTaken()
    {
        var (version, train) = Read(PrepPage, "<li>2026.2.9</li><li>2026.31.4</li><li>2026.30.1</li>");

        Assert.Equal("2026.3", train);
        Assert.Null(version);
    }

    [Fact]
    public void AnOlderTrainThanThePrepPageNamesIsNotTheTrain()
    {
        var (_, train) = Read("<p>2025.3</p><p>2026.1</p><p>2025.4.2</p>", TrainPage);
        Assert.Equal("2026.1", train);
    }

    [Fact]
    public void WhenThePageOfDesktopNamesTheTrainOnlyTheVersionIsTheTrainAndTheTransformAddsPatchZero()
    {
        var (version, _) = Read(PrepPage, "<li>2026.3</li><li>2026.2.3</li>");
        Assert.Equal("2026.3", version);

        var transform = DefinitionFixture.Load("tableauprep.json").VersionScraper.VersionTransform!;
        Assert.StartsWith("regex_replace:", transform);
        Assert.Equal("2026.3.0", ScraperRegistry.ApplyTransform("2026.3", transform));
        Assert.Equal("2026.3.1", ScraperRegistry.ApplyTransform("2026.3.1", transform));
    }

    [Fact]
    public void AQuantifierInAPatternIsNotAPlaceholder_AndACaptureIsEscaped()
    {
        Assert.Equal(@"\d{2}-2026\.3", ChainedHttpRegexScraper.SubstitutePattern(@"\d{2}-{capture}", ["2026.3"]));
        Assert.Equal(@"a-1\.5\+x-2026\.3", ChainedHttpRegexScraper.SubstitutePattern("a-{capture0}-{capture1}", ["1.5+x", "2026.3"]).Replace("-{", "-{"));
    }

    [Fact]
    public void TheDownloadAddressIsTheEsdaltPathWithTheDottedReleaseInTheFolderAndTheHyphenatedOneInTheFile()
    {
        var def = DefinitionFixture.Load("tableauprep.json");
        var url = StagingService.ApplyDashedVersion(def.DownloadUrl.Replace("{display_version}", "2026.3.1"), "2026.3.1");

        Assert.Equal("https://downloads.tableau.com/esdalt/tableau_prep/2026.3.1/TableauPrep-2026-3-1.exe", url);
        Assert.DoesNotContain("tssoftware", def.DownloadUrl);
    }

    [Fact]
    public void TheDefinitionKeepsTheBrowserHeadersForBothPagesAndTheSignatureCheck()
    {
        var def = DefinitionFixture.Load("tableauprep.json");

        Assert.Equal("ChainedHttpRegex", def.VersionScraper.Type);
        Assert.Equal(2, def.VersionScraper.Hops!.Count);
        Assert.Contains(def.VersionScraper.RequestHeaders!, h => h.StartsWith("User-Agent: Mozilla/5.0", StringComparison.Ordinal));
        Assert.Contains(def.VersionScraper.RequestHeaders!, h => h.StartsWith("Referer: https://www.tableau.com/", StringComparison.Ordinal));
        Assert.True(def.RequireValidSignature);
    }
}
