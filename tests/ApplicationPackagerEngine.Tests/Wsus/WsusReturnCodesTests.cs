using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// WSUS runs the installer itself and has no wrapper that turns the exit codes of the definition into 3010 and 0, so the
/// return codes of the update carry them: a reboot code as a success with a restart, a success code as a success.
/// </summary>
public sealed class WsusReturnCodesTests
{
    private static readonly (int, string, bool)[] Table = [(0, "Succeeded", false), (1707, "Succeeded", false), (3010, "Succeeded", true), (1641, "Succeeded", true)];

    private static StageManifest Exe(Action<StageManifest>? change = null)
    {
        var manifest = new StageManifest { InstallerType = "EXE", InstallerFile = "setup.exe" };
        change?.Invoke(manifest);
        return manifest;
    }

    [Fact]
    public void WithoutExitCodesOfTheDefinitionTheTableIsTheFixedOne() =>
        Assert.Equal(Table, WsusPublishService.ReturnCodesFor(Exe()));

    [Fact]
    public void ARebootCodeBecomesASuccessWithARestartAndASuccessCodeASuccess() =>
        Assert.Equal([.. Table, (2, "Succeeded", true), (1223, "Succeeded", false)],
            WsusPublishService.ReturnCodesFor(Exe(m => { m.RebootExitCodes = [2]; m.SuccessExitCodes = [1223]; })));

    [Fact]
    public void ACodeThatTheTableHasKeepsTheMeaningOfTheTableAndAppearsOnce() =>
        Assert.Equal([.. Table, (2, "Succeeded", true)],
            WsusPublishService.ReturnCodesFor(Exe(m => { m.RebootExitCodes = [3010, 2, 2]; m.SuccessExitCodes = [0, 1641, 2]; })));

    [Fact]
    public void AnMsiIgnoresTheExitCodesOfTheDefinition() =>
        Assert.Equal(Table, WsusPublishService.ReturnCodesFor(Exe(m => { m.InstallerType = "MSI"; m.RebootExitCodes = [2]; })));

    [Fact]
    public void EmptyListsAddNothing() =>
        Assert.Equal(Table, WsusPublishService.ReturnCodesFor(Exe(m => { m.RebootExitCodes = []; m.SuccessExitCodes = []; })));

    // ---- a manifest that was staged before the manifest carried the codes --------------------------------------

    [Fact]
    public void AnOlderManifestWithoutCodesTakesThemFromTheDefinition()
    {
        var manifest = Exe();

        WsusPublishService.CarryExitCodes(manifest, new AppDefinition { RebootExitCodes = [2], SuccessExitCodes = [1223] });

        Assert.Equal([2], manifest.RebootExitCodes);
        Assert.Equal([1223], manifest.SuccessExitCodes);
    }

    [Fact]
    public void AManifestThatHasCodesKeepsThem()
    {
        var manifest = Exe(m => m.RebootExitCodes = [5]);

        WsusPublishService.CarryExitCodes(manifest, new AppDefinition { RebootExitCodes = [2], SuccessExitCodes = [1223] });

        Assert.Equal([5], manifest.RebootExitCodes);
        Assert.Null(manifest.SuccessExitCodes);
    }

    [Fact]
    public void WithoutADefinitionTheManifestStaysAsItIs()
    {
        var manifest = Exe();

        WsusPublishService.CarryExitCodes(manifest, null);

        Assert.Null(manifest.RebootExitCodes);
        Assert.Null(manifest.SuccessExitCodes);
    }
}