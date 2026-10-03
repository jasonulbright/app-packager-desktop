using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>Exit code and error text for one packaging run.</summary>
public sealed record PackagingExitOutcome(int ExitCode, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>What the MECM half of a run did.</summary>
public enum MecmStepStatus
{
    /// <summary>The target did not ask for an MECM application.</summary>
    NotRequested,
    /// <summary>The application was created or updated.</summary>
    Created,
    /// <summary>An application already held the title and was left alone.</summary>
    AlreadyExisted,
    /// <summary>The run failed.</summary>
    Failed
}

/// <summary>
/// Settles whether a packaging run succeeded. A run answers for every target the operator
/// selected, so a requested Intune step counts only when it published; anything else,
/// including never having run, fails the run. The decision reads statuses, never note text.
/// </summary>
public static class PackagingOutcomeComposer
{
    public static PackagingExitOutcome Compose(
        MecmStepStatus mecm,
        string? mecmError,
        bool intuneRequested,
        IntuneRunOutcome intune)
    {
        // MECM runs first and gates the Intune step, so its failure is the whole story.
        // A failed run does not always carry a message, and an empty StdErr on a failing
        // exit code tells the operator nothing.
        if (mecm == MecmStepStatus.Failed)
            return new PackagingExitOutcome(1,
                string.IsNullOrWhiteSpace(mecmError) ? "ConfigMgr packaging failed." : mecmError);

        // Only a published app satisfies a requested Intune target. Treating "did not run"
        // as acceptable is how a skipped step passes for a success.
        if (!intuneRequested || intune.Succeeded)
            return new PackagingExitOutcome(0, "");

        var reason = intune.Status switch
        {
            IntuneStepStatus.BuildFailed => "Intune package build failed",
            IntuneStepStatus.PublishFailed => "Intune publish failed",
            IntuneStepStatus.PublishRefused => "Intune publish did not run",
            _ => "Intune publish was not attempted"
        };
        var note = string.IsNullOrWhiteSpace(intune.FailureNote) ? "" : $": {intune.FailureNote}";

        // What MECM actually did stays reported, so an operator does not go looking for
        // something already on the site, and is not told a new application exists when the
        // run only found one that already did.
        var prefix = mecm switch
        {
            MecmStepStatus.Created => "ConfigMgr application created or updated. ",
            MecmStepStatus.AlreadyExisted => "Existing ConfigMgr application kept. ",
            _ => ""
        };

        return new PackagingExitOutcome(1, $"{prefix}{reason}{note}");
    }

    /// <summary>The MECM status for one run, from the target's own result.</summary>
    public static MecmStepStatus MecmStatus(bool requested, bool succeeded, bool alreadyExists) =>
        !requested ? MecmStepStatus.NotRequested
        : !succeeded ? MecmStepStatus.Failed
        : alreadyExists ? MecmStepStatus.AlreadyExisted
        : MecmStepStatus.Created;
}
