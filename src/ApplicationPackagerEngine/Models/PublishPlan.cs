using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerEngine.Models;

/// <summary>What the plan knows about the application in ConfigMgr.</summary>
public enum ConfigMgrFactKind
{
    /// <summary>No application is known: the last check found none, or no check ran.</summary>
    None,
    Found,
    /// <summary>The read before the write did not get an answer from the site.</summary>
    Unreadable
}

/// <summary>The ConfigMgr facts of one application: from the last check, or from the read before the write.</summary>
public sealed record ConfigMgrFact(ConfigMgrFactKind Kind, string Version = "", string Detail = "")
{
    public static ConfigMgrFact None { get; } = new(ConfigMgrFactKind.None);

    public static ConfigMgrFact Found(string version) => new(ConfigMgrFactKind.Found, version);

    public static ConfigMgrFact Unreadable(string reason) => new(ConfigMgrFactKind.Unreadable, "", reason);
}

/// <summary>One checked application in the publish review. Hidden is a checked row that the grid view does not show.</summary>
public sealed record PublishPlanApp(string Uid, string Application, string Version, bool Hidden)
{
    public ConfigMgrFact Site { get; init; } = ConfigMgrFact.None;

    /// <summary>The steps that the ConfigMgr run takes after it creates the application: distribution, test deployment, supersedence. Empty when none.</summary>
    public string ConfigMgrSteps { get; init; } = "";

    /// <summary>Why Intune cannot take this application; empty when it can.</summary>
    public string IntuneNotSupported { get; init; } = "";

    /// <summary>Why WSUS cannot take this application; empty when it can.</summary>
    public string WsusNotSupported { get; init; } = "";
}

public enum PublishCellKind
{
    /// <summary>The run creates the object at the destination.</summary>
    Create,
    /// <summary>The destination is not ready, so nothing is written there.</summary>
    Blocked,
    /// <summary>The destination cannot take this application, so nothing is written there.</summary>
    NotSupported
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
    public bool CanRun => Writes > 0 && Blocked.Count == 0;

    /// <summary>The number of publishes the run plans: one for each application and system that can take it.</summary>
    public int Writes => Rows.Sum(r => r.Cells.Count(c => c.Kind == PublishCellKind.Create));

    public int HiddenCount => Rows.Count(r => r.App.Hidden);

