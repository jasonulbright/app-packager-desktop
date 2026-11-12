using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

public class ConfigMgrNativeMsixRemovedTests
{
    private static StageManifest Manifest(string detectionType) => new()
    {
        AppName = "Contoso Msix",
        Publisher = "Contoso",
        SoftwareVersion = "1.0.0.0",
        Detection = new StageDetection { Type = detectionType }
    };

    [Theory]
    [InlineData("MSIX")]
    [InlineData("msix")]
    public void AManifestWithTheNativeMsixDetectionIsRefusedWithTheWayOut(string detectionType)
    {
        var refusal = MecmTarget.NativeMsixRefusal(Manifest(detectionType));

        Assert.NotNull(refusal);
        Assert.Contains("native MSIX", refusal);
        Assert.Contains("script deployment type", refusal);
        Assert.Contains("Stage", refusal);
    }

    [Theory]
    [InlineData("Script")]
    [InlineData("RegistryKey")]
    [InlineData("RegistryKeyValue")]
    [InlineData("File")]
    [InlineData("Compound")]
    public void AnyOtherDetectionIsNotRefused(string detectionType)
    {
        Assert.Null(MecmTarget.NativeMsixRefusal(Manifest(detectionType)));
    }

    [Fact]
    public async Task ThePackageRunStopsBeforeAnyWorkWhenTheManifestAsksForTheNativeMsixType()
    {
        var log = new List<string>();
        var result = await new MecmTarget().PackageAsync(
            Manifest("MSIX"), Path.GetTempPath(), "XYZ", Path.GetTempPath(), "comment", onProgress: log.Add);

        Assert.False(result.Success);
        Assert.Contains("native MSIX", result.ErrorMessage);
        Assert.DoesNotContain(log, l => l.StartsWith("Connecting to ConfigMgr"));
        Assert.DoesNotContain(log, l => l.StartsWith("Network content path"));
    }

    [Fact]
    public void TheNativeMsixScriptBuilderIsGone()
    {
        var methods = typeof(MecmTarget).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(methods, m => m.Name == "BuildMsixMecmScript");
    }
}