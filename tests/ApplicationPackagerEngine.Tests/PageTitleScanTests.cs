using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A Page does not pass the theme text brush to its content, so a title TextBlock without its own
/// Foreground draws black on the dark theme. The scan reads every XAML source of the UI project.
/// </summary>
public sealed class PageTitleScanTests
{
    /// <summary>A text this large or larger is a title or a headline.</summary>
    private const double TitleFontSize = 22;

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string UiRoot() => Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");

    private static IEnumerable<string> XamlFiles(params string[] folders) =>
        folders.SelectMany(f => Directory.EnumerateFiles(Path.Combine(UiRoot(), f), "*.xaml", SearchOption.AllDirectories));

    private static bool SetsForeground(XElement style) =>
        style.Elements(Presentation + "Setter").Any(s => (string?)s.Attribute("Property") == "Foreground");

    /// <summary>The keys of all styles that set a Foreground, directly or through a BasedOn chain of styles in the project.</summary>
    private static HashSet<string> ForegroundStyleKeys()
    {
        var styles = XamlFiles("Views", "Controls")
            .SelectMany(f => XDocument.Load(f).Descendants(Presentation + "Style"))
            .Where(s => (string?)s.Attribute(Xaml + "Key") is not null)
            .GroupBy(s => (string)s.Attribute(Xaml + "Key")!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var keys = new HashSet<string>(styles.Where(p => p.Value.Any(SetsForeground)).Select(p => p.Key));
        bool changed;
        do
        {
            changed = false;
            foreach (var (key, list) in styles.Where(p => !keys.Contains(p.Key)))
            {
                var basedOn = list.Select(s => (string?)s.Attribute("BasedOn")).FirstOrDefault(b => b is not null);
                if (basedOn is null) continue;
                var parent = basedOn.Trim('{', '}').Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
                if (!keys.Contains(parent)) continue;
                keys.Add(key);
                changed = true;
            }
        } while (changed);
        return keys;
    }

    private static string StyleKey(XElement textBlock)
    {
        var style = (string?)textBlock.Attribute("Style");
        return style is null ? "" : style.Trim('{', '}').Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
    }

    /// <summary>
    /// Each large TextBlock of the XAML text that has no Foreground, no inline style with a Foreground and no keyed style
    /// with a Foreground. The fixed texts in the list below are not titles.
    /// </summary>
    internal static List<string> TitlesWithoutForeground(IEnumerable<(string Name, XDocument Document)> documents, HashSet<string> foregroundStyles)
    {
        var found = new List<string>();
        foreach (var (name, document) in documents)
        {
            foreach (var text in document.Descendants(Presentation + "TextBlock"))
            {
                var isLarge = double.TryParse((string?)text.Attribute("FontSize"), System.Globalization.CultureInfo.InvariantCulture, out var size) && size >= TitleFontSize;
                var isNamedTitle = StyleKey(text).EndsWith("Title", StringComparison.Ordinal);
                if (!isLarge && !isNamedTitle) continue;
                var hasForeground = text.Attribute("Foreground") is not null
                    || text.Element(Presentation + "TextBlock.Foreground") is not null
                    || text.Element(Presentation + "TextBlock.Style")?.Descendants(Presentation + "Style").Any(SetsForeground) == true
                    || foregroundStyles.Contains(StyleKey(text));
                if (hasForeground) continue;
                var line = ((System.Xml.IXmlLineInfo)text).LineNumber;
                found.Add($"{name}:{line}: {(string?)text.Attribute("Text") ?? "(no text)"}");
            }
        }
        return found;
    }

    [Fact]
    public void NoTitleTextBlockOfAPageOrWindowLacksAForeground()
    {
        var documents = XamlFiles("Views", "Controls")
            .Select(f => (Name: Path.GetRelativePath(UiRoot(), f), Document: XDocument.Load(f, LoadOptions.SetLineInfo)))
            .ToList();
        Assert.True(documents.Count > 20, "The scan found too few XAML files.");
        var missing = TitlesWithoutForeground(documents, ForegroundStyleKeys());
        Assert.True(missing.Count == 0, "Title text without a Foreground draws black on the dark theme:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void TheScanFlagsATitleThatLacksAForegroundAndAcceptsEveryWayToSetOne()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
                <TextBlock Text="Bare" FontSize="28" />
                <TextBlock Text="BareStyleName" Style="{StaticResource PageTitle}" />
                <TextBlock Text="Direct" FontSize="28" Foreground="Red" />
                <TextBlock Text="Keyed" FontSize="28" Style="{StaticResource WithBrush}" />
                <TextBlock Text="Inline" FontSize="28">
                  <TextBlock.Style><Style TargetType="TextBlock"><Setter Property="Foreground" Value="Red" /></Style></TextBlock.Style>
                </TextBlock>
                <TextBlock Text="Small" FontSize="14" />
              </StackPanel>
            </Page>
            """;
        var found = TitlesWithoutForeground([("sample.xaml", XDocument.Parse(xaml, LoadOptions.SetLineInfo))], ["WithBrush"]);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Contains("Bare:") || f.EndsWith("Bare"));
        Assert.Contains(found, f => f.EndsWith("BareStyleName"));
    }

    [Fact]
    public void EverySidebarPageShowsItsTitleThroughThePageHeader()
    {
        var pages = new[]
        {
            "PackagesPage", "AddAppPage", "AddFilePage", "SettingsPage", "InstallerAnalysisPage", "DetectionTesterPage", "DeploymentHelperPage",
            "PivotsScriptsPage", "CollectionCompliancePage", "MaintenanceWindowsPage", "DpContentManagerPage", "SiteHygienePage"
        };
        var without = pages
            .Where(p => !XDocument.Load(Path.Combine(UiRoot(), "Views", "Pages", p + ".xaml")).Descendants().Any(e => e.Name.LocalName == "PageHeader"))
            .ToList();
        Assert.True(without.Count == 0, "These pages do not use the shared page header: " + string.Join(", ", without));
    }
}
