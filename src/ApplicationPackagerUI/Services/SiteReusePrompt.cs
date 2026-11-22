using System.Windows;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

/// <summary>The question that a reused site code asks: same site with another provider, or a different environment (B9 question 1).</summary>
public static class SiteReusePrompt
{
    public const string Title = "Site code already in use";

    public static string Text(SiteInfo existing) =>
        $"Site code {existing.SiteCode} is already used by a site{(existing.DefaultProvider.Length > 0 ? " with the SMS Provider " + existing.DefaultProvider : "")}.\n\n"
        + "Yes: this is the same site with another SMS Provider. The existing data is used.\n"
        + "No: this is a different environment that reuses the code. It gets its own data folder.\n"
        + "Cancel: do not add a site.";

    public static SiteReuseAnswer Ask(Window? owner, SiteInfo existing)
    {
        var answer = owner is null
            ? MessageBox.Show(Text(existing), Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show(owner, Text(existing), Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SiteReuseAnswer.SameSiteOtherProvider,
            MessageBoxResult.No => SiteReuseAnswer.DifferentEnvironment,
            _ => SiteReuseAnswer.Cancel,
        };
    }
}
