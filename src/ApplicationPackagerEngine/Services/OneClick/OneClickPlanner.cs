using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.OneClick;

/// <summary>Stored tokens for the One Click action.</summary>
public static class OneClickActionNames
{
    public const string Report = "Report";
    public const string Stage = "Stage";
    public const string StageAndPackage = "StageAndPackage";

    public static string Normalize(string? value) => value switch
    {
        Stage => Stage,
        StageAndPackage => StageAndPackage,
        _ => Report
    };

    /// <summary>The label the settings page, the plan window and the report use.</summary>
    public static string Label(string action) => Normalize(action) switch
    {
        Stage => "Stage",
        StageAndPackage => "Stage and Publish",
        _ => "Report only"
    };
}

/// <summary>Skip or Overwrite: what a One Click run does with an existing ConfigMgr application.</summary>
public static class OneClickOnExisting
{
    public const string Skip = "Skip";
    public const string Overwrite = "Overwrite";

    public static string Normalize(string? value) => value == Overwrite ? Overwrite : Skip;
}

/// <summary>The row outcomes a run and its report use.</summary>
public static class OneClickOutcomes
{
    public const string Published = "Published";
    public const string Staged = "Staged";
    public const string Checked = "Checked";
    public const string Skipped = "Skipped";
    public const string NotSupported = "Not supported";
    public const string Failed = "Failed";
    public const string Planned = "Planned";
}

/// <summary>One tracked application as the planner reads it. CurrentVersion is the version the ConfigMgr site carries.</summary>
public sealed record OneClickApp(string Key, string Application, string Vendor, string LatestVersion = "", string CurrentVersion = "")
{
    /// <summary>
    /// The refusal codes of an application whose default build WSUS cannot take, joined
    /// with '|'; empty when WSUS can take it or the application declares nothing.
    /// </summary>
    public string WsusNotSupported { get; init; } = "";
}

/// <summary>Whether a destination can take a publish, and why not.</summary>
public sealed record OneClickReadiness(bool Ready, string Reason)
{
    public static readonly OneClickReadiness Yes = new(true, "");
}

/// <summary>Where a publish lands, as the plan and the report state it.</summary>
public sealed record OneClickScopeSettings(
    bool AutoDistribute = false, string DpGroupName = "", bool DeployToTestCollection = false, string TestCollectionName = "",
    string WsusApprovalGroup = "", bool TestCollectionIsUser = false);

