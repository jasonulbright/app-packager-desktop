using System.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Controls;

/// <summary>The setup checklist beside the grid of the App Packager page. Its data context is the checklist view model.</summary>
public partial class SetupChecklistPanel
{
    public SetupChecklistPanel() => InitializeComponent();

    private void OpenSetup_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenSetup();
}
