using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using Microsoft.Extensions.DependencyInjection;
using Point = System.Windows.Point;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The title bar, the status bar and the dim layer of the main window, as the mock draws them: a 40 px title bar of the app's own with the three
/// window buttons. Windows still moves, sizes and snaps the window.
/// </summary>
public partial class MainWindow
{
    private const int WmNcHitTest = 0x0084, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, HtMaximizeButton = 9;

    private int _scrims;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
        });
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SnapLayoutHook);
        StateChanged += (_, _) => ApplyWindowState();
        ApplyWindowState();
    }

    // A maximized window with its own caption reaches past the work area by the width of its frame.
    private void ApplyWindowState()
    {
        Frame.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        var restore = WindowState == WindowState.Maximized;
        System.Windows.Automation.AutomationProperties.SetName(btnMaximize, restore ? "Restore" : "Maximize");
        btnMaximize.ToolTip = restore ? "Restore the window." : "Maximize the window.";
    }

    // Windows 11 shows the snap layouts over the maximize button only when the window reports that part of itself as that button.
    private IntPtr SnapLayoutHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is not (WmNcHitTest or WmNcLButtonDown or WmNcLButtonUp)) return IntPtr.Zero;
        if (message != WmNcHitTest)
        {
            if (wParam.ToInt64() != HtMaximizeButton) return IntPtr.Zero;
            handled = true;
            if (message == WmNcLButtonUp) ToggleMaximized();
            return IntPtr.Zero;
        }
        if (btnMaximize is not { IsVisible: true, ActualWidth: > 0 }) return IntPtr.Zero;
        var packed = lParam.ToInt64();
        var screen = new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
        var local = btnMaximize.PointFromScreen(screen);
        if (local.X < 0 || local.Y < 0 || local.X >= btnMaximize.ActualWidth || local.Y >= btnMaximize.ActualHeight) return IntPtr.Zero;
        handled = true;
        return HtMaximizeButton;
    }

    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximized();

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>The left word of the status bar. The line of a run stands under the grid of App Packager.</summary>
    private void FollowStatus()
    {
        if (_serviceProvider.GetService<PackagesViewModel>() is not { } packages || DataContext is not MainWindowViewModel view) return;
        string Word() => packages.IsOperationRunning ? "Running" : "Ready";
        view.StatusText = Word();
        packages.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PackagesViewModel.IsOperationRunning)) return;
            if (Dispatcher.CheckAccess()) view.StatusText = Word();
            else Dispatcher.BeginInvoke(() => view.StatusText = Word());
        };
    }

    /// <summary>Dims the workspace until the returned object is disposed: a window of the app that is open over the main window uses it.</summary>
    public IDisposable Dim()
    {
        _scrims++;
        Scrim.Visibility = Visibility.Visible;
        return new DimScope(this);
    }

    private sealed class DimScope(MainWindow window) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--window._scrims == 0) window.Scrim.Visibility = Visibility.Collapsed;
        }
    }
}
