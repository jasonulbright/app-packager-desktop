using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Stage tokens resolve parts of the staged version and values read from the
/// payload. Text that carries none of them comes back unchanged.
/// </summary>
public sealed class StageTokenTests
{
    [Theory]
    [InlineData("{version_parts:3}", "4.5.6608.6492", "4.5.6608")]
    [InlineData("{version_parts:4}", "10.0.7", "10.0.7.0")]
    [InlineData("{version_parts:1}", "2026.2.3", "2026")]
    [InlineData("Rtools{version_part:1}{version_part:2}_is1", "4.5.6608.6492", "Rtools45_is1")]
    [InlineData(@"C:\rtools{version_part:1}{version_part:2}\unins000.exe", "4.10.7000.1", @"C:\rtools410\unins000.exe")]
    public void VersionPartTokensReadTheStagedVersion(string text, string version, string expected)
    {
        Assert.Equal(expected, StageTokens.ApplyVersionParts(text, version));
    }

    [Fact]
    public void AMissingVersionPartRefusesTheStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StageTokens.ApplyVersionParts("{version_part:3}", "1.2"));
        Assert.Contains("no part 3", ex.Message);
    }

    [Theory]
    [InlineData("/silent /norestart")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6B7F2C1E-4D8A-4F3B-9E21-5A0C7D3B8E64}_is1")]
    [InlineData("$x = @{ a = 1 }; if ($y) { exit 0 }")]
    [InlineData("{version} {ProductCode} {setting:Port}")]
    [InlineData("")]
    public void TextWithoutAnOwnedTokenIsReturnedUnchanged(string text)
    {
        var tokens = new StageTokens { Version = "1.2.3", ProductVersion = "1.2.3.4", HeaderDisplayVersion = "1.2.3" };
        Assert.Same(text, tokens.Apply(text));
    }

    [Fact]
    public void ProductVersionIsSubstituted()
    {
        Assert.Equal("jdk-21.0.12.1-hotspot", new StageTokens { ProductVersion = "21.0.12.1" }.Apply("jdk-{ProductVersion}-hotspot"));
    }

    [Fact]
    public void AMissingProductVersionRefusesTheStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new StageTokens().Apply("jdk-{ProductVersion}"));
        Assert.Contains("{ProductVersion}", ex.Message);
    }

    [Fact]
    public void AZipTokenThatWasNotReadRefusesTheStage()
    {
        Assert.Throws<InvalidOperationException>(() => new StageTokens().Apply("studio_{zip:StudioBuild}"));
        Assert.Equal("studio_7.28.0.1", new StageTokens { Zip = new Dictionary<string, string> { ["StudioBuild"] = "7.28.0.1" } }
            .Apply("studio_{zip:StudioBuild}"));
    }

    [Fact]
    public void ADetectionWithoutTokensIsBuiltAsBefore()
    {
        var def = new AppDefinition
        {
            Detection = new DetectionConfig
            {
                Type = "RegistryKeyValue",
                RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}",
                PropertyType = "Version",
                Operator = "GreaterEquals"
            }
        };

        var withTokens = StagingService.BuildDetection(def, "{P}", "1.2.3", "1.2.3.4",
            tokens: new StageTokens { Version = "1.2.3", ProductVersion = "9.9.9" });
        var without = StagingService.BuildDetection(def, "{P}", "1.2.3", "1.2.3.4");

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(without), System.Text.Json.JsonSerializer.Serialize(withTokens));
        Assert.Equal("1.2.3", without.ExpectedValue);
    }

    [Fact]
    public void AnExpectedValueTemplateReplacesTheScrapedVersion()
    {
        var def = new AppDefinition
        {
            Detection = new DetectionConfig
            {
                Type = "File",
                FilePath = @"C:\App",
                FileName = "app.exe",
                PropertyType = "Version",
                Operator = "GreaterEquals",
                ExpectedValue = "{version_parts:3}"
            }
        };

        Assert.Equal("4.5.6608", StagingService.BuildDetection(def, null, "4.5.6608.6492", "4.5.6608.6492").ExpectedValue);
    }

    [Fact]
    public void TheDashedDisplayVersionTokenReplacesEveryDot()
    {
        Assert.Equal("https://x/esdalt/2026.2.0/TableauDesktop-64bit-2026-2-0.exe",
            StagingService.ApplyDashedVersion("https://x/esdalt/2026.2.0/TableauDesktop-64bit-{display_version_dashed}.exe", "2026.2.0"));
        Assert.Equal("https://x/{version}.exe", StagingService.ApplyDashedVersion("https://x/{version}.exe", "1.2"));
    }
}
