using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The ids of the sidebar entries, as saved in settings.json. Applications names an entry
/// that the sidebar no longer has; a saved layout that names it loads without it.
/// </summary>
public static class SidebarIds
{
    public const string Packages = "packages";
    public const string Applications = "applications";
    public const string AddApp = "add-app";
    public const string AddFile = "addfile";
    public const string WsusUpdates = "wsus-updates";
    public const string Workbench = "workbench";
    public const string DetectionTester = "detection-tester";
    public const string MaintenanceWindows = "maintenance-windows";
    public const string CollectionCompliance = "collection-and-compliance";
    public const string InstallerAnalysis = "installer-analysis";
    public const string DeploymentHelper = "deployment-helper";
    public const string DpContentManager = "dp-content-manager";
    public const string PivotsScripts = "pivots-and-scripts";
    public const string SiteHygiene = "site-hygiene";
}

public static class SidebarGroupIds
{
    public const string Tools = "tools";
}

/// <summary>
/// The sidebar groups and entries in their default order. Settings is a footer entry and is
/// not registered. Ids compare without regard to case. The entries of one group are
/// registered one after another, so the registry order is also the group order.
/// </summary>
public sealed class SidebarRegistry
{
    private readonly List<SidebarItemDefinition> _items = [];
    private readonly List<SidebarGroup> _groups = [];

    public IReadOnlyList<SidebarItemDefinition> Items => _items;

    public IReadOnlyList<SidebarGroup> Groups => _groups;

