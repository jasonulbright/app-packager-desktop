using System.Text.Json;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Max-by-version selection in HttpRegex (SelectHighestVersion) and JsonQuery ([max:field]).
/// Canned fixtures only — no network.
/// </summary>
public class MaxVersionSelectionTests
{
    private const string ListingHtml = """
        <a href="/dl/app-1.2.3-x64.msi">old</a>
        <a href="/dl/app-1.10.0-x64.msi">newest</a>
        <a href="/dl/app-1.9.4-x64.msi">middle</a>
        """;

    private const string Pattern = @"app-(\d+(?:\.\d+)+)-x64\.msi";

    [Fact]
    public void ExtractVersion_FirstMatch_ReturnsPageOrder()
    {
        Assert.Equal("1.2.3", HttpRegexScraper.ExtractVersion(ListingHtml, Pattern, selectHighest: false));
    }

    [Fact]
    public void ExtractVersion_SelectHighest_ReturnsHighestVersion()
    {
        Assert.Equal("1.10.0", HttpRegexScraper.ExtractVersion(ListingHtml, Pattern, selectHighest: true));
    }

    [Fact]
    public void ExtractVersion_SelectHighest_SingleMatchUnchanged()
    {
        const string html = "<a href=\"/dl/app-3.0.1-x64.msi\">only</a>";
        Assert.Equal("3.0.1", HttpRegexScraper.ExtractVersion(html, Pattern, selectHighest: true));
    }

    [Fact]
    public void ExtractVersion_NoMatch_ReturnsNull()
    {
        Assert.Null(HttpRegexScraper.ExtractVersion("nothing here", Pattern, selectHighest: true));
    }

    [Fact]
    public void ExtractVersion_SelectHighest_UnparsableCaptureFallsThrough()
    {
        const string html = "build-latest.msi build-2.4.msi";
        Assert.Equal("2.4", HttpRegexScraper.ExtractVersion(html, @"build-(.+?)\.msi", selectHighest: true));
    }

    // ── JsonQuery [max:field] ───────────────────────────────────────

    private const string ReleasesJson = """
        [
          { "version": "21.0.12+10",  "downloadUrl": "https://example.test/a.msi" },
          { "version": "21.0.12.1+1", "downloadUrl": "https://example.test/b.msi" },
          { "version": "17.0.9",      "downloadUrl": "https://example.test/c.msi" }
        ]
        """;

    private static string? Evaluate(string json, string query)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(JsonQueryScraper).GetMethod(
            "EvaluateQuery",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return method!.Invoke(null, [doc.RootElement, query]) as string;
    }

    [Fact]
    public void JsonQuery_IndexZero_ReturnsFirstElement()
    {
        Assert.Equal("21.0.12+10", Evaluate(ReleasesJson, "[0].version"));
    }

    [Fact]
    public void JsonQuery_MaxVersion_ReturnsHighestElement()
    {
        Assert.Equal("21.0.12.1+1", Evaluate(ReleasesJson, "[max:version].version"));
    }

    [Fact]
    public void JsonQuery_MaxVersion_SelectsSiblingFieldFromSameElement()
    {
        Assert.Equal("https://example.test/b.msi", Evaluate(ReleasesJson, "[max:version].downloadUrl"));
    }

    [Fact]
    public void JsonQuery_MaxVersion_NestedUnderProperty()
    {
        const string json = """
            { "releases": [ { "v": "2.1" }, { "v": "2.10" }, { "v": "2.2" } ] }
            """;
        Assert.Equal("2.10", Evaluate(json, "releases[max:v].v"));
    }

    [Fact]
    public void JsonQuery_MaxVersion_MissingField_ReturnsNull()
    {
        const string json = """[ { "other": 1 } ]""";
        Assert.Null(Evaluate(json, "[max:version].version"));
    }
}