/// <summary>Everything the planner reads from the settings.</summary>
public sealed record OneClickPlanSettings
{
    public IReadOnlyCollection<string> Tracked { get; init; } = [];
    /// <summary>Rows with a selection of their own; any other row uses the default.</summary>
    public IReadOnlyDictionary<string, DestinationSet> Destinations { get; init; } = new Dictionary<string, DestinationSet>();
    /// <summary>Null when the settings file carries no default; <see cref="LegacyTarget"/> then decides.</summary>
    public DestinationSet? DefaultDestinations { get; init; }
    public string? LegacyTarget { get; init; }
    public int DefaultCadenceDays { get; init; } = 7;
    public IReadOnlyDictionary<string, int> CadenceOverrides { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, OneClickReadiness> Readiness { get; init; } = new Dictionary<string, OneClickReadiness>();
    public OneClickScopeSettings Scope { get; init; } = new();
}

/// <summary>One row of the plan window.</summary>
public sealed record OneClickPlanRow
{
    public required string Key { get; init; }
    public required string Application { get; init; }
    public string Vendor { get; init; } = "";
    public string Version { get; init; } = "";
    public string Current { get; init; } = "";
    public required DestinationSet Destinations { get; init; }
    /// <summary>Row when the application has its own selection, Default otherwise.</summary>
    public string Source { get; init; } = "Default";
    public IReadOnlyDictionary<string, string> LastPublished { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> PublishTo { get; init; } = [];
    /// <summary>The selected destinations the plan leaves out as not ready, with the reason; the run skips them too.</summary>
    public IReadOnlyDictionary<string, string> NotReady { get; init; } = new Dictionary<string, string>();
    public string Planned { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool Include { get; init; }

    /// <summary>"ConfigMgr 1.0, WSUS 2.0" for the destinations that have a published version.</summary>
    public string LastPublishedText => string.Join(", ",
        OneClickDestinations.All.Where(d => LastPublished.TryGetValue(d, out var v) && v.Length > 0).Select(d => $"{d} {LastPublished[d]}"));
}

/// <summary>The count line above the plan grid and the prerequisites that block a destination for every row.</summary>
public sealed record OneClickPlanSummary(string Line, IReadOnlyDictionary<string, int> Counts, int Included, IReadOnlyList<string> Blocking);

/// <summary>What one row does after its version check.</summary>
public sealed record OneClickRunDecision
{
    public IReadOnlyList<string> PublishTo { get; init; } = [];
    public IReadOnlyList<string> SkipReasons { get; init; } = [];
    /// <summary>The result cell of a destination the guard skipped.</summary>
    public IReadOnlyDictionary<string, string> GuardResults { get; init; } = new Dictionary<string, string>();
    /// <summary>True when the row stages.</summary>
    public bool Stage { get; init; }
    /// <summary>The reason the row ends as Skipped without staging; empty when it stages.</summary>
    public string SkipReason { get; init; } = "";
}

/// <summary>
/// The One Click plan, the run's per-row decisions and the run outcome. Pure:
/// nothing here runs a packager or contacts a server, and the current
/// version is the one the last check recorded, or empty when the run has to
/// check first.
/// </summary>
public static class OneClickPlanner
{
    /// <summary>
    /// Whether each destination is in use and has the settings a publish
    /// needs. ConfigMgr also needs the console on this computer.
    /// </summary>
    public static IReadOnlyDictionary<string, OneClickReadiness> Readiness(PublishReadiness readiness)
    {
        var result = new Dictionary<string, OneClickReadiness>(StringComparer.OrdinalIgnoreCase);
        var systems = readiness.Systems;

        result[OneClickDestinations.ConfigMgr] =
            !systems.ConfigMgr ? new(false, "ConfigMgr is not in use")
            : !readiness.ConsoleFound ? new(false, "ConfigMgr console not detected")
            : string.IsNullOrWhiteSpace(readiness.SiteCode) ? new(false, "site code not set")
            : string.IsNullOrWhiteSpace(readiness.FileShareRoot) ? new(false, "file share root not set")
            : OneClickReadiness.Yes;
        result[OneClickDestinations.Wsus] =
            !systems.Wsus ? new(false, "WSUS is not in use")
            : !readiness.WsusServerSet ? new(false, "WSUS server not set")
            : OneClickReadiness.Yes;
        result[OneClickDestinations.Intune] =
            !systems.Intune ? new(false, "Intune is not in use")
            : !readiness.IntuneCredentialsSet ? new(false, "Intune credentials not set")
            : OneClickReadiness.Yes;
        return result;
    }

    /// <summary>The destinations of one application, and whether they came from the row (Row) or the default (Default).</summary>
    public static (DestinationSet Set, string Source) DestinationsFor(OneClickPlanSettings settings, string key)
    {
        foreach (var (name, set) in settings.Destinations)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && set is not null)
                return (set.Copy(), "Row");
        return ((settings.DefaultDestinations ?? DestinationSet.FromTarget(settings.LegacyTarget)).Copy(), "Default");
    }

    public static int CadenceDays(OneClickPlanSettings settings, string key)
    {
        foreach (var (name, days) in settings.CadenceOverrides)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && days >= 1) return days;
        return settings.DefaultCadenceDays >= 1 ? settings.DefaultCadenceDays : 7;
    }

    /// <summary>
    /// The version a destination already carries: the last version published
    /// there. For ConfigMgr without a record, the version the site reported.
    /// </summary>
    public static string CarriedVersion(OneClickHistoryEntry? entry, string destination, string siteVersion)
    {
        var published = entry?.PublishedVersion(destination) ?? "";
        if (published.Length == 0 && destination == OneClickDestinations.ConfigMgr) published = siteVersion ?? "";
        return published;
    }

    /// <summary>The start of a result cell and of a plan reason for a destination that cannot take the build.</summary>
    public const string NotSupportedPrefix = "not supported: ";

    /// <summary>
    /// The reason a destination is left out because it holds a newer release than the
    /// vendor now reports; null when it does not. A publish would put the older build over
    /// the newer one. A publish record and the vendor version come from the same feed, so
    /// all four parts count. A site version can carry a build number the vendor does not
    /// publish, so only the parts the vendor version carries count.
    /// </summary>
    public static string? NewerThanVendor(OneClickHistoryEntry? entry, string destination, string? siteVersion, string latest)
    {
        if (latest.Length == 0) return null;
        var published = entry?.PublishedVersion(destination) ?? "";
        if (published.Length > 0)
            return VersionCompare.CompareStrict(published, latest) > 0 ? $"published {published} is newer than {latest}" : null;
        if (destination == OneClickDestinations.ConfigMgr && !string.IsNullOrWhiteSpace(siteVersion)
            && VersionCompare.Compare(siteVersion, latest) > 0)
            return $"site version {siteVersion.Trim()} is newer than {latest}";
        return null;
    }

