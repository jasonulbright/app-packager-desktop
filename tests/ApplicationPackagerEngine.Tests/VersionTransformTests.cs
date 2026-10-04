using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The version transform pipeline (insert_dot, replace, strip_prefix, regex_replace) in
/// the private ScraperRegistry.ApplyTransform, invoked through reflection.
/// </summary>
public class VersionTransformTests
{
    private readonly ScraperRegistry _registry = new();

    private async Task<string?> ApplyTransform(string rawVersion, string transform)
    {
        var method = typeof(ScraperRegistry).GetMethod("ApplyTransform",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [rawVersion, transform]);
        return result as string;
    }

    [Theory]
    [InlineData("2600", "insert_dot:2", "26.00")]
    [InlineData("12345", "insert_dot:1", "1.2345")]
    [InlineData("ABC", "insert_dot:0", ".ABC")]
    public async Task InsertDot_InsertsAtCorrectPosition(string input, string transform, string expected)
    {
        var result = await ApplyTransform(input, transform);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("v1.2.3", "strip_prefix:v", "1.2.3")]
    [InlineData("release-1.0", "strip_prefix:release-", "1.0")]
    [InlineData("1.0", "strip_prefix:v", "1.0")] // no-op when prefix not present
    public async Task StripPrefix_RemovesPrefix(string input, string transform, string expected)
    {
        var result = await ApplyTransform(input, transform);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.2.3.4", "replace:.:_", "1_2_3_4")]
    [InlineData("hello-world", "replace:-:_", "hello_world")]
    public async Task Replace_SubstitutesAll(string input, string transform, string expected)
    {
        var result = await ApplyTransform(input, transform);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.2.3.4", @"regex_replace:\.\d+$", "1.2.3")]
    [InlineData("v2.0.0-beta", @"regex_replace:-\w+$", "v2.0.0")]
    public async Task RegexReplace_AppliesPattern(string input, string transform, string expected)
    {
        var result = await ApplyTransform(input, transform);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task PipelinedTransforms_ApplyInOrder()
    {
        var result = await ApplyTransform("v2600", "strip_prefix:v|insert_dot:2");
        Assert.Equal("26.00", result);
    }

    [Fact]
    public async Task EmptyTransform_ReturnsOriginal()
    {
        var result = await ApplyTransform("1.2.3", "");
        Assert.Equal("1.2.3", result);
    }
}
