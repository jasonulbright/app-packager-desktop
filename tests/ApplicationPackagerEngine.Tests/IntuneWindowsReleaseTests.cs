using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerEngine.Tests;

public class IntuneWindowsReleaseTests
{
    [Fact]
    public void TheShippedDefaultIsWindows10_21H2AndTheOptionsClassAgrees()
    {
        Assert.Equal("Windows10_21H2", IntuneWindowsReleases.Default);
        Assert.Equal(IntuneWindowsReleases.Default, new IntunePublishOptions().MinimumSupportedWindowsRelease);
        Assert.Equal(IntuneWindowsReleases.Default, new IntuneSettings().MinimumWindowsRelease);
    }

    [Fact]
    public void TheListOffersTheDefaultAndNoDuplicate()
    {
        Assert.Contains(IntuneWindowsReleases.Default, IntuneWindowsReleases.Known);
        Assert.Equal(IntuneWindowsReleases.Known.Count, IntuneWindowsReleases.Known.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(IntuneWindowsReleases.Known, r => Assert.True(IntuneWindowsReleases.IsWellFormed(r), r));
    }

    [Theory]
    [InlineData(null, "Windows10_21H2")]
    [InlineData("", "Windows10_21H2")]
    [InlineData("   ", "Windows10_21H2")]
    [InlineData("Windows11_23H2", "Windows11_23H2")]
    [InlineData("windows11_23h2", "Windows11_23H2")]
    [InlineData("  Windows10_22H2 ", "Windows10_22H2")]
    [InlineData("Windows10_1809", "Windows10_1809")]
    public void AStoredValueResolvesToTheCanonicalSpelling(string? stored, string expected)
    {
        Assert.Equal(expected, IntuneWindowsReleases.Resolve(stored));
    }

    [Theory]
    [InlineData("Windows12_30H1")]
    [InlineData("Windows 11 23H2")]
    [InlineData("Windows11_")]
    [InlineData("'; drop")]
    [InlineData("Windows11_23H2\"}")]
    [InlineData("Windows11_23-H2")]
    public void AValueThatIsNotARelease_FallsBackToTheDefault(string stored)
    {
        Assert.Equal(IntuneWindowsReleases.Default, IntuneWindowsReleases.Resolve(stored));
        Assert.False(IntuneWindowsReleases.IsWellFormed(stored));
    }

    [Fact]
    public void AWellFormedValueThatTheListDoesNotKnow_PassesThroughForGraphToJudge()
    {
        Assert.Equal("Windows11_26H1", IntuneWindowsReleases.Resolve("Windows11_26H1"));
    }

    [Fact]
    public void TheStoredSettingSurvivesASaveAndALoad_AndAnOldFileGetsTheDefault()
    {
        var json = JsonSerializer.Serialize(new IntuneSettings { MinimumWindowsRelease = "Windows11_23H2" });
        Assert.Equal("Windows11_23H2", JsonSerializer.Deserialize<IntuneSettings>(json)!.MinimumWindowsRelease);

        var old = JsonSerializer.Deserialize<IntuneSettings>("""{ "TenantId": "t", "ClientId": "c" }""")!;
        Assert.Equal(IntuneWindowsReleases.Default, old.MinimumWindowsRelease);
    }
}
