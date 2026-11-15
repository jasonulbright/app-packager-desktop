using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The leftovers of the rewrite window: Restore for one line only, the plan read again before the write.</summary>
public sealed class RewriteLeftoversTests
{
    private const string LineA = "APD-Line:a|default|x64";
    private const string LineB = "APD-Line:b|default|x64";

    private static readonly StoredClause Clause = new("File", "Setting_1", "GreaterEquals", "1.0", @"C:\App", "app.exe", true, "", "", "");
    private static readonly RewriteScope Scope = new(LineA, "catalog:a", "P01", null, new SupersedenceSettings());

    private static RewriteLogEntry Entry(int minute, string app, string line, bool restored = false) =>
        new(new DateTimeOffset(2026, 11, 15, 10, minute, 0, TimeSpan.Zero), app, "DT", restored ? "IsEquals" : "GreaterEquals",
            restored ? "GreaterEquals" : "IsEquals", "Setting_1", restored, line);

    // ---- Restore for one line ----

    [Fact]
    public void RestoreOfOneLineLeavesTheRewritesOfAnotherLine()
    {
        var log = new[] { Entry(1, "A 1.0", LineA), Entry(2, "B 1.0", LineB) };

        var forA = RewriteLog.ToRestore(log, ["A 1.0", "B 1.0"], LineA);
        var forB = RewriteLog.ToRestore(log, ["A 1.0", "B 1.0"], LineB);

        Assert.Equal(["A 1.0"], forA.Select(e => e.Application));
        Assert.Equal(["B 1.0"], forB.Select(e => e.Application));
    }

    [Fact]
    public void AnEntryFromBeforeTheLogCarriedTheLineStaysOpenToEveryLine()
    {
        var log = new[] { Entry(1, "Old 1.0", line: null!) };

        Assert.Single(RewriteLog.ToRestore(log, ["Old 1.0"], LineA));
        Assert.Single(RewriteLog.ToRestore(log, ["Old 1.0"], LineB));
        Assert.Single(RewriteLog.ToRestore(log, ["Old 1.0"]));
    }

    [Fact]
    public void WithoutALineEveryEntryIsRestorable()
    {
        var log = new[] { Entry(1, "A 1.0", LineA), Entry(2, "B 1.0", LineB) };

        Assert.Equal(2, RewriteLog.ToRestore(log, ["A 1.0", "B 1.0"]).Count);
    }

    [Fact]
    public void ARestoreOfAnOldEntryCancelsItInEveryLine()
    {
        // An old rewrite (no line) and the restore that cancels it (also no line): neither line sees it as pending.
        var log = new[] { Entry(1, "Old 1.0", line: null!), Entry(2, "Old 1.0", line: null!, restored: true) };

        Assert.Empty(RewriteLog.ToRestore(log, ["Old 1.0"], LineA));
        Assert.Empty(RewriteLog.ToRestore(log, ["Old 1.0"], LineB));
    }

    [Fact]
    public void ARewriteOfALineAndItsRestoreCancelEachOtherInThatLine()
    {
        var log = new[] { Entry(1, "A 1.0", LineA), Entry(2, "A 1.0", LineA, restored: true) };

        Assert.Empty(RewriteLog.ToRestore(log, ["A 1.0"], LineA));
    }

    [Fact]
    public void TheLineIsPartOfTheLogAndAnOldLogWithoutItStillReads()
    {
        var text = RewriteLog.Serialize(Entry(1, "A 1.0", LineA)) + "\n"
                   + """{"At":"2026-11-12T10:02:00+00:00","Application":"Old","DeploymentType":"DT","OldOperator":"GreaterEquals","NewOperator":"IsEquals","LogicalName":"S","Restored":false}""";

        var entries = RewriteLog.Read(text);

        Assert.Equal([LineA, null], entries.Select(e => e.Line));
    }

    // ---- the plan is read again before the write ----

    private static RewritePlan PlanOf(string expected = "1.0", string application = "App 1.0") =>
        new([new RewriteItem(application, "1.0", "DT", Clause with { ExpectedValue = expected }, null)], []);

