using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Counting and wording only. These drive the tally directly with recorded outcomes; the
/// loops that decide which outcome each item gets are covered at their own boundaries
/// (see MecmQueryClassifierTests and RunBoundaryTests), not here.
///
/// The completion lines were built from the loop index, which counts attempts. A run where
/// applications failed, were skipped, or were interrupted therefore claimed every one of
/// them succeeded, and a loop that broke on cancellation went on to say it had completed.
///
/// This is the helper the Latest check, MECM check, Stage and Package loops all report
/// through, and the flag One Click reads to decide whether to start its next phase.
/// </summary>
public class PackagingRunTallyTests
{
    private static PackagingRunTally Tally(
        int succeeded = 0, int failed = 0, int skipped = 0, int interrupted = 0, bool cancelled = false)
    {
        var tally = new PackagingRunTally();
        for (var i = 0; i < succeeded; i++) tally.RecordSuccess();
        for (var i = 0; i < failed; i++) tally.RecordFailure();
        for (var i = 0; i < skipped; i++) tally.RecordSkip();
        for (var i = 0; i < interrupted; i++) tally.RecordInterrupted();
        if (cancelled) tally.MarkCancelled();
        return tally;
    }

    // ── Counting ────────────────────────────────────────────────

    /// <summary>A clean run reads the way it always did.</summary>
    [Fact]
    public void AllSucceededReadsAsBefore() =>
        Assert.Equal("5 of 5 packaged", Tally(succeeded: 5).Describe(5, "packaged"));

    [Fact]
    public void FailuresAreNotCountedAsSuccesses()
    {
        var line = Tally(succeeded: 3, failed: 2).Describe(5, "staged");

        Assert.Equal("3 of 5 staged, 2 failed", line);
        Assert.DoesNotContain("5 of 5", line);
    }

    /// <summary>Every category the loops can record appears in one line.</summary>
    [Fact]
    public void AMixedRunNamesEveryCategoryThatOccurred() =>
        Assert.Equal("2 of 6 checked, 2 failed, 1 skipped, 1 interrupted",
            Tally(succeeded: 2, failed: 2, skipped: 1, interrupted: 1).Describe(6, "checked"));

    [Fact]
    public void AWhollyFailedRunClaimsNoSuccesses() =>
        Assert.Equal("0 of 3 packaged, 3 failed", Tally(failed: 3).Describe(3, "packaged"));

    [Fact]
    public void AttemptedCountsEveryVerdictIncludingTheInterruptedItem()
    {
        var tally = Tally(succeeded: 2, failed: 3, skipped: 1, interrupted: 1);

        Assert.Equal(7, tally.Attempted);
        Assert.Equal(2, tally.Succeeded);
        Assert.Equal(3, tally.Failed);
        Assert.Equal(1, tally.Skipped);
        Assert.Equal(1, tally.Interrupted);
    }

    [Fact]
    public void AnEmptyRunClaimsNothing() =>
        Assert.Equal("0 of 0 checked", new PackagingRunTally().Describe(0, "checked"));

    // ── Cancellation ────────────────────────────────────────────

    /// <summary>Cancelled before the first item: nothing was attempted and nothing interrupted.</summary>
    [Fact]
    public void CancellingBeforeTheFirstItemAttemptsNothing()
    {
        var tally = Tally(cancelled: true);

        Assert.Equal(0, tally.Attempted);
        Assert.Equal(0, tally.Interrupted);
        Assert.Equal("Stage cancelled. 0 of 4 staged, 4 not attempted.",
            tally.Summarize("Stage", 4, "staged"));
    }

    /// <summary>
    /// Cancelled between items: the finished items keep their verdicts and no row is
    /// invented as interrupted.
    /// </summary>
    [Fact]
    public void CancellingBetweenItemsInterruptsNothing()
    {
        var tally = Tally(succeeded: 2, cancelled: true);

        Assert.Equal(0, tally.Interrupted);
        Assert.Equal("Stage cancelled. 2 of 5 staged, 3 not attempted.",
            tally.Summarize("Stage", 5, "staged"));
    }

