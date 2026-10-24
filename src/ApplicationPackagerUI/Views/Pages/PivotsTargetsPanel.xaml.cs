using System.Windows;
using System.Windows.Input;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>The targets of a run: kind, text, import and Resolve. The Pivot and Script tabs share one view model.</summary>
public partial class PivotsTargetsPanel
{
    private bool _resolveHadFocus;

    public PivotsTargetsPanel()
    {
        InitializeComponent();
        btnResolve.IsEnabledChanged += OnResolveEnabledChanged;
        btnResolve.LostKeyboardFocus += (_, _) => _resolveHadFocus |= !btnResolve.IsEnabled;
    }

    /// <summary>
    /// The Resolve button is disabled while the read runs, and a disabled button loses the keyboard
    /// focus to the scroll area of the tab or to the window. When the read ends, the focus goes back
    /// to the button, so the next Tab goes on from the targets and not from the top of the tab.
    /// </summary>
    private void OnResolveEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            _resolveHadFocus |= btnResolve.IsKeyboardFocusWithin;
            return;
        }
        if (!_resolveHadFocus) return;
        _resolveHadFocus = false;
        var focused = Keyboard.FocusedElement;
        if (IsVisible && (focused is null or Window || (focused is System.Windows.Media.Visual visual && visual.IsAncestorOf(btnResolve)))) Keyboard.Focus(btnResolve);
    }
}
