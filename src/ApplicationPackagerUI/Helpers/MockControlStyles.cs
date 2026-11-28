using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ApplicationPackagerUI.Controls.Mock;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Gives the controls of a shared panel the styles of the mock when a page of the mock hosts the panel. A panel takes its styles from its own
/// resources, which a page cannot replace, and other pages host the same panel.
/// </summary>
public static class MockControlStyles
{
    /// <summary>Walks the logical tree, which holds each element of the XAML of a panel before the panel shows.</summary>
    public static void Apply(DependencyObject root, FrameworkElement resources)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            switch (child)
            {
                case Border { Child: System.Windows.Controls.TextBlock } warning when AutomationProperties.GetAutomationId(warning) is "pnlDetectionWarning" or "pnlAddResultReason":
                    var gap = warning.Margin;
                    Set(warning, resources, "Mk.Notice.Warn");
                    warning.Margin = gap;
                    break;
                case Wpf.Ui.Controls.TextBox field:
                    var placeholder = field.PlaceholderText ?? "";
                    var margin = field.Margin;
                    Set(field, resources, "Mk.TextBox");
                    Mk.SetPlaceholder(field, placeholder);
                    field.Margin = new Thickness(margin.Left, margin.Top, margin.Right, 8);
                    break;
                case ComboBox list:
                    Set(list, resources, "Mk.ComboBox");
                    list.Margin = new Thickness(0, 0, 0, 8);
                    break;
                case CheckBox check:
                    Set(check, resources, "Mk.CheckBox");
                    break;
                case Button button:
                    var spacing = button.Margin;
                    Set(button, resources, "WD.DefaultButton");
                    button.Margin = spacing;
                    if (button.Content is string label && Glyph(label) is { } glyph) Mk.SetIcon(button, glyph);
                    break;
                default:
                    Apply(child, resources);
                    break;
            }
        }
    }

    private static string? Glyph(string label) => label switch
    {
        "Open in Workbench" => "wrench",
        "Stage now" => "arrow_download",
        "Done" => "checkmark",
        _ => null
    };

    private static void Set(FrameworkElement element, FrameworkElement resources, string key)
    {
        var help = AutomationProperties.GetHelpText(element);
        element.Style = (Style)resources.FindResource(key);
        if (help.Length > 0) AutomationProperties.SetHelpText(element, help);
    }
}
