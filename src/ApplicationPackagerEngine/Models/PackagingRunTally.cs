namespace ApplicationPackagerEngine.Models;

/// <summary>
/// What a multi-application run actually did. A loop index counts attempts, not outcomes,
/// so a summary built from one reports failures, skips and interruptions as successes.
///
/// The item a cancellation interrupts is its own category. It was attempted and has no
/// verdict, which is not the same as an item the run never reached, and cancellation
/// observed between items interrupts nothing at all.
/// </summary>
public sealed class PackagingRunTally
{
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Interrupted { get; private set; }

    /// <summary>True once the run stopped early, whether or not an item was interrupted.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Applications the run reached, including the one a cancellation interrupted.</summary>
    public int Attempted => Succeeded + Failed + Skipped + Interrupted;

    public void RecordSuccess() => Succeeded++;
    public void RecordFailure() => Failed++;

    /// <summary>An item the operator declined, such as a cancelled configuration dialog.</summary>
    public void RecordSkip() => Skipped++;

    /// <summary>The item that was in flight when the run was cancelled.</summary>
    public void RecordInterrupted() => Interrupted++;

    public void MarkCancelled() => Cancelled = true;

    /// <summary>
    /// Records an item that produced no usable outcome, deciding which kind it was from
    /// the caller's own token.
    ///
    /// A cancellation surfacing from inside a service is not necessarily the operator: an
    /// HTTP timeout throws the same exception type. Only the caller's token being
    /// cancelled means the batch was stopped. Returns true when the run should stop.
    /// </summary>
    public bool RecordInterruptedOrFailed(CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            RecordInterrupted();
            MarkCancelled();
            return true;
        }

        RecordFailure();
        return false;
    }

    /// <summary>
    /// Settles the run before its summary is read. A service can return successfully after
    /// the operator cancelled, especially on a cached or synchronous path, and a run that
    /// ends that way is still cancelled even though its last item succeeded.
    /// </summary>
    public PackagingRunTally Finish(CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested) MarkCancelled();
        return this;
    }

    /// <summary>
    /// True when the run reached every item and each one succeeded. A caller that replaces
    /// the summary with a conclusion of its own has to know whether the run earned it.
    /// </summary>
    public bool AllSucceeded(int total) => !Cancelled && Succeeded == total;

    /// <summary>
    /// The outcome clause for a completion line, naming only the categories that occurred.
    /// A run with nothing but successes reads "N of M {verb}" alone.
    /// </summary>
    public string Describe(int total, string successVerb)
    {
        var parts = new List<string> { $"{Succeeded} of {total} {successVerb}" };
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Interrupted > 0) parts.Add($"{Interrupted} interrupted");

        var unreached = total - Attempted;
        if (unreached > 0) parts.Add($"{unreached} not attempted");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The whole status line. A cancelled run says so rather than claiming completion,
    /// which is what a loop that breaks on cancellation and then falls through does.
    /// </summary>
    public string Summarize(string operationLabel, int total, string successVerb) =>
        Cancelled
            ? $"{operationLabel} cancelled. {Describe(total, successVerb)}."
            : $"{operationLabel} complete. {Describe(total, successVerb)}.";
}
