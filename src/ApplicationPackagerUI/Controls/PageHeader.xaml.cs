using System.Windows;
using System.Windows.Automation;
using Wpf.Ui.Controls;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// The title, the optional description, the optional status line and the optional actions at
/// the top of a page or a window. The header does not scroll with the page content.
/// </summary>
public partial class PageHeader
{
    /// <summary>Below this header width the description keeps one line, trims, and shows the full text as a tooltip.</summary>
    public const double CompactWidth = 760;

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(PageHeader),
            new PropertyMetadata("", (d, _) => ((PageHeader)d).UpdateParts()));

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(string), typeof(PageHeader),
            new PropertyMetadata("", (d, _) => ((PageHeader)d).UpdateParts()));

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader),
            new PropertyMetadata(null, (d, _) => ((PageHeader)d).UpdateParts()));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(PageHeader),
            new PropertyMetadata(SymbolRegular.Empty, (d, _) => ((PageHeader)d).UpdateParts()));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(PageHeader),
            new PropertyMetadata(false, (d, _) => ((PageHeader)d).UpdateParts()));

    public static readonly DependencyProperty TitleAutomationIdProperty =
        DependencyProperty.Register(nameof(TitleAutomationId), typeof(string), typeof(PageHeader),
            new PropertyMetadata("txtPageTitle", (d, e) => AutomationProperties.SetAutomationId(((PageHeader)d).TitleText, (string)e.NewValue)));

    public static readonly DependencyProperty DescriptionAutomationIdProperty =
        DependencyProperty.Register(nameof(DescriptionAutomationId), typeof(string), typeof(PageHeader),
            new PropertyMetadata("txtPageDescription", (d, e) => AutomationProperties.SetAutomationId(((PageHeader)d).DescriptionText, (string)e.NewValue)));

    public PageHeader()
    {
        InitializeComponent();
        AutomationProperties.SetAutomationId(TitleText, TitleAutomationId);
        AutomationProperties.SetAutomationId(DescriptionText, DescriptionAutomationId);
        SizeChanged += (_, _) => UpdateParts();
        UpdateParts();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>A caption line under the description, for example a count or the state of a connection.</summary>
    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>The primary buttons at the right of the title.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>
    /// The plain page title of the V1 workspace (overhaul V1, phase 2): the title at 28 with the actions beside it, with no card, no
    /// icon, no description and no status line. Not the width-driven <see cref="IsCompact"/>, which only trims the description.
    /// </summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>The page icon at the left of the title. Empty shows no icon.</summary>
    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The automation id of the title text; tests and UI automation find the title by it.</summary>
    public string TitleAutomationId
    {
        get => (string)GetValue(TitleAutomationIdProperty);
        set => SetValue(TitleAutomationIdProperty, value);
    }

    /// <summary>The automation id of the description text.</summary>
    public string DescriptionAutomationId
    {
        get => (string)GetValue(DescriptionAutomationIdProperty);
        set => SetValue(DescriptionAutomationIdProperty, value);
    }

    public System.Windows.Controls.TextBlock TitleElement => TitleText;

    public System.Windows.Controls.TextBlock DescriptionElement => DescriptionText;

    public bool IsCompact => ActualWidth > 0 && ActualWidth < CompactWidth;

    private void UpdateParts()
    {
        if (DescriptionText is null || IconHost is null) return;
        var plain = Compact;
        DescriptionText.Visibility = plain || string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
        IconHost.Visibility = plain || Icon == SymbolRegular.Empty ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = plain || string.IsNullOrWhiteSpace(Status) ? Visibility.Collapsed : Visibility.Visible;
        // The plain title has no card: no fill, no border, no padding.
        if (plain)
        {
            Band.Background = System.Windows.Media.Brushes.Transparent;
            Band.BorderThickness = new Thickness(0);
            Band.Padding = new Thickness(0);
            TitleText.SetResourceReference(System.Windows.Controls.TextBlock.StyleProperty, "PageTitleLarge");
        }
        else
        {
            Band.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            Band.BorderThickness = new Thickness(1);
            Band.Padding = new Thickness(12, 8, 12, 8);
            TitleText.SetResourceReference(System.Windows.Controls.TextBlock.StyleProperty, "PageTitle");
        }
        ActionsPresenter.Visibility = Actions is null ? Visibility.Collapsed : Visibility.Visible;
        var compact = IsCompact;
        DescriptionText.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        DescriptionText.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        DescriptionText.ToolTip = compact ? Description : null;
    }
}
