using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The .intunewin a ConfigMgr run stores on the share sits beside that application's content in both layouts.</summary>
public sealed class IntuneWinNetworkPathTests
{
    private static readonly StageManifest Manifest = new() { AppName = "Contoso Tool", SoftwareVersion = "1.0" };

    [Fact]
    public void NestedStoresItInTheApplicationFolderBesideTheVersionFolder()
    {
        var content = ContentLayouts.ContentPath(@"\\srv\share", ContentLayouts.Nested, "Contoso", "Contoso Tool", "1.0");

        Assert.Equal(@"\\srv\share\Applications\Contoso\Contoso Tool\Contoso_Tool-1.0.intunewin",
            IntuneWinArtifact.NetworkPathFor(Manifest, content, ContentLayouts.Nested));
    }

    [Fact]
    public void FlatStoresItBesideTheFlatFolderUnderTheSameNameNotLooseInTheApplicationsRoot()
    {
        var content = ContentLayouts.ContentPath(@"\\srv\share", ContentLayouts.Flat, "Contoso", "Contoso Tool", "1.0");

        var path = IntuneWinArtifact.NetworkPathFor(Manifest, content, ContentLayouts.Flat);

        Assert.Equal(@"\\srv\share\Applications\Contoso-Contoso Tool-1.0.intunewin", path);
        Assert.NotEqual(@"\\srv\share\Applications\Contoso_Tool-1.0.intunewin", path);
    }
}
