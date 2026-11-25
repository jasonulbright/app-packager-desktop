using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-127: every action that works through a counted set shows "N of M" in the status line while it runs, and in the line of a finished or cancelled run.
/// One wording for all of them (RunProgress, PackagingRunTally).
/// </summary>
public sealed class RunProgressTests
{
    [Theory]
    [InlineData("Check Latest", 3, 328, "7-Zip", "Check Latest: 3 of 328 — 7-Zip")]
    [InlineData("Check ConfigMgr", 1, 5, "PuTTY", "Check ConfigMgr: 1 of 5 — PuTTY")]
    [InlineData("CVEs", 12, 40, "Git", "CVEs: 12 of 40 — Git")]
    [InlineData("Staging", 2, 2, "VLC", "Staging: 2 of 2 — VLC")]
    [InlineData("Packaging", 1, 1, "Notepad++", "Packaging: 1 of 1 — Notepad++")]
    [InlineData("Publishing to WSUS", 4, 9, "Zoom", "Publishing to WSUS: 4 of 9 — Zoom")]
    [InlineData("One Click", 7, 8, "Firefox", "One Click: 7 of 8 — Firefox")]
    public void TheRunningLineCountsTheItemOfTheSet(string action, int current, int total, string item, string expected) =>
        Assert.Equal(expected, RunProgress.Running(action, current, total, item));

    [Fact]
    public void TheLineBeforeTheFirstItemSaysNoneOfTheSet() =>
        Assert.Equal("Check ConfigMgr: 0 of 328", RunProgress.Starting("Check ConfigMgr", 328));

    [Fact]
    public void AnItemWithoutANameLeavesTheCountAlone() =>
        Assert.Equal("Check Latest: 2 of 9", RunProgress.Running("Check Latest", 2, 9));

    [Theory]
    [InlineData("One Click", 3, 8, "product", "done", "One Click cancelled: 3 of 8 products done.")]
    [InlineData("Check ConfigMgr", 0, 1, "product", "checked", "Check ConfigMgr cancelled: 0 of 1 product checked.")]
    public void TheCancelledLineCountsTheSet(string action, int done, int total, string noun, string verb, string expected) =>
        Assert.Equal(expected, RunProgress.Cancelled(action, done, total, noun, verb));

    [Fact]
    public void ACancelledCheckLineIsTheLineOfTheTally()
    {
        var tally = new PackagingRunTally();
        tally.RecordSuccess();
        tally.RecordSuccess();
        tally.MarkCancelled();

        Assert.Equal("Check ConfigMgr cancelled: 2 of 328 products checked.", tally.CheckCancelledLine("Check ConfigMgr", 328));
    }

    [Fact]
    public void EveryCountedActionOfThePackagesPageSetsItsLineWithTheSharedPattern()
    {
        var root = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "ViewModels", "Pages");
        var text = string.Join('\n', Directory.EnumerateFiles(root, "PackagesViewModel*.cs").Select(File.ReadAllText));

        foreach (var action in new[] { "\"Check Latest\"", "\"Check ConfigMgr\"", "\"CVEs\"", "\"Staging\"", "\"Packaging\"", "\"Publishing to WSUS\"", "\"One Click\"" })
            Assert.Matches(new Regex(@"RunProgress\.Running\(" + Regex.Escape(action) + ",", RegexOptions.None), text);
        Assert.Contains("RunProgress.Starting(\"Check ConfigMgr\"", text, StringComparison.Ordinal);
        Assert.Contains("RunProgress.Cancelled(\"One Click\"", text, StringComparison.Ordinal);
        // No loop writes its own "N of M" line: the wording is in one place.
        Assert.DoesNotMatch(new Regex(@"StatusText = \$""[^""]*\{ProgressCurrent\} of \{ProgressTotal\}"), text);
        Assert.DoesNotMatch(new Regex(@"One Click \{i"), text);
    }
}
