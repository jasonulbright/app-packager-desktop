using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A bare number such as NetBeans "31" compares as 31.0, a build suffix is dropped, and
/// only the parts both sides carry are compared.
/// </summary>
public sealed class VersionCompareTests
{
    [Theory]
    [InlineData("30", "31", -1)]
    [InlineData("31", "31", 0)]
    [InlineData("31", "30", 1)]
    [InlineData("31", "31.0.1", 0)]
    [InlineData("26.2.2.2", "26.2.2", 0)]
    [InlineData("11.0.30+7", "11.0.31", -1)]
    [InlineData("9", "10", -1)]
    [InlineData("2024a", "2024b", 0)]
    public void VersionsCompareTheWayTheSiteCheckReadsThem(string a, string b, int expected)
    {
        Assert.Equal(expected, VersionCompare.Compare(a, b));
        Assert.Equal(expected, PackagesViewModel.CompareVersions(a, b));
    }

    /// <summary>Each expected value is the result the site check gives for the pair, including unparsable input.</summary>
    [Theory]
    [InlineData("31", "31.0.1", 0)]
    [InlineData("31", "32", -1)]
    [InlineData("31.0.1", "31", 0)]
    [InlineData("26.2.3", "26.2.2.9", 1)]
    [InlineData("1.2-beta", "1.2", 0)]
    [InlineData("1.2+5", "1.2.0", 0)]
    [InlineData("abc", "1.0", 0)]
    [InlineData("1.0", "abc", 0)]
    [InlineData("abc", "def", 0)]
    [InlineData("v1.2", "1.2", 0)]
    [InlineData("1.2.3.4.5", "1.2", 0)]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("10.0", "9.9.9", 1)]
    [InlineData(" 1.2", "1.2", 0)]
    [InlineData("1.2 ", "1.3", -1)]
    [InlineData("2147483648.0", "1.0", 0)]
    [InlineData("1..2", "1.2", 0)]
    [InlineData("1.2.0", "1.2", 0)]
    [InlineData("1", "1.0.0.0", 0)]
    [InlineData("0", "0.1", -1)]
    [InlineData("24.08", "24.8", 0)]
    [InlineData("1.02", "1.2", 0)]
    [InlineData("1.2.3-rc1", "1.2.3", 0)]
    [InlineData("7", "7.0.1", 0)]
    public void ComparesEveryPairAsTheSiteCheckDoes(string a, string b, int expected) =>
        Assert.Equal(expected, VersionCompare.Compare(a, b));

    [Theory]
    [InlineData("31", "31.0")]
    [InlineData("31.0.1", "31.0.1")]
    [InlineData("11.0.30+7", "11.0.30")]
    public void ABareNumberIsPadded(string input, string expected) =>
        Assert.Equal(expected, VersionCompare.Normalize(input));
}
