using System.Windows;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>Asks for a PFX password. The text leaves this window only through <see cref="Password"/> after OK.</summary>
public partial class WsusPasswordWindow
{
    public string? Password { get; private set; }

    public WsusPasswordWindow(string prompt)
    {
        InitializeComponent();
        txtPrompt.Text = prompt;
        Loaded += (_, _) => pwdValue.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = pwdValue.Password;
        DialogResult = true;
    }
}
