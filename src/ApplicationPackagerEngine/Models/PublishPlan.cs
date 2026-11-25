namespace ApplicationPackagerEngine.Models;

/// <summary>One checked application in the publish review. Hidden is a checked row that the grid view does not show.</summary>
public sealed record PublishPlanApp(string Uid, string Application, string Version, bool Hidden);

public enum PublishCellKind
{
    /// <summary>The run creates the object at the destination.</summary>
    Create,
    /// <summary>The destination is not ready, so nothing is written there.</summary>
    Blocked
}

/// <summary>One destination of one application in the plan: what the run does there.</summary>
public sealed record PublishPlanCell(string Destination, PublishCellKind Kind, string Text);

/// <summary>One application with one cell for each system in use, in the order ConfigMgr, Intune, WSUS.</summary>
public sealed record PublishPlanRow(PublishPlanApp App, IReadOnlyList<PublishPlanCell> Cells);

/// <summary>
/// The publish review (overhaul V1, phase 5a): every checked application against every system in use, so the operator sees what Publish All will write
/// before it writes. A system that is in use and not ready is Blocked with its reason, and a plan with a Blocked system cannot run: the run never publishes
/// to fewer systems than Settings names. The plan is read from the last check; the review says so.
/// </summary>
public sealed record PublishPlan(IReadOnlyList<string> Systems, IReadOnlyList<PublishPlanRow> Rows, IReadOnlyList<(string Destination, string Reason)> Blocked)
{
    public bool CanRun => Rows.Count > 0 && Systems.Count > 0 && Blocked.Count == 0;

    /// <summary>The number of writes the run makes: one for each application and system.</summary>
    public int Writes => Rows.Count * Systems.Count;

    public int HiddenCount => Rows.Count(r => r.App.Hidden);

    /// <summary>"3 applications, 2 systems: 6 publishes are planned."</summary>
    public string CountsText =>
        $"{Rows.Count} {(Rows.Count == 1 ? "application" : "applications")}, {Systems.Count} {(Systems.Count == 1 ? "system" : "systems")}: " +
        $"{Writes} {(Writes == 1 ? "publish is" : "publishes are")} planned.";

    /// <summary>The text of the confirm button: "Publish 3 applications".</summary>
    public string ConfirmText => $"Publish {Rows.Count} {(Rows.Count == 1 ? "application" : "applications")}";

    public const string ConfigMgrAction = "Create application and deployment type";
    public const string IntuneAction = "Create Win32 app";
    public const string WsusAction = "Create software update";
}

public static class PublishPlanner
{
    /// <summary>The plan for the checked applications and the systems of Settings, Systems in Use, with the readiness of each system.</summary>
    public static PublishPlan Plan(IEnumerable<PublishPlanApp> apps, SystemsInUse? systems, PublishReadiness readiness)
    {
        var set = new DestinationSet(systems?.ConfigMgr ?? false, systems?.Wsus ?? false, systems?.Intune ?? false);
        var order = new[] { OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus }.Where(set.Has).ToList();
        List<(string Destination, string Reason)> blocked = order.Select(d => (Destination: d, Reason: PublishActionState.DestinationGate(d, readiness)))
            .Where(b => b.Reason is not null).Select(b => (Destination: b.Destination, Reason: b.Reason!)).ToList();
        var rows = apps.Select(app => new PublishPlanRow(app, order.Select(d =>
            blocked.Any(b => b.Destination == d)
                ? new PublishPlanCell(d, PublishCellKind.Blocked, "Blocked: " + blocked.First(b => b.Destination == d).Reason)
                : new PublishPlanCell(d, PublishCellKind.Create, d switch
                {
                    OneClickDestinations.Intune => PublishPlan.IntuneAction,
                    OneClickDestinations.Wsus => PublishPlan.WsusAction,
                    _ => PublishPlan.ConfigMgrAction
                })).ToList())).ToList();
        return new PublishPlan(order, rows, blocked);
    }
}
