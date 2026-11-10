using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A replace (OnExisting=Overwrite, or a new version of an application that exists) must leave the application object with the
/// same metadata as a new application would get: the Software Center name from the manifest, and the comment as the description,
/// also when the comment is empty. Without this, an old name or an old comment stays on the application.
/// </summary>
public sealed class ConfigMgrReplaceMetadataTests
{
    private static StageManifest Manifest(string? displayName) => new()
    {
        AppName = "App", DisplayName = displayName, Publisher = "Vendor", SoftwareVersion = "2.0",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "V", ExpectedValue = "2.0" }
    };

    private static string Replace(StageManifest manifest, string comment) =>
        MecmTarget.BuildPackageScript(manifest, "App", @"\\server\share\Apps\Vendor\App\2.0", "P01", comment, 15, 30,
            null, null, null, null, null, OnExistingBehavior.Overwrite);

    private static string ReplaceBlock(string script)
    {
        var at = script.IndexOf("Set-CMApplication -Name 'App' -SoftwareVersion", StringComparison.Ordinal);
        Assert.True(at > 0, "The replace block is in the script.");
        return script[at..script.IndexOf("Updated application version", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void AReplaceWritesTheSoftwareCenterNameFromTheManifest()
    {
        var block = ReplaceBlock(Replace(Manifest("Contoso App Pro"), "a comment"));

        Assert.Contains("-LocalizedApplicationName 'Contoso App Pro'", block);
    }

    [Fact]
    public void AReplaceWithoutADisplayNameLeavesTheSoftwareCenterNameAlone()
    {
        var block = ReplaceBlock(Replace(Manifest(null), "a comment"));

        Assert.DoesNotContain("-LocalizedApplicationName", block);
    }

    [Theory]
    [InlineData("a comment", "-Description 'a comment'")]
    [InlineData("", "-Description ''")]
    [InlineData("   ", "-Description '   '")]
    public void AReplaceWritesTheCommentAsTheDescriptionAlsoWhenItIsEmpty(string comment, string expected)
    {
        var block = ReplaceBlock(Replace(Manifest("Contoso App"), comment));

        Assert.Contains(expected, block);
    }

    [Fact]
    public void ANewApplicationAndAReplaceUseTheSameMetadata()
    {
        var script = Replace(Manifest("Contoso App"), "a comment");
        var create = script[script.IndexOf("New-CMApplication -Name 'App'", StringComparison.Ordinal)..];
        create = create[..create.IndexOf('\n')];

        Assert.Contains("-LocalizedApplicationName 'Contoso App'", create);
        Assert.Contains("-Description 'a comment'", create);
        var block = ReplaceBlock(script);
        Assert.Contains("-LocalizedApplicationName 'Contoso App'", block);
        Assert.Contains("-Description 'a comment'", block);
    }

    [Fact]
    public void QuotesInTheNameAndTheCommentAreEscaped()
    {
        var block = ReplaceBlock(Replace(Manifest("O'Brien's Tool"), "it's a comment"));

        Assert.Contains("-LocalizedApplicationName 'O''Brien''s Tool'", block);
        Assert.Contains("-Description 'it''s a comment'", block);
    }
}