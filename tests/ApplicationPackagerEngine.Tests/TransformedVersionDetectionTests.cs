using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The detection value of a definition whose scraped token is not the version the
/// installed product reports. The stage passes the scraped token beside the version.
/// </summary>
public sealed class TransformedVersionDetectionTests
{
    [Theory]
    [InlineData("ultravnc.json", "1.8.3.0", "1-8-3-0")]
    [InlineData("irfanview.json", "4.73", "473")]
    public void TheDetectionComparesTheTransformedVersion(string file, string version, string scraped)
    {
        var staged = StagingService.BuildDetection(DefinitionFixture.Load(file), null, version, scraped);

        Assert.Equal(version, staged.ExpectedValue);
    }

    [Theory]
    [InlineData("1-8-3-0")]
    [InlineData("v1.8.3.0")]
    [InlineData("1_8_3_0")]
    [InlineData("1830")]
    [InlineData("release/1.8.3.0")]
    public void ARegistryValueDetectionNeverTakesATokenThatIsNotADottedVersion(string scraped)
    {
        var staged = StagingService.BuildDetection(RegistryValue(), null, "1.8.3.0", scraped);

        Assert.Equal("1.8.3.0", staged.ExpectedValue);
    }

    [Theory]
    [InlineData("473")]
    [InlineData("4_73")]
    [InlineData("v4.73")]
    public void AFileVersionDetectionNeverTakesATokenThatIsNotADottedVersion(string scraped)
    {
        var staged = StagingService.BuildDetection(FileVersion(), null, "4.73", scraped);

        Assert.Equal("4.73", staged.ExpectedValue);
    }

    [Fact]
    public void ADottedInstallerVersionStillWinsOverTheDisplayedVersion()
    {
        Assert.Equal("26.1.0", StagingService.BuildDetection(RegistryValue(), null, "2026.1.0", "26.1.0").ExpectedValue);
        Assert.Equal("140.1.79.123", StagingService.BuildDetection(FileVersion(), null, "1.79.123", "140.1.79.123").ExpectedValue);
    }

    private static AppDefinition RegistryValue() => new()
    {
        Application = "Sample",
        Detection = new DetectionConfig
        {
            Type = "RegistryKeyValue",
            RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Sample_is1",
            ValueName = "DisplayVersion",
            Operator = "IsEquals"
        }
    };

    private static AppDefinition FileVersion() => new()
    {
        Application = "Sample",
        Detection = new DetectionConfig
        {
            Type = "File",
            FilePath = @"C:\Program Files\Sample",
            FileName = "sample.exe",
            PropertyType = "Version",
            Operator = "GreaterEquals"
        }
    };
}
