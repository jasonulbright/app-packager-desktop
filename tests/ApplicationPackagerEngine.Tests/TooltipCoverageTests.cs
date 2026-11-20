using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-103: every control that takes an action or a value (a button, an input, a check box, a radio button, a tree
/// entry) explains itself with a tooltip. The scan reads every XAML file of the windows, pages and controls, so a new
/// control without a tooltip fails here.
/// </summary>
public sealed class TooltipCoverageTests
{
    /// <summary>The control types that must carry a tooltip.</summary>
    private static readonly HashSet<string> Kinds =
    [
        "Button", "CheckBox", "RadioButton", "ToggleButton", "ComboBox", "TextBox", "PasswordBox", "DatePicker", "NumberBox",
        "SplitButton", "DropDownButton", "Hyperlink", "TreeViewItem", "NavigationViewItem"
    ];

    /// <summary>The words of a dialog button that closes the dialog. The label is the whole explanation.</summary>
    private static readonly HashSet<string> DialogWords = new(StringComparer.OrdinalIgnoreCase) { "OK", "Cancel", "Close" };

    /// <summary>A check box or radio button whose label is a full sentence of at least this many words explains itself.</summary>
    private const int SentenceWords = 5;

    private static string Attribute(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    private static bool HasToolTip(XElement e) =>
        e.Attributes().Any(a => a.Name.LocalName.StartsWith("ToolTip", StringComparison.Ordinal) || a.Name.LocalName.StartsWith("ToolTipService.", StringComparison.Ordinal))
        || e.Elements().Any(c => c.Name.LocalName.Contains(".ToolTip", StringComparison.Ordinal));

    /// <summary>True when the label is only the dialog word, ignoring the access key underscore.</summary>
    private static bool IsDialogButton(XElement e) =>
        e.Name.LocalName == "Button" && DialogWords.Contains(Attribute(e, "Content").Replace("_", "")) && !Attribute(e, "Content").Contains('{');

    private static bool IsSentenceLabel(XElement e)
    {
        if (e.Name.LocalName is not ("CheckBox" or "RadioButton")) return false;
        var content = Attribute(e, "Content");
        return !content.Contains('{') && content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= SentenceWords;
    }

    /// <summary>A text box that only shows a result (read-only) is labelled by its heading and needs no tooltip.</summary>
    private static bool IsReadOnlyDisplay(XElement e) =>
        e.Name.LocalName == "TextBox" && (Attribute(e, "IsReadOnly") == "True" || Attribute(e, "Text").Contains("Mode=OneWay", StringComparison.Ordinal));

    /// <summary>The scan skips control templates: the buttons of a template belong to the control that owns it.</summary>
    private static bool InControlTemplate(XElement e) => e.Ancestors().Any(a => a.Name.LocalName is "ControlTemplate" or "Style");

    [Fact]
    public void EveryActionAndInputControlHasATooltip()
    {
        var ui = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var missing = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine("Views", "Styles"), StringComparison.Ordinal) || Path.GetFileName(file) == "App.xaml") continue;
            foreach (var e in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants().Where(d => Kinds.Contains(d.Name.LocalName)))
            {
                if (InControlTemplate(e) || IsDialogButton(e) || IsSentenceLabel(e) || IsReadOnlyDisplay(e)) continue;
                scanned++;
                if (!HasToolTip(e)) missing.Add($"{Path.GetRelativePath(ui, file)}:{((System.Xml.IXmlLineInfo)e).LineNumber} {e.Name.LocalName}");
            }
        }
        Assert.True(scanned > 300, "The scan found too few controls; the control kinds or the folder changed.");
        Assert.True(missing.Count == 0, $"{missing.Count} controls have no tooltip:{Environment.NewLine}" + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EverySidebarEntryHasADescriptionThatAddsToItsTitle()
    {
        foreach (var item in SidebarRegistry.CreateDefault().Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Description), $"The sidebar entry '{item.Title}' has no description.");
            Assert.NotEqual(item.Title, item.Description);
        }
    }
}
