using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class ProductSigningCheckTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "apkg-dhsigning-" + Guid.NewGuid().ToString("N"));
    private readonly ApplicationInventoryService _inventory;
    private readonly SigningPolicy _policy = new() { RequireDeployment = true };
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-20T10:00:00Z");

    public ProductSigningCheckTests()
    {
        Directory.CreateDirectory(_folder);
        _inventory = new ApplicationInventoryService(Path.Combine(_folder, "history.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private ProductSigningCheck Check(string site = "MCM") => new(_inventory, () => new ConfigMgrConnection(site, "cm01.contoso.com"), () => _policy);

    private void Publish(string name, string? deploymentStatus, string build = "build-1", string publishedBuild = "build-1")
    {
        var identity = new InventoryApplicationIdentity("uid-" + name, "catalog:" + name, "default", 1);
        var manifest = Path.Combine(_folder, name + "-manifest.json");
        if (deploymentStatus is not null)
            File.WriteAllText(manifest, $$"""{ "ScriptSigning": { "Deployment": { "Status": "{{deploymentStatus}}", "Files": [] } } }""");
        _inventory.RecordStagedBuild(new InventoryStagedBuild(identity, "1.0", build, "build.json", manifest, At));
        _inventory.RecordDestinationOutcome(new InventoryDestinationOutcome("uid-" + name, identity, InventoryEnvironmentIdentity.ConfigMgr("MCM", "cm01.contoso.com"),
            "Publish", "1.0", InventoryOutcome.Succeeded, name, At, publishedBuild));
    }

    [Fact]
    public void ARequiredDeploymentOfAnUnsignedPackagedApplicationIsRefused()
    {
        Publish("Tool", SigningStatus.NotRequested);

        Assert.Contains("Tool", Check().PackagedApplications());
        Assert.Contains("A Required deployment is refused", Check().RequiredDeploymentRefusal("tool"));
    }

    [Theory]
    [InlineData(SigningStatus.SignedAndVerified)]
    [InlineData(SigningStatus.ExistingSignatureValid)]
    public void ASignedBuildIsAllowed(string status)
    {
        Publish("Tool", status);
        Assert.Null(Check().RequiredDeploymentRefusal("Tool"));
    }

    [Fact]
    public void TheRuleDoesNotApplyWithoutThePolicyOrForAnotherApplicationOrSite()
    {
        Publish("Tool", SigningStatus.NotRequested);

        Assert.Null(Check().RequiredDeploymentRefusal("Other"));
        Assert.Empty(Check("XYZ").PackagedApplications());
        Assert.Null(Check("XYZ").RequiredDeploymentRefusal("Tool"));
        _policy.RequireDeployment = false;
        Assert.Null(Check().RequiredDeploymentRefusal("Tool"));
    }

    [Fact]
    public void AMissingManifestOrABuildOtherThanThePublishedOneIsRefusedUnderThePolicy()
    {
        Publish("NoManifest", null);
        Publish("Rebuilt", SigningStatus.SignedAndVerified, build: "build-2", publishedBuild: "build-1");

        Assert.NotNull(Check().RequiredDeploymentRefusal("NoManifest"));
        Assert.NotNull(Check().RequiredDeploymentRefusal("Rebuilt"));
    }
}
