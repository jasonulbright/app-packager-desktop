using System.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Controls;

/// <summary>The strip at the top of the window. The "Connections" button opens the Settings page.</summary>
public partial class ContextStrip
{
    public ContextStrip() => InitializeComponent();

    private void Connections_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenConnections();
}
