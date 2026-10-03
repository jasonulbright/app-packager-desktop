using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// One Click packages each row with the snapshot it was staged with, so a row
/// staged under a non-default profile packages the stage under that profile's
/// folder. A snapshot the store cannot create refuses the row with the reason.
/// </summary>
public sealed class OneClickSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-oneclick-snap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static RunSnapshot Snapshot(string profileId) => new()
    {
        ApplicationId = "catalog:contoso",
        ProfileId = profileId,
        BuildId = "20261003-120000-" + profileId.PadLeft(8, '0')[..8]
    };

    [Fact]
    public void ARowStagedWithASnapshotPackagesWithThatSnapshot()
    {
        var staged = new PackagerInfo { Application = "Contoso" };
        var other = new PackagerInfo { Application = "Fabrikam" };
        var pilot = Snapshot("pilot001");
        var rowSnapshots = new Dictionary<PackagerInfo, RunSnapshot> { [staged] = pilot };

        Assert.Same(pilot, PackagesViewModel.SnapshotForRow(staged, null, rowSnapshots));
        Assert.Null(PackagesViewModel.SnapshotForRow(other, null, rowSnapshots));
        var shared = Snapshot("default");
        Assert.Same(shared, PackagesViewModel.SnapshotForRow(other, shared, rowSnapshots));
        Assert.Same(shared, PackagesViewModel.SnapshotForRow(staged, shared, null));
    }

    [Fact]
    public void ASnapshotTheStoreCannotCreateIsARefusalWithTheReason()
    {
        Directory.CreateDirectory(_root);
        var service = new PackagerExecutionService(new SettingsService(), null!, null!, null!);
        var row = new PackagerInfo { Application = "Contoso", IsJsonDefined = true };
        var plan = new PackagerExecutionService.OneClickBuildPlan(_root, "catalog:no-such-application", "pilot001", 3);

        var snapshot = service.CreateOneClickSnapshot(row, plan, out var refusal);

        Assert.Null(snapshot);
        Assert.NotNull(refusal);
        Assert.Contains("pilot001", refusal);
    }
}
