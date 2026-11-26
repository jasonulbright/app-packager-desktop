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

    /// <summary>When the version of this application was last checked; null when no check time is stored.</summary>
    public DateTime? CheckedAt { get; init; }

    /// <summary>The publish and refusal records of the application for the current environments; null when none are read.</summary>
    public ApplicationPackagerEngine.Services.OneClick.OneClickHistoryEntry? History { get; init; }

    /// <summary>True when the last sealed build of this version differs in profile revision or signing policy, so the content changed.</summary>
    public bool BuildStale { get; init; }

    /// <summary>The key of the build that this run would publish; empty when not known.</summary>
    public string BuildKey { get; init; } = "";

    /// <summary>The detection method of the definition, for the ConfigMgr card.</summary>
    public string Detection { get; init; } = "";

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
    NotSupported,
    /// <summary>The destination holds this version or a newer one, or refused this version before. The run writes nothing there.</summary>
    Skip
}

/// <summary>One detail card of the review: a system and its lines of label and text.</summary>
public sealed record PublishCard(string Destination, IReadOnlyList<PublishCardLine> Lines);

public sealed record PublishCardLine(string Label, string Text);

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

    /// <summary>The cells that the run leaves alone because the destination has the version or refused it.</summary>
    public int Skips => Rows.Sum(r => r.Cells.Count(c => c.Kind == PublishCellKind.Skip));

    /// <summary>The applications that the run publishes to at least one system.</summary>
    public int ApplicationsToWrite => Rows.Count(r => r.Cells.Any(c => c.Kind == PublishCellKind.Create));

    /// <summary>The detail card of each system; empty when the plan was built with no settings.</summary>
    public IReadOnlyList<PublishCard> Cards { get; init; } = [];

    /// <summary>The number of publishes the run plans: one for each application and system that can take it.</summary>
    public int Writes => Rows.Sum(r => r.Cells.Count(c => c.Kind == PublishCellKind.Create));

    public int HiddenCount => Rows.Count(r => r.App.Hidden);

    /// <summary>The ConfigMgr site and provider the plan writes to; empty when the plan was not given one.</summary>
    public string Site { get; init; } = "";

    public string SiteCode => Site.Split('|')[0];

    public string Provider => Site.Contains('|') ? Site[(Site.IndexOf('|') + 1)..] : "";

    /// <summary>The one destination of a retry; empty for a publish to every system in use.</summary>
    public string RetryDestination { get; init; } = "";

    /// <summary>
    /// A digest of what the run depends on: the systems, the blocked systems, and for each application its version and its ConfigMgr facts.
    /// The reason text of an unreadable fact is left out, because two failed reads of one fault can word it differently.
    /// </summary>
    public string Digest
    {
        get
        {
            var text = new StringBuilder();
            text.Append(string.Join(",", Systems)).Append('\n').Append(Site).Append('\n');
            foreach (var b in Blocked) text.Append("blocked|").Append(b.Destination).Append('\n');
            foreach (var r in Rows.OrderBy(r => r.App.Uid, StringComparer.Ordinal))
                text.Append(r.App.Uid).Append('|').Append(r.App.Version).Append('|').Append(r.App.Site.Kind).Append('|').Append(r.App.Site.Version)
                    .Append('|').Append(r.App.ConfigMgrSteps).Append('|').Append(r.App.IntuneNotSupported).Append('|').Append(r.App.WsusNotSupported)
                    // What each system gets: a change from Skip to Create must open the review again.
                    .Append('|').Append(string.Join(",", r.Cells.Select(c => c.Kind))).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    /// <summary>"3 applications, 2 systems: 6 publishes are planned."</summary>
    public string CountsText =>
        $"{Rows.Count} {(Rows.Count == 1 ? "application" : "applications")}, {Systems.Count} {(Systems.Count == 1 ? "system" : "systems")}: " +
        $"{Writes} {(Writes == 1 ? "publish is" : "publishes are")} planned." + (Skips > 0 ? $" {Skips} skipped." : "");

    /// <summary>The text of the confirm button: "Publish 3 applications".</summary>
    public string ConfirmText
    {
        get
        {
            var count = Skips > 0 ? ApplicationsToWrite : Rows.Count;
            return $"Publish {count} {(count == 1 ? "application" : "applications")}";
        }
    }

    public const string ConfigMgrAction = "Create application and deployment type";
    public const string IntuneAction = "Create Win32 app";
    public const string WsusAction = "Create software update";
}

public static class PublishPlanner
{
    /// <summary>The plan for the checked applications and the systems of Settings, Systems in Use, with the readiness of each system.</summary>
    /// <param name="skips">False for a retry, which has its own rules for a destination that may have the version.</param>
    public static PublishPlan Plan(IEnumerable<PublishPlanApp> apps, SystemsInUse? systems, PublishReadiness readiness, bool skips = true)
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
                : skips && SkipReason(app, d) is { } held
                ? new PublishPlanCell(d, PublishCellKind.Skip, "Skip: " + held)
                : new PublishPlanCell(d, PublishCellKind.Create, d switch
                {
                    OneClickDestinations.Intune => PublishPlan.IntuneAction,
                    OneClickDestinations.Wsus => PublishPlan.WsusAction,
                    _ => ConfigMgrText(app)
                })).ToList())).ToList();
        return new PublishPlan(order, rows, blocked);
    }

    /// <summary>
    /// Why the run leaves one system of one application alone, or null when it writes there. The rules are those of the One Click plan:
    /// the system has a newer version, it has this version from the same build, or it refused this version before. For ConfigMgr the
    /// answer of the site decides what the site has: a publish record does not skip an application that the site no longer has.
    /// </summary>
    public static string? SkipReason(PublishPlanApp app, string destination)
    {
        if (app.Version.Length == 0) return null;
        var configMgr = destination == OneClickDestinations.ConfigMgr;
        // With no answer from the site, nothing proves what it has.
        if (configMgr && app.Site.Kind == ConfigMgrFactKind.Unreadable) return null;
        var site = configMgr && app.Site.Kind == ConfigMgrFactKind.Found ? app.Site.Version : "";
        var records = configMgr ? null : app.History;
        if (ApplicationPackagerEngine.Services.OneClick.OneClickPlanner.NewerThanVendor(records, destination, site, app.Version) is { } newer)
            return newer;

        var recordedBuild = app.History?.PublishedBuildKey(destination) ?? "";
        var sameBuild = !app.BuildStale && (app.BuildKey.Length == 0 || recordedBuild.Length == 0
            || string.Equals(recordedBuild, app.BuildKey, StringComparison.OrdinalIgnoreCase));
        var carried = configMgr ? site : records?.PublishedVersion(destination) ?? "";
        if (sameBuild && string.Equals(carried, app.Version, StringComparison.OrdinalIgnoreCase)) return "version already published";
        return string.Equals(app.History?.RefusedVersion(destination), app.Version, StringComparison.OrdinalIgnoreCase)
            ? "this version was refused before"
            : null;
    }

    private static string ConfigMgrText(PublishPlanApp app) =>
        app.ConfigMgrSteps.Length > 0 ? ConfigMgrCreate(app) + ". Then: " + app.ConfigMgrSteps : ConfigMgrCreate(app);

    private static string Has(PublishPlanApp app) =>
        app.Site.Version.Length == 0 ? "an application with no version"
        : string.Equals(app.Site.Version, app.Version, StringComparison.OrdinalIgnoreCase) ? "this version"
        : app.Site.Version;

    private static string ConfigMgrCreate(PublishPlanApp app) => app.Site.Kind switch
    {
        // The run asks only when the title of the new application matches one on the site, and the title is known when the run stages. So the cell says "if".
        ConfigMgrFactKind.Found => PublishPlan.ConfigMgrAction + " (ConfigMgr has " + Has(app) + "; if the title matches, the run asks before it replaces it)",
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
        if (shown.Site != fresh.Site) changes["site"] = "The ConfigMgr site or its provider changed.";
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
            var was = shown.Rows.First(r => r.App.Uid == app.Uid).Cells;
            var now = fresh.Rows.First(r => r.App.Uid == app.Uid).Cells;
            // A change of what a system gets that no fact above explains, such as a new publish record.
            if (parts.Count == 0 && !was.Select(c => c.Kind).SequenceEqual(now.Select(c => c.Kind)))
                parts.Add("the planned actions are now: " + string.Join("; ", now.Select(c => c.Destination + ", " + c.Text)));
            if (parts.Count > 0) changes[app.Uid] = app.Application + ": " + string.Join("; ", parts) + ".";
        }
        foreach (var gone in shown.Rows.Select(r => r.App).Where(a => fresh.Rows.All(r => r.App.Uid != a.Uid)))
            changes[gone.Uid] = gone.Application + " is no longer in the plan.";
        // A digest that differs always has a named difference.
        if (changes.Count == 0) changes[""] = "The plan changed.";
        return changes;
    }
}
