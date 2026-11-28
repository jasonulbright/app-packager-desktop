using System.Windows;
using System.Windows.Controls;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Windows;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerUI.Controls;

/// <summary>The setup checklist beside the grid of the App Packager page. Its data context is the checklist view model.</summary>
public partial class SetupChecklistPanel
{
    public SetupChecklistPanel() => InitializeComponent();

    private SetupChecklistViewModel? Checklist => DataContext as SetupChecklistViewModel;

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    /// <summary>The button of a step: the settings page that finishes it, or the scan of the discovery step.</summary>
    private void StepAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SetupStepItem step }) return;
        switch (step.Key)
        {
            case SetupStepKey.Site: Main?.OpenSettingsPage("configmgr"); break;
            case SetupStepKey.Folders: Main?.OpenSettingsPage("folders"); break;
            case SetupStepKey.Discovery: Checklist?.ScanCommand.Execute(null); break;
            default: Main?.OpenSettingsPage("systems"); break;
        }
    }

    /// <summary>
    /// The other actions of a step are in its menu: the Setup window, the connection test and the skip of the scan. The step takes the keyboard
    /// focus, so the Apps key and Shift+F10 open the menu.
    /// </summary>
    private void Step_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SetupStepItem step, ContextMenu: { } menu } || Checklist is not { } checklist) return;
        menu.Items.Clear();
        if (step.ShowSetup)
            menu.Items.Add(Item("Open Setup", "btnSetupOpenWindow" + step.Key, "Open the Setup window: the systems, the site and the content folders.",
                checklist.CanOpenSetup, () => Main?.OpenSetup()));
        if (step.ShowTest)
            menu.Items.Add(Item("Test Connection", "btnSetupTestConnection", "Test the connection to the ConfigMgr site with the saved settings.",
                checklist.TestConnectionCommand.CanExecute(null), () => checklist.TestConnectionCommand.Execute(null)));
        if (step.ShowScan)
            menu.Items.Add(Item(step.ScanText, "btnSetupScan", "Read application names and versions from the site inventory. You then choose the products.",
                checklist.ScanCommand.CanExecute(null), () => checklist.ScanCommand.Execute(null)));
        if (step.ShowSkip)
            menu.Items.Add(Item("Skip Scan", "btnSetupSkipScan", "Complete this step for this site without a scan. You can scan later.", true,
                () => checklist.SkipScanCommand.Execute(null)));
        if (checklist.IsDue)
            menu.Items.Add(Item("Do Not Show Again", "btnSetupDismiss", "Stop opening the checklist at the start. The settings do not change.", true,
                () => checklist.DoNotShowAgainCommand.Execute(null)));
        if (menu.Items.Count == 0) e.Handled = true;
    }

    private static MenuItem Item(string header, string id, string tip, bool enabled, Action run)
    {
        var item = new MenuItem { Header = header, ToolTip = tip, IsEnabled = enabled };
        System.Windows.Automation.AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => run();
        return item;
    }
}
