using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests.GuideImages;

/// <summary>Every guide image that a document shows is produced by a capture step or listed as pending, and the pending list only shrinks (decision D-21).</summary>
public sealed class GuideImageCoverageTests
{
    private static readonly Regex Reference = new(@"images/([A-Za-z0-9._-]+)\.png", RegexOptions.Compiled);

    private static string Root => CatalogServiceTests.RepositoryRoot();

    /// <summary>The ids of the images that the documents show.</summary>
    internal static SortedSet<string> Referenced()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories).Append(Path.Combine(Root, "README.md"));
        return new SortedSet<string>(files.Where(File.Exists).SelectMany(f => Reference.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)), StringComparer.Ordinal);
    }

    private static SortedSet<string> Pending() =>
        new(File.ReadAllLines(Path.Combine(Root, "tests", "ApplicationPackagerEngine.Tests", "GuideImages", "GuideImages.pending.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')), StringComparer.Ordinal);

    [Fact]
    public void EveryImageOfTheGuideIsProducedByAStepOrListedAsPending()
    {
        var produced = GuideCaptureSteps.Ids.ToHashSet(StringComparer.Ordinal);
        var unlisted = Referenced().Where(id => !produced.Contains(id) && !Pending().Contains(id)).ToList();

        Assert.True(unlisted.Count == 0, "No capture step produces these images and the pending list does not name them: " + string.Join(", ", unlisted));
    }

    [Fact]
    public void ThePendingListNamesNoProducedImageAndNoImageThatNoDocumentShows()
    {
        var produced = GuideCaptureSteps.Ids.ToHashSet(StringComparer.Ordinal);
        var referenced = Referenced();

        Assert.Empty(Pending().Where(produced.Contains));
        Assert.Empty(Pending().Where(id => !referenced.Contains(id)));
    }

    [Fact]
    public void EveryStepProducesAnImageThatADocumentShows()
    {
        var referenced = Referenced();

        Assert.Empty(GuideCaptureSteps.Ids.Where(id => !referenced.Contains(id)));
    }
}