    /// <summary>
    /// A digest of what the run depends on: the systems, the blocked systems, and for each application its version and its ConfigMgr facts.
    /// The reason text of an unreadable fact is left out, because two failed reads of one fault can word it differently.
    /// </summary>
    public string Digest
    {
        get
        {
            var text = new StringBuilder();
            text.Append(string.Join(",", Systems)).Append('\n');
            foreach (var b in Blocked) text.Append("blocked|").Append(b.Destination).Append('\n');
            foreach (var r in Rows.OrderBy(r => r.App.Uid, StringComparer.Ordinal))
                text.Append(r.App.Uid).Append('|').Append(r.App.Version).Append('|').Append(r.App.Site.Kind).Append('|').Append(r.App.Site.Version)
                    .Append('|').Append(r.App.ConfigMgrSteps).Append('|').Append(r.App.IntuneNotSupported).Append('|').Append(r.App.WsusNotSupported).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

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
        string Unsupported(PublishPlanApp app, string d) => d == OneClickDestinations.Intune ? app.IntuneNotSupported
            : d == OneClickDestinations.Wsus ? app.WsusNotSupported : "";
        var rows = apps.Select(app => new PublishPlanRow(app, order.Select(d =>
            blocked.Any(b => b.Destination == d)
                ? new PublishPlanCell(d, PublishCellKind.Blocked, "Blocked: " + blocked.First(b => b.Destination == d).Reason)
                : Unsupported(app, d) is { Length: > 0 } why
                ? new PublishPlanCell(d, PublishCellKind.NotSupported, "Not supported: " + why)
                : new PublishPlanCell(d, PublishCellKind.Create, d switch
                {
                    OneClickDestinations.Intune => PublishPlan.IntuneAction,
                    OneClickDestinations.Wsus => PublishPlan.WsusAction,
                    _ => ConfigMgrText(app)
                })).ToList())).ToList();
        return new PublishPlan(order, rows, blocked);
    }

    private static string ConfigMgrText(PublishPlanApp app) =>
        app.ConfigMgrSteps.Length > 0 ? ConfigMgrCreate(app) + ". Then: " + app.ConfigMgrSteps : ConfigMgrCreate(app);

    private static string ConfigMgrCreate(PublishPlanApp app) => app.Site.Kind switch
    {
        ConfigMgrFactKind.Found when app.Site.Version.Length > 0 && string.Equals(app.Site.Version, app.Version, StringComparison.OrdinalIgnoreCase) =>
            "ConfigMgr has this version: the run asks before it replaces it",
        ConfigMgrFactKind.Found => PublishPlan.ConfigMgrAction + " (ConfigMgr has " + (app.Site.Version.Length > 0 ? app.Site.Version : "an application with no version") + ")",
        ConfigMgrFactKind.Unreadable => PublishPlan.ConfigMgrAction + " (ConfigMgr was not read)",
        _ => PublishPlan.ConfigMgrAction
    };

    /// <summary>
    /// What differs between the plan the operator confirmed and a plan from a fresh read, one text for each application, keyed by its Uid.
    /// A difference of the systems or their readiness is keyed by the empty string. Empty when the two plans have the same digest.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Changes(PublishPlan shown, PublishPlan fresh)
    {
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (shown.Digest == fresh.Digest) return changes;
        if (!shown.Systems.SequenceEqual(fresh.Systems) || !shown.Blocked.Select(b => b.Destination).SequenceEqual(fresh.Blocked.Select(b => b.Destination)))
            changes[""] = "The systems in use or their readiness changed.";
        var before = shown.Rows.ToDictionary(r => r.App.Uid, r => r.App, StringComparer.Ordinal);
        foreach (var app in fresh.Rows.Select(r => r.App))
        {
            if (!before.TryGetValue(app.Uid, out var old)) { changes[app.Uid] = app.Application + " was added to the plan."; continue; }
            var parts = new List<string>();
            if (!string.Equals(old.Version, app.Version, StringComparison.Ordinal))
                parts.Add("the version is now " + (app.Version.Length > 0 ? app.Version : "unknown"));
            if (old.ConfigMgrSteps != app.ConfigMgrSteps)
                parts.Add("the ConfigMgr steps after the application are now: " + (app.ConfigMgrSteps.Length > 0 ? app.ConfigMgrSteps : "none"));
            if (old.IntuneNotSupported != app.IntuneNotSupported || old.WsusNotSupported != app.WsusNotSupported)
                parts.Add("what the systems can take changed");
            if (old.Site.Kind != app.Site.Kind || !string.Equals(old.Site.Version, app.Site.Version, StringComparison.Ordinal))
                parts.Add(app.Site.Kind switch
                {
                    ConfigMgrFactKind.Found => "ConfigMgr has " + (app.Site.Version.Length > 0 ? "version " + app.Site.Version : "an application with no version"),
                    ConfigMgrFactKind.Unreadable => "ConfigMgr could not be read (" + app.Site.Detail + ")",
                    _ => "ConfigMgr no longer has the application"
                });
            if (parts.Count > 0) changes[app.Uid] = app.Application + ": " + string.Join("; ", parts) + ".";
        }
        foreach (var gone in shown.Rows.Select(r => r.App).Where(a => fresh.Rows.All(r => r.App.Uid != a.Uid)))
            changes[gone.Uid] = gone.Application + " is no longer in the plan.";
        // A digest that differs always has a named difference.
        if (changes.Count == 0) changes[""] = "The plan changed.";
        return changes;
    }
}
