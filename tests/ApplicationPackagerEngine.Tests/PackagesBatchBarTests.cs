using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Helpers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Overhaul V1 phase 3b (Q-126): the scope line, the Columns menu and the removal of the per-system publish buttons.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PackagesBatchBarTests
{
    [Theory]
    [InlineData(true, false, false, "ConfigMgr \u00b7 Site MCM")]
    [InlineData(true, true, false, "ConfigMgr and Intune \u00b7 Site MCM")]
    [InlineData(true, false, true, "ConfigMgr and WSUS \u00b7 Site MCM")]
    [InlineData(true, true, true, "ConfigMgr, Intune and WSUS \u00b7 Site MCM")]
    [InlineData(false, true, false, "Intune")]
    [InlineData(false, false, true, "WSUS")]
    [InlineData(false, true, true, "Intune and WSUS")]
    [InlineData(false, false, false, "no system in use")]
    public void TheScopeTextNamesTheSystemsAndTheSiteOfConfigMgr(bool configMgr, bool intune, bool wsus, string expected) =>
        Assert.Equal(expected + " \u00b7 from Settings, Systems in Use",
            ScopeText.For(ScopeText.SetOf(new SystemsInUse { ConfigMgr = configMgr, Intune = intune, Wsus = wsus }), ScopeSource.SystemsInUse, "MCM"));

    [Theory]
    [InlineData(ScopeSource.SavedForApplication, "saved for this application", "Saved for this application")]
    [InlineData(ScopeSource.Default, "default", "Default")]
    [InlineData(ScopeSource.ChangedForRun, "changed for this run", "Changed for this run")]
    public void EachOneClickSourceHasItsWords(ScopeSource source, string words, string cell)
    {
        Assert.Equal(words, ScopeText.Words(source));
        Assert.Equal(cell, ScopeText.Cell(source));
        Assert.Equal("WSUS \u00b7 " + words, ScopeText.For(new DestinationSet(false, true, false), source, "MCM"));
    }

    [Fact]
    public void NoSavedChoiceShowsTheColumnsFromBeforeAndAnUnknownColumnIsDropped()
    {
        Assert.Equal(PackagesColumns.Legacy, PackagesColumns.Shown(null));
        Assert.Equal(["ConfigMgr version", "KEV"], PackagesColumns.Shown(["KEV", "Application", "Gone", "Current (ConfigMgr)"]));
    }

    [Fact]
    public void HidingAndShowingAColumnKeepsMenuOrderAndTheApplicationColumnIsNoChoice()
    {
        var shown = PackagesColumns.With(PackagesColumns.Compact, "KEV", visible: true);
        shown = PackagesColumns.With(shown, "Vendor", visible: true);
        Assert.Equal(["ConfigMgr version", "Latest", "Status", "CVE check", "Vendor", "KEV"], shown);
        Assert.Equal(["ConfigMgr version", "Latest", "CVE check", "Vendor", "KEV"], PackagesColumns.With(shown, "Status", visible: false));
        Assert.Equal(shown, PackagesColumns.With(shown, "Application", visible: false));
        Assert.DoesNotContain("Application", PackagesColumns.Toggleable);
        foreach (var column in new[] { "Source", "Intune", "CMName", "Script", "Max EPSS", "NVD updated", "CVEs Checked (UTC)" })
            Assert.Contains(column, PackagesColumns.Toggleable);
    }

    [Fact]
    public void ANewInstallGetsTheCompactColumns()
    {
        var settings = new ApplicationPackagerUI.Models.AppSettings();
        Assert.True(PackagesColumns.Migrate(settings, newInstall: true));
        Assert.Equal(["ConfigMgr version", "Latest", "Status", "CVE check"], settings.PackagesShownColumns);
        Assert.False(PackagesColumns.Migrate(settings, newInstall: true));
    }

    [Fact]
    public void AnUpgradeKeepsTheColumnsThatTheOperatorSawAndTheSavedChoices()
    {
        var plain = new ApplicationPackagerUI.Models.AppSettings();
        PackagesColumns.Migrate(plain, newInstall: false);
        Assert.Equal(PackagesColumns.Legacy.OrderBy(c => c), plain.PackagesShownColumns!.OrderBy(c => c));

        var chosen = new ApplicationPackagerUI.Models.AppSettings
        {
            PackagesHiddenColumns = ["Vendor", "Current (ConfigMgr)", "KEV"],
            PackagesGridLayout = new() { Columns = [new() { Id = "Current (ConfigMgr)", Width = 200 }], SortId = "Current (ConfigMgr)" }
        };
        PackagesColumns.Migrate(chosen, newInstall: false);
        Assert.DoesNotContain("Vendor", chosen.PackagesShownColumns!);
        Assert.DoesNotContain("ConfigMgr version", chosen.PackagesShownColumns!);
        Assert.DoesNotContain("KEV", chosen.PackagesShownColumns!);
        Assert.Contains("CVEs", chosen.PackagesShownColumns!);
        // An earlier build that reads the file finds the choices under the names it knows.
        Assert.Equal(["Vendor", "Current (ConfigMgr)", "KEV"], chosen.PackagesHiddenColumns);
        Assert.Equal("Current (ConfigMgr)", chosen.PackagesGridLayout!.Columns[0].Id);
        var read = PackagesColumns.ReadLayout(chosen.PackagesGridLayout)!;
        Assert.Equal(("ConfigMgr version", 200d, "ConfigMgr version"), (read.Columns[0].Id, read.Columns[0].Width, read.SortId));
        Assert.Equal("Current (ConfigMgr)", PackagesColumns.StoredLayout(read)!.SortId);
    }

    [Fact]
    public void TheHiddenListFollowsALaterChoice()
    {
        var settings = new ApplicationPackagerUI.Models.AppSettings();
        PackagesColumns.Store(settings, PackagesColumns.With(PackagesColumns.Legacy, "Vendor", visible: false));
        Assert.Equal(["Vendor"], settings.PackagesHiddenColumns);
        PackagesColumns.Store(settings, PackagesColumns.Compact);
        Assert.Equal(["Vendor", "CVEs", "Max CVSS", "KEV", "In latest", "Newest CVE"], settings.PackagesHiddenColumns);
    }

    [Fact]
    public void ASettingsFileFromAnEarlierStartIsNotANewInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-newinstall-" + Guid.NewGuid().ToString("N"));
        var saved = Environment.GetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable);
        Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable, root);
        try
        {
            using var scope = ApplicationPackagerEngine.Services.SiteContext.Scope(null, root);
            var first = new ApplicationPackagerUI.Services.SettingsService();
            var settings = first.Load();
            first.Save(settings);
            Assert.False(first.FileExistedAtStart);

            // The operator closed Setup before the end, so FirstRunCompleted is still false.
            var next = new ApplicationPackagerUI.Services.SettingsService();
            Assert.False(next.Load().FirstRunCompleted == true);
            Assert.True(next.FileExistedAtStart);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable, saved);
            TestCleanup.DeleteDirectory(root);
        }
    }

    [Fact]
    public void TheSavedChoiceRoundTripsThroughTheSettingsFile()
    {
        var settings = new ApplicationPackagerUI.Models.AppSettings { PackagesShownColumns = ["Status", "KEV"] };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<ApplicationPackagerUI.Models.AppSettings>(json)!;
        Assert.Equal(["Status", "KEV"], back.PackagesShownColumns);
        Assert.Null(new ApplicationPackagerUI.Models.AppSettings().PackagesShownColumns);
    }

    [Theory]
    [InlineData("3", "cpe:x", "3 found")]
    [InlineData("0", "cpe:x", "0 found")]
    [InlineData("No ConfigMgr version", "cpe:x", "No site version")]
    [InlineData("", "cpe:x", "Not checked")]
    [InlineData("", "", "CPE missing")]
    [InlineData("Error", "cpe:x", "Error")]
    public void TheCveCheckColumnSaysTheStateInOneText(string count, string cpe, string expected) =>
        Assert.Equal(expected, ApplicationPackagerUI.Models.CveCheck.Text(count, cpe));
}
