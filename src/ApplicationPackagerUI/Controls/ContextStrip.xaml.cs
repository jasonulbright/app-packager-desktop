using System.Windows;
using ApplicationPackagerUI.Views.Windows;

namespace ApplicationPackagerUI.Controls;

/// <summary>The strip at the top of the window. The "Connections" button opens the Settings page.</summary>
public partial class ContextStrip
{
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(ContextStrip),
        new PropertyMetadata(false, (d, e) => ((ContextStrip)d).ApplyCompact((bool)e.NewValue)));

    public ContextStrip() => InitializeComponent();

    /// <summary>The strip of a window of the app: 43 px high with 18 px at its sides, and no Connections button.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private int _lift;

    /// <summary>How far the texts of the strip stand above their place; the glyphs and the strip keep theirs.</summary>
    public int Lift
    {
        get => _lift;
        set
        {
            _lift = value;
            Resources["StripText"] = new System.Windows.Media.TranslateTransform(0, -value);
        }
    }

    private void ApplyCompact(bool compact)
    {
        Strip.Height = compact ? 43 : 51;
        Strip.Padding = new Thickness(compact ? 18 : 0, 0, compact ? 18 : 0, 0);
        btnConnections.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Connections_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.OpenConnections();
}
