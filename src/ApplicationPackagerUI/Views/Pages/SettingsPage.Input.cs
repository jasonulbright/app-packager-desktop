using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using ApplicationPackagerUI.ViewModels.Pages;
using NumberBox = Wpf.Ui.Controls.NumberBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>
/// The input side of the dirty line of Settings. Some values of the edit buffer raise no change notice (the Citrix switches, the install
/// parameters), so every input event of the page asks the view model to compare again, after the event wrote its value. A number box and
/// a text cell of a grid write their value when they lose the focus; while one holds new typed text, the line reads "Unsaved changes".
/// </summary>
public partial class SettingsPage
{
    /// <summary>The boxes that write on lost focus and have the focus, with the text that each one had when it got it.</summary>
    private readonly Dictionary<TextBox, string> _enteredText = new();
    private bool _dirtyCheckQueued;

    private void TrackInput()
    {
        void Watch(RoutedEvent routed) => AddHandler(routed, new RoutedEventHandler((_, _) => QueueDirtyCheck()), handledEventsToo: true);
        Watch(ToggleButton.CheckedEvent);
        Watch(ToggleButton.UncheckedEvent);
        Watch(ToggleButton.IndeterminateEvent);
        Watch(ButtonBase.ClickEvent);
        AddHandler(NumberBox.ValueChangedEvent, new RoutedEventHandler((_, _) => QueueDirtyCheck()), handledEventsToo: true);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => QueueDirtyCheck()), handledEventsToo: true);
        AddHandler(UIElement.GotFocusEvent, new RoutedEventHandler(DeferredBox_GotFocus), handledEventsToo: true);
        AddHandler(UIElement.LostFocusEvent, new RoutedEventHandler(DeferredBox_LostFocus), handledEventsToo: true);
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(DeferredBox_TextChanged), handledEventsToo: true);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.UnsavedChangesText))
                UIElementAutomationPeer.FromElement(txtUnsavedChanges)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
    }

    /// <summary>True for a box that writes its value when it loses the focus.</summary>
    internal static bool CommitsOnLostFocus(TextBox box) =>
        box is NumberBox || BindingOperations.GetBinding(box, TextBox.TextProperty) is { UpdateSourceTrigger: UpdateSourceTrigger.LostFocus or UpdateSourceTrigger.Default };

    private void DeferredBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox box && CommitsOnLostFocus(box)) _enteredText.TryAdd(box, box.Text);
    }

    private void DeferredBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox box && _enteredText.ContainsKey(box) && box.IsKeyboardFocusWithin) UpdatePendingEntry();
        QueueDirtyCheck();
    }

    private void DeferredBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // The binding writes the value in this event; the entry ends after it.
        if (e.OriginalSource is TextBox box && _enteredText.ContainsKey(box))
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
            {
                if (box.IsKeyboardFocusWithin) return;
                _enteredText.Remove(box);
                UpdatePendingEntry();
            });
        QueueDirtyCheck();
    }

    private void UpdatePendingEntry() =>
        _viewModel.HasPendingEntry = _enteredText.Any(p => p.Key.IsKeyboardFocusWithin && p.Key.Text != p.Value);

    private void QueueDirtyCheck()
    {
        if (_dirtyCheckQueued) return;
        _dirtyCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _dirtyCheckQueued = false;
            _viewModel.RefreshDirtyState();
        });
    }
}
