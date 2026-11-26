using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Gives the parts inside a date box or an editable list the name of their owner. A screen reader and a Tab walk reach the parts
/// (the text box, the calendar button), and without this they have no name.
/// </summary>
public static class TemplatePartNames
{
    public static readonly DependencyProperty FromOwnerProperty = DependencyProperty.RegisterAttached(
        "FromOwner", typeof(bool), typeof(TemplatePartNames), new PropertyMetadata(false, OnChanged));

    public static void SetFromOwner(DependencyObject element, bool value) => element.SetValue(FromOwnerProperty, value);

    public static bool GetFromOwner(DependencyObject element) => (bool)element.GetValue(FromOwnerProperty);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement owner || e.NewValue is not true) return;
        owner.Loaded += (_, _) => Apply(owner);
    }

    /// <summary>Names the parts that exist now; called when the owner has loaded.</summary>
    public static void Apply(FrameworkElement owner)
    {
        owner.ApplyTemplate();
        owner.UpdateLayout();
        var name = AutomationProperties.GetName(owner);
        if (name.Length == 0) return;
        foreach (var part in Descendants(owner))
        {
            if (part is DatePicker or DatePickerTextBox or System.Windows.Controls.TextBox)
            {
                if (part is System.Windows.Controls.Control box && box != owner && AutomationProperties.GetName(box).Length == 0)
                {
                    box.ApplyTemplate();
                    AutomationProperties.SetName(box, name);
                }
            }
            else if (owner is DatePicker && part is System.Windows.Controls.Button button && AutomationProperties.GetName(button).Length == 0)
                AutomationProperties.SetName(button, name + ", open the calendar");
        }
    }

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