    [Fact]
    public void TwoPlansWithTheSameClausesAreTheSameRewrite()
    {
        Assert.True(RewriteOldVersionsPlans.SameRewrites(PlanOf(), PlanOf()));
        Assert.True(RewriteOldVersionsPlans.SameRewrites(PlanOf(), PlanOf(application: "APP 1.0")));
    }

    [Theory]
    [InlineData("2.0", "App 1.0")]
    [InlineData("1.0", "Another 1.0")]
    public void AChangedClauseOrApplicationIsAnotherRewrite(string expected, string application) =>
        Assert.False(RewriteOldVersionsPlans.SameRewrites(PlanOf(), PlanOf(expected, application)));

    [Fact]
    public void ARewriteThatLosesAClauseWhileTheWindowIsOpenIsAnotherRewrite()
    {
        var empty = new RewritePlan([new RewriteItem("App 1.0", "1.0", "DT", Clause, "the operator is Equals, not GreaterEquals")], []);

        Assert.False(RewriteOldVersionsPlans.SameRewrites(PlanOf(), empty));
    }

    private static (List<ChangePlanItem> Items, List<(string, string)> Keys) RowsOf(RewritePlan plan) =>
        (plan.Items.Where(i => i.Rewrites).Select(RewriteOldVersionsPlans.ItemOf).ToList(),
         plan.Items.Where(i => i.Rewrites).Select(i => (i.Application, i.DeploymentType)).ToList());

    private sealed class Progress : IProgress<ChangePlanItemResult>
    {
        public List<ChangePlanItemResult> Reports { get; } = [];
        public void Report(ChangePlanItemResult value) => Reports.Add(value);
    }

    [Fact]
    public async Task WhenTheSiteChangedAfterThePlanWasShownNothingIsWritten()
    {
        var shown = PlanOf();
        var (items, keys) = RowsOf(shown);
        var wrote = false;

        var result = await RewriteOldVersionsPlans.RewriteRunAsync(shown, Scope, items, keys,
            (_, _) => Task.FromResult(PlanOf(expected: "2.0")),
            (_, _, _, _, _) => { wrote = true; return Task.FromResult(new RewriteOutcome([], [])); },
            new Progress(), CancellationToken.None);

        Assert.False(wrote);
        Assert.Equal(ChangePlanStop.Failed, result.Stop);
        Assert.All(result.Items, row => Assert.True(row.Skipped));
        Assert.Contains("site changed", result.Items[0].Message);
        Assert.False(result.UndoAvailable);
    }

    [Fact]
    public async Task WhenTheSiteCannotBeReadAgainNothingIsWrittenAndTheReasonIsShown()
    {
        var shown = PlanOf();
        var (items, keys) = RowsOf(shown);
        var wrote = false;

        var result = await RewriteOldVersionsPlans.RewriteRunAsync(shown, Scope, items, keys,
            (_, _) => throw new InvalidOperationException("The deployments of 'App 1.0' could not be read."),
            (_, _, _, _, _) => { wrote = true; return Task.FromResult(new RewriteOutcome([], [])); },
            new Progress(), CancellationToken.None);

        Assert.False(wrote);
        Assert.Equal(ChangePlanStop.Failed, result.Stop);
        Assert.Contains("could not be read", result.Items[0].Message);
        Assert.False(result.UndoAvailable);
    }

    [Fact]
    public async Task WhenThePlanIsUnchangedTheRewriteIsWritten()
    {
        var shown = PlanOf();
        var (items, keys) = RowsOf(shown);
        RewritePlan? written = null;

        var result = await RewriteOldVersionsPlans.RewriteRunAsync(shown, Scope, items, keys,
            (_, _) => Task.FromResult(PlanOf()),
            (plan, _, _, _, _) =>
            {
                written = plan;
                return Task.FromResult(new RewriteOutcome([Entry(1, "App 1.0", LineA)], []));
            },
            new Progress(), CancellationToken.None);

        Assert.Same(shown, written);
        Assert.True(result.Items[0].Succeeded);
    }
}
