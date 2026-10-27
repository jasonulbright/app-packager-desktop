using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerUI.Models;
using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// The caption row above a sidebar group. With the pane open it shows the caption text; with
/// the pane closed a thin divider takes its place. It takes no focus and no click, and a
/// screen reader reads it once, by its automation name.
/// </summary>
public sealed class SidebarGroupCaption : Decorator
{
    public static readonly DependencyProperty IsPaneOpenProperty = DependencyProperty.Register(
        nameof(IsPaneOpen), typeof(bool), typeof(SidebarGroupCaption),
        new FrameworkPropertyMetadata(true, (d, _) => ((SidebarGroupCaption)d).ApplyPaneState()));

    public SidebarGroupCaption(SidebarGroup group)
    {
        Group = group;
        Focusable = false;
        KeyboardNavigation.SetIsTabStop(this, false);
        AutomationProperties.SetName(this, group.AutomationName);
        AutomationProperties.SetAutomationId(this, "nav-group-" + group.Id);

        Text = new TextBlock
        {
            Text = group.Caption,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
            Margin = new Thickness(16, 12, 12, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        Text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Text.SetResourceReference(TextBlock.FontSizeProperty, "CaptionTextBlockFontSize");

        Divider = new Border { Height = 1, Margin = new Thickness(8, 8, 8, 8), VerticalAlignment = VerticalAlignment.Center };
        Divider.SetResourceReference(Border.BackgroundProperty, "ControlStrongStrokeColorDefaultBrush");

        var grid = new Grid { IsHitTestVisible = false };
        grid.Children.Add(Text);
        grid.Children.Add(Divider);
        Child = grid;
        ApplyPaneState();
    }

    public SidebarGroup Group { get; }

    public TextBlock Text { get; }

    public Border Divider { get; }

    public bool IsPaneOpen
    {
        get => (bool)GetValue(IsPaneOpenProperty);
        set => SetValue(IsPaneOpenProperty, value);
    }

    private void ApplyPaneState()
    {
        Text.Visibility = IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CaptionAutomationPeer(this);

    private sealed class CaptionAutomationPeer(SidebarGroupCaption owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(SidebarGroupCaption);

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
