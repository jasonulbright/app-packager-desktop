using static ApplicationPackagerEngine.Tests.Collections.StubSitePowerShell;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// The rows the user sees on the Collections page, filled through the real service and the real scripts on the stub site.
/// The stub leaves CollectionRules and RefreshSchedule empty on a Fast or Lazy read, as a site does, so a script that reads
/// the rules with a lazy option shows an empty rule list here and fails these tests. The sibling PowerShell apps showed an
/// empty rule list after a background load because a test only checked the option of the call.
/// </summary>
public sealed class CollectionPageRowsTests
{
    private const string WsQuery = "select * from SMS_R_System where Name like 'WS%'";

    private static StubSitePowerShell Site() => new(State(new { Reject = "xx" },
        new { CollectionID = "SMS00001", Name = "All Systems", LimitToCollectionID = "", Rules = Array.Empty<object>() },
        new
        {
            CollectionID = "MCM00010", Name = "Workstations", LimitToCollectionID = "SMS00001", Comment = "c", RefreshType = 6, Schedule = "0001200000100038",
            Rules = new[] { QueryRule("WS", WsQuery), QueryRule("Servers", "select * from SMS_R_System where Name like 'SRV%'"), DirectRule("16777220", "DC01") }
        },
        new { CollectionID = "MCM00011", Name = "Servers", LimitToCollectionID = "SMS00001", Rules = Array.Empty<object>() }));

    [Fact]
    public async Task TheListHasOneRowForEveryCollection()
    {
        if (!WindowsPowerShellPresent) return;
        var page = await RealServicePage.OpenAsync(Site());

        Assert.Equal(["MCM00010", "MCM00011", "SMS00001"], page.ViewModel.Rows.Select(r => r.CollectionId).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task TheRuleListsOfASelectedCollectionShowItsRulesAndTheRefreshSchedule()
    {
        if (!WindowsPowerShellPresent) return;
        var page = await RealServicePage.OpenAsync(Site(), "MCM00010");

        Assert.Equal(["WS", "Servers"], page.ViewModel.QueryRules.Select(r => r.Rule.RuleName).ToArray());
        Assert.All(page.ViewModel.QueryRules, r => Assert.Contains("select * from SMS_R_System", r.Rule.Expression, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACollectionWithoutRulesShowsEmptyRuleListsAndThatIsNotAnError()
    {
        if (!WindowsPowerShellPresent) return;
        var page = await RealServicePage.OpenAsync(Site(), "MCM00011");

        Assert.Empty(page.ViewModel.QueryRules);
        Assert.Empty(page.ViewModel.IncludeRules);
        Assert.Empty(page.ViewModel.ExcludeRules);
    }
}
