using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        FitWorkArea(SystemParameters.WorkArea.Height);
        Loaded += (_, _) => FocusCancel();
        ContentRendered += (_, _) => KeepInsideWorkArea();
        Activated += (_, _) => FocusCancel();
    }

    private Rect _workArea = SystemParameters.WorkArea;

    /// <summary>Caps the window at the height of the work area it opens in.</summary>
    public void FitWorkArea(double height) => MaxHeight = height;

    private void FitWorkArea(Rect area)
    {
        _workArea = area;
        FitWorkArea(area.Height);
    }

    /// <summary>A tall question centered over a short owner extends past the work area; it moves up or down until its footer is on the screen.</summary>
    private void KeepInsideWorkArea()
    {
        var area = _workArea;
        if (Left + ActualWidth <= area.Left || Left >= area.Right) return;
        if (Top + ActualHeight > area.Bottom) Top = area.Bottom - ActualHeight;
        if (Top < area.Top) Top = area.Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>The work area of the monitor that shows <paramref name="window"/>, in device-independent units; null before the window has a handle.</summary>
    internal static Rect? WorkAreaOf(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return null;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info)) return null;
        var toDips = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(toDips.Transform(new Point(info.Work.Left, info.Work.Top)), toDips.Transform(new Point(info.Work.Right, info.Work.Bottom)));
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
        if (owner is not null)
        {
            window.Owner = owner;
            if (WorkAreaOf(owner) is { } area) window.FitWorkArea(area);
        }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Confirmed;
    }
}
