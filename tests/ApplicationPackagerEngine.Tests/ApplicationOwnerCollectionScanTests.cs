using ApplicationPackagerEngine.Tests.Compliance;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A test class that creates a WPF Application, or reads the theme resources next to one, must not run in parallel with another: Application.Current is process-wide and its unfrozen resources fail on every other UI thread.</summary>
public sealed class ApplicationOwnerCollectionScanTests
{
    private static readonly Regex CreatesApplication = new(@"WithApplication\(|new\s+(System\.Windows\.)?Application\s*\(", RegexOptions.Compiled);

    [Fact]
    public void EveryTestClassThatCreatesAnApplicationRunsInTheSerialCollection()
    {
        var root = Path.Combine(CatalogServiceTests.RepositoryRoot(), "tests", "ApplicationPackagerEngine.Tests");
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.EndsWith("RenderKit.cs", StringComparison.Ordinal) || file.EndsWith(nameof(ApplicationOwnerCollectionScanTests) + ".cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (!CreatesApplication.IsMatch(text) || text.Contains("Collection(ProcessEnvironmentCollection.Name)", StringComparison.Ordinal)) continue;
            wrong.Add(Path.GetRelativePath(root, file));
        }
        Assert.True(wrong.Count == 0, "These files create an Application outside the serial collection: " + string.Join(", ", wrong));
    }

    [Fact]
    public void TheThemeResourceWindowTestsRunInTheSerialCollection()
    {
        var file = Path.Combine(CatalogServiceTests.RepositoryRoot(), "tests", "ApplicationPackagerEngine.Tests", "DiscoveryWindowTests.cs");
        Assert.Contains("Collection(ProcessEnvironmentCollection.Name)", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
