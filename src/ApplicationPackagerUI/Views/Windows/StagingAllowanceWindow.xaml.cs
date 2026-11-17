using System.Windows;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The acknowledgement of a staging folder that other accounts can write to. Accept stays disabled until the box is checked; Cancel is the default,
/// so Enter and Escape decline.
/// </summary>
public partial class StagingAllowanceWindow
{
    public StagingAllowanceWindow(string folder, string reason)
    {
        InitializeComponent();
        txtFolder.Text = "Folder: " + folder;
        txtReason.Text = reason;
    }

    /// <summary>True when the user checked the box and chose Accept.</summary>
    public bool Accepted { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (chkAcknowledge.IsChecked != true) return;
        Accepted = true;
        DialogResult = true;
    }
}

/// <summary>The WPF prompt of "Allow this folder anyway".</summary>
public sealed class StagingAllowancePrompt : IStagingAllowancePrompt
{
    public bool Confirm(string folder, string reason)
    {
        var window = new StagingAllowanceWindow(folder, reason);
        if (DiscoveryPrompt.UsableOwner(Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow) is { } owner)
            window.Owner = owner;
        window.ShowDialog();
        return window.Accepted;
    }
}
