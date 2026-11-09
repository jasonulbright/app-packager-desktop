using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The counts that the documentation states about the shipped definitions are computed from the JSON files here, and
/// the test fails when the text drifts from the files (the Detection and Counts tables of the definitions page and
/// the counts of the shipped-content page).
/// </summary>
public sealed class ShippedDefinitionCountsDocTests
{
    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static readonly Lazy<List<JsonElement>> Definitions = new(() =>
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        return Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal)
            .Select(file => JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone()).ToList();
    });

    private static string? Text(JsonElement def, params string[] path)
    {
        var current = def;
        foreach (var name in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static Dictionary<string, int> CountBy(Func<JsonElement, string?> key) =>
        Definitions.Value.Select(key).Where(k => !string.IsNullOrEmpty(k)).GroupBy(k => k!).ToDictionary(g => g.Key, g => g.Count());

    private static readonly string Definitions_md = File.ReadAllText(RepoFile("docs", "core", "definitions.md"));
    private static readonly string ShippedContent_md = File.ReadAllText(RepoFile("docs", "reference", "shipped-content.md"));

    /// <summary>The cells of the table row that starts with <paramref name="first"/>, trimmed.</summary>
    private static string[] Row(string document, string first)
    {
        var line = document.Split('\n').Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith("| " + first + " |", StringComparison.Ordinal));
        Assert.True(line is not null, $"No table row starts with '{first}'.");
        return line!.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>The "`name` count" pairs of a table cell.</summary>
    private static Dictionary<string, int> Pairs(string cell) =>
        Regex.Matches(cell, @"`([^`]+)`\s+(\d+)").ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));

    private static void AssertPairs(string label, Dictionary<string, int> expected, string cell)
    {
        var documented = Pairs(cell);
        Assert.True(expected.OrderBy(p => p.Key).SequenceEqual(documented.OrderBy(p => p.Key)),
            $"{label}: the files give {string.Join(", ", expected.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => $"`{p.Key}` {p.Value}"))} but the page says: {cell}");
    }

    [Fact]
    public void TheDefinitionCountIsStatedWhereverTheDocumentationNamesIt()
    {
        var total = Definitions.Value.Count;

        Assert.Contains($"The release ships {total} definitions.", Definitions_md);
        Assert.Contains($"These counts come from the {total} files.", Definitions_md);
        Assert.Contains($"holds {total} definition files", ShippedContent_md);
        Assert.Equal(total.ToString(), Row(ShippedContent_md, "Application definitions")[1]);
        Assert.Contains($"The catalog has {total} application definitions.", File.ReadAllText(RepoFile("README.md")));
    }

    [Fact]
    public void TheDetectionTableMatchesTheFiles()
    {
        var byType = CountBy(d => Text(d, "Detection", "Type"));
        foreach (var (type, count) in byType)
            Assert.True(Row(Definitions_md, $"`{type}`")[2] == count.ToString(), $"Detection type {type}: the files have {count}, the page says {Row(Definitions_md, $"`{type}`")[2]}.");
        Assert.Equal(6, byType.Count);
    }

    [Fact]
    public void TheCountsTableMatchesTheFiles()
    {
        AssertPairs("InstallerType", CountBy(d => Text(d, "InstallerType")), Row(Definitions_md, "`InstallerType`")[1]);
        AssertPairs("InstallerFramework", CountBy(d => Text(d, "InstallerFramework")), Row(Definitions_md, "`InstallerFramework`")[1]);
        AssertPairs("Architecture", CountBy(d => Text(d, "Architecture")), Row(Definitions_md, "`Architecture`")[1]);

        Assert.Equal(Definitions.Value.Select(d => Text(d, "Vendor")).Where(v => !string.IsNullOrEmpty(v)).Distinct().Count().ToString(), Row(Definitions_md, "Vendors")[1]);
        var userRow = Row(Definitions_md, "`InstallContext` `User`")[1];
        Assert.Equal(Definitions.Value.Count(d => Text(d, "InstallContext") == "User"), int.Parse(Regex.Match(userRow, @"^\d+").Value));

        var none = Definitions.Value.Count(d => Text(d, "ArpName") == "none");
        var pattern = Definitions.Value.Count(d => !string.IsNullOrEmpty(Text(d, "ArpName")) && Text(d, "ArpName") != "none");
        Assert.Equal($"{pattern} / {none}", Row(Definitions_md, "`ArpName` pattern / `none`")[1]);

        Assert.Equal(Definitions.Value.Count(d => !string.IsNullOrEmpty(Text(d, "Cpe"))).ToString(), Row(Definitions_md, "`Cpe`")[1]);

        var yes = Definitions.Value.Count(d => Text(d, "WsusSupport") == "Yes");
        var no = Definitions.Value.Count(d => Text(d, "WsusSupport") is { } w && w != "Yes");
        Assert.Equal($"{yes} / {no}", Row(Definitions_md, "`WsusSupport` `Yes` / `No (...)`")[1]);

        var icon = Row(Definitions_md, "`IconSource`")[1];
        AssertPairs("IconSource", CountBy(d => Text(d, "IconSource")), icon);
        var blank = Definitions.Value.Count(d => string.IsNullOrEmpty(Text(d, "IconSource")));
        Assert.Contains($"{blank} definitions declare no value", icon);

        var settings = Definitions.Value.Count(d => d.TryGetProperty("InstallerSettings", out var s) && s.ValueKind is JsonValueKind.Object or JsonValueKind.Array && s.GetRawText().Length > 2);
        Assert.Equal(settings.ToString(), Row(Definitions_md, "`InstallerSettings`")[1]);
    }

    [Fact]
    public void TheProseCountsMatchTheFiles()
    {
        var total = Definitions.Value.Count;
        var github = Definitions.Value.Count(d => d.GetRawText().Contains("api.github.com", StringComparison.Ordinal));
        var emptyUrl = Definitions.Value.Count(d => string.IsNullOrEmpty(Text(d, "DownloadUrl")));

        Assert.Contains($"{github} of the {total} definitions read `api.github.com`", Definitions_md);
        Assert.Contains($"{emptyUrl} shipped definitions have an empty `DownloadUrl`", Definitions_md);
    }

    [Fact]
    public void TheShippedContentPageMatchesTheFiles()
    {
        var scrapers = CountBy(d => Text(d, "VersionScraper", "Type"));
        foreach (var (type, count) in scrapers)
            Assert.True(Row(ShippedContent_md, $"Version scraper `{type}`")[1] == count.ToString(),
                $"Scraper {type}: the files have {count}, the page says {Row(ShippedContent_md, $"Version scraper `{type}`")[1]}.");
        Assert.Equal(scrapers.Count.ToString(), Row(ShippedContent_md, "Scraper types used by the definitions")[1]);

        var none = Definitions.Value.Count(d => Text(d, "ArpName") == "none");
        var pattern = Definitions.Value.Count(d => !string.IsNullOrEmpty(Text(d, "ArpName")) && Text(d, "ArpName") != "none");
        Assert.Equal(pattern.ToString(), Row(ShippedContent_md, "`ArpName` with a pattern")[1]);
        Assert.Equal(none.ToString(), Row(ShippedContent_md, "`ArpName` set to `none`")[1]);
        Assert.Equal(Definitions.Value.Count(d => !string.IsNullOrEmpty(Text(d, "Cpe"))).ToString(), Row(ShippedContent_md, "With a `Cpe`")[1]);
        Assert.Equal(Definitions.Value.Count(d => d.GetRawText().Contains("api.github.com", StringComparison.Ordinal)).ToString(), Row(ShippedContent_md, "That read `api.github.com`")[1]);
        var settings = Definitions.Value.Count(d => d.TryGetProperty("InstallerSettings", out var s) && s.ValueKind is JsonValueKind.Object or JsonValueKind.Array && s.GetRawText().Length > 2);
        Assert.Equal(settings.ToString(), Row(ShippedContent_md, "With `InstallerSettings`")[1]);
        Assert.Contains($"The {settings} definitions with `InstallerSettings`", ShippedContent_md);
    }
}
