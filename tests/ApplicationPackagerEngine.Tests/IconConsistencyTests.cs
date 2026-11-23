using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>One action label shows one glyph on every button of the interface (Q-113).</summary>
public sealed class IconConsistencyTests
{
    private static readonly string[] Labels =
        ["Refresh", "Export HTML", "Export JSON", "Export CSV", "Cancel", "Save", "Delete", "Remove", "Copy", "Browse"];

    private static readonly Regex Button = new(@"<ui:(?:Button|SplitButton)\b[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Content = new(@"\bContent=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Glyph = new(@"\bIcon=""\{ui:SymbolIcon\s+([A-Za-z0-9]+)\}""", RegexOptions.Compiled);

    private static string Normalize(string label) => label.Replace("_", "").TrimEnd('.', '…').Trim();

    private static List<(string File, string Label, string Glyph)> Buttons()
    {
        var ui = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var found = new List<(string, string, string)>();
        foreach (var file in Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match tag in Button.Matches(File.ReadAllText(file)))
            {
                var content = Content.Match(tag.Value);
                var glyph = Glyph.Match(tag.Value);
                if (content.Success && glyph.Success)
                {
                    found.Add((Path.GetRelativePath(ui, file), Normalize(content.Groups[1].Value), glyph.Groups[1].Value));
                }
            }
        }

        return found;
    }

    [Fact]
    public void TheScanFindsButtonsForEveryLabel()
    {
        var all = Buttons();

        var missing = Labels.Where(label => !all.Any(b => b.Label == label)).ToList();
        Assert.True(missing.Count == 0, "No icon button found for: " + string.Join(", ", missing));
    }

    [Fact]
    public void OneActionLabelShowsOneGlyph()
    {
        var conflicts = Buttons()
            .Where(b => Labels.Contains(b.Label, StringComparer.Ordinal))
            .GroupBy(b => b.Label)
            .Where(g => g.Select(b => b.Glyph).Distinct().Count() > 1)
            .Select(g => g.Key + ": " + string.Join(", ", g.GroupBy(b => b.Glyph).Select(x => x.Key + " (" + string.Join("; ", x.Select(b => b.File).Distinct()) + ")")))
            .ToList();

        Assert.True(conflicts.Count == 0, "One label shows more than one glyph: " + string.Join(" | ", conflicts));
    }

    [Fact]
    public void OneGlyphMarksOneExportFormat()
    {
        var formats = Buttons().Where(b => b.Label.StartsWith("Export ", StringComparison.Ordinal) && b.Label.Length <= 12).ToList();
        var shared = formats.GroupBy(b => b.Glyph).Where(g => g.Select(b => b.Label).Distinct().Count() > 1).Select(g => g.Key).ToList();

        Assert.True(shared.Count == 0, "A glyph marks more than one export format: " + string.Join(", ", shared));
    }
}
