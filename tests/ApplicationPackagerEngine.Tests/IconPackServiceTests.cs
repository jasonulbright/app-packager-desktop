using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Checksum parsing and verification, manifest reading, the MinAppVersion gate,
/// and the packager-name to definition-name mapping. No network.
/// </summary>
public sealed class IconPackServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "packr-iconpack-test-" + Guid.NewGuid().ToString("N"));

    public IconPackServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static IconPackEntry Entry(string packager, string? file = null)
        => new() { Packager = packager, File = file ?? packager + ".png", SourceKind = "Installer", PixelSize = 256 };

    // ---- checksums ----

    [Fact]
    public void ParseChecksum_ReadsBinaryMarkerForm()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, IconPackService.ParseChecksum($"{hash} *icon-pack.zip\n", "icon-pack.zip"));
    }

    [Fact]
    public void ParseChecksum_ReadsTwoSpaceForm()
    {
        var hash = new string('B', 64);
        Assert.Equal(hash.ToLowerInvariant(),
            IconPackService.ParseChecksum($"{hash}  icon-pack.zip", "icon-pack.zip"));
    }

    [Fact]
    public void ParseChecksum_PicksTheNamedFileOutOfSeveralLines()
    {
        var wanted = new string('c', 64);
        var text = $"{new string('d', 64)}  other.zip\r\n{wanted}  icon-pack.zip\r\n";
        Assert.Equal(wanted, IconPackService.ParseChecksum(text, "icon-pack.zip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a checksum line")]
    [InlineData("abc  icon-pack.zip")]
    public void ParseChecksum_UnusableTextReturnsNull(string text)
    {
        Assert.Null(IconPackService.ParseChecksum(text, "icon-pack.zip"));
    }

    [Fact]
    public void ParseChecksum_MissingFileNameReturnsNull()
    {
        Assert.Null(IconPackService.ParseChecksum($"{new string('e', 64)}  other.zip", "icon-pack.zip"));
    }

    [Fact]
    public void VerifyChecksum_MatchesTheFileHash()
    {
        var path = Path.Combine(_dir, "payload.bin");
        File.WriteAllText(path, "icon pack bytes");
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("icon pack bytes"))).ToUpperInvariant();

        Assert.True(IconPackService.VerifyChecksum(path, expected));
    }

    [Fact]
    public void VerifyChecksum_DifferentContentFails()
    {
        var path = Path.Combine(_dir, "payload.bin");
        File.WriteAllText(path, "tampered");
        Assert.False(IconPackService.VerifyChecksum(path, new string('f', 64)));
    }

    [Fact]
    public void VerifyChecksum_BlankExpectationFails()
    {
        var path = Path.Combine(_dir, "payload.bin");
        File.WriteAllText(path, "x");
        Assert.False(IconPackService.VerifyChecksum(path, null));
    }

    [Fact]
    public void VerifyChecksum_MissingFileFails()
    {
        Assert.False(IconPackService.VerifyChecksum(Path.Combine(_dir, "absent.bin"), new string('a', 64)));
    }

    // ---- manifest ----

    [Fact]
    public void ReadManifest_ReadsVersionsAndIcons()
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(path, """
        {
          "PackVersion": "1.2.0",
          "MinAppVersion": "1.5.0.0",
          "Icons": [
            { "File": "7zip.png", "Packager": "7zip", "SourceKind": "Installer", "PixelSize": 256 },
            { "File": "notepadplusplus.png", "Packager": "notepadplusplus", "SourceKind": "Vendor", "PixelSize": 128 }
          ]
        }
        """);

        var manifest = IconPackService.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("1.2.0", manifest!.PackVersion);
        Assert.Equal("1.5.0.0", manifest.MinAppVersion);
        Assert.Equal(2, manifest.Icons.Count);
        Assert.Equal(256, manifest.Icons[0].PixelSize);
    }

    [Fact]
    public void ReadManifest_MissingIconsArrayReadsAsEmpty()
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(path, """{ "PackVersion": "1.0.0" }""");

        var manifest = IconPackService.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Icons);
    }

    [Fact]
    public void ReadManifest_MalformedJsonReturnsNull()
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(IconPackService.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_MissingFileReturnsNull()
    {
        Assert.Null(IconPackService.ReadManifest(Path.Combine(_dir, "absent.json")));
    }

    // ---- version gate ----

    [Theory]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("2.0.0", "2.0.0", true)]
    [InlineData("3.0.0", "2.0.0", false)]
    [InlineData("2.2.0.1", "2.2.0.0", false)]
    public void SatisfiesAppVersion_ComparesVersions(string min, string current, bool expected)
    {
        Assert.Equal(expected, IconPackService.SatisfiesAppVersion(min, current));
    }

    [Theory]
    [InlineData(null, "2.0.0")]
    [InlineData("", "2.0.0")]
    [InlineData("alpha", "2.0.0")]
    [InlineData("2.0.0", null)]
    [InlineData("2.0.0", "nightly")]
    public void SatisfiesAppVersion_UnparseableBoundIsSatisfied(string? min, string? current)
    {
        Assert.True(IconPackService.SatisfiesAppVersion(min, current));
    }

    // ---- name normalization ----

    [Theory]
    [InlineData("Dotnet10x64", "dotnet10")]
    [InlineData("dotnet8-x86", "dotnet8")]
    [InlineData("vcredist-both", "vcredist")]
    [InlineData("Notepad++", "notepad")]
    [InlineData("7-Zip", "7zip")]
    [InlineData("", "")]
    public void NormalizeName_StripsCaseSeparatorsAndArchSuffix(string input, string expected)
    {
        Assert.Equal(expected, IconPackService.NormalizeName(input));
    }

    // ---- mapping ----

    [Fact]
    public void MapEntries_ExactNameWins()
    {
        var result = IconPackService.MapEntries(new[] { Entry("adobereader") }, new[] { "adobereader", "adobereaderdc" });

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal("adobereader", mapping.DefinitionBaseName);
        Assert.True(mapping.Exact);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void MapEntries_NormalizedNameMatchesDifferentSpelling()
    {
        var result = IconPackService.MapEntries(new[] { Entry("notepad-plus-plus") }, new[] { "NotepadPlusPlus" });

        var mapping = Assert.Single(result.Mappings);
        Assert.Equal("NotepadPlusPlus", mapping.DefinitionBaseName);
        Assert.False(mapping.Exact);
    }

    [Fact]
    public void MapEntries_ArchCollapsedEntryReachesEveryVariant()
    {
        var result = IconPackService.MapEntries(new[] { Entry("dotnet8") }, new[] { "Dotnet8x64", "Dotnet8x86" });

        Assert.Equal(2, result.Mappings.Count);
        Assert.Equal(new[] { "Dotnet8x64", "Dotnet8x86" }, result.Mappings.Select(m => m.DefinitionBaseName).ToArray());
        Assert.Equal(1, result.MatchedEntries);
        Assert.Equal(1, result.FannedCopies);
    }

    [Fact]
    public void MapEntries_TeamsNewMapsToTeams()
    {
        var result = IconPackService.MapEntries(new[] { Entry("teams-new") }, new[] { "teams" });

        Assert.Equal("teams", Assert.Single(result.Mappings).DefinitionBaseName);
    }

    [Fact]
    public void MapEntries_EntryWithoutDefinitionIsSkipped()
    {
        var result = IconPackService.MapEntries(new[] { Entry("someretiredapp") }, new[] { "7zip" });

        Assert.Empty(result.Mappings);
        Assert.Equal("someretiredapp", Assert.Single(result.Unmatched).Packager);
    }

    [Fact]
    public void MapEntries_FallsBackToTheFileNameWhenPackagerIsBlank()
    {
        var entry = new IconPackEntry { File = "7zip.png", Packager = "" };
        var result = IconPackService.MapEntries(new[] { entry }, new[] { "7zip" });

        Assert.Equal("7zip", Assert.Single(result.Mappings).DefinitionBaseName);
    }

    [Fact]
    public void MapEntries_CountsAcrossAMixedManifest()
    {
        var entries = new[] { Entry("7zip"), Entry("dotnet8"), Entry("teams-new"), Entry("goneapp") };
        var definitions = new[] { "7zip", "Dotnet8x64", "Dotnet8x86", "teams" };

        var result = IconPackService.MapEntries(entries, definitions);

        Assert.Equal(3, result.MatchedEntries);
        Assert.Equal(4, result.Mappings.Count);
        Assert.Single(result.Unmatched);
    }

    // ---- release assets ----

    [Fact]
    public void SelectAssets_ReadsBothDownloadUrls()
    {
        var json = """
        {
          "tag_name": "v1.2.0",
          "assets": [
            { "name": "icon-pack.zip", "browser_download_url": "https://example.invalid/icon-pack.zip" },
            { "name": "checksums.txt", "browser_download_url": "https://example.invalid/checksums.txt" }
          ]
        }
        """;

        var (tag, pack, sums) = IconPackService.SelectAssets(json);

        Assert.Equal("v1.2.0", tag);
        Assert.Equal("https://example.invalid/icon-pack.zip", pack);
        Assert.Equal("https://example.invalid/checksums.txt", sums);
    }

    [Fact]
    public void SelectAssets_ReleaseWithoutAssetsYieldsNulls()
    {
        var (_, pack, sums) = IconPackService.SelectAssets("""{ "tag_name": "v1.2.0", "assets": [] }""");
        Assert.Null(pack);
        Assert.Null(sums);
    }

    [Fact]
    public void SelectAssets_MalformedJsonYieldsNulls()
    {
        var (_, pack, sums) = IconPackService.SelectAssets("not json");
        Assert.Null(pack);
        Assert.Null(sums);
    }

    // ---- status ----

    [Fact]
    public void ReadStatus_ReportsVersionAndInstalledIconCount()
    {
        var icons = IconPackService.IconsDirectory(_dir);
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(icons, "manifest.json"), """{ "PackVersion": "1.2.0", "Icons": [] }""");
        File.WriteAllBytes(Path.Combine(icons, "7zip.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(icons, "teams.png"), new byte[] { 1 });

        var status = IconPackService.ReadStatus(_dir);

        Assert.True(status.Installed);
        Assert.Equal("1.2.0", status.PackVersion);
        Assert.Equal(2, status.IconCount);
        Assert.Contains("2 icons", IconPackService.StatusText(status));
    }

    [Fact]
    public void ReadStatus_NoManifestReadsAsNotInstalled()
    {
        var status = IconPackService.ReadStatus(_dir);

        Assert.False(status.Installed);
        Assert.Contains("Not installed", IconPackService.StatusText(status));
    }

    // ---- install failure paths ----

    [Fact]
    public async Task InstallAsync_MissingDefinitionsFolderReturnsAMessage()
    {
        var result = await new IconPackService().InstallAsync(Path.Combine(_dir, "absent"));

        Assert.False(result.Installed);
        Assert.Contains("Definitions folder not found", result.Message);
    }

    // ---- install from file ----

    /// <summary>A definitions folder holding one definition, plus a pack zip carrying its icon.</summary>
    private (string Definitions, string Zip) BuildPack(string subfolder, string definitionName = "7zip")
    {
        var definitions = Path.Combine(_dir, subfolder, "Definitions");
        Directory.CreateDirectory(definitions);
        File.WriteAllText(Path.Combine(definitions, definitionName + ".json"), "{}");

        var staging = Path.Combine(_dir, subfolder, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "manifest.json"),
            $$"""{ "PackVersion": "1.3.0", "Icons": [ { "File": "{{definitionName}}.png", "Packager": "{{definitionName}}" } ] }""");
        File.WriteAllBytes(Path.Combine(staging, definitionName + ".png"), new byte[] { 1, 2, 3 });

        var source = Path.Combine(_dir, subfolder, "source");
        Directory.CreateDirectory(source);
        var zip = Path.Combine(source, IconPackService.PackAssetName);
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);
        return (definitions, zip);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    [Fact]
    public async Task InstallFromFileAsync_MissingFileReturnsAMessage()
    {
        var result = await new IconPackService().InstallFromFileAsync(Path.Combine(_dir, "absent.zip"), _dir);

        Assert.False(result.Installed);
        Assert.Contains("Icon pack file not found", result.Message);
    }

    [Fact]
    public async Task InstallFromFileAsync_ChecksumsBesideTheZipAreVerified()
    {
        var (definitions, zip) = BuildPack("verified");
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(zip)!, IconPackService.ChecksumAssetName),
            $"{Sha256Of(zip)} *{IconPackService.PackAssetName}\n");

        var result = await new IconPackService().InstallFromFileAsync(zip, definitions);

        Assert.True(result.Installed);
        Assert.DoesNotContain("not verified", result.Message);
        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(IconPackService.IconsDirectory(definitions), "7zip.png")));
    }

    [Fact]
    public async Task InstallFromFileAsync_NoChecksumsInstallsWithAnUnverifiedNote()
    {
        var (definitions, zip) = BuildPack("unverified");

        var result = await new IconPackService().InstallFromFileAsync(zip, definitions);

        Assert.True(result.Installed);
        Assert.Contains("Checksum not verified", result.Message);
        Assert.True(File.Exists(Path.Combine(IconPackService.IconsDirectory(definitions), "7zip.png")));
    }

    [Fact]
    public async Task InstallFromFileAsync_ChecksumMismatchExtractsNothing()
    {
        var (definitions, zip) = BuildPack("mismatch");
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(zip)!, IconPackService.ChecksumAssetName),
            $"{new string('a', 64)} *{IconPackService.PackAssetName}\n");

        var result = await new IconPackService().InstallFromFileAsync(zip, definitions);

        Assert.False(result.Installed);
        Assert.Contains("checksum did not match", result.Message);
        Assert.False(Directory.Exists(IconPackService.IconsDirectory(definitions)));
    }
}
