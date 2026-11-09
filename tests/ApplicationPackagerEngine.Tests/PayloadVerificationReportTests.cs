using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The page that lists which definitions verify their payload is generated from the shipped definitions. These tests
/// build the page and compare it with the committed file. To write the file again after a definition changed, run this
/// class with the environment variable APD_WRITE_PAYLOAD_REPORT=1.
/// </summary>
public sealed class PayloadVerificationReportTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static List<PayloadVerificationReport.Entry> Shipped() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(file => (File: Path.GetFileName(file), Def: JsonSerializer.Deserialize<AppDefinition>(System.IO.File.ReadAllText(file), Options)!))
            .Select(item => PayloadVerificationReport.Describe(item.File, item.Def)).ToList();

    [Fact]
    public void TheCommittedPageMatchesTheShippedDefinitions()
    {
        var expected = PayloadVerificationReport.Build(Shipped());
        var path = RepoFile("docs", "reference", "payload-verification.md");
        if (Environment.GetEnvironmentVariable("APD_WRITE_PAYLOAD_REPORT") == "1")
            System.IO.File.WriteAllText(path, expected.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));

        var actual = System.IO.File.ReadAllText(path).Replace("\r\n", "\n");

        Assert.True(expected == actual,
            "docs/reference/payload-verification.md differs from the shipped definitions. Run PayloadVerificationReportTests with APD_WRITE_PAYLOAD_REPORT=1 and commit the file.");
    }

    [Fact]
    public void ADefinitionIsDescribedByTheFieldsItSets()
    {
        var def = new AppDefinition
        {
            Application = "Probe", PayloadSha256 = new string('A', 64), RequireValidSignature = true,
            VersionScraper = new VersionScraperConfig { PayloadHashQuery = "$.sha" }, PayloadSha256Url = "https://example.test/sums.txt"
        };

        var entry = PayloadVerificationReport.Describe("probe.json", def);

        Assert.True(entry.PinnedHash && entry.FeedHash && entry.ChecksumFile && entry.RequireSignature && entry.HasHashSource);
        Assert.False(entry.RequireHash);
        var bare = PayloadVerificationReport.Describe("bare.json", new AppDefinition { Application = "Bare" });
        Assert.False(bare.HasHashSource || bare.RequireSignature);
    }

    [Fact]
    public void ThePageCountsTheDefinitionsWithNeitherAHashSourceNorASignature()
    {
        var entries = new[]
        {
            new PayloadVerificationReport.Entry("a.json", "A", true, false, false, false, false),
            new PayloadVerificationReport.Entry("b.json", "B", false, false, false, false, true),
            new PayloadVerificationReport.Entry("c.json", "C", false, false, false, false, false),
            new PayloadVerificationReport.Entry("d.json", "D", false, false, false, false, false)
        };

        var page = PayloadVerificationReport.Build(entries);

        Assert.Contains("| Shipped definitions | 4 |", page);
        Assert.Contains("| With neither a SHA-256 source nor a required signature (file-type check only) | 2 |", page);
        Assert.Contains("| `a.json` | Yes |", page);
        Assert.Contains("- `b.json`", page);
    }
}
