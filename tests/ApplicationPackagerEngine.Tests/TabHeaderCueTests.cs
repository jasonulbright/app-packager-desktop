using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The selected tab shows with the primary text color and an accent underline, never with bold text (decision D-07 of the suite:
/// bold is for the title of a field and for the text of a button).
/// </summary>
public sealed class TabHeaderCueTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string Root() => Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");

    private static IEnumerable<string> AllXaml() => Directory.EnumerateFiles(Root(), "*.xaml", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void NoTriggerOrSetterOfATabHeaderTurnsItsTextBold()
    {
        var offenders = new List<string>();
        foreach (var file in AllXaml())
            foreach (var setter in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "Setter")
                         .Where(s => (string?)s.Attribute("TargetName") == "TabHeaderText" && (string?)s.Attribute("Property") == "FontWeight"))
                offenders.Add($"{Path.GetRelativePath(Root(), file)}:{((System.Xml.IXmlLineInfo)setter).LineNumber}");

        Assert.True(offenders.Count == 0, "A selected tab is not bold: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryTabHeaderTemplateStillShowsTheSelectedTabByColorAndUnderline()
    {
        var checkedTemplates = 0;
        foreach (var file in AllXaml())
            foreach (var template in XDocument.Load(file).Descendants(Presentation + "DataTemplate")
                         .Where(t => t.Descendants(Presentation + "TextBlock").Any(b => ((string?)b.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")) == "TabHeaderText")))
            {
                checkedTemplates++;
                var setters = template.Descendants(Presentation + "Setter").Where(s => (string?)s.Attribute("TargetName") == "TabHeaderText" || (string?)s.Attribute("TargetName") == "TabHeaderUnderline").ToList();
                Assert.Contains(setters, s => (string?)s.Attribute("TargetName") == "TabHeaderText" && (string?)s.Attribute("Property") == "Foreground");
                Assert.Contains(setters, s => (string?)s.Attribute("TargetName") == "TabHeaderUnderline" && (string?)s.Attribute("Property") == "Background");
            }

        Assert.True(checkedTemplates >= 8, $"Only {checkedTemplates} tab header templates found.");
    }
}
