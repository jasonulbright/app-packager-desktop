using System.Windows;

namespace ApplicationPackagerUI.Helpers;

/// <summary>What a control of the Settings page tells the settings search. The search reads the page itself; these add what the page does not say.</summary>
public static class SettingsSearchHints
{
    /// <summary>The name of a result, for a control that has no label of its own.</summary>
    public static readonly DependencyProperty TitleProperty = Register("Title");

    /// <summary>More words that find the control. They do not show.</summary>
    public static readonly DependencyProperty KeywordsProperty = Register("Keywords");

    /// <summary>Why the control is unavailable, in words the app already uses. A result shows it while the control is disabled or hidden.</summary>
    public static readonly DependencyProperty UnavailableProperty = Register("Unavailable");

    /// <summary>The parameter that a control built in code edits.</summary>
    public static readonly DependencyProperty KeyProperty = Register("Key");

    /// <summary>True for a button that belongs to another result, for example Browse.</summary>
    public static readonly DependencyProperty ExcludeProperty =
        DependencyProperty.RegisterAttached("Exclude", typeof(bool), typeof(SettingsSearchHints), new PropertyMetadata(false));

    private static DependencyProperty Register(string name) =>
        DependencyProperty.RegisterAttached(name, typeof(string), typeof(SettingsSearchHints), new PropertyMetadata(null));

    public static string? GetTitle(DependencyObject element) => (string?)element.GetValue(TitleProperty);
    public static void SetTitle(DependencyObject element, string? value) => element.SetValue(TitleProperty, value);
    public static string? GetKeywords(DependencyObject element) => (string?)element.GetValue(KeywordsProperty);
    public static void SetKeywords(DependencyObject element, string? value) => element.SetValue(KeywordsProperty, value);
    public static string? GetUnavailable(DependencyObject element) => (string?)element.GetValue(UnavailableProperty);
    public static void SetUnavailable(DependencyObject element, string? value) => element.SetValue(UnavailableProperty, value);
    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);
    public static bool GetExclude(DependencyObject element) => (bool)element.GetValue(ExcludeProperty);
    public static void SetExclude(DependencyObject element, bool value) => element.SetValue(ExcludeProperty, value);
}
