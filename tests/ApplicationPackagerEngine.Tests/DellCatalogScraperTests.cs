using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Component selection from a canned Dell catalog fragment: highest vendorVersion,
/// then newest release date, then the widest supported-model list.
/// The cab download and expansion are not exercised — no network in unit tests.
/// </summary>
public class DellCatalogScraperTests
{
    private const string Pattern = "Dell-Command-Update-Windows-Universal-Application[^\"]*WIN64";

    private static string Component(string version, string releaseDate, string path, int models)
    {
        var modelXml = string.Concat(Enumerable.Range(0, models)
            .Select(i => $"<Model systemID=\"0{i}A\"/>"));

        return $"""
            <SoftwareComponent schemaVersion="2.0" releaseID="X{version}" vendorVersion="{version}"
                               releaseDate="{releaseDate}" path="{path}">
              <SupportedSystems><Brand><Display>Latitude</Display>{modelXml}</Brand></SupportedSystems>
            </SoftwareComponent>
            """;
    }

    [Fact]
    public void SelectBestComponent_PicksHighestVendorVersion()
    {
        var xml = Component("5.6.0", "2026-01-04T00:00:00", "FOLDER1/Dell-Command-Update-Windows-Universal-Application_A_WIN64_5.6.0_A00.EXE", 2)
                + Component("5.7.0", "2026-05-04T00:00:00", "FOLDER2/Dell-Command-Update-Windows-Universal-Application_B_WIN64_5.7.0_A00.EXE", 1);

        var best = DellCatalogScraper.SelectBestComponent(xml, Pattern);

        Assert.NotNull(best);
        Assert.Equal("5.7.0", best!.Value.Version);
        Assert.Contains("FOLDER2", best.Value.Path);
    }

    [Fact]
    public void SelectBestComponent_TieOnVersion_PrefersNewerReleaseDate()
    {
        var xml = Component("5.7.0", "2026-01-04T00:00:00", "OLD/Dell-Command-Update-Windows-Universal-Application_A_WIN64_5.7.0_A00.EXE", 9)
                + Component("5.7.0", "2026-06-04T00:00:00", "NEW/Dell-Command-Update-Windows-Universal-Application_B_WIN64_5.7.0_A00.EXE", 1);

        var best = DellCatalogScraper.SelectBestComponent(xml, Pattern);

        Assert.Contains("NEW", best!.Value.Path);
    }

    [Fact]
    public void SelectBestComponent_TieOnVersionAndDate_PrefersWiderModelList()
    {
        var xml = Component("5.7.0", "2026-06-04T00:00:00", "NARROW/Dell-Command-Update-Windows-Universal-Application_A_WIN64_5.7.0_A00.EXE", 1)
                + Component("5.7.0", "2026-06-04T00:00:00", "WIDE/Dell-Command-Update-Windows-Universal-Application_B_WIN64_5.7.0_A00.EXE", 12);

        var best = DellCatalogScraper.SelectBestComponent(xml, Pattern);

        Assert.Contains("WIDE", best!.Value.Path);
    }

    [Fact]
    public void SelectBestComponent_IgnoresNonMatchingComponents()
    {
        var xml = Component("9.9.9", "2026-06-04T00:00:00", "OTHER/Dell-Command-Update-Windows-32-Application_A_WIN32_9.9.9_A00.EXE", 5)
                + Component("5.7.0", "2026-05-04T00:00:00", "MATCH/Dell-Command-Update-Windows-Universal-Application_B_WIN64_5.7.0_A00.EXE", 1);

        var best = DellCatalogScraper.SelectBestComponent(xml, Pattern);

        Assert.Equal("5.7.0", best!.Value.Version);
        Assert.Contains("MATCH", best.Value.Path);
    }

    [Fact]
    public void SelectBestComponent_NoMatch_ReturnsNull()
    {
        var xml = Component("1.0.0", "2026-06-04T00:00:00", "OTHER/Some-Other-Component_WIN64_1.0.0.EXE", 1);

        Assert.Null(DellCatalogScraper.SelectBestComponent(xml, Pattern));
    }
}
