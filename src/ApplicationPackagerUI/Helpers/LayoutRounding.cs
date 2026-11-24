using System.Windows;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Rounds the layout of every window and every page to whole device pixels (Q-125). A control at a fractional position puts its text and its glyph on different pixel rows
/// (the text snaps to the pixel grid, the glyph does not), so the icon of a button sat a pixel off the text of its label on some pages and not on others.
/// UseLayoutRounding is inherited, but the value of the window does not reach the page that a frame hosts, so the page gets the value as well.
/// </summary>
public static class LayoutRounding
{
    private static bool _registered;

    /// <summary>Registers the class handlers once; every window and every page that loads after this call gets the rounding.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Page), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) element.UseLayoutRounding = true;
    }
}
