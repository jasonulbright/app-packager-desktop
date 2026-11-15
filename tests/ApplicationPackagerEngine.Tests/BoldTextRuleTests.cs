using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Decision D-07 of the suite: bold text is for the title of a field only, not for a window or page header, a summary or a status text,
/// and the text of a button is always bold, in both themes.
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
            var style = Assert.Single(styles, s => (string?)s.Attribute("TargetType") == target);
            Assert.Null(style.Attribute(Xaml + "Key"));
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
    private static readonly string[] FieldTitleFiles = [Path.Combine("Views", "Windows", "VendorImportWindow.xaml")];

    [Fact]
    public void OnlyTheTitleOfAFieldIsBoldOutsideAButton()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml())
        {
            var relative = Path.GetRelativePath(Root(), file);
            if (FieldTitleFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var element in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants()
                         .Where(e => e.Name.LocalName is "TextBlock" or "Run" or "Label" or "Span" or "Bold" && IsBold((string?)e.Attribute("FontWeight"))))
                offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)element).LineNumber}");
        }

        Assert.True(offenders.Count == 0, "These texts are bold and are not the title of a field (use the FieldLabel style for a field title): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoStyleOfATextIsBoldExceptTheFieldLabel()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml())
            foreach (var style in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "Style")
                         .Where(s => (string?)s.Attribute("TargetType") is "TextBlock" or "Label" or "Run" && (string?)s.Attribute(Xaml + "Key") != "FieldLabel"))
                foreach (var setter in style.Elements(Presentation + "Setter").Where(x => (string?)x.Attribute("Property") == "FontWeight" && IsBold((string?)x.Attribute("Value"))))
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}:{((System.Xml.IXmlLineInfo)setter).LineNumber} ({(string?)style.Attribute(Xaml + "Key")})");

        Assert.True(offenders.Count == 0, "These text styles are bold; only the title of a field is: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryStyleOfAButtonIsBoldOrBasedOnTheStyleThatIs()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml().Where(f => !f.EndsWith("ButtonText.xaml")))
            foreach (var style in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "Style")
                         .Where(s => ((string?)s.Attribute("TargetType"))?.Replace("{x:Type ", "").TrimEnd('}') is "ui:Button" or "Button" && (string?)s.Attribute(Xaml + "Key") is not null))
            {
                var bold = style.Elements(Presentation + "Setter").Any(x => (string?)x.Attribute("Property") == "FontWeight" && IsBold((string?)x.Attribute("Value")));
                if (!bold && style.Attribute("BasedOn") is null)
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
