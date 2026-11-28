using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace ApplicationPackagerUI.Controls.Mock;

/// <summary>Attached properties of the control styles in Views/Styles/Mock.xaml: the glyph before and after the text of a button, a menu entry or a sidebar entry.</summary>
public static class Mk
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Mk), new PropertyMetadata(null));
    public static string? GetIcon(DependencyObject element) => (string?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, string? value) => element.SetValue(IconProperty, value);

    public static readonly DependencyProperty TrailingIconProperty = DependencyProperty.RegisterAttached("TrailingIcon", typeof(string), typeof(Mk), new PropertyMetadata(null));
    public static string? GetTrailingIcon(DependencyObject element) => (string?)element.GetValue(TrailingIconProperty);
    public static void SetTrailingIcon(DependencyObject element, string? value) => element.SetValue(TrailingIconProperty, value);

    /// <summary>
    /// How far the glyphs of a button stand below the middle. The mock's browser rounds a half pixel down the page and WPF up it; a button at
    /// such a place gets 1.
    /// </summary>
    public static readonly DependencyProperty IconDropProperty = DependencyProperty.RegisterAttached("IconDrop", typeof(double), typeof(Mk), new PropertyMetadata(0d));
    public static double GetIconDrop(DependencyObject element) => (double)element.GetValue(IconDropProperty);
    public static void SetIconDrop(DependencyObject element, double value) => element.SetValue(IconDropProperty, value);

    /// <summary>How far the outline and the fill of a button stand below their layout place. The label and the glyphs do not move with them.</summary>
    public static readonly DependencyProperty FaceDropProperty = DependencyProperty.RegisterAttached("FaceDrop", typeof(double), typeof(Mk), new PropertyMetadata(0d));
    public static double GetFaceDrop(DependencyObject element) => (double)element.GetValue(FaceDropProperty);
    public static void SetFaceDrop(DependencyObject element, double value) => element.SetValue(FaceDropProperty, value);

    /// <summary>How far the label of a button, or the text and the placeholder of a text box, stands below the middle.</summary>
    public static readonly DependencyProperty TextDropProperty = DependencyProperty.RegisterAttached("TextDrop", typeof(double), typeof(Mk), new PropertyMetadata(0d));
    public static double GetTextDrop(DependencyObject element) => (double)element.GetValue(TextDropProperty);
    public static void SetTextDrop(DependencyObject element, double value) => element.SetValue(TextDropProperty, value);

    /// <summary>
    /// Draws a glyph lower by this many units without a change of the layout. A template sets it on the glyph from the value of its button: a
    /// transform of its own cannot bind to the button.
    /// </summary>
    public static readonly DependencyProperty DropProperty = DependencyProperty.RegisterAttached("Drop", typeof(double), typeof(Mk),
        new PropertyMetadata(0d, (d, e) =>
        {
            if (d is UIElement element) element.RenderTransform = new TranslateTransform(0, (double)e.NewValue);
        }));
    public static double GetDrop(DependencyObject element) => (double)element.GetValue(DropProperty);
    public static void SetDrop(DependencyObject element, double value) => element.SetValue(DropProperty, value);

    /// <summary>The gray text of an empty text box.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Mk), new PropertyMetadata(""));
    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    /// <summary>The line above the grid or the section of a sidebar entry: set on the entry that is the current one.</summary>
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached("IsCurrent", typeof(bool), typeof(Mk), new PropertyMetadata(false));
    public static bool GetIsCurrent(DependencyObject element) => (bool)element.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(DependencyObject element, bool value) => element.SetValue(IsCurrentProperty, value);
}

/// <summary>
/// The Mk brushes live in the palette of the theme, which App.xaml and the theme applier put in the application resources. Mock.xaml merges this
/// empty dictionary: where the application has no palette yet, it adds the palette of the loaded theme there, so a view that is built on its own
/// finds its brushes. It never adds a second palette and never replaces one.
/// </summary>
public sealed class PaletteFallback : ResourceDictionary
{
    public PaletteFallback()
    {
        if (System.Windows.Application.Current is not { } application || !application.CheckAccess() || application.TryFindResource("MkTextBrush") is not null) return;
        var dark = application.Resources.MergedDictionaries.Any(d => d.Source?.OriginalString.EndsWith("/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light;
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Services.ThemeApplier.PalettePath(theme), UriKind.Relative) });
    }
}

