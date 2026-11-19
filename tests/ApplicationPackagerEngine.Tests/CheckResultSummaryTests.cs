using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The log line after Check Latest and Check ConfigMgr, over a fake list of statuses.</summary>
public sealed class CheckResultSummaryTests
{
    private static List<string?> Statuses(params (string Status, int Count)[] groups) =>
        groups.SelectMany(g => Enumerable.Repeat<string?>(g.Status, g.Count)).ToList();

    [Fact]
    public void StatesTheEligibleAndTheSelectedProducts()
    {
        var list = Statuses(("Update available", 12), ("Up to date", 300), ("Not found in ConfigMgr", 6));
        Assert.Equal("Check ConfigMgr finished: 12 of 318 products need an update; 12 selected.", CheckResultSummary.Line("Check ConfigMgr", list, 12));
    }

    [Fact]
    public void CountsAStatusWithACveCountAsEligible()
    {
        var list = Statuses(("Update available (3 CVEs, 1 in KEV)", 2), ("Up to date", 1));
        Assert.Equal("Check ConfigMgr finished: 2 of 3 products need an update; 2 selected.", CheckResultSummary.Line("Check ConfigMgr", list, 2));
    }

    [Fact]
    public void GivesALineForTheZeroCaseAndForNothingChecked()
    {
        Assert.Equal("Check ConfigMgr finished: 0 of 5 products need an update; 0 selected.",
            CheckResultSummary.Line("Check ConfigMgr", Statuses(("Up to date", 5)), 0));
        Assert.Equal("Check ConfigMgr finished: 0 of 0 products need an update; 0 selected.",
            CheckResultSummary.Line("Check ConfigMgr", [], 0));
    }

    [Fact]
    public void NamesTheFailedChecksAndDoesNotCountThemAsEligible()
    {
        var list = Statuses(("Update available", 3), ("Error", 2), ("Up to date", 5));
        Assert.Equal("Check ConfigMgr finished: 3 of 10 products need an update; 3 selected; 2 checks failed.", CheckResultSummary.Line("Check ConfigMgr", list, 3));
    }

    [Fact]
    public void UsesTheSingularForOneProductAndOneFailedCheck()
    {
        Assert.Equal("Check Latest finished: 0 of 1 product needs an update; 1 check failed.",
            CheckResultSummary.Line("Check Latest", Statuses(("Error", 1)), null));
        Assert.Equal("Check Latest finished: 1 of 1 product needs an update.",
            CheckResultSummary.Line("Check Latest", Statuses(("Update available", 1)), null));
    }

    [Fact]
    public void LeavesOutTheSelectionWhenTheCheckSelectsNothing()
    {
        var list = Statuses(("Update available", 4), ("Up to date", 6));
        Assert.Equal("Check Latest finished: 4 of 10 products need an update.", CheckResultSummary.Line("Check Latest", list, null));
    }
}
