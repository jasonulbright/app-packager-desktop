namespace ApplicationPackagerEngine.Models;

/// <summary>What became of the Intune half of a packaging run.</summary>
public enum IntuneStepStatus
{
    /// <summary>The step was not selected for this run.</summary>
    NotRun,
    /// <summary>The .intunewin was built and the app published.</summary>
    Published,
    /// <summary>The .intunewin could not be built; nothing was published.</summary>
    BuildFailed,
    /// <summary>The package was built and the publish attempt failed.</summary>
    PublishFailed,
    /// <summary>The package was built and the publish deliberately did not run.</summary>
    PublishRefused
}

/// <summary>
/// Outcome of one Intune post-step. The status is what callers decide on; the notes carry
/// the lines the operator sees, and an empty note means that half of the step never ran.
/// Callers read the status rather than matching on note text.
/// </summary>
public sealed record IntuneRunOutcome(IntuneStepStatus Status, string BuildNote, string PublishNote)
{
    public static readonly IntuneRunOutcome NotRun = new(IntuneStepStatus.NotRun, "", "");

    public static IntuneRunOutcome Published(string buildNote, string publishNote) =>
        new(IntuneStepStatus.Published, buildNote, publishNote);

    public static IntuneRunOutcome BuildFailed(string buildNote) =>
        new(IntuneStepStatus.BuildFailed, buildNote, "");

    public static IntuneRunOutcome PublishFailed(string buildNote, string publishNote) =>
        new(IntuneStepStatus.PublishFailed, buildNote, publishNote);

    public static IntuneRunOutcome PublishRefused(string buildNote, string publishNote) =>
        new(IntuneStepStatus.PublishRefused, buildNote, publishNote);

    /// <summary>
    /// True only when an app was published. A requested target is satisfied by nothing
    /// else, so callers test this rather than a set of failure states that would let a
    /// step which never ran pass for a success.
    /// </summary>
    public bool Succeeded => Status == IntuneStepStatus.Published;

    /// <summary>The note describing why the step did not publish.</summary>
    public string FailureNote => Status == IntuneStepStatus.BuildFailed ? BuildNote : PublishNote;
}
