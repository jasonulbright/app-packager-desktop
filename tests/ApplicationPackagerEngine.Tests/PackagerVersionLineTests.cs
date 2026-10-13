using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A packager child can write log lines to standard output before its version, so the
/// version is the first line shaped like one.
/// </summary>
public sealed class PackagerVersionLineTests
{
    [Theory]
    [InlineData("1.2.3\r\n", "1.2.3")]
    [InlineData("\r\n  31  \r\n", "31")]
    [InlineData("Analyzing file: C:\\x\\setup.exe\r\n140.17.0\r\n", "140.17.0")]
    [InlineData("1.2.3.4\n9.9.9\n", "1.2.3.4")]
    [InlineData("21.0.12.1+1\n", "21.0.12.1+1")]
    [InlineData("11.0.30-7\n", "11.0.30-7")]
    public void TheFirstLineShapedLikeAVersionIsTheVersion(string stdout, string expected) =>
        Assert.Equal(expected, PackagerExecutionService.VersionLine(stdout));

    [Theory]
    [InlineData("")]
    [InlineData("Error 31 found\r\n")]
    [InlineData("140.17.0esr\r\n")]
    [InlineData("1.2.3.4.5\r\n")]
    [InlineData("v1.2\r\n")]
    public void OutputWithoutAVersionLineHasNoVersion(string stdout) =>
        Assert.Null(PackagerExecutionService.VersionLine(stdout));
}
