using System.Windows;
using System.Windows.Controls;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// Editor for one app's install/uninstall command overrides. An empty
/// field keeps the packager's shipped command, so Revert clears both
/// rather than restoring any remembered text.
/// </summary>
public partial class CommandOverridePanel : UserControl
{
    public CommandOverridePanel(string appLabel, string install, string uninstall)
    {
        InitializeComponent();

        IntroText.Text =
            $"Replaces the deployment type command lines the next time {appLabel} is packaged. " +
            "An empty field keeps the packager's shipped command. Not applied to variant-split " +
            "apps, whose variants carry their own commands.";
        InstallBox.Text = install;
        UninstallBox.Text = uninstall;
    }

    public string Install => InstallBox.Text.Trim();
    public string Uninstall => UninstallBox.Text.Trim();

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        InstallBox.Text = "";
        UninstallBox.Text = "";
    }
}