    private static OneClickReadiness ReadinessOf(OneClickPlanSettings settings, string destination) =>
        settings.Readiness.TryGetValue(destination, out var r) ? r : new OneClickReadiness(false, destination + " is not in use");

    /// <summary>One plan row per tracked application, in the order of <paramref name="apps"/>.</summary>
    public static IReadOnlyList<OneClickPlanRow> Plan(IEnumerable<OneClickApp> apps, OneClickPlanSettings settings,
        IReadOnlyDictionary<string, OneClickHistoryEntry> history, string action, bool force, DateTime now)
    {
        action = OneClickActionNames.Normalize(action);
        var tracked = new HashSet<string>(settings.Tracked, StringComparer.OrdinalIgnoreCase);
        var rows = new List<OneClickPlanRow>();

        foreach (var app in apps)
        {
            if (!tracked.Contains(app.Key)) continue;
            history.TryGetValue(app.Key, out var entry);

            var latest = string.IsNullOrWhiteSpace(app.LatestVersion) ? entry?.LastKnownVersion ?? "" : app.LatestVersion;
            var (destinations, source) = DestinationsFor(settings, app.Key);
            var lastPublished = OneClickDestinations.All.ToDictionary(d => d, d => CarriedVersion(entry, d, app.CurrentVersion));

            var reasons = new List<string>();
            var publishTo = new List<string>();
            var notReady = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var selected = destinations.Names;
            string planned;
            var include = true;

            if (selected.Count == 0)
            {
                include = false;
                planned = "Skip";
                reasons.Add("no destination selected");
            }
            else
            {
                var cadenceSkip = false;
                if (action == OneClickActionNames.Report && !force && entry?.LastChecked is { } lastChecked)
                {
                    var days = CadenceDays(settings, app.Key);
                    if (lastChecked.ToUniversalTime().AddDays(days) > now.ToUniversalTime())
                    {
                        cadenceSkip = true;
                        reasons.Add($"checked within the {days}-day cadence");
                    }
                }

                if (cadenceSkip)
                {
                    include = false;
                    planned = "Skip";
                }
                else
                {
                    var steps = new List<string> { "Check" };
                    if (action != OneClickActionNames.Report) steps.Add("Stage");
                    if (action == OneClickActionNames.StageAndPackage)
                    {
                        foreach (var d in selected)
                        {
                            var ready = ReadinessOf(settings, d);
                            if (!ready.Ready) { reasons.Add($"{d}: {ready.Reason}"); notReady[d] = ready.Reason; continue; }
                            if (d == OneClickDestinations.Wsus && app.WsusNotSupported.Length > 0)
                            {
                                reasons.Add($"{d}: not supported");
                                notReady[d] = NotSupportedPrefix + app.WsusNotSupported;
                                continue;
                            }
                            if (!force && latest.Length > 0 && entry?.RefusedVersion(d) == latest) { reasons.Add($"{d}: not supported"); continue; }
                            if (!force && NewerThanVendor(entry, d, app.CurrentVersion, latest) is { } newer) { reasons.Add($"{d}: {newer}"); continue; }
                            if (!force && latest.Length > 0 && lastPublished[d] == latest) { reasons.Add($"{d}: {latest} already published"); continue; }
                            publishTo.Add(d);
                        }
                        if (publishTo.Count > 0) steps.Add("Publish to " + string.Join("+", publishTo));
                        else if (reasons.Count > 0 && latest.Length == 0) steps.Add("Publish after check");
                    }
                    planned = string.Join(", ", steps);
                }
            }

            rows.Add(new OneClickPlanRow
            {
                Key = app.Key,
                Application = app.Application,
                Vendor = app.Vendor,
                Version = latest,
                Current = app.CurrentVersion,
                Destinations = destinations,
                Source = source,
                LastPublished = lastPublished,
                PublishTo = publishTo,
                NotReady = notReady,
                Planned = planned,
                Reason = string.Join("; ", reasons),
                Include = include
            });
        }
        return rows;
    }

