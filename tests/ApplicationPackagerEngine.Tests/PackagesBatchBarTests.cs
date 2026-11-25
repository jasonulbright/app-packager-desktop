using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Helpers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 3b (Q-126): the scope line, the Columns menu and the removal of the per-system publish buttons.</summary>
public sealed class PackagesBatchBarTests
{
    [Theory]
    [InlineData(true, false, false, "Publish All scope: ConfigMgr (Settings, Systems in Use)")]
    [InlineData(true, true, false, "Publish All scope: ConfigMgr and Intune (Settings, Systems in Use)")]
    [InlineData(true, true, true, "Publish All scope: ConfigMgr, Intune and WSUS (Settings, Systems in Use)")]
    [InlineData(false, false, true, "Publish All scope: WSUS (Settings, Systems in Use)")]
    [InlineData(false, false, false, "Publish All scope: no system in use (Settings, Systems in Use)")]
    public void TheScopeLineNamesTheSystemsOfSettings(bool configMgr, bool intune, bool wsus, string expected) =>
        Assert.Equal(expected, PublishAllState.ScopeText(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }));

    [Fact]
    public void AnEmptySavedListShowsEveryColumnAndAnUnknownColumnIsDropped()
    {
        Assert.Empty(PackagesColumns.Clean(null));
        Assert.Empty(PackagesColumns.Clean([]));
        Assert.Equal(["KEV"], PackagesColumns.Clean(["KEV", "Application", "Gone"]));
    }

    [Fact]
    public void HidingAndShowingAColumnKeepsMenuOrderAndNeverHidesAMandatoryColumn()
    {
        var hidden = PackagesColumns.With([], "KEV", visible: false);
        hidden = PackagesColumns.With(hidden, "Vendor", visible: false);
        Assert.Equal(["Vendor", "KEV"], hidden);
        Assert.Equal(["Vendor"], PackagesColumns.With(hidden, "KEV", visible: true));
        Assert.Equal(["Vendor", "KEV"], PackagesColumns.With(hidden, "Application", visible: false));
        Assert.Equal(["Vendor", "KEV"], PackagesColumns.With(hidden, "Status", visible: false));
    }

    [Fact]
    public void TheSavedListRoundTripsThroughTheSettingsFile()
    {
        var settings = new ApplicationPackagerUI.Models.AppSettings { PackagesHiddenColumns = ["Vendor", "KEV"] };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<ApplicationPackagerUI.Models.AppSettings>(json)!;
        Assert.Equal(["Vendor", "KEV"], back.PackagesHiddenColumns);
        Assert.Empty(new ApplicationPackagerUI.Models.AppSettings().PackagesHiddenColumns);
    }
}
