using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Packagers folder resolves from the saved setting or from the folder beside the
/// executable, and from no fixed machine path.
/// </summary>
public sealed class PackagersRootResolutionTests
{
    private const string BaseDirectory = @"D:\apps\packager\";

    [Fact]
    public void TheCandidatesAreTheSavedFolderThenTheFolderBesideTheExecutable()
    {
        var candidates = PackagerDiscoveryService.PackagersRootCandidates(@"E:\scripts", BaseDirectory);

        Assert.Equal([@"E:\scripts", Path.Combine(BaseDirectory, "Packagers")], candidates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptySettingLeavesOnlyTheFolderBesideTheExecutable(string? configured)
    {
        var candidates = PackagerDiscoveryService.PackagersRootCandidates(configured, BaseDirectory);

        Assert.Equal([Path.Combine(BaseDirectory, "Packagers")], candidates);
    }
}