    public SidebarItemDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _items.Find(i => string.Equals(i.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public SidebarGroup? FindGroup(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _groups.Find(g => string.Equals(g.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public void RegisterGroup(SidebarGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.Id))
            throw new InvalidOperationException("A sidebar group needs an id.");
        if (FindGroup(group.Id) is not null)
            throw new InvalidOperationException($"The sidebar already has a group with the id '{group.Id}'.");
        if (string.IsNullOrWhiteSpace(group.Caption) || string.IsNullOrWhiteSpace(group.AutomationName))
            throw new InvalidOperationException($"The sidebar group '{group.Id}' needs a caption and an automation name.");
        _groups.Add(group);
    }

    /// <summary>
    /// Appends an entry. Pinned entries come before every entry that can move, so a pinned
    /// entry registered after one of those is refused. An entry of a group that already has
    /// entries is refused when an entry of another group follows them.
    /// </summary>
    public void Register(SidebarItemDefinition item)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
            throw new InvalidOperationException("A sidebar entry needs an id.");
        if (Find(item.Id) is not null)
            throw new InvalidOperationException($"The sidebar already has an entry with the id '{item.Id}'.");
        if (item.Kind == SidebarItemKind.Page && item.PageType is null)
            throw new InvalidOperationException($"The sidebar page entry '{item.Id}' needs a page type.");
        if (item.Kind != SidebarItemKind.Page && item.Activate is null)
            throw new InvalidOperationException($"The sidebar entry '{item.Id}' needs an action.");
        if (item.IsPinned && _items.Any(i => !i.IsPinned))
            throw new InvalidOperationException($"The pinned sidebar entry '{item.Id}' must be registered before the entries that can move.");
        if (item.Group is not null && FindGroup(item.Group) is null)
            throw new InvalidOperationException($"The sidebar entry '{item.Id}' names the unknown group '{item.Group}'.");
        if (_items.Count > 0 && !SameGroup(_items[^1].Group, item.Group) && _items.Any(i => SameGroup(i.Group, item.Group)))
            throw new InvalidOperationException($"The sidebar entry '{item.Id}' must be registered next to the other entries of its group.");
        _items.Add(item);
    }

    /// <summary>Group ids compare as <see cref="FindGroup"/> looks them up: trimmed and without regard to case.</summary>
    public static bool SameGroup(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static SidebarRegistry CreateDefault()
    {
        var registry = new SidebarRegistry();
        registry.RegisterGroup(new SidebarGroup(Id: SidebarGroupIds.Tools, Caption: "Tools", AutomationName: "Tools group"));

        registry.Register(new SidebarItemDefinition(Id: SidebarIds.Packages, Title: "App Packager", Symbol: "Box24", Kind: SidebarItemKind.Page)
        {
            Description = "App Packager: the list of applications. Check the latest versions, stage the files and publish to ConfigMgr, Intune or WSUS.",
            PageType = typeof(PackagesPage),
            IsPinned = true
        });
        registry.Register(new SidebarItemDefinition(Id: SidebarIds.AddApp, Title: "Add from web", Symbol: "Globe24", Kind: SidebarItemKind.Page)
        {
            Description = "Add an application from a vendor web page: read the version and installer from the page and save a definition.",
            PageType = typeof(AddAppPage)
        });
        registry.Register(new SidebarItemDefinition(Id: SidebarIds.AddFile, Title: "Add from file", Symbol: "DocumentAdd24", Kind: SidebarItemKind.Page)
        {
            Description = "Add an application from a local installer file: analyze the file and save a definition.",
            PageType = typeof(AddFilePage)
        });
        registry.Register(new SidebarItemDefinition(Id: SidebarIds.WsusUpdates, Title: "WSUS Updates", Symbol: "ArrowSyncCircle24", Kind: SidebarItemKind.Window)
        {
            Description = "Open the WSUS Updates window: the locally published updates on your WSUS server, and their approval for a computer group.",
            Activate = services => services.GetRequiredService<ApplicationPackagerUI.ViewModels.Pages.PackagesViewModel>().OpenWsusUpdates()
        });
        registry.Register(new SidebarItemDefinition(Id: SidebarIds.Workbench, Title: "Workbench", Symbol: "Wrench24", Kind: SidebarItemKind.Window)
        {
            Description = "Open the Workbench for the selected application: edit the definition, the detection and the install commands.",
            Activate = services => services.GetRequiredService<MainWindow>().OpenWorkbenchForGridSelection()
        });

        Tool(registry, SidebarIds.InstallerAnalysis, "Installer Analysis", "BoxSearch24", typeof(InstallerAnalysisPage), "Open Installer Analysis: analyze an installer file and see its type, install commands, contents, registry settings and strings.");
        Tool(registry, SidebarIds.DetectionTester, "Detection Tester", "Beaker24", typeof(DetectionTesterPage), "Open Detection Tester: run a detection rule on this computer and see whether it finds the application.");
        Tool(registry, SidebarIds.DeploymentHelper, "Deployment Helper", "Rocket24", typeof(DeploymentHelperPage), "Open Deployment Helper: create deployments of an application with a template or a ring plan, and review their history.");
        Tool(registry, SidebarIds.PivotsScripts, "Pivots and Scripts", "Code24", typeof(PivotsScriptsPage), "Open Pivots and Scripts: run the pivots and the saved scripts against the Device Collections and devices of the site.");
        Tool(registry, SidebarIds.CollectionCompliance, "Collection and Compliance", "ShieldCheckmark24", typeof(CollectionCompliancePage), "Open Collection and Compliance: manage Device Collections and their rules, and review and fix compliance settings.");
        Tool(registry, SidebarIds.MaintenanceWindows, "Maintenance Windows", "CalendarClock24", typeof(MaintenanceWindowsPage), "Open Maintenance Windows: list, create, change and apply maintenance windows of Device Collections.");
        Tool(registry, SidebarIds.DpContentManager, "DP Content Manager", "Server24", typeof(DpContentManagerPage), "Open DP Content Manager: review and fix the content of the distribution points, and its distribution history.");
        Tool(registry, SidebarIds.SiteHygiene, "Site Hygiene", "Broom24", typeof(SiteHygienePage), "Open Site Hygiene: review the checks of the site, the live health data and the findings you can fix.");
        return registry;
    }

    private static void Tool(SidebarRegistry registry, string id, string title, string symbol, Type page, string description) =>
        registry.Register(new SidebarItemDefinition(Id: id, Title: title, Symbol: symbol, Kind: SidebarItemKind.Page)
        {
            PageType = page,
            Description = description,
            Group = SidebarGroupIds.Tools
        });
}
