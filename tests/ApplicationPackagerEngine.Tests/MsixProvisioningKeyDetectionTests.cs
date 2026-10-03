using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Provisioning an MSIX writes one key per package full name under the
/// all-user store. The full name ends in a publisher id derived from the
/// manifest's Publisher string, so detection can name the key at stage time
/// without a script.
/// </summary>
public class MsixProvisioningKeyDetectionTests
{
    private static StageMsixPackage Package(string arch = "x64") => new()
    {
        Name = "ohmyposh.cli",
        Version = "31.1.1.0",
        Publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        ProcessorArchitecture = arch,
        SignerThumbprint = "AABBCC"
    };

    /// <summary>
    /// Publisher ids Windows itself uses: the Store-signed family suffix and the
    /// inbox-component suffix.
    /// </summary>
    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "8wekyb3d8bbwe")]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "cw5n1h2txyewy")]
    public void PublisherHash_MatchesTheIdWindowsAssigns(string publisher, string expected)
    {
        Assert.Equal(expected, MsixPackageReader.PublisherHash(publisher));
    }

    [Fact]
    public void PublisherHash_IsThirteenCrockfordCharacters()
    {
        var hash = MsixPackageReader.PublisherHash("CN=Contoso");

        Assert.Equal(13, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdefghjkmnpqrstvwxyz"));
        Assert.NotEqual(hash, MsixPackageReader.PublisherHash("CN=Contoso "));
    }

    [Fact]
    public void ProvisioningKey_IsTheFullNameUnderTheAllUserStore()
    {
        var detection = StagingService.BuildMsixProvisioningKeyDetection(Package());

        Assert.Equal("RegistryKey", detection.Type);
        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications\ohmyposh.cli_31.1.1.0_x64__8wekyb3d8bbwe",
            detection.RegistryKeyRelative);
        Assert.Equal("LocalMachine", detection.Hive);
        Assert.True(detection.Is64Bit);
        Assert.Null(detection.ScriptText);
    }

    [Fact]
    public void ProvisioningKey_ReadsTheNativeViewForAnX86PackageToo()
    {
        var detection = StagingService.BuildMsixProvisioningKeyDetection(Package("x86"));

        Assert.EndsWith("_x86__8wekyb3d8bbwe", detection.RegistryKeyRelative);
        Assert.True(detection.Is64Bit);
    }

    [Fact]
    public void ProvisioningKey_WithoutAnArchitecture_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StagingService.BuildMsixProvisioningKeyDetection(Package("")));
        Assert.Contains("ProcessorArchitecture", ex.Message);
    }

    [Fact]
    public void ARegistryKeyDefinition_TakesTheProvisioningKey()
    {
        var detection = StagingService.BuildMsixDetection(new DetectionConfig { Type = "RegistryKey", Is64Bit = true }, Package());

        Assert.Equal("RegistryKey", detection.Type);
    }

    [Theory]
    [InlineData("Script")]
    [InlineData("File")]
    [InlineData("RegistryKeyValue")]
    public void AnyOtherDefinitionType_KeepsTheProvisionedPackageScript(string type)
    {
        var detection = StagingService.BuildMsixDetection(new DetectionConfig { Type = type }, Package());

        Assert.Equal("Script", detection.Type);
        Assert.Contains("Get-AppxProvisionedPackage", detection.ScriptText);
    }

    [Fact]
    public void OhMyPosh_DetectsThroughTheProvisioningKey()
    {
        var def = DefinitionFixture.Load("ohmyposh.json");

        Assert.Equal("MSIX", def.InstallerType);
        Assert.Equal("RegistryKey", def.Detection.Type);
        Assert.Equal("RegistryKey", StagingService.BuildMsixDetection(def.Detection, Package()).Type);
    }

    [Fact]
    public void Slack_KeepsTheProvisionedPackageScript()
    {
        var def = DefinitionFixture.Load("slack.json");

        Assert.Equal("MSIX", def.InstallerType);
        Assert.Equal("Script", StagingService.BuildMsixDetection(def.Detection, Package()).Type);
    }

    [Fact]
    public void TheKeyReachesMecmAsAnExistenceClause()
    {
        var manifest = new StageManifest
        {
            AppName = "Oh My Posh",
            Publisher = "Jan De Dobbeleer",
            SoftwareVersion = "31.1.1",
            InstallerFile = "install-x64.msix",
            InstallerType = "MSIX",
            Detection = StagingService.BuildMsixProvisioningKeyDetection(Package())
        };

        var script = MecmTarget.BuildPackageScript(manifest, manifest.AppName,
            @"\\server\share\Oh My Posh\31.1.1", "P01", "comment", 15, 30);

        Assert.Contains(
            @"New-CMDetectionClauseRegistryKey -Hive LocalMachine -KeyName 'SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications\ohmyposh.cli_31.1.1.0_x64__8wekyb3d8bbwe' -Existence -Is64Bit",
            script);
    }

    [Fact]
    public void TheKeyReachesIntuneAsARegistryExistenceRule()
    {
        var rule = (JsonObject)IntunePublishService.BuildRules(StagingService.BuildMsixProvisioningKeyDetection(Package()))[0]!;

        Assert.Equal("#microsoft.graph.win32LobAppRegistryRule", (string?)rule["@odata.type"]);
        Assert.Equal(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications\ohmyposh.cli_31.1.1.0_x64__8wekyb3d8bbwe",
            (string?)rule["keyPath"]);
        Assert.Equal("exists", (string?)rule["operationType"]);
        Assert.False((bool?)rule["check32BitOn64System"]);
    }
}
