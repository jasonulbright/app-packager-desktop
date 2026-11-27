using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

/// <summary>The navigation control has buttons with only a glyph: one shows or hides the labels of the sidebar, one goes back. They get names here.</summary>
public static class NavigationNames
{
    public const string ToggleName = "Show or hide the sidebar labels";

    public const string BackName = "Back";

    /// <summary>Names each glyph button of the navigation template that has no name, and returns how many it named.</summary>
    public static int Apply(DependencyObject navigation)
    {
        var named = 0;
        foreach (var button in Descendants(navigation).OfType<ButtonBase>())
        {
            var name = IsToggle(button) ? ToggleName : button.Name == "PART_BackButton" ? BackName : null;
            if (name is null || AutomationProperties.GetName(button).Length > 0) continue;
            AutomationProperties.SetName(button, name);
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
