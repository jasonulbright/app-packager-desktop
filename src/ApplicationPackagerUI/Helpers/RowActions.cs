using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerUI.Models;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerUI.Helpers;

public enum RowActionKind
{
    History,
    EditApplication,
    OpenLogFolder,
    OpenStagedFolder,
    OpenNetworkShare,
    CopyLatestVersion,
    OpenVendorUrl,
    ShowCves,
    OpenCveSources,
    CopyCveIds
}

/// <summary>One action on one application. <see cref="NeedsCpe"/> turns the action off for an application without a CPE.</summary>
public sealed record RowAction(RowActionKind Kind, string Header, SymbolRegular Glyph, bool StartsGroup = false, bool NeedsCpe = false)
{
    public string AutomationId => "miRow" + Kind;

    public bool IsEnabledFor(PackagerInfo? row) => row is not null && (!NeedsCpe || row.Cpe.Length > 0);
}

/// <summary>
/// The actions on one application. The context menu of the grid and the More actions menu of the detail pane are both built from this list, so
/// the two always offer the same actions.
/// </summary>
public static class RowActions
{
    public static IReadOnlyList<RowAction> All { get; } =
    [
        new(RowActionKind.History, "View operation history...", SymbolRegular.TextBulletListSquare24),
        new(RowActionKind.EditApplication, "Edit application...", SymbolRegular.Edit24, StartsGroup: true),
        new(RowActionKind.OpenLogFolder, "Open Log Folder", SymbolRegular.DocumentText24, StartsGroup: true),
        new(RowActionKind.OpenStagedFolder, "Open Staged Folder", SymbolRegular.FolderOpen24),
        new(RowActionKind.OpenNetworkShare, "Open Network Share", SymbolRegular.FolderLink24),
        new(RowActionKind.CopyLatestVersion, "Copy Latest Version", SymbolRegular.Copy24, StartsGroup: true),
        new(RowActionKind.OpenVendorUrl, "Open Vendor URL", SymbolRegular.Link24),
        new(RowActionKind.ShowCves, "Show CVEs...", SymbolRegular.ShieldError24, StartsGroup: true),
        new(RowActionKind.OpenCveSources, "Open CVE Sources", SymbolRegular.Globe24, NeedsCpe: true),
        new(RowActionKind.CopyCveIds, "Copy CVE IDs", SymbolRegular.Copy24, NeedsCpe: true)
    ];

    /// <summary>Puts the actions for <paramref name="row"/> into the menu, in place of what it held.</summary>
    /// <param name="run">Runs an action on the row that the menu was built for.</param>
    /// <param name="cveSources">The CVE source links of a row, the submenu of Open CVE Sources.</param>
    public static void Fill(ItemsControl menu, PackagerInfo? row, Action<RowActionKind, PackagerInfo> run,
        Func<PackagerInfo, IReadOnlyList<KeyValuePair<string, string>>> cveSources, Action<string> openLink)
    {
        menu.Items.Clear();
        foreach (var action in All)
        {
            if (action.StartsGroup) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = action.Header, Icon = new SymbolIcon(action.Glyph), IsEnabled = action.IsEnabledFor(row) };
            AutomationProperties.SetAutomationId(item, action.AutomationId);
            if (action.Kind == RowActionKind.OpenCveSources)
            {
                if (item.IsEnabled)
                    foreach (var (name, url) in cveSources(row!))
                    {
                        var source = new MenuItem { Header = name, ToolTip = url };
                        source.Click += (_, _) => openLink(url);
                        item.Items.Add(source);
                    }
            }
            else if (row is not null) item.Click += (_, _) => run(action.Kind, row);
            menu.Items.Add(item);
        }
    }
}
