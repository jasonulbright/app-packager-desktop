using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A Fail run decides an existing application before any content reaches the
/// share, as a Skip run does: the share content of a published application
/// stays as it is.
/// </summary>
public class OnExistingFailProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ap-fail-probe-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData(OnExistingBehavior.Skip, true)]
    [InlineData(OnExistingBehavior.Fail, true)]
    [InlineData(OnExistingBehavior.Overwrite, false)]
    public void SkipAndFailProbeBeforeTheContentCopy(OnExistingBehavior behavior, bool probes)
    {
        Assert.Equal(probes, MecmTarget.ProbesBeforeContentCopy(behavior));
    }

    [Fact]
    public void AFailRunThatFindsTheTitleNamesTheApplicationAndItsVersion()
    {
        Assert.Equal("Existing ConfigMgr application 'Contoso Tool' already exists at version 5.4.1 and OnExisting=Fail was requested.",
            MecmTarget.ExistingApplicationFailMessage("Contoso Tool", "5.4.1"));
    }

    [Theory]
    [InlineData(OnExistingBehavior.Skip)]
    [InlineData(OnExistingBehavior.Fail)]
    public async Task ASkipOrFailRunWhoseProbeCannotReachTheSiteCopiesNothing(OnExistingBehavior behavior)
    {
        var staged = Path.Combine(_root, "stage");
        var share = Path.Combine(_root, "share");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(share);
        File.WriteAllText(Path.Combine(staged, "setup.exe"), "payload");
        var manifest = new StageManifest { AppName = "Contoso Tool", Publisher = "Contoso", SoftwareVersion = "5.4.2", InstallerFile = "setup.exe", InstallerType = "EXE" };

        var result = await new MecmTarget().PackageAsync(manifest, staged, "ZZZ", share, "", providerMachine: "ap-no-such-provider.invalid", onExisting: behavior);

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFileSystemEntries(share));
    }
}
