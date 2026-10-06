using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class SevenZipDefinitionTests
{
    private const string ExpectedX64MsiHash = "c0680064d698a62dd4a5a47f403db356a6531a5473e4c4b1d090ea2590513926";

    [Fact]
    public void DefinitionRequiresTheDigestForTheVersionedX64Msi()
    {
        var definition = DefinitionFixture.Load("7zip.json");

        Assert.True(definition.RequirePayloadHash);
        var query = Assert.IsType<string>(definition.VersionScraper.PayloadHashQuery);
        Assert.Contains("7z{version}-x64\\.msi", query);
        Assert.Contains("sha256:", query);
    }

    [Fact]
    public void FeedHashQuerySelectsOnlyTheMatchingX64MsiAsset()
    {
        var definition = DefinitionFixture.Load("7zip.json");
        var release = $$"""
            {
              "assets": [
                { "name": "7z2603-x64.exe", "digest": "sha256:{{new string('1', 64)}}" },
                { "name": "7z2603-x64.msi", "digest": "sha256:{{ExpectedX64MsiHash}}", "uploader": { "login": "ip7z" } },
                { "name": "7z2603.msi", "digest": "sha256:{{new string('2', 64)}}" }
              ]
            }
            """;

        Assert.Equal(ExpectedX64MsiHash, StagingService.ExtractFeedHash(release, definition.VersionScraper, "2603"));
    }

    [Fact]
    public void FeedHashQueryDoesNotBorrowAnotherAssetsDigestWhenTheMsiDigestIsMissing()
    {
        var definition = DefinitionFixture.Load("7zip.json");
        var release = $$"""
            {
              "assets": [
                { "name": "7z2603-x64.msi" },
                { "name": "7z2603.msi", "digest": "sha256:{{new string('2', 64)}}" }
              ]
            }
            """;

        Assert.Null(StagingService.ExtractFeedHash(release, definition.VersionScraper, "2603"));
    }
}
