using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A replace (OnExisting=Overwrite) must leave the application object with the Software Center name of the manifest, as a new
/// application gets it, and with the run comment as the description when there is one. An empty comment never clears a
/// description: the command line and One Click pass an empty comment when the operator gave none.
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

    [Fact]
    public void AReplaceWritesTheRunCommentAsTheDescription()
    {
        var block = ReplaceBlock(Replace(Manifest("Contoso App"), "a comment"));

        Assert.Contains("-Description 'a comment'", block);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AReplaceWithoutACommentLeavesTheDescriptionAlone(string comment)
    {
        var block = ReplaceBlock(Replace(Manifest("Contoso App"), comment));

        Assert.DoesNotContain("-Description", block);
    }

    [Fact]
    public void AnEmptyDisplayNameLeavesTheSoftwareCenterNameAlone()
    {
        var block = ReplaceBlock(Replace(Manifest(""), "a comment"));

        Assert.DoesNotContain("-LocalizedApplicationName", block);
    }

    [Fact]
    public void ANewApplicationAndAReplaceUseTheSameNameAndComment()
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