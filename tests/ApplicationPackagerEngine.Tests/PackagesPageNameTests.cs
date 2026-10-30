using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// User strings name the start page App Packager and send the reader to the Catalog button for
/// hidden applications. The scan reads every string literal of the C# sources and every
/// attribute value of the XAML sources; comments are not user strings.
/// </summary>
public sealed partial class PackagesPageNameTests
{
    [GeneratedRegex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex CSharpString();

    [GeneratedRegex("=\"[^\"]*\"")]
    private static partial Regex XamlAttribute();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();

    /// <summary>Each user string of the UI and build sources with its file and line.</summary>
    internal static IEnumerable<(string File, int Line, string Text)> UserStrings()
    {
        var src = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(src, file);
            if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimStart();
                    if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                    foreach (Match m in CSharpString().Matches(lines[i])) yield return (relative, i + 1, m.Value);
                }
            }
            else if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                var text = XamlComment().Replace(File.ReadAllText(file), m => new string('\n', m.Value.Count(c => c == '\n')));
                foreach (Match m in XamlAttribute().Matches(text))
                    yield return (relative, text[..m.Index].Count(c => c == '\n') + 1, m.Value);
            }
        }
    }

    private static void AssertNoUserString(params string[] phrases)
    {
        var found = UserStrings()
            .Where(s => phrases.Any(p => s.Text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Select(s => $"{s.File}:{s.Line}: {s.Text}")
            .ToList();
        Assert.True(found.Count == 0, "User strings name a removed or renamed page:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void TheScanFindsTheUserStringsOfBothFileKinds()
    {
        var strings = UserStrings().ToList();
        Assert.Contains(strings, s => s.File.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && s.Text.Contains("Discover unmanaged software", StringComparison.Ordinal));
        Assert.Contains(strings, s => s.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && s.Text.Contains("Discover unmanaged software", StringComparison.Ordinal));
    }

    [Fact]
    public void NoUserStringNamesThePackagesPageOrGrid() =>
        AssertNoUserString("Packages page", "Packages grid", "Export Packages");

    [Fact]
    public void NoUserStringNamesTheApplicationsPage() =>
        AssertNoUserString("Applications page", "Applications grid");
}
