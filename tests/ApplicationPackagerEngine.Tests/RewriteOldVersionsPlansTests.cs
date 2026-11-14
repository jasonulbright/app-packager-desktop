using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

public sealed class RewriteOldVersionsPlansTests
{
    private static readonly StoredClause Clause = new("File", "Setting_1", "GreaterEquals", "1.0", @"C:\Program Files\App", "app.exe", true, "", "", "");
    private static readonly RewriteScope Scope = new("APD-Line:x|default|x64", "catalog:x", "P01", null, new SupersedenceSettings());

    private static RewritePlan PlanOf(params RewriteItem[] items) => new(items, ["'Old 0.9' has a required deployment (All Users)."]);

    [Fact]
    public void APlanWithNoRewritableClauseHasNoWindow()
    {
        var plan = PlanOf(new RewriteItem("App 1.0", "1.0", "DT", Clause, "not rewritten: no installed value"));

        Assert.Null(RewriteOldVersionsPlans.ForRewrite(plan, Scope, 0));
    }

    [Fact]
    public void TheWindowListsOneRowPerClauseWithTheConfirmationAndUndo()
    {
        var plan = PlanOf(new RewriteItem("App 1.0", "1.0", "DT", Clause, null), new RewriteItem("App 0.9", "0.9", "DT", Clause, "the operator is Equals, not GreaterEquals"));

        var (change, _, undo) = RewriteOldVersionsPlans.ForRewrite(plan, Scope, 2)!.Value;

        var item = Assert.Single(change.Items);
        Assert.Equal("App 1.0 / DT", item.Target);
        Assert.Equal("GreaterEquals -> IsEquals", item.Detail);
        Assert.True(change.HasUndo);
        Assert.True(change.IsDestructive);
        Assert.Equal(RewritePlanner.ConfirmationText, change.DestructiveConfirmation);
        Assert.NotNull(undo);
        Assert.Contains(change.Notes, n => n.StartsWith("Blocked: 'Old 0.9'"));
        Assert.Contains(change.Notes, n => n.StartsWith("Left: App 0.9 / DT: the operator is Equals"));
        Assert.Contains(change.Notes, n => n.StartsWith("2 earlier rewrite(s)"));
    }

    [Fact]
    public void TheResultRowsFollowWhatWasWrittenWhatFailedAndWhatWasNotReached()
    {
        var items = new[] { "A / one", "B / two", "C / three" }.Select(t => new ChangePlanItem("Rewrite clause", t, null)).ToList();
        var keys = new[] { ("A", "one"), ("B", "two"), ("C", "three") };
        var written = new RewriteLogEntry(DateTimeOffset.UtcNow, "A", "one", "GreaterEquals", "IsEquals", "Setting_1");

        var result = RewriteOldVersionsPlans.Result(items, keys, new RewriteOutcome([written], ["B / two: the site refused it"]));

        Assert.True(result.Items[0].Succeeded);
        Assert.False(result.Items[1].Succeeded);
        Assert.Equal("the site refused it", result.Items[1].Message);
        Assert.True(result.Items[2].Skipped);
        Assert.True(result.UndoAvailable);
        Assert.Equal("1 changed, 1 failed, 1 skipped.", result.Summary);
    }

    [Fact]
    public void ARestoreItemNamesTheOperatorThatComesBack()
    {
        var entry = new RewriteLogEntry(DateTimeOffset.UtcNow, "App 1.0", "DT", "GreaterEquals", "IsEquals", "Setting_1");

        var item = RewriteOldVersionsPlans.RestoreItemOf(entry);

        Assert.Equal("Restore clause", item.Action);
        Assert.Equal("App 1.0 / DT", item.Target);
        Assert.Equal("IsEquals -> GreaterEquals", item.Detail);
    }
}
