using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class PayloadHashVerificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "payload-hash-tests-" + Guid.NewGuid().ToString("N"));

    public PayloadHashVerificationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WritePayload(string content = "packr")
    {
        var path = Path.Combine(_dir, "payload.bin");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MatchingHash_Passes_AndReportsTheDigest()
    {
        var path = WritePayload();
        var actual = StagingService.ComputeSha256(path);

        var log = new List<string>();
        StagingService.VerifyPayloadHash(path, actual, log.Add);

        Assert.Contains(log, l => l.Contains("Payload SHA256 verified") && l.Contains(actual));
    }

    [Fact]
    public void MatchingHash_IsCaseInsensitive()
    {
        var path = WritePayload();
        var actual = StagingService.ComputeSha256(path);

        StagingService.VerifyPayloadHash(path, actual.ToLowerInvariant());
    }

    [Fact]
    public void MismatchedHash_Throws_AndNamesBothDigests()
    {
        var path = WritePayload();
        var wrong = new string('A', 64);

        var ex = Assert.Throws<InvalidOperationException>(
            () => StagingService.VerifyPayloadHash(path, wrong));

        Assert.Contains(StagingService.ComputeSha256(path), ex.Message);
        Assert.Contains(wrong, ex.Message);
    }

    [Fact]
    public void MissingExpectedHash_IsNotAVerification_AndPasses()
    {
        var path = WritePayload();

        var log = new List<string>();
        StagingService.VerifyPayloadHash(path, null, log.Add);
        StagingService.VerifyPayloadHash(path, "   ", log.Add);

        // The run says so: no hash was checked, and the log never reads as a verification.
        Assert.Equal(2, log.Count);
        Assert.All(log, line =>
        {
            Assert.StartsWith("Payload SHA256 NOT verified:", line);
            Assert.DoesNotContain("verified:", line.Replace("NOT verified:", ""));
        });
    }

    [Fact]
    public void MalformedExpectedHash_Throws_RatherThanSilentlySkipping()
    {
        var path = WritePayload();

        Assert.Throws<InvalidOperationException>(
            () => StagingService.VerifyPayloadHash(path, "not-a-digest"));
        // A 63-character digest is a truncated paste, not a shorter algorithm.
        Assert.Throws<InvalidOperationException>(
            () => StagingService.VerifyPayloadHash(path, new string('a', 63)));
    }

    [Fact]
    public void MissingPayload_WithAnExpectedHash_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => StagingService.VerifyPayloadHash(
                Path.Combine(_dir, "absent.bin"), new string('A', 64)));
    }

    [Fact]
    public void ChangedPayload_NoLongerMatchesTheRecordedDigest()
    {
        var path = WritePayload("packr");
        var recorded = StagingService.ComputeSha256(path);

        File.WriteAllText(path, "packr ");

        Assert.Throws<InvalidOperationException>(
            () => StagingService.VerifyPayloadHash(path, recorded));
    }

    [Fact]
    public void TeraCopyFeedShape_CapturesTheHashFromTheNamedGroup()
    {
        const string feed = """
            [Update]
            ProductVersion = 4.0.3.2
            URL = https://example.invalid/teracopy4.0.3.2.exe
            SHA256 = C1A7D43F85F109C663EB57B9A3BFD03F12F27F70D3472C52B5F6D77C537C9547
            MD5 = 7dc94194d57d13dd680c19249c4e820d
            """;
        var config = DefinitionFixture.Load("teracopy.json").VersionScraper;

        Assert.Equal(
            "C1A7D43F85F109C663EB57B9A3BFD03F12F27F70D3472C52B5F6D77C537C9547",
            StagingService.ExtractFeedHash(feed, config, "4.0.3.2"));
    }

    [Fact]
    public void TeraCopyCapture_DoesNotPickUpTheMd5Line()
    {
        // MD5 is 32 hex characters and sits directly below SHA256 in the feed;
        // a loose capture would return it as the payload digest.
        const string feed = "MD5 = 7dc94194d57d13dd680c19249c4e820d";
        var config = DefinitionFixture.Load("teracopy.json").VersionScraper;

        Assert.Null(StagingService.ExtractFeedHash(feed, config, null));
    }

    [Fact]
    public void DevolutionsFeedShape_MatchesTheX64KeyWholeAndNotTheBetaKey()
    {
        const string feed = """
            RDMmsiX64Beta.hash=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
            RDMmsiX64.Version=2026.2.18.0
            RDMmsiX64.hash=939F55ADC51A86D1757380DE23FC442DCF310300935A0A737C4347FC2EEA8882
            """;
        var config = DefinitionFixture.Load("remotedesktopmanager.json").VersionScraper;

        Assert.Equal(
            "939F55ADC51A86D1757380DE23FC442DCF310300935A0A737C4347FC2EEA8882",
            StagingService.ExtractFeedHash(feed, config, "2026.2.18.0"));
    }

    [Fact]
    public void GoIndexShape_SelectsTheWindowsAmd64InstallerHashNotTheArchive()
    {
        const string index = """
            [
              {
                "version": "go1.27.0",
                "stable": true,
                "files": [
                  {"filename":"go1.27.0.windows-amd64.zip","os":"windows","arch":"amd64","kind":"archive",
                   "sha256":"f0c0a0d33ba94f4d2c5dbc887334ce678b21813504ddb3aafcb06e60a5a667c4"},
                  {"filename":"go1.27.0.windows-amd64.msi","os":"windows","arch":"amd64","kind":"installer",
                   "sha256":"728e9318c061b95082bcf167bab8b6c6e0d91a045f89f403b3285fa39f0c455b"}
                ]
              }
            ]
            """;
        var config = DefinitionFixture.Load("go.json").VersionScraper;

        Assert.Equal(
            "728e9318c061b95082bcf167bab8b6c6e0d91a045f89f403b3285fa39f0c455b",
            StagingService.ExtractFeedHash(index, config, "1.27.0"));
    }

    [Fact]
    public void NoHashQuery_YieldsNull_SoStagingSkipsVerification()
    {
        var config = new VersionScraperConfig { Type = "HttpRegex", Url = "https://example.invalid/feed" };

        Assert.Null(StagingService.ExtractFeedHash("SHA256 = deadbeef", config, null));
    }

    [Fact]
    public void FeedWithoutTheHashLine_YieldsNull_RatherThanAFalseDigest()
    {
        var config = DefinitionFixture.Load("teracopy.json").VersionScraper;

        Assert.Null(StagingService.ExtractFeedHash("ProductVersion = 4.0.3.2", config, "4.0.3.2"));
    }

    [Fact]
    public void HashQuery_SubstitutesTheVersionPlaceholder()
    {
        var config = new VersionScraperConfig
        {
            Type = "HttpRegex",
            Url = "https://example.invalid/feed",
            PayloadHashQuery = @"app-{version}\.exe\s+(?<hash>[0-9a-f]{64})"
        };
        var wanted = new string('a', 64);
        var other = new string('b', 64);
        var feed = $"app-1.0.0.exe  {other}\napp-9.9.9.exe  {wanted}";

        Assert.Equal(wanted, StagingService.ExtractFeedHash(feed, config, "9.9.9"));
    }

    [Fact]
    public void RestoredDefinitions_AllDeclareAHashCapture()
    {
        foreach (var name in new[] { "teracopy.json", "remotedesktopmanager.json", "go.json" })
        {
            var def = DefinitionFixture.Load(name);
            Assert.False(string.IsNullOrWhiteSpace(def.VersionScraper.PayloadHashQuery),
                $"{name} lost its payload hash capture.");
        }
    }
}
