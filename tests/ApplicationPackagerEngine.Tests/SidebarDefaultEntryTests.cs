using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;
public sealed class SidebarDefaultEntryTests
{
    private static readonly string[] ToolIds =
    [
        "detection-tester", "maintenance-windows", "collection-and-compliance", "installer-analysis",
        "deployment-helper", "dp-content-manager", "pivots-and-scripts", "site-hygiene"
    ];

    [Fact]
    public void TheEightToolEntriesFollowTheWorkbenchInTheDefaultOrder()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages, SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench, .. ToolIds], SidebarFixtures.Ids(registry.Items));
        Assert.Equal(
        [
            "Detection Tester", "Maintenance Windows", "Collection and Compliance", "Installer Analysis",
            "Deployment Helper", "DP Content Manager", "Pivots and Scripts", "Site Hygiene"
        ], registry.Items.Skip(4).Select(i => i.Title));
    }

    [Fact]
    public void TheDetectionTesterEntryIsAMovablePageWithItsSavedId()
    {
        var item = SidebarRegistry.CreateDefault().Find("detection-tester")!;
        Assert.Equal(SidebarIds.DetectionTester, item.Id);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DetectionTesterPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Beaker24", item.Symbol);
    }

    [Fact]
    public void TheMaintenanceWindowsEntryIsAMovablePageWithItsSavedIdAndIcon()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("maintenance-windows")!;
        Assert.Equal(SidebarIds.MaintenanceWindows, item.Id);
        Assert.Equal("Maintenance Windows", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.MaintenanceWindowsPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("CalendarClock24", item.Symbol);
        Assert.Equal(5, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void TheCollectionAndComplianceEntryIsAMovablePageWithItsSavedIdIconAndPlace()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("collection-and-compliance")!;
        Assert.Equal(SidebarIds.CollectionCompliance, item.Id);
        Assert.Equal("Collection and Compliance", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.CollectionCompliancePage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("ShieldCheckmark24", item.Symbol);
        Assert.Equal(6, registry.Items.ToList().IndexOf(item));
        Assert.DoesNotContain(registry.Items, i => i.Title == "Collection Manager");
    }

    [Fact]
    public void TheInstallerAnalysisEntryIsAMovablePageWithItsSavedIdIconAndPlace()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("installer-analysis")!;
        Assert.Equal(SidebarIds.InstallerAnalysis, item.Id);
        Assert.Equal("Installer Analysis", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.InstallerAnalysisPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("BoxSearch24", item.Symbol);
        Assert.Equal(7, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void ASavedLayoutWithTheInstallerAnalysisEntryHiddenAndMovedKeepsItAsAPage()
    {
        var layout = new SidebarLayout
        {
            Order = [SidebarIds.Packages, "installer-analysis", SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench, SidebarIds.DetectionTester,
                "maintenance-windows", "collection-and-compliance", "deployment-helper", "dp-content-manager", "pivots-and-scripts", "site-hygiene"],
            Hidden = ["installer-analysis"]
        };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, "installer-analysis", SidebarIds.Applications], SidebarFixtures.Ids(service.All).Take(3));
        Assert.True(service.IsHidden("installer-analysis"));
        Assert.True(service.Show("installer-analysis"));
        Assert.Equal(SidebarItemKind.Page, service.Items[1].Kind);
    }

    [Fact]
    public void TheDeploymentHelperEntryIsAMovablePageWithItsSavedIdAndIcon()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("deployment-helper")!;
        Assert.Equal(SidebarIds.DeploymentHelper, item.Id);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DeploymentHelperPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Rocket24", item.Symbol);
        Assert.Equal(8, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void TheDpContentManagerEntryIsAMovablePageWithItsSavedIdAndIcon()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("dp-content-manager")!;
        Assert.Equal(SidebarIds.DpContentManager, item.Id);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.DpContentManagerPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Server24", item.Symbol);
        Assert.Equal(9, registry.Items.ToList().IndexOf(item));
        Assert.Equal(registry.Items.Count, registry.Items.Select(i => i.Symbol).Distinct().Count());
    }

    [Fact]
    public void ThePivotsAndScriptsEntryIsAMovablePageWithItsSavedIdIconAndPlace()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("pivots-and-scripts")!;
        Assert.Equal(SidebarIds.PivotsScripts, item.Id);
        Assert.Equal("Pivots and Scripts", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.PivotsScriptsPage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Code24", item.Symbol);
        Assert.Equal(10, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void TheSiteHygieneEntryIsAMovablePageWithTheSavedIdIconAndPlaceOfTheTool()
    {
        var registry = SidebarRegistry.CreateDefault();
        var item = registry.Find("site-hygiene")!;
        Assert.Equal(SidebarIds.SiteHygiene, item.Id);
        Assert.Equal("Site Hygiene", item.Title);
        Assert.Equal(SidebarItemKind.Page, item.Kind);
        Assert.Equal(typeof(ApplicationPackagerUI.Views.Pages.SiteHygienePage), item.PageType);
        Assert.Null(item.Activate);
        Assert.False(item.IsPinned);
        Assert.Equal("Broom24", item.Symbol);
        Assert.Equal(11, registry.Items.ToList().IndexOf(item));
    }

    [Fact]
    public void NoSidebarEntryIsAToolAndTheDesktopHasNoSuiteLauncher()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.DoesNotContain(registry.Items, i => i.Kind == SidebarItemKind.Tool);
        Assert.Equal(12, registry.Items.Count);
        var types = typeof(SidebarRegistry).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[] { "SuiteToolLauncher", "SuiteTools", "SuiteTool", "ToolProcessStarter", "IToolProcessStarter" }) Assert.DoesNotContain(name, types);
    }

    [Fact]
    public void ASavedLayoutWithTheDetectionTesterHiddenAndMovedKeepsItsPlace()
    {
        var layout = new SidebarLayout
        {
            Order = [SidebarIds.Packages, "site-hygiene", SidebarIds.DetectionTester, SidebarIds.Applications, SidebarIds.AddApp, SidebarIds.Workbench,
                "maintenance-windows", "collection-and-compliance", "installer-analysis", "deployment-helper", "dp-content-manager", "pivots-and-scripts"],
            Hidden = [SidebarIds.DetectionTester]
        };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, "site-hygiene", SidebarIds.DetectionTester, SidebarIds.Applications], SidebarFixtures.Ids(service.All).Take(4));
        Assert.True(service.IsHidden(SidebarIds.DetectionTester));
        Assert.True(service.Show(SidebarIds.DetectionTester));
        Assert.Equal(SidebarIds.DetectionTester, service.Items[2].Id);
    }

    [Fact]
    public void OnlyPackagesIsPinned()
    {
        var registry = SidebarRegistry.CreateDefault();
        Assert.Equal([SidebarIds.Packages], registry.Items.Where(i => i.IsPinned).Select(i => i.Id));
        Assert.Equal(SidebarIds.Packages, registry.Items[0].Id);
    }

    [Fact]
    public void EveryEntryHasAUniqueIdTitleAndExistingIcon()
    {
        var items = SidebarRegistry.CreateDefault().Items;
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.All(items, i => Assert.True(Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(i.Symbol, out _), i.Symbol));
    }

    [Fact]
    public void ToolEntriesMoveHideAndShowThroughTheService()
    {
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore());
        Assert.True(service.Hide("site-hygiene"));
        Assert.DoesNotContain(service.Items, i => i.Id == "site-hygiene");
        Assert.True(service.Show("site-hygiene"));
        Assert.Equal("site-hygiene", service.Items[^1].Id);
        Assert.True(service.Move("detection-tester", 1));
        Assert.Equal("detection-tester", service.Items[1].Id);
        Assert.Equal(SidebarIds.Packages, service.Items[0].Id);
    }

    [Fact]
    public void ASavedLayoutFromBeforeTheToolsAppendsThemShownAtTheEnd()
    {
        var layout = new SidebarLayout { Order = [SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.Applications, SidebarIds.AddApp], Hidden = [SidebarIds.AddApp] };
        var service = new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(layout));
        Assert.Equal([SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.Applications, .. ToolIds], SidebarFixtures.Ids(service.Items));
    }
}
