using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// Runs one single-product stage (a dropped installer or a bring-your-own application) under the
/// operation token and settles it in a <see cref="PackagingRunTally"/>, so the status line of a
/// cancelled stage is the same "Cancelled: N of M products done." line as in every other run.
/// </summary>
public static class CancellableStageRun
{
    /// <summary>The outcome of one stage: the result (null when cancelled or when the stage threw) and the settled tally.</summary>
    public sealed record Outcome(StagingResult? Result, PackagingRunTally Tally, string? ErrorMessage)
    {
        public bool Cancelled => Tally.Cancelled;
    }

    /// <summary>
    /// Calls <paramref name="stage"/> with the token. A cancellation from the operator's token is an
    /// interrupted product; the same exception type from a service timeout is a failure.
    /// </summary>
    public static async Task<Outcome> RunAsync(Func<CancellationToken, Task<StagingResult>> stage, CancellationToken ct)
    {
        var tally = new PackagingRunTally();
        try
        {
            var result = await stage(ct);
            if (result.Success) tally.RecordSuccess(); else tally.RecordFailure();
            return new Outcome(result, tally, result.ErrorMessage);
        }
        catch (OperationCanceledException ex)
        {
            tally.RecordInterruptedOrFailed(ct);
            return new Outcome(null, tally, tally.Cancelled ? null : ex.Message);
        }
    }
}