    /// <summary>
    /// The count line and the blocking prerequisites. <paramref name="included"/>
    /// decides which rows count; it defaults to each row's own Include.
    /// </summary>
    public static OneClickPlanSummary Summarize(IReadOnlyList<OneClickPlanRow> plan, OneClickPlanSettings settings, string action,
        Func<OneClickPlanRow, bool>? included = null)
    {
        action = OneClickActionNames.Normalize(action);
        included ??= r => r.Include;
        var rows = plan.Where(included).ToList();
        var counts = OneClickDestinations.All.ToDictionary(d => d, d => rows.Count(r => r.PublishTo.Contains(d)));

        var line = $"{rows.Count} application(s)";
        if (plan.Count != rows.Count) line += $", {plan.Count - rows.Count} skipped";
        line += action switch
        {
            OneClickActionNames.StageAndPackage => ": " + string.Join(", ", OneClickDestinations.All.Select(d => $"{counts[d]} to {d}")),
            OneClickActionNames.Stage => ": check and stage, no publish",
            _ => ": check only, no publish"
        };

        var blocking = new List<string>();
        if (action == OneClickActionNames.StageAndPackage)
        {
            foreach (var d in OneClickDestinations.All)
            {
                var wanted = plan.Count(r => r.Destinations.Has(d));
                if (wanted == 0) continue;
                var ready = ReadinessOf(settings, d);
                if (!ready.Ready) blocking.Add($"{d}: {ready.Reason} ({wanted} application(s) select it)");
            }
        }
        return new OneClickPlanSummary(line, counts, rows.Count, blocking);
    }

    /// <summary>Where a publish to the destination lands: the DP group and collection, the WSUS approval group, the Intune assignment.</summary>
    public static string Scope(string destination, OneClickScopeSettings scope)
    {
        switch (destination)
        {
            case OneClickDestinations.ConfigMgr:
                var parts = new List<string>();
                if (scope.AutoDistribute && !string.IsNullOrWhiteSpace(scope.DpGroupName)) parts.Add("distribute to DP group " + scope.DpGroupName.Trim());
                if (scope.AutoDistribute && scope.DeployToTestCollection && !string.IsNullOrWhiteSpace(scope.TestCollectionName))
                    parts.Add("deploy to " + (scope.TestCollectionIsUser ? "user" : "device") + " collection " + scope.TestCollectionName.Trim());
                return parts.Count == 0 ? "application only, no distribution, no deployment" : string.Join("; ", parts);
            case OneClickDestinations.Wsus:
                return string.IsNullOrWhiteSpace(scope.WsusApprovalGroup) ? "published, not approved" : "approved for group " + scope.WsusApprovalGroup.Trim();
            case OneClickDestinations.Intune:
                return "app created, not assigned";
            default:
                return "";
        }
    }

    /// <summary>"Scope. ConfigMgr: … . WSUS: … ." for the destinations an included row publishes to; empty when none.</summary>
    public static string ScopeLine(IEnumerable<OneClickPlanRow> includedRows, OneClickScopeSettings scope)
    {
        var rows = includedRows.ToList();
        var parts = OneClickDestinations.All
            .Where(d => rows.Any(r => r.PublishTo.Contains(d)))
            .Select(d => $"{d}: {Scope(d, scope)}")
            .ToList();
        return parts.Count == 0 ? "" : "Scope. " + string.Join(". ", parts) + ".";
    }

    /// <summary>"Action: check, stage and publish. Existing ConfigMgr application: skip."</summary>
    public static string ActionLine(string action, string onExisting) =>
        $"Action: {OneClickActionNames.Normalize(action) switch { OneClickActionNames.Report => "check only", OneClickActionNames.Stage => "check and stage", _ => "check, stage and publish" }}. " +
        $"Existing ConfigMgr application: {(OneClickOnExisting.Normalize(onExisting) == OneClickOnExisting.Overwrite ? "overwrite" : "skip")}.";

