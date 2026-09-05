using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Stage-time naming takes the installer's own product name only when that
/// name is the product's. The largest EXE inside an NSIS or Inno Setup
/// payload is often a bundled component (a 7-Zip, a GnuPG installer), and
/// naming the MECM application after it collides with the real product.
/// </summary>
public class MetadataNamingTests
{
    private static AppDefinition Def(string application, string cmName) =>
        new() { Application = application, CMName = cmName };

    private static InstallerMetadata Meta(string? name, string source) =>
        new() { Framework = "NSIS", ProductName = name, ProductVersion = "9.9", MetadataSource = source };

    [Fact]
    public void BundledComponentNamesAreRejected()
    {
        Assert.False(StagingService.IsProductNamePlausible(Meta("7-Zip", "primary-exe-version"), Def("KDiff3 (x64)", "KDiff3")));
        Assert.False(StagingService.IsProductNamePlausible(Meta("GnuPG (2.5.21)", "primary-exe-version"), Def("Gpg4win (x64)", "Gpg4win")));
    }

    [Fact]
    public void VendorSpellingsOfTheSameNameAreAccepted()
    {
        Assert.True(StagingService.IsProductNamePlausible(Meta("Notepad++ (64-bit x64)", "primary-exe-version"), Def("Notepad++ (x64)", "Notepad++")));
        Assert.True(StagingService.IsProductNamePlausible(Meta("Git", "innosetup-app-exe"), Def("Git for Windows (x64)", "Git")));
        Assert.True(StagingService.IsProductNamePlausible(Meta("VLC media player", "primary-exe-version"), Def("VLC (x64)", "VLC media player")));
    }

    [Theory]
    [InlineData("product-info.json")]
    [InlineData("BurnManifest-MsiPackage")]
    [InlineData("inner-msi:setup.msi")]
    [InlineData("AppxManifest.xml")]
    public void ManifestBackedSourcesAreTakenAsWritten(string source)
    {
        Assert.True(StagingService.IsProductNamePlausible(Meta("Something Else Entirely", source), Def("KDiff3 (x64)", "KDiff3")));
    }

    [Fact]
    public void EmptyNamesAreNeverPlausible()
    {
        Assert.False(StagingService.IsProductNamePlausible(Meta(null, "primary-exe-version"), Def("KDiff3 (x64)", "KDiff3")));
        Assert.False(StagingService.IsProductNamePlausible(Meta("   ", "inner-msi:x.msi"), Def("KDiff3 (x64)", "KDiff3")));
        Assert.False(StagingService.IsProductNamePlausible(Meta("(x64)", "primary-exe-version"), Def("KDiff3 (x64)", "KDiff3")));
    }
}
