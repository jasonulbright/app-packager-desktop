using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Ordering rules behind max-by-version selection.
/// </summary>
public class LooseVersionTests
{
    [Theory]
    [InlineData("21.0.12.1+1", "21.0.12+10")]   // longer core outranks build metadata
    [InlineData("1.2.3", "1.2")]
    [InlineData("6.90b", "6.90a")]
    [InlineData("9.10", "8.41a")]
    [InlineData("2.0.0", "2.0.0-rc1")]          // release outranks its prerelease
    [InlineData("1-8-2-4", "1-8-2-2")]          // all-numeric dash tail is more parts
    [InlineData("1-10-0-0", "1-8-2-4")]
    [InlineData("4.2.0-20058", "4.2.0-19999")]
    [InlineData("11,0,32,1", "11,0,32")]        // comma-separated parts
    [InlineData("475", "474")]
    [InlineData("21.0.12+10", "21.0.12+9")]
    public void CompareTo_HigherIsGreater(string higher, string lower)
    {
        var a = LooseVersion.TryParse(higher);
        var b = LooseVersion.TryParse(lower);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(a!.CompareTo(b) > 0, $"{higher} should outrank {lower}");
        Assert.True(b!.CompareTo(a) < 0, $"{lower} should not outrank {higher}");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    public void CompareTo_EquivalentIsZero(string a, string b)
    {
        Assert.Equal(0, LooseVersion.TryParse(a)!.CompareTo(LooseVersion.TryParse(b)));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("Version 6.90a", "6.90a")]
    public void TryParse_IgnoresLeadingDecoration(string decorated, string plain)
    {
        Assert.Equal(0, LooseVersion.TryParse(decorated)!.CompareTo(LooseVersion.TryParse(plain)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData(null)]
    public void TryParse_NoDigits_ReturnsNull(string? text)
    {
        Assert.Null(LooseVersion.TryParse(text));
    }

    [Fact]
    public void SelectHighest_ReturnsOriginalText()
    {
        var result = LooseVersion.SelectHighest(["21.0.12+10", "21.0.12.1+1", "17.0.9"]);
        Assert.Equal("21.0.12.1+1", result);
    }

    [Fact]
    public void SelectHighest_SkipsUnparsableCandidates()
    {
        Assert.Equal("3.1", LooseVersion.SelectHighest(["latest", "3.1", "2.9"]));
    }

    [Fact]
    public void SelectHighest_NoParsableCandidates_ReturnsNull()
    {
        Assert.Null(LooseVersion.SelectHighest(["latest", "stable"]));
    }

    [Fact]
    public void SelectHighest_TieKeepsFirstOccurrence()
    {
        Assert.Equal("1.2", LooseVersion.SelectHighest(["1.2", "1.2.0"]));
    }
}
