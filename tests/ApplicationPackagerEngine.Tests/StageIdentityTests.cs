using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The staged name, publisher and version do not depend on whether 7-Zip could read
/// metadata out of the installer: a stage with the archive service and one without
/// record the same identity for a definition that fixes its name, and the scraped
/// version for every definition.
/// </summary>
public class StageIdentityTests
{
    private static InstallerMetadata BundleMetadata(string name, string version) => new()
    {
        Framework = "WixBurn",
        ProductName = name,
        ProductVersion = version,
        Manufacturer = "Microsoft Corporation",
        ProductCode = "{761A3F2A-8FA8-30EC-AC3F-82A0A1E50FEC}",
        MetadataSource = "inner-msi:AspNetCoreModule.msi"
    };

    [Theory]
    [InlineData("aspnethostingbundle8.json", "8.0.31", "Microsoft .NET 8.0.31 - Windows Server Hosting", "8.0.31.26421", "Microsoft .NET 8.0.31 - Windows Server Hosting", "Microsoft Corporation")]
    [InlineData("aspnethostingbundle10.json", "10.0.12", "Microsoft .NET 10.0.12 - Windows Server Hosting", "10.0.12.26050", "Microsoft .NET 10.0.12 - Windows Server Hosting", "Microsoft Corporation")]
    [InlineData("tableaureader.json", "2026.2.3", "Tableau Reader 2026.2 (20262.26.0912.1023)", "26.2.1954", "Tableau Reader", "Salesforce")]
    [InlineData("jabradirect.json", "7.2.4", "Jabra Direct", "7.2.4.0", "Jabra Direct", "GN Audio A/S")]
    [InlineData("msvcruntimesx64.json", "14.51.36247", "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "14.51.36247.0", "Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "Microsoft Corporation")]
    [InlineData("msvcruntimesx86.json", "14.51.36247", "Microsoft Visual C++ v14 Redistributable (x86) - 14.51.36247", "14.51.36247.0", "Microsoft Visual C++ v14 Redistributable (x86) - 14.51.36247", "Microsoft Corporation")]
    public void AFixedNameDefinitionStagesTheSameIdentityWithAndWithout7Zip(
        string file, string scraped, string metadataName, string metadataVersion, string expectedName, string expectedPublisher)
    {
        var def = DefinitionFixture.Load(file);

        var with7Zip = StagingService.ResolveStageIdentity(def, scraped, def.Vendor, BundleMetadata(metadataName, metadataVersion));
        var without7Zip = StagingService.ResolveStageIdentity(def, scraped, def.Vendor, null);

        Assert.Equal(without7Zip, with7Zip);
        Assert.Equal(new StagingService.StageIdentity(expectedName, expectedName, expectedPublisher, scraped), with7Zip);
    }

    [Fact]
    public void TheScrapedVersionWinsOverTheInnerMsiVersionForEveryDefinition()
    {
        var def = new AppDefinition { Application = "Widget (x64)", CMName = "Widget", Vendor = "Contoso", Architecture = "x64" };

        var identity = StagingService.ResolveStageIdentity(def, "8.0.31", "Contoso", BundleMetadata("Widget", "8.0.31.26421"));

        Assert.Equal("8.0.31", identity.SoftwareVersion);
        Assert.Equal("Widget", identity.AppName);
    }

    [Fact]
    public void AFixedNameIgnoresTheMetadataPublisherAndFallsBackToTheVendor()
    {
        var def = new AppDefinition { Application = "Widget (x64)", Vendor = "Contoso", AppName = "Widget {version}" };

        var identity = StagingService.ResolveStageIdentity(def, "2.0", "Microsoft Corporation", BundleMetadata("Other", "9.9"));

        Assert.Equal(new StagingService.StageIdentity("Widget 2.0", "Widget 2.0", "Contoso", "2.0"), identity);
    }

    [Theory]
    [InlineData("windowsadk.json", true)]
    [InlineData("windowspeaddon.json", true)]
    [InlineData("aspnethostingbundle8.json", false)]
    [InlineData("msvcruntimesx64.json", false)]
    [InlineData("tableaureader.json", false)]
    public void OnlyADefinitionThatResolvesProductCodeTakesTheExtractedOne(string file, bool references) =>
        Assert.Equal(references, StagingService.ReferencesProductCode(DefinitionFixture.Load(file)));
}
