using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Decision D-07 of the suite: bold text is for the title of a field only, not for a window or page header, a summary or a status text,
/// and the text of a button is always bold, in both themes. The approved mock images draw a few more bold texts and three controls of
/// normal weight; each one is listed here by name, so any other new one still fails.
/// </summary>
public sealed class BoldTextRuleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace WpfUi = "http://schemas.lepo.co/wpfui/2022/xaml";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root() => Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");

    private static IEnumerable<string> AllXaml() => Directory.EnumerateFiles(Root(), "*.xaml", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static bool IsBold(string? weight) => weight is "SemiBold" or "Bold" or "ExtraBold" or "Black";

    [Fact]
    public void TheAppStyleMakesTheTextOfEveryButtonBold()
    {
        var app = XDocument.Load(Path.Combine(Root(), "App.xaml"));
        Assert.Contains(app.Descendants(Presentation + "ResourceDictionary"), d => ((string?)d.Attribute("Source"))?.EndsWith("Views/Styles/ButtonText.xaml") == true);
        var styles = XDocument.Load(Path.Combine(Root(), "Views", "Styles", "ButtonText.xaml")).Descendants(Presentation + "Style").ToList();

        foreach (var target in new[] { "ui:Button", "Button" })
        {
            // The keyed CancelSlot style (overhaul V1, Q-126) is based on this one; the style of the app is the one with no key.
            var style = Assert.Single(styles, s => (string?)s.Attribute("TargetType") == target && s.Attribute(Xaml + "Key") is null);
            Assert.Contains(style.Elements(Presentation + "Setter"), s => (string?)s.Attribute("Property") == "FontWeight" && IsBold((string?)s.Attribute("Value")));
        }
    }

    [Fact]
    public void NoButtonAndNoButtonStyleTurnsItsTextNormal()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml())
        {
            var relative = Path.GetRelativePath(Root(), file);
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var button in document.Descendants().Where(e => e.Name == WpfUi + "Button" || e.Name == Presentation + "Button"))
                if (button.Attribute("FontWeight") is { } weight && !IsBold(weight.Value))
                    offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)button).LineNumber} button FontWeight {weight.Value}");
            foreach (var style in document.Descendants(Presentation + "Style").Where(s => ((string?)s.Attribute("TargetType")) is "ui:Button" or "Button"))
                foreach (var setter in style.Elements(Presentation + "Setter").Where(s => (string?)s.Attribute("Property") == "FontWeight" && !IsBold((string?)s.Attribute("Value"))))
                    offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)setter).LineNumber} button style FontWeight {(string?)setter.Attribute("Value")}");
        }

        Assert.True(offenders.Count == 0, "A button must keep bold text: " + string.Join(", ", offenders));
    }

    // The windows whose own labels name a field: the title of a field is bold.
    private static readonly string[] FieldTitleFiles =
    [
        Path.Combine("Views", "Windows", "VendorImportWindow.xaml"),
        // The name of a parameter is the title of the box beside it.
        Path.Combine("Views", "Windows", "PivotsParametersWindow.xaml")
    ];

    // The bold texts that the mock draws: the file, then the text or the binding of the element.
    private static readonly (string File, string Text)[] MockBoldTexts =
    [
        ("ContextStrip.xaml", "{Binding SiteText}"),
        ("PackagesPage.xaml", "{Binding SelectionText}"),
        ("PackagesPage.xaml", "{Binding RunSummaryTitle}"),
        ("PackagesPage.xaml", "{Binding Mark}"),
        ("PackagesPage.xaml", "{Binding Application}"),
        ("SettingsPage.xaml", "Replace and uninstall requires Exact."),
        ("SettingsPage.xaml", "Bulk edit"),
        ("Mock.Dialogs.xaml", "{TemplateBinding Title}"),
        ("Mock.Settings.xaml", "{TemplateBinding Header}"),
        ("OneClickPlanWindow.xaml", "Action"),
        ("OneClickPlanWindow.xaml", "Changes apply to this run."),
        ("OneClickPlanWindow.xaml", "{Binding Application}"),
        ("PublishReviewWindow.xaml", "{Binding Application}"),
        ("WorkbenchWindow.xaml", "Build"),
        ("WorkbenchWindow.xaml", "Application"),
        ("WorkbenchWindow.xaml", "Profile"),
        ("WorkbenchWindow.xaml", "Icon")
    ];

    [Fact]
    public void OnlyTheTitleOfAFieldIsBoldOutsideAButton()
    {
        var offenders = new List<string>();
        var found = new HashSet<(string, string)>();
        foreach (var file in AllXaml())
        {
            var relative = Path.GetRelativePath(Root(), file);
            if (FieldTitleFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var element in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants()
                         .Where(e => e.Name.LocalName is "TextBlock" or "Run" or "Label" or "Span" or "Bold" && IsBold((string?)e.Attribute("FontWeight"))))
            {
                var key = (Path.GetFileName(file), (string?)element.Attribute("Text") ?? "");
                if (MockBoldTexts.Contains(key)) found.Add(key);
                else offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)element).LineNumber}");
            }
        }

        Assert.True(offenders.Count == 0, "These texts are bold and are not the title of a field (use the FieldLabel style for a field title): " + string.Join(", ", offenders));
        Assert.Equal(MockBoldTexts.Length, found.Count);
    }

    // The bold text styles of the mock: a strong word, the name of a field, a section title, the mark of an application, the number of a result.
    private static readonly string[] MockBoldTextStyles = ["SubHeader", "Mk.AppMarkText", "Mk.Strong", "Mk.FieldName", "Mk.H3", "Metric"];

    [Fact]
    public void NoStyleOfATextIsBoldExceptTheFieldLabel()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml())
            foreach (var style in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "Style")
                         .Where(s => (string?)s.Attribute("TargetType") is "TextBlock" or "Label" or "Run" && (string?)s.Attribute(Xaml + "Key") != "FieldLabel"
                             && !MockBoldTextStyles.Contains((string?)s.Attribute(Xaml + "Key"))))
                foreach (var setter in style.Elements(Presentation + "Setter").Where(x => (string?)x.Attribute("Property") == "FontWeight" && IsBold((string?)x.Attribute("Value"))))
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}:{((System.Xml.IXmlLineInfo)setter).LineNumber} ({(string?)style.Attribute(Xaml + "Key")})");

        Assert.True(offenders.Count == 0, "These text styles are bold; only the title of a field is: " + string.Join(", ", offenders));
    }

    // The controls of the mock whose text has the normal weight: the Menu entry of the sidebar, the glyph buttons of the title bar and the
    // site text of the status bar.
    private static readonly string[] MockNormalButtonStyles = ["Mk.NavToggle", "Mk.SystemButton", "Mk.FooterSwitcher"];

    [Fact]
    public void EveryStyleOfAButtonIsBoldOrBasedOnTheStyleThatIs()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml().Where(f => !f.EndsWith("ButtonText.xaml")))
            foreach (var style in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "Style")
                         .Where(s => ((string?)s.Attribute("TargetType"))?.Replace("{x:Type ", "").TrimEnd('}') is "ui:Button" or "Button" && (string?)s.Attribute(Xaml + "Key") is not null))
            {
                var bold = style.Elements(Presentation + "Setter").Any(x => (string?)x.Attribute("Property") == "FontWeight" && IsBold((string?)x.Attribute("Value")));
                if (!bold && style.Attribute("BasedOn") is null && !MockNormalButtonStyles.Contains((string?)style.Attribute(Xaml + "Key")))
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}:{((System.Xml.IXmlLineInfo)style).LineNumber} ({(string?)style.Attribute(Xaml + "Key")})");
            }

        Assert.True(offenders.Count == 0, "A button style needs a bold weight or a BasedOn that has it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheFieldLabelStyleOfSettingsIsBold()
    {
        var style = XDocument.Load(Path.Combine(Root(), "Views", "Pages", "SettingsPage.xaml")).Descendants(Presentation + "Style")
            .Single(s => (string?)s.Attribute(Xaml + "Key") == "FieldLabel");

        Assert.Contains(style.Elements(Presentation + "Setter"), x => (string?)x.Attribute("Property") == "FontWeight" && IsBold((string?)x.Attribute("Value")));
    }
}
