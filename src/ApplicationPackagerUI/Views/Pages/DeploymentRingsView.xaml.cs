using System.Windows;
using System.Windows.Input;

namespace ApplicationPackagerUI.Views.Pages;

public partial class DeploymentRingsView
{
    private bool _validateLostFocus;
    private IInputElement? _focusWhileValidating;

    public DeploymentRingsView()
    {
        InitializeComponent();
        btnRingValidate.LostKeyboardFocus += OnValidateLostFocus;
        btnRingValidate.IsEnabledChanged += OnValidateEnabledChanged;
    }

    // A command button is disabled while its command runs, and a disabled button loses the keyboard
    // focus to the first stop of the page; the focus goes back when the operator did not move it.
    private void OnValidateLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (btnRingValidate.IsEnabled) return;
        _validateLostFocus = true;
        _focusWhileValidating = e.NewFocus;
    }

    private void OnValidateEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !_validateLostFocus) return;
        _validateLostFocus = false;
        var now = Keyboard.FocusedElement;
        if (now is null || ReferenceEquals(now, _focusWhileValidating)) btnRingValidate.Focus();
    }
}
