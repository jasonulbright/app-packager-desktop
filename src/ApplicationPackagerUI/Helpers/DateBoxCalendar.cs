using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DatePicker = System.Windows.Controls.DatePicker;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// The date box of the mock has no calendar button. A double click or the menu of the box opens the calendar; Alt+Down opens it too, as
/// the date box of Windows does.
/// </summary>
public static class DateBoxCalendar
{
    public static readonly DependencyProperty OnDemandProperty = DependencyProperty.RegisterAttached(
        "OnDemand", typeof(bool), typeof(DateBoxCalendar), new PropertyMetadata(false, OnChanged));

    public static bool GetOnDemand(DependencyObject element) => (bool)element.GetValue(OnDemandProperty);

    public static void SetOnDemand(DependencyObject element, bool value) => element.SetValue(OnDemandProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not DatePicker box || e.NewValue is not true) return;
        var open = new MenuItem { Header = "Open calendar" };
        // The menu closes before the calendar opens, or the close of the menu would close the calendar again.
        open.Click += (_, _) => box.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => box.IsDropDownOpen = true);
        box.ContextMenu = new ContextMenu { Items = { open } };
        box.PreviewMouseDoubleClick += (_, _) => box.IsDropDownOpen = true;
    }
}
