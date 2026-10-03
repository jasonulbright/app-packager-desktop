using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Appearance;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The stored theme for the running application: the resource dictionaries, and the
/// backdrop and dark-mode frame of every open window, at startup and on each Settings save.
/// </summary>
public static class ThemeApplier
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Dark or Light for a stored theme name; null for any other value, which leaves the current theme.</summary>
    public static ApplicationTheme? Resolve(string? stored) => stored?.Trim() switch
    {
        var s when string.Equals(s, nameof(ApplicationTheme.Dark), StringComparison.OrdinalIgnoreCase) => ApplicationTheme.Dark,
        var s when string.Equals(s, nameof(ApplicationTheme.Light), StringComparison.OrdinalIgnoreCase) => ApplicationTheme.Light,
        _ => null
    };

    /// <summary>Applies the theme a stored name selects; any other value changes nothing.</summary>
    public static void Apply(string? stored)
    {
        if (Resolve(stored) is { } theme) Apply(theme);
    }

    /// <summary>
    /// Swaps the theme dictionary and repaints every open window. The library repaints the
    /// main window and the windows it owns only; a window opened on its own, such as the
    /// Workbench, keeps the old backdrop without the loop below. DWM keeps the old frame and
    /// backdrop color after a dark-mode change until the frame is recalculated.
    /// </summary>
    public static void Apply(ApplicationTheme theme)
    {
        if (Application.Current is null) return;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
        foreach (var window in Application.Current.Windows.OfType<FluentWindow>())
        {
            WindowBackgroundManager.UpdateBackground(window, theme, window.WindowBackdropType);
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }
}
