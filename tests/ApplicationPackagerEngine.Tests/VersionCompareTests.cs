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

    [Theory]
    [InlineData("31", "31.0")]
    [InlineData("31.0.1", "31.0.1")]
    [InlineData("11.0.30+7", "11.0.30")]
    public void ABareNumberIsPadded(string input, string expected) =>
        Assert.Equal(expected, VersionCompare.Normalize(input));
}
