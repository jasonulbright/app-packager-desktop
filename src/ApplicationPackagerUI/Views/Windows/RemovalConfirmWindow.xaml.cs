using System.Windows;
using System.Windows.Input;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The removal question of an Undo. Cancel is the default and has the focus when the window
/// opens, so Enter and Escape both decline; only the Remove button confirms.
/// </summary>
public partial class RemovalConfirmWindow
{
    public RemovalConfirmWindow(string question) : this(question, "Remove deployment", "_Remove deployment")
    {
    }

    /// <param name="confirmText">The text of the button that confirms; an underscore marks its access key.</param>
    public RemovalConfirmWindow(string question, string title, string confirmText)
    {
        InitializeComponent();
        Title = title;
        titleBar.Title = title;
        btnRemove.Content = confirmText;
        txtQuestion.Text = question;
        Loaded += (_, _) => FocusCancel();
        Activated += (_, _) => FocusCancel();
    }

    /// <summary>True when the operator selected Remove deployment.</summary>
    public bool Confirmed { get; private set; }

    private void FocusCancel()
    {
        if (IsKeyboardFocusWithin && !ReferenceEquals(Keyboard.FocusedElement, this)) return;
        btnCancel.Focus();
        Keyboard.Focus(btnCancel);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    /// <summary>
    /// The owner of the question: the newest visible change plan window, since the Undo runs from
    /// it and it is modal; otherwise the active window, then the main window.
    /// </summary>
    public static Window? OwnerFor(IEnumerable<Window> windows, Window? main)
    {
        var all = windows.ToList();
        var candidate = all.OfType<ChangePlanWindow>().LastOrDefault(w => w.IsVisible) ?? all.FirstOrDefault(w => w.IsActive) ?? main;
        return DiscoveryPrompt.UsableOwner(candidate);
    }

    /// <summary>Shows the question modally over <paramref name="owner"/>; false when declined or closed.</summary>
    public static bool Ask(Window? owner, string question) => Ask(owner, new RemovalConfirmWindow(question));

    /// <summary>Shows <paramref name="window"/> modally over <paramref name="owner"/>; false when declined or closed.</summary>
    public static bool Ask(Window? owner, RemovalConfirmWindow window)
    {
        if (owner is not null) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Confirmed;
    }
}