    /// <summary>
    /// What one row does after its check. The guard reads the history again:
    /// the check may have found a version the plan did not know. A destination
    /// whose publish record carries the version is published again when the
    /// build is stale or the record names another build: the profile or the
    /// signing policy changed since that publish. A ConfigMgr site that carries
    /// the version without a publish record names no build, so it is not
    /// published again; the row is then restaged only.
    /// </summary>
    /// <param name="wanted">The destinations the operator kept for this row in the plan window.</param>
    /// <param name="versionChanged">The check found a version other than the last known one.</param>
    /// <param name="buildStale">The newest sealed build differs in version, profile revision or signing policy, or none exists.</param>
    /// <param name="staged">A stage of this application exists.</param>
    /// <param name="notReady">The plan's left-out destinations with their reason; each is skipped, Force or not.</param>
    /// <param name="buildKey">The key (<see cref="OneClickHistory.BuildKey"/>) of the build this run makes; empty when the row has no build plan.</param>
    public static OneClickRunDecision Decide(string action, IReadOnlyList<string> wanted, OneClickHistoryEntry? entry, string latest,
        string siteVersion, bool force, bool versionChanged, bool buildStale, bool staged,
        IReadOnlyDictionary<string, string>? notReady = null, string buildKey = "")
    {
        action = OneClickActionNames.Normalize(action);
        var publishTo = new List<string>();
        var reasons = new List<string>();
        var guard = new Dictionary<string, string>();

        if (action == OneClickActionNames.StageAndPackage)
        {
            foreach (var d in OneClickDestinations.All.Where(wanted.Contains))
            {
                if (notReady is not null && notReady.TryGetValue(d, out var why))
                {
                    reasons.Add($"{d}: {why}");
                    guard[d] = why.StartsWith(NotSupportedPrefix, StringComparison.Ordinal) ? why : "skipped: " + why;
                    continue;
                }
                if (!force && NewerThanVendor(entry, d, siteVersion, latest) is { } newer)
                {
                    reasons.Add($"{d}: {newer}");
                    guard[d] = "skipped: " + newer;
                    continue;
                }
                var published = entry?.PublishedVersion(d) ?? "";
                var publishedBuild = entry?.PublishedBuildKey(d) ?? "";
                var sameBuild = !buildStale && (buildKey.Length == 0 || publishedBuild.Length == 0
                    || string.Equals(publishedBuild, buildKey, StringComparison.OrdinalIgnoreCase));
                var carried = published.Length > 0 ? published == latest && sameBuild : CarriedVersion(entry, d, siteVersion) == latest;
                if (!force && carried && latest.Length > 0)
                {
                    reasons.Add($"{d}: {latest} already published");
                    guard[d] = "already published " + latest;
                    continue;
                }
                if (!force && latest.Length > 0 && entry?.RefusedVersion(d) == latest)
                {
                    reasons.Add($"{d}: not supported");
                    var reason = entry.RefusedReason(d);
                    guard[d] = reason.StartsWith("not supported: ", StringComparison.Ordinal) ? reason : "not supported " + latest;
                    continue;
                }
                publishTo.Add(d);
            }
        }

        var needStage = force || buildStale || versionChanged || !staged || publishTo.Count > 0;
        if (!needStage)
            return new OneClickRunDecision { SkipReasons = reasons, GuardResults = guard, SkipReason = "up to date" };
        if (action == OneClickActionNames.StageAndPackage && publishTo.Count == 0 && !versionChanged && !buildStale && !force)
            return new OneClickRunDecision { SkipReasons = reasons, GuardResults = guard, SkipReason = string.Join("; ", reasons) };
        return new OneClickRunDecision { PublishTo = publishTo, SkipReasons = reasons, GuardResults = guard, Stage = true };
    }

    /// <summary>The outcome and grid status of a row after its publish steps.</summary>
    public static (string Outcome, string Status) Conclude(int published, int failed, int notSupported) =>
        failed > 0 ? (OneClickOutcomes.Failed, published > 0 ? "Published, partial" : "Publish error")
        : published > 0 ? (OneClickOutcomes.Published, "Published")
        : notSupported > 0 ? (OneClickOutcomes.NotSupported, "WSUS: not supported")
        : (OneClickOutcomes.Skipped, "Skipped");

    /// <summary>
    /// The WSUS cell, the report detail and the package id of one WSUS step.
    /// A refusal cell carries only the code; the detail carries the full text.
    /// A reused update is on the server but is not a new publish.
    /// </summary>
    public static (string Cell, string Detail, string Id, bool Ok, bool AlreadyPresent) WsusResult(WsusStepResult step) => step.Status switch
    {
        WsusStepStatus.Published => ("published " + step.PackageId, "", step.PackageId, true, false),
        WsusStepStatus.AlreadyPublished => ("already on the server " + step.PackageId, "", step.PackageId, true, true),
        WsusStepStatus.Refused => (step.NotSupportedReason, step.Message, "", false, false),
        _ => (string.IsNullOrWhiteSpace(step.Message) ? "failed" : "failed: " + step.Message, step.Message, "", false, false)
    };

    /// <summary>True for a result cell that counts as neither a publish nor a failure.</summary>
    public static bool IsSkipOrRefusal(string cell) =>
        cell.StartsWith("skipped", StringComparison.Ordinal) || cell.StartsWith("not supported", StringComparison.Ordinal);
}
