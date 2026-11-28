using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Shell;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Controls.Mock;

/// <summary>
/// The frame of a window of the app as the mock draws it over the main window: its own head with the title and the close button, the context
/// strip, the body and the foot. Its template is in Views/Styles/Mock.Dialogs.xaml. <see cref="Attach"/> turns a window into such a frame:
/// no system frame, centered on its owner, with the owner dimmed while it is open.
/// </summary>
[ContentProperty(nameof(Body))]
[TemplatePart(Name = "PART_Close", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_Head", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Strip", Type = typeof(ContextStrip))]
public sealed class DialogFrame : System.Windows.Controls.Control
{
    private static DependencyProperty Register(string name) => DependencyProperty.Register(name, typeof(object), typeof(DialogFrame),
        new PropertyMetadata(null, (d, e) => ((DialogFrame)d).Adopt(e.OldValue, e.NewValue)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(DialogFrame), new PropertyMetadata(""));
    public static readonly DependencyProperty BodyProperty = Register(nameof(Body));
    public static readonly DependencyProperty FootLeftProperty = Register(nameof(FootLeft));
    public static readonly DependencyProperty FootRightProperty = Register(nameof(FootRight));

    /// <summary>
    /// The window of the mock has an odd height, so its browser draws it half a pixel below a whole one. The close button and the texts of the
    /// context strip then round to other pixels than in a window of an even height.
    /// </summary>
    public static readonly DependencyProperty HalfPixelProperty = DependencyProperty.Register(nameof(HalfPixel), typeof(bool), typeof(DialogFrame),
        new PropertyMetadata(false, (d, _) => ((DialogFrame)d).LiftStrip()));

    public bool HalfPixel { get => (bool)GetValue(HalfPixelProperty); set => SetValue(HalfPixelProperty, value); }

    private void LiftStrip()
    {
        if (GetTemplateChild("PART_Strip") is ContextStrip strip) strip.Lift = HalfPixel ? 1 : 0;
    }

    /// <summary>Sets the rounding of the frame of a window from the height that the mock gives it.</summary>
    public static void RoundAs(System.Windows.Window window, double mockHeight)
    {
        if (window.Content is DependencyObject content && Find(content) is { } frame) frame.HalfPixel = (int)mockHeight % 2 != 0;
    }

    private static DialogFrame? Find(DependencyObject element) =>
        element as DialogFrame ?? LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().Select(Find).FirstOrDefault(f => f is not null);

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public object? FootLeft { get => GetValue(FootLeftProperty); set => SetValue(FootLeftProperty, value); }
    public object? FootRight { get => GetValue(FootRightProperty); set => SetValue(FootRightProperty, value); }

    public DialogFrame()
    {
        Focusable = false;
        IsTabStop = false;
    }

    // The content of the frame belongs to it logically, so that it takes the data context and the resources of the window.
    protected override System.Collections.IEnumerator LogicalChildren =>
        new[] { Body, FootLeft, FootRight }.Where(c => c is not null).GetEnumerator();

    private void Adopt(object? old, object? added)
    {
        if (old is not null) RemoveLogicalChild(old);
        if (added is not null) AddLogicalChild(added);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Close") is ButtonBase close) close.Click += (_, _) => System.Windows.Window.GetWindow(this)?.Close();
        if (GetTemplateChild("PART_Head") is FrameworkElement head) head.MouseLeftButtonDown += Drag;
        LiftStrip();
        // The strip shows the state of the site as the strip of the main window does; a window that a test builds shows an empty one.
        if (GetTemplateChild("PART_Strip") is ContextStrip strip) strip.DataContext = TryStrip() ?? new ContextStripViewModel();
    }

    private static ContextStripViewModel? TryStrip()
    {
        if (System.Windows.Application.Current is not App) return null;
        try { return App.Services.GetService<ContextStripViewModel>(); }
        catch (NullReferenceException) { return null; }
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && System.Windows.Window.GetWindow(this) is { } window) window.DragMove();
    }

    /// <summary>
    /// Limits a window to the work area, so the foot with its buttons stays on the screen; the body scrolls. The window keeps its center.
    /// </summary>
    public static void FitToWorkArea(System.Windows.Window window, Rect area)
    {
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        var width = Math.Min(window.Width, area.Width);
        var height = Math.Min(window.Height, area.Height);
        if (!double.IsNaN(window.Left)) window.Left += (window.Width - width) / 2;
        if (!double.IsNaN(window.Top)) window.Top += (window.Height - height) / 2;
        window.Width = width;
        window.Height = height;
    }

    /// <summary>
    /// A window of a fixed size of the mock: it opens no larger than the work area. A warning or another line that the mock does not draw makes it
    /// taller, up to the work area, so the line shows without a scroll.
    /// </summary>
    public static void KeepInWorkArea(System.Windows.Window window, System.Windows.Controls.ScrollViewer body)
    {
        window.SourceInitialized += (_, _) => FitToWorkArea(window, SystemParameters.WorkArea);
        window.Loaded += (_, _) =>
        {
            window.UpdateLayout();
            var more = Math.Min(body.ScrollableHeight, SystemParameters.WorkArea.Height - window.Height);
            if (more < 1) return;
            window.Height += more;
            if (!double.IsNaN(window.Top)) window.Top = Math.Max(Math.Min(window.Top, SystemParameters.WorkArea.Top), window.Top - more / 2);
        };
    }

    /// <summary>
    /// Gives the window the frame of the mock. Windows moves it by its head. The main window is dimmed while the window is open.
    /// </summary>
    public static void Attach(System.Windows.Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 42,
            ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 4),
            CornerRadius = new CornerRadius(8),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
        });
        IDisposable? dim = null;
        window.Loaded += (_, _) => dim ??= (System.Windows.Application.Current?.MainWindow as MainWindow)?.Dim();
        window.Closed += (_, _) => dim?.Dispose();
    }
}