    /// <summary>
    /// Cancelled during an item: that row was reached and has no verdict, so it is counted
    /// as interrupted rather than left among the items the run never got to.
    /// </summary>
    [Fact]
    public void AnInterruptedItemIsNotCalledUnattempted()
    {
        var tally = Tally(succeeded: 2, interrupted: 1, cancelled: true);

        Assert.Equal(3, tally.Attempted);
        Assert.Equal("Stage cancelled. 2 of 5 staged, 1 interrupted, 2 not attempted.",
            tally.Summarize("Stage", 5, "staged"));
    }

    [Fact]
    public void AnInterruptedItemIsNeitherFailedNorSkipped()
    {
        var tally = Tally(interrupted: 1, cancelled: true);

        Assert.Equal(0, tally.Failed);
        Assert.Equal(0, tally.Skipped);
        Assert.Equal(1, tally.Interrupted);
    }

    /// <summary>
    /// The whole point of the summary: a run that stopped early says so instead of
    /// claiming it finished.
    /// </summary>
    [Fact]
    public void ACancelledRunDoesNotClaimCompletion()
    {
        var cancelled = Tally(succeeded: 1, cancelled: true).Summarize("Latest check", 3, "checked");

        Assert.StartsWith("Latest check cancelled.", cancelled);
        Assert.DoesNotContain("complete", cancelled);
    }

    [Fact]
    public void ACompletedRunSaysComplete() =>
        Assert.Equal("Latest check complete. 3 of 3 checked.",
            Tally(succeeded: 3).Summarize("Latest check", 3, "checked"));

    /// <summary>A run that finished with failures still finished.</summary>
    [Fact]
    public void FailuresAloneDoNotMakeARunCancelled()
    {
        var tally = Tally(succeeded: 1, failed: 2);

        Assert.False(tally.Cancelled);
        Assert.Equal("Package complete. 1 of 3 packaged, 2 failed.",
            tally.Summarize("Package", 3, "packaged"));
    }

    // ── The flag One Click reads ────────────────────────────────

    /// <summary>
    /// One Click starts its next phase only when the previous one was not cancelled, so
    /// the flag has to be set by every route that stops a loop early.
    /// </summary>
    [Fact]
    public void CancelledIsFalseUntilTheRunIsStopped()
    {
        var tally = new PackagingRunTally();
        Assert.False(tally.Cancelled);

        tally.RecordSuccess();
        Assert.False(tally.Cancelled);

        tally.MarkCancelled();
        Assert.True(tally.Cancelled);
    }

    [Fact]
    public void MarkingCancelledTwiceIsHarmless()
    {
        var tally = Tally(succeeded: 1, cancelled: true);
        tally.MarkCancelled();

        Assert.True(tally.Cancelled);
        Assert.Equal(1, tally.Attempted);
    }

    // ── Per-loop wording ────────────────────────────────────────

    /// <summary>The wording the MECM loop passes in. Which results count as successes is
    /// decided by the classifier, not here.</summary>
    [Fact]
    public void TheMecmSummaryUsesItsOwnLabelAndVerb() =>
        Assert.Equal("MECM query complete. 4 of 4 checked.",
            Tally(succeeded: 4).Summarize("MECM query", 4, "checked"));

    /// <summary>A recorded skip is reported separately from a failure.</summary>
    [Fact]
    public void ASkipIsReportedSeparatelyFromAFailure()
    {
        var tally = Tally(succeeded: 2, skipped: 1);

        Assert.Equal(0, tally.Failed);
        Assert.Equal("Stage complete. 2 of 3 staged, 1 skipped.", tally.Summarize("Stage", 3, "staged"));
    }

    [Theory]
    [InlineData("Latest check", "checked")]
    [InlineData("MECM query", "checked")]
    [InlineData("Stage", "staged")]
    [InlineData("Package", "packaged")]
    public void EverySummaryLabelFormatsTheSameWay(string label, string verb)
    {
        var tally = Tally(succeeded: 1, failed: 1, interrupted: 1, cancelled: true);

        Assert.Equal($"{label} cancelled. 1 of 4 {verb}, 1 failed, 1 interrupted, 1 not attempted.",
            tally.Summarize(label, 4, verb));
    }
}
