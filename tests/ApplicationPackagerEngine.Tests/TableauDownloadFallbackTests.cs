using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Tableau serves one file under two paths: esdalt and tssoftware. The Tableau definitions download from esdalt and from tssoftware when the
/// host answers 404 for the first address. A 403 is no answer about the file and never switches the path. The Prep definition stays on the
/// version that the Prep page publishes (2026.3, installer 2026-3-0); no point release is derived. No request goes to Tableau.
/// </summary>
public sealed class TableauDownloadFallbackTests
{
    private const string First = "https://downloads.tableau.com/esdalt/tableau_prep/2026.3.0/TableauPrep-2026-3-0.exe";
    private const string Second = "https://downloads.tableau.com/tssoftware/TableauPrep-2026-3-0.exe";

    private static Func<string, Task<UrlState>> Probe(Dictionary<string, UrlState> table, List<string> asked) =>
        url => { asked.Add(url); return Task.FromResult(table[url]); };

    [Theory]
    [InlineData("200", UrlState.Exists)]
    [InlineData("206", UrlState.Exists)]
    [InlineData("404", UrlState.Missing)]
    [InlineData("410", UrlState.Missing)]
    [InlineData("403", UrlState.Unknown)]
    [InlineData("500", UrlState.Unknown)]
    [InlineData("000", UrlState.Unknown)]
    [InlineData("", UrlState.Unknown)]
    [InlineData(null, UrlState.Unknown)]
    public void TheStatusIsReadAsExistsMissingOrUnknown(string? status, UrlState expected) =>
        Assert.Equal(expected, UrlAvailability.Classify(status));

    [Fact]
    public async Task TheFirstAddressIsTakenWhenItExistsAndTheSecondIsNotAsked()
    {
        var asked = new List<string>();
        var url = await UrlAvailability.SelectAsync([First, Second], Probe(new() { [First] = UrlState.Exists, [Second] = UrlState.Exists }, asked));

        Assert.Equal(First, url);
        Assert.Equal([First], asked);
    }

    [Fact]
    public async Task TheSecondAddressIsTakenWhenTheFirstIsReportedMissing()
    {
        var asked = new List<string>();
        var log = new List<string>();
        var url = await UrlAvailability.SelectAsync([First, Second], Probe(new() { [First] = UrlState.Missing, [Second] = UrlState.Exists }, asked), log.Add);

        Assert.Equal(Second, url);
        Assert.Equal([First, Second], asked);
        Assert.Contains(log, l => l.StartsWith("Not on the host (404)", StringComparison.Ordinal) && l.Contains("esdalt"));
    }

    [Fact]
    public async Task A403OnTheFirstAddressNeverSwitchesToTheSecond()
    {
        var asked = new List<string>();
        var log = new List<string>();
        var url = await UrlAvailability.SelectAsync([First, Second], Probe(new() { [First] = UrlState.Unknown, [Second] = UrlState.Exists }, asked), log.Add);

        Assert.Equal(First, url);
        Assert.Equal([First], asked);
        Assert.Contains(log, l => l.Contains("cannot tell"));
    }

    [Fact]
    public async Task EveryAddressMissingStopsWithAClearMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UrlAvailability.SelectAsync([First, Second], Probe(new() { [First] = UrlState.Missing, [Second] = UrlState.Missing }, [])));

        Assert.Contains("not on the host at any of its addresses (404)", ex.Message);
        Assert.Contains("esdalt", ex.Message);
        Assert.Contains("tssoftware", ex.Message);
    }

    [Theory]
    [InlineData("tableaudesktop.json", "TableauDesktop-64bit", "https://downloads.tableau.com/esdalt/2026.3.0/TableauDesktop-64bit-2026-3-0.exe")]
    [InlineData("tableaureader.json", "TableauReader-64bit", "https://downloads.tableau.com/esdalt/2026.3.0/TableauReader-64bit-2026-3-0.exe")]
    [InlineData("tableauprep.json", "TableauPrep", First)]
    public void EveryTableauDefinitionDownloadsFromEsdaltFirstAndTssoftwareSecond(string file, string name, string expectedFirst)
    {
        var def = DefinitionFixture.Load(file);
        static string Fill(string template) => StagingService.ApplyDashedVersion(template.Replace("{display_version}", "2026.3.0"), "2026.3.0");

        Assert.Equal(expectedFirst, Fill(def.DownloadUrl));
        Assert.Equal($"https://downloads.tableau.com/tssoftware/{name}-2026-3-0.exe", Fill(def.FallbackDownloadUrl!));
        Assert.True(def.RequireValidSignature);
    }

    [Fact]
    public void ThePrepDefinitionStaysOnTheVersionThatThePrepPagePublishes()
    {
        var def = DefinitionFixture.Load("tableauprep.json");
        const string prepPage = "<p>Current release: 2026.3</p><ul><li>2026.2</li><li>2026.1</li></ul>";

        Assert.Equal("HttpRegex", def.VersionScraper.Type);
        Assert.Equal("https://www.tableau.com/support/releases/prep", def.VersionScraper.Url);
        Assert.Equal("2026.3", HttpRegexScraper.ExtractVersion(prepPage, def.VersionScraper.Pattern, def.VersionScraper.SelectHighestVersion));
        Assert.Null(def.VersionScraper.Hops);
    }

    [Fact]
    public void ADefinitionWithoutAFallbackDownloadsFromItsOneAddress() =>
        Assert.True(string.IsNullOrWhiteSpace(DefinitionFixture.Load("7zip.json").FallbackDownloadUrl));
}
