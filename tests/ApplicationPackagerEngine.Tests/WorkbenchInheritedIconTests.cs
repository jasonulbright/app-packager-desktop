using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class WorkbenchInheritedIconTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly string _pack;
    private readonly string _stageRoot;
    private readonly string _stage;

    public WorkbenchInheritedIconTests()
    {
        _pack = _t.NewFolder("Icons");
        _stageRoot = _t.NewFolder("7-Zip");
        _stage = Path.Combine(_stageRoot, "26.03");
        Directory.CreateDirectory(_stage);
    }

    public void Dispose() => _t.Dispose();

    private void Staged(string iconName = "app-icon.ico")
    {
        _t.NewFile(Path.Combine(_stage, "stage-manifest.json"), "{}");
        _t.NewFile(Path.Combine(_stage, iconName), "x");
    }

    [Fact]
    public void TheIconPackEntry_ComesBeforeTheNewestStagedAppIcon()
    {
        var pack = _t.NewFile(Path.Combine(_pack, "7zip.png"), "x");
        Staged();

        Assert.Equal(pack, WorkbenchInheritedIcon.Resolve(_pack, "7zip", "Installer", _stageRoot));
    }

    [Fact]
    public void TheStagedAppIcon_IsUsedWhenThePackHasNoEntry()
    {
        Staged();

        Assert.Equal(Path.Combine(_stage, "app-icon.ico"), WorkbenchInheritedIcon.Resolve(_pack, "7zip", "Installer", _stageRoot));
    }

    [Fact]
    public void TheNewestStage_Wins()
    {
        Staged();
        File.SetLastWriteTimeUtc(Path.Combine(_stage, "stage-manifest.json"), DateTime.UtcNow.AddDays(-2));
        var newer = Path.Combine(_stageRoot, "26.04");
        _t.NewFile(Path.Combine(newer, "stage-manifest.json"), "{}");
        _t.NewFile(Path.Combine(newer, "app-icon.png"), "x");

        Assert.Equal(Path.Combine(newer, "app-icon.png"), WorkbenchInheritedIcon.Resolve(_pack, "7zip", "Installer", _stageRoot));
    }

    [Fact]
    public void ThePackEntry_IsReturnedWhenNothingIsStaged_IcoBeforePng()
    {
        _t.NewFile(Path.Combine(_pack, "7zip.png"), "x");
        var ico = _t.NewFile(Path.Combine(_pack, "7zip.ico"), "x");

        Assert.Equal(ico, WorkbenchInheritedIcon.Resolve(_pack, "7zip", "Installer", null));
    }

    [Fact]
    public void ALongerPackagerNameSharingThePrefix_DoesNotMatch()
    {
        _t.NewFile(Path.Combine(_pack, "chromeremotedesktophost.png"), "x");

        Assert.Equal("", WorkbenchInheritedIcon.Resolve(_pack, "chrome", "Installer", null));
    }

    [Fact]
    public void AnIconSourceOfNone_TakesThePackEntryButNeverAStagedIcon()
    {
        var pack = _t.NewFile(Path.Combine(_pack, "7zip.png"), "x");
        Assert.Equal(pack, WorkbenchInheritedIcon.Resolve(_pack, "7zip", "None", _stageRoot));

        File.Delete(pack);
        Staged();
        Assert.Equal("", WorkbenchInheritedIcon.Resolve(_pack, "7zip", null, _stageRoot));
    }

    [Fact]
    public void WithoutAStageFolder_NothingIsSearched()
    {
        Staged();

        Assert.Equal("", WorkbenchInheritedIcon.Resolve(_pack, "7zip", "Installer", null));
        Assert.Equal("", WorkbenchInheritedIcon.Resolve(_pack, "7zip", "Installer", Path.Combine(_t.Root, "missing")));
    }

    [Fact]
    public void AnApplicationWithoutAPackagerName_HasNoInheritedIcon()
    {
        Staged();

        Assert.Equal("", WorkbenchInheritedIcon.Resolve(_pack, "", "Installer", _stageRoot));
        Assert.Equal("Inherited from the packager. No staged icon or icon pack entry was found.", WorkbenchInheritedIcon.InheritedStateText(""));
        Assert.Equal(@"Inherited from the packager: C:\x.png", WorkbenchInheritedIcon.InheritedStateText(@"C:\x.png"));
    }
}
