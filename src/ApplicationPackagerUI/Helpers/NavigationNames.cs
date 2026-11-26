using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

/// <summary>The navigation control has a button with only a glyph, which shows or hides the labels of the sidebar. It gets a name here.</summary>
public static class NavigationNames
{
    public const string ToggleName = "Show or hide the sidebar labels";

    /// <summary>Names each button of the navigation template that has no name and no text, and returns how many it named.</summary>
    public static int Apply(DependencyObject navigation)
    {
        var named = 0;
        foreach (var button in Descendants(navigation).OfType<ButtonBase>())
        {
            if (!IsToggle(button) || AutomationProperties.GetName(button).Length > 0) continue;
            AutomationProperties.SetName(button, ToggleName);
            named++;
        }
        return named;
    }

    public static bool IsToggle(ButtonBase button) =>
        button.Name.Contains("Toggle", StringComparison.Ordinal) || button.GetType().Name.Contains("NavigationToggle", StringComparison.Ordinal)
        || AutomationProperties.GetAutomationId(button).Contains("NavigationToggle", StringComparison.Ordinal);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
