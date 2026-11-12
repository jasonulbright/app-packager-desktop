using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;

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
        IntuneRunOutcome intune,
        string? contentStepFailure = null)
    {
        // MECM runs first and gates the Intune step, so its failure is the whole story.
        // A failed run does not always carry a message, and an empty StdErr on a failing
        // exit code tells the operator nothing.
        if (mecm == MecmStepStatus.Failed)
            return new PackagingExitOutcome(1,
                string.IsNullOrWhiteSpace(mecmError) ? "ConfigMgr packaging failed." : mecmError);

        // Only a published app satisfies a requested Intune target. Treating "did not run"
        // as acceptable is how a skipped step passes for a success.
        // A content step (distribution, test deployment) that failed leaves an application that clients cannot install.
        // It fails the run, whatever the Intune step did.
        var contentFailure = mecm == MecmStepStatus.NotRequested || string.IsNullOrWhiteSpace(contentStepFailure)
            ? null : contentStepFailure.Trim();

        if (!intuneRequested || intune.Succeeded)
            return contentFailure is null
                ? new PackagingExitOutcome(0, "")
                : new PackagingExitOutcome(1, $"ConfigMgr application created, but a step after it failed: {contentFailure}");

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

        var also = contentFailure is null ? "" : $" A step after the ConfigMgr application also failed: {contentFailure}";
        return new PackagingExitOutcome(1, $"{prefix}{reason}{note}{also}");
    }

    /// <summary>
    /// The inventory outcome of the ConfigMgr destination. An application that exists but whose content step failed is
    /// Partial: the object is there, and clients cannot install it. Null when the target did not ask for ConfigMgr.
    /// </summary>
    public static InventoryOutcome? ConfigMgrInventoryOutcome(bool requested, bool succeeded, bool alreadyExists, string? contentStepFailure, string? supersedenceFailure = null) =>
        !requested ? null
        : alreadyExists ? InventoryOutcome.AlreadyPresent
        : !succeeded ? InventoryOutcome.Failed
        : !string.IsNullOrWhiteSpace(contentStepFailure) ? InventoryOutcome.Partial
        // Not Partial: the application is complete. A separate state, with the reason in the detail.
        : !string.IsNullOrWhiteSpace(supersedenceFailure) ? InventoryOutcome.PublishedSupersedenceNotApplied
        : InventoryOutcome.Succeeded;

    /// <summary>
    /// The ConfigMgr detail of a run for the history: the error, else the failed content step, else the
    /// warnings of a run that succeeded (joined with "; "); null when there is none of them.
    /// </summary>
    public static string? ConfigMgrDetail(string? error, string? contentStepFailure, IReadOnlyList<string>? warnings, string? supersedenceFailure = null)
    {
        if (!string.IsNullOrWhiteSpace(error)) return error;
        if (!string.IsNullOrWhiteSpace(contentStepFailure)) return contentStepFailure;
        if (!string.IsNullOrWhiteSpace(supersedenceFailure)) return "Published, supersedence not applied: " + supersedenceFailure;
        var joined = string.Join("; ", (warnings ?? []).Where(w => !string.IsNullOrWhiteSpace(w)));
        return joined.Length > 0 ? joined : null;
    }

    /// <summary>One outcome for the run from the outcomes of its destinations (ConfigMgr and Intune, as requested).</summary>
    public static InventoryOutcome SummarizeDestinations(IReadOnlyList<InventoryOutcome> outcomes)
    {
        if (outcomes.Any(value => value is InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent or InventoryOutcome.PublishedSupersedenceNotApplied)
            && outcomes.Any(value => value is InventoryOutcome.Failed or InventoryOutcome.Refused or InventoryOutcome.Partial)) return InventoryOutcome.Partial;
        if (outcomes.Any(value => value == InventoryOutcome.Refused)) return InventoryOutcome.Refused;
        if (outcomes.All(value => value is InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent or InventoryOutcome.PublishedSupersedenceNotApplied))
            return outcomes.Any(value => value == InventoryOutcome.PublishedSupersedenceNotApplied)
                ? InventoryOutcome.PublishedSupersedenceNotApplied : InventoryOutcome.Succeeded;
        if (outcomes.All(value => value == InventoryOutcome.Skipped)) return InventoryOutcome.Skipped;
        return InventoryOutcome.Failed;
    }

    /// <summary>The MECM status for one run, from the target's own result.</summary>
    public static MecmStepStatus MecmStatus(bool requested, bool succeeded, bool alreadyExists) =>
        !requested ? MecmStepStatus.NotRequested
        : !succeeded ? MecmStepStatus.Failed
        : alreadyExists ? MecmStepStatus.AlreadyExisted
        : MecmStepStatus.Created;
}