/// <summary>One glyph of the icon font of the mock (Fluent System Icons) in a square box.</summary>
public sealed class Ico : System.Windows.Controls.TextBlock
{
    private static readonly Dictionary<string, int> Codes = new(StringComparer.Ordinal)
    {
        ["box"] = 57787, ["globe"] = 62554, ["document_add"] = 58587, ["arrow_sync"] = 61840, ["wrench"] = 61062, ["box_search"] = 57809,
        ["beaker"] = 61911, ["rocket"] = 63095, ["code"] = 62191, ["people_community"] = 62896, ["calendar_clock"] = 61981, ["server"] = 63337,
        ["broom"] = 61953, ["settings"] = 63145, ["search"] = 63119, ["chevron_down"] = 62115, ["checkmark_circle"] = 62104, ["warning"] = 63593,
        ["info"] = 62627, ["clock"] = 62173, ["add"] = 61705, ["arrow_right"] = 61825, ["play"] = 62981, ["more_horizontal"] = 59428,
        ["panel_right"] = 59581, ["navigation"] = 62816, ["dismiss"] = 62313, ["subtract"] = 60368, ["square"] = 60277, ["shield"] = 63166,
        ["history"] = 62590, ["checkmark"] = 62100, ["arrow_up"] = 61851, ["filter"] = 62470, ["open"] = 62850, ["save"] = 63103,
        ["arrow_left"] = 61787, ["cloud_arrow_up"] = 58259, ["folder_open"] = 62510, ["link"] = 62692, ["error_circle"] = 62449,
        ["document"] = 62328, ["list"] = 62701, ["arrow_download"] = 61776,
    };

    public static readonly FontFamily Font = new(new Uri("pack://application:,,,/ApplicationPackagerUI;component/"), "./Assets/#FluentSystemIcons-Regular");

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(Ico),
        new PropertyMetadata(null, (d, e) => ((Ico)d).Text = Character((string?)e.NewValue)));

    public static string Character(string? name) => name is not null && Codes.TryGetValue(name, out var code) ? char.ConvertFromUtf32(code) : "";

    public static bool Knows(string name) => Codes.ContainsKey(name);

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>
    /// The size and the font of a glyph. They come from a style, so that a template or a view can set another size: a value that the
    /// constructor set would win over the template. A style of a view that adds triggers is based on this one.
    /// </summary>
    public static Style BaseStyle { get; } = CreateBaseStyle();

    private static Style CreateBaseStyle()
    {
        var style = new Style(typeof(Ico));
        style.Setters.Add(new Setter(FontFamilyProperty, Font));
        // A glyph keeps its own weight inside a semi-bold button.
        style.Setters.Add(new Setter(FontWeightProperty, FontWeights.Normal));
        style.Setters.Add(new Setter(FontStyleProperty, FontStyles.Normal));
        style.Setters.Add(new Setter(FontSizeProperty, 18d));
        style.Setters.Add(new Setter(WidthProperty, 18d));
        style.Setters.Add(new Setter(HeightProperty, 18d));
        style.Setters.Add(new Setter(TextAlignmentProperty, TextAlignment.Center));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextWrappingProperty, TextWrapping.NoWrap));
        style.Seal();
        return style;
    }

    // A style that a template or a view gives is in place when the element is initialized. Only a glyph without one takes the base style: a
    // style that the constructor set would win over the style of a template.
    protected override void OnInitialized(EventArgs e)
    {
        if (Style is null) Style = BaseStyle;
        base.OnInitialized(e);
    }

    // A glyph says nothing to a screen reader: the text beside it names the control.
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

/// <summary>
/// A heading with the letter spacing of the mock. WPF text has no letter spacing, so the heading draws its glyphs itself with the advance of each
/// one changed by <see cref="Tracking"/>. A screen reader reads it as text.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    private static FrameworkPropertyMetadata Layout(object value) =>
        new(value, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(TrackedText), Layout(""));
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(TrackedText), Layout(0d));
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(TrackedText), Layout(30d));
    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(TrackedText), Layout(FontWeights.Bold));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(TrackedText), Layout(Brushes.Black));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly FontFamily Family = new("Segoe UI");

    private (GlyphTypeface Face, ushort[] Glyphs, double[] Advances, double Width)? Shape()
    {
        var typeface = new Typeface(Family, FontStyles.Normal, FontWeight, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var face)) return null;
        var text = Text ?? "";
        var glyphs = new ushort[text.Length];
        var advances = new double[text.Length];
        var width = 0d;
        for (var i = 0; i < text.Length; i++)
        {
            glyphs[i] = face.CharacterToGlyphMap.TryGetValue(text[i], out var glyph) ? glyph : (ushort)0;
            advances[i] = face.AdvanceWidths[glyphs[i]] * FontSize + Tracking;
            width += advances[i];
        }
        return (face, glyphs, advances, width);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shape = Shape();
        return shape is { } s ? new Size(Math.Max(0, s.Width), s.Face.Height * FontSize) : new Size();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Shape() is not { } s || s.Glyphs.Length == 0) return;
        var origin = new Point(0, s.Face.Baseline * FontSize);
        var run = new GlyphRun(s.Face, 0, false, FontSize, (float)VisualTreeHelper.GetDpi(this).PixelsPerDip, s.Glyphs, origin, s.Advances,
            null, null, null, null, null, null);
        drawingContext.DrawGlyphRun(Foreground, run);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(TrackedText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(TrackedText);
        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((TrackedText)Owner).Text : name;
        }
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
