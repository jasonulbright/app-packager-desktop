using ApplicationPackagerEngine.Models.Inventory;

namespace ApplicationPackagerEngine.Models;

/// <summary>What became of one destination in one publish run.</summary>
public enum PublishDestinationKind
{
    /// <summary>The destination holds the application (new, or already there).</summary>
    Published,
    /// <summary>A step failed. Step names which one; earlier steps may have written objects.</summary>
    Failed,
    /// <summary>Nothing was written on purpose: an existing application was kept, or the run stopped first.</summary>
    Skipped,
    /// <summary>The destination cannot carry this application.</summary>
    NotSupported,
    /// <summary>The destination was not ready, so nothing was attempted.</summary>
    Blocked
}

/// <summary>
/// The result of one destination for one application: the cell of the V1 results grid.
/// Step is the part that failed ("content upload", "supersedence"), empty when none did.
/// </summary>
public sealed record PublishDestinationResult(
    string Destination, PublishDestinationKind Kind, string Step, string Message, string? ObjectId = null)
{
    /// <summary>The cell text: "Published", "Failed at content upload", "Skipped", "Not supported", "Blocked".</summary>
    public string Text => Kind switch
    {
        PublishDestinationKind.Published => "Published",
        PublishDestinationKind.Failed => Step.Length > 0 ? "Failed at " + Step : "Failed",
        PublishDestinationKind.Skipped => "Skipped",
        PublishDestinationKind.NotSupported => "Not supported",
        _ => "Blocked"
    };

    /// <summary>The ConfigMgr cell of a package result: Partial means the application exists and a later step failed.</summary>
    public static PublishDestinationResult FromConfigMgr(PackagerExecutionResult result)
    {
        const string name = OneClickDestinations.ConfigMgr;
        var detail = result.ConfigMgrDetail ?? result.StdErr;
        var id = result.ConfigMgrObjectId;
        if (result.Conflict is not null && result.ExitCode == 0)
            return new(name, PublishDestinationKind.Skipped, "", $"Existing application kept: {result.Conflict.ApplicationName} v{result.Conflict.Version}", result.Conflict.ApplicationName);
        return result.ConfigMgrOutcome switch
        {
            InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent or InventoryOutcome.PublishedSupersedenceNotApplied
                => new(name, PublishDestinationKind.Published, "", detail, id),
            InventoryOutcome.Partial => new(name, PublishDestinationKind.Failed, "content", detail, id),
            InventoryOutcome.Skipped or InventoryOutcome.Cancelled => new(name, PublishDestinationKind.Skipped, "", detail, id),
            InventoryOutcome.Refused => new(name, PublishDestinationKind.NotSupported, "", detail, id),
            InventoryOutcome.Failed => new(name, PublishDestinationKind.Failed, "", detail, id),
            _ => result.ExitCode == 0
                ? new(name, PublishDestinationKind.Published, "", detail, id)
                : new(name, PublishDestinationKind.Failed, "", detail, id)
        };
    }

    /// <summary>The Intune cell of a package result.</summary>
    public static PublishDestinationResult FromIntune(PackagerExecutionResult result)
    {
        const string name = OneClickDestinations.Intune;
        var detail = result.IntuneDetail ?? result.IntunePublishNote;
        if (string.IsNullOrWhiteSpace(detail)) detail = result.StdErr;
        var id = result.IntuneObjectId;
        return result.IntuneOutcome switch
        {
            InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent => new(name, PublishDestinationKind.Published, "", detail, id),
            InventoryOutcome.Partial => new(name, PublishDestinationKind.Failed, "content upload", detail, id),
            InventoryOutcome.Skipped or InventoryOutcome.Cancelled => new(name, PublishDestinationKind.Skipped, "", detail, id),
            InventoryOutcome.Refused => new(name, PublishDestinationKind.NotSupported, "", detail, id),
            InventoryOutcome.Failed => new(name, PublishDestinationKind.Failed, "", detail, id),
            _ => result.ExitCode == 0
                ? new(name, PublishDestinationKind.Published, "", detail, id)
                : new(name, PublishDestinationKind.Failed, "", detail, id)
        };
    }

    /// <summary>The WSUS cell of a WSUS step.</summary>
    public static PublishDestinationResult FromWsus(WsusStepResult step)
    {
        const string name = OneClickDestinations.Wsus;
        var id = step.PackageId.Length > 0 ? step.PackageId : null;
        return step.Status switch
        {
            WsusStepStatus.Published or WsusStepStatus.AlreadyPublished => new(name, PublishDestinationKind.Published, "", step.Message, id),
            WsusStepStatus.Refused => new(name, PublishDestinationKind.NotSupported, "", step.Message, id),
            WsusStepStatus.Failed => new(name, PublishDestinationKind.Failed, "", step.Message, id),
            _ => new(name, PublishDestinationKind.Skipped, "", step.Message, id)
        };
    }

    /// <summary>A destination that was not attempted because the run stopped or an earlier gate refused it.</summary>
    public static PublishDestinationResult NotAttempted(string destination, PublishDestinationKind kind, string message) =>
        new(destination, kind, "", message);
}

/// <summary>The cells of one application in one publish run: one per destination of the run, in publish order.</summary>
public sealed record PublishRowResult(string Uid, string Version, IReadOnlyList<PublishDestinationResult> Destinations)
{
    public PublishDestinationResult? For(string destination) =>
        Destinations.FirstOrDefault(d => string.Equals(d.Destination, destination, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when every cell is Published or Skipped on purpose.</summary>
    public bool Succeeded => Destinations.All(d => d.Kind is PublishDestinationKind.Published or PublishDestinationKind.Skipped);

    /// <summary>True when some cell is Published and another is not: the run is partial and the published cells stay.</summary>
    public bool Partial => Destinations.Any(d => d.Kind == PublishDestinationKind.Published)
        && Destinations.Any(d => d.Kind is PublishDestinationKind.Failed or PublishDestinationKind.Blocked or PublishDestinationKind.NotSupported);
}
