using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

/// <summary>The ids of the built-in sidebar entries, as saved in settings.json.</summary>
public static class SidebarIds
{
    public const string Packages = "packages";
    public const string Applications = "applications";
    public const string AddApp = "add-app";
    public const string Workbench = "workbench";
    public const string DetectionTester = "detection-tester";
    public const string MaintenanceWindows = "maintenance-windows";
}

/// <summary>
/// The sidebar entries in their default order. Settings is a footer entry and is not
/// registered. Ids compare without regard to case.
/// </summary>
public sealed class SidebarRegistry
{
    private readonly List<SidebarItemDefinition> _items = [];

    public IReadOnlyList<SidebarItemDefinition> Items => _items;

    public SidebarItemDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _items.Find(i => string.Equals(i.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Appends an entry. Pinned entries come before every entry that can move, so a pinned
    /// entry registered after one of those is refused.
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
        _items.Add(item);
    }

    public static SidebarRegistry CreateDefault()
    {
        var registry = new SidebarRegistry();
        registry.Register(new SidebarItemDefinition(SidebarIds.Packages, "Packages", "Box24", SidebarItemKind.Page)
        {
            PageType = typeof(PackagesPage),
            IsPinned = true
        });
        registry.Register(new SidebarItemDefinition(SidebarIds.Applications, "Applications", "Apps24", SidebarItemKind.Page) { PageType = typeof(ApplicationsPage) });
        registry.Register(new SidebarItemDefinition(SidebarIds.AddApp, "Add App", "Add24", SidebarItemKind.Page) { PageType = typeof(AddAppPage) });
        registry.Register(new SidebarItemDefinition(SidebarIds.Workbench, "Application Workbench", "Wrench24", SidebarItemKind.Window)
        {
            Activate = services => services.GetRequiredService<MainWindow>().OpenWorkbenchForGridSelection()
        });
        registry.Register(new SidebarItemDefinition(SidebarIds.DetectionTester, "Detection Tester", "Beaker24", SidebarItemKind.Page) { PageType = typeof(DetectionTesterPage) });
        registry.Register(new SidebarItemDefinition(SidebarIds.MaintenanceWindows, "Maintenance Windows", "CalendarClock24", SidebarItemKind.Page)
        {
            PageType = typeof(MaintenanceWindowsPage)
        });
        foreach (var tool in SuiteTools.All)
        {
            registry.Register(new SidebarItemDefinition(tool.Id, tool.Title, tool.Symbol, SidebarItemKind.Tool)
            {
                Activate = services => services.GetRequiredService<MainWindow>().OpenSuiteTool(tool)
            });
        }
        return registry;
    }
}
