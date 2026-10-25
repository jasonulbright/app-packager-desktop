using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Builds each Live view from the rows a source returned, in time linear in the rows: every join is a
/// dictionary lookup. A view whose read was cut has every count null; a count over rows with an unknown
/// status is null too, because the unknown rows could be in any state.
/// </summary>
internal static class LiveAggregator
{
    public static readonly IReadOnlyList<string> DeploymentColumns =
        ["Name", "Type", "Collection", "Purpose", "Targeted", "Success", "Errors", "In progress", "Unknown", "Compliance (%)", "Deployment ID", "Summarized"];

    public static readonly IReadOnlyList<string> ContentColumns =
        ["Name", "Type", "Package ID", "Distribution points", "Installed", "In progress", "Failed", "Installed (%)"];

    public static readonly IReadOnlyList<string> DpColumns = ["Name", "Site", "Status", "Pull DP", "PXE", "Kind"];

    public static readonly IReadOnlyList<string> SiteColumns = ["Name", "Item", "Server", "Status", "State", "Last started"];

    public static readonly IReadOnlyList<string> ClientColumns =
        ["Device", "Health", "Active", "Client state", "Last active", "Last online", "Last DDR", "Last policy request", "Last hardware inventory", "Last health evaluation", "Client version", "Operating system"];

    public static readonly IReadOnlyList<string> InactiveColumns = ["Device", "Last active", "Last DDR", "Days since contact", "Operating system", "Client version"];

    /// <summary>Above this compliance a deployment with nothing in progress and no errors shows OK.</summary>
    public const double DeploymentOkPercent = 95;

    /// <summary>An inactive device past this many days, or with no contact on record, shows Failed.</summary>
    public const int InactiveFailedDays = 60;

    public static IReadOnlyList<string> Columns(LiveView view) => view switch
    {
        LiveView.Deployments => DeploymentColumns,
        LiveView.Content => ContentColumns,
        LiveView.DistributionPoints => DpColumns,
        LiveView.ClientHealth => ClientColumns,
        LiveView.InactiveDevices => InactiveColumns,
        LiveView.SiteHealth => SiteColumns,
        _ => []
    };

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Pct(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static int I(string text, int fallback = 0) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static int? NullableInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Keys stay unique: a repeated key gets "#2", "#3" in arrival order.</summary>
    private sealed class Keys
    {
        private readonly Dictionary<string, int> _seen = new(StringComparer.OrdinalIgnoreCase);

        public string Next(string key)
        {
            var n = _seen.TryGetValue(key, out var c) ? c + 1 : 1;
            _seen[key] = n;
            return n == 1 ? key : key + "#" + N(n);
        }
    }

    private static LiveCounts Counts(LiveView view, bool complete, Func<string, double?> value) =>
        new(LiveMetrics.Of(view).ToDictionary(c => c, c => complete ? value(c) : null, StringComparer.Ordinal));

    // ---- Deployments ---------------------------------------------------------------------------

    public static string FeatureType(int value) => value switch
    {
        1 => "Application",
        2 => "Program",
        3 => "Mobile program",
        4 => "Script",
        5 => "Software update",
        6 => "Baseline",
        7 => "Task sequence",
        8 => "Content distribution",
        9 => "DP group",
        10 => "DP health",
        11 => "Configuration policy",
        _ => "Other (" + N(value) + ")"
    };

    public static string Purpose(int value) => value switch
    {
        1 => "Required",
        2 => "Available",
        3 => "Simulate",
        _ => "Unknown"
    };

    public static (IReadOnlyList<LiveRow> Rows, LiveCounts Counts) Deployments(HygieneTable table, bool complete)
    {
        var rows = new List<LiveRow>(table.Rows.Count);
        var keys = new Keys();
        long targetedSum = 0, successSum = 0;
        var withErrors = 0;
        foreach (var r in table.Records)
        {
            var targeted = r.I("NumberTargeted");
            var success = r.I("NumberSuccess");
            var errors = r.I("NumberErrors");
            var progress = r.I("NumberInProgress");
            var unknown = r.I("NumberUnknown");
            targetedSum += targeted;
            successSum += success;
            if (errors > 0) withErrors++;
            double? pct = targeted > 0 ? Math.Round(success * 100.0 / targeted, 1, MidpointRounding.AwayFromZero) : null;
            var (status, text) = errors > 0 ? (LiveStatus.Failed, "Errors")
                : targeted == 0 ? (LiveStatus.Unknown, "Nothing targeted")
                : progress > 0 ? (LiveStatus.Warning, "In progress")
                : pct < DeploymentOkPercent ? (LiveStatus.Warning, "Below " + N((long)DeploymentOkPercent) + " %")
                : (LiveStatus.Ok, "OK");
            var id = r.S("DeploymentID");
            var name = r.S("SoftwareName") is { Length: > 0 } s ? s : r.S("ApplicationName") is { Length: > 0 } a ? a : id;
            rows.Add(new LiveRow(keys.Next(id), status, text,
            [
                name, FeatureType(r.I("FeatureType", -1)), r.S("CollectionName") is { Length: > 0 } c ? c : r.S("CollectionID"), Purpose(r.I("DeploymentIntent", -1)),
                N(targeted), N(success), N(errors), N(progress), N(unknown), pct is { } p ? Pct(p) : "", id, r.S("SummarizationTime").Replace('T', ' ')
            ]));
        }
        double? overall = targetedSum > 0 ? Math.Round(successSum * 100.0 / targetedSum, 1, MidpointRounding.AwayFromZero) : null;
        var counts = Counts(LiveView.Deployments, complete, c => c switch
        {
            LiveMetrics.DeploymentTotal => rows.Count,
            LiveMetrics.DeploymentFailed => withErrors,
            LiveMetrics.CompliancePct => overall,
            _ => null
        });
        return (rows, counts);
    }

    // ---- Content -------------------------------------------------------------------------------

    public static string ContentType(int packageType) => DpContentAggregator.TypeOfPackageType(packageType) switch
    {
        DpContentTypes.Package => "Package",
        DpContentTypes.Application => "Application",
        DpContentTypes.SoftwareUpdatePackage => "Software update package",
        DpContentTypes.BootImage => "Boot image",
        DpContentTypes.OsImage => "OS image",
        DpContentTypes.OsUpgradePackage => "OS upgrade package",
        DpContentTypes.DriverPackage => "Driver package",
        DpContentTypes.TaskSequence => "Task sequence",
        _ => "Type " + N(packageType)
    };

    private sealed class Tally
    {
        public int Total, Installed, InProgress, Failed, PackageType = -1;
    }

    public static (IReadOnlyList<LiveRow> Rows, LiveCounts Counts) Content(HygieneTable pairs, HygieneTable messages, HygieneTable names, bool complete)
    {
        var lastMessage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in messages.Records)
            if (NullableInt(m.S("MessageState")) is { } ms) lastMessage[DpContentAggregator.Key(m.S("PackageID"), m.S("NalPath"))] = ms;
        var nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.Records) nameOf.TryAdd(n.S("PackageID"), n.S("Name"));

        var byPackage = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var p in pairs.Records)
        {
            var id = p.S("PackageID").ToUpperInvariant();
            if (id.Length == 0) continue;
            if (!byPackage.TryGetValue(id, out var t))
            {
                byPackage[id] = t = new Tally { PackageType = p.I("PackageType", -1) };
                order.Add(id);
            }
            var state = p.I("State", -1);
            int? message = lastMessage.TryGetValue(DpContentAggregator.Key(id, p.S("ServerNALPath")), out var ms) ? ms : null;
            t.Total++;
            if (DpContentStates.PairFailed(state, message)) t.Failed++;
            else if (DpContentStates.IsOk(state)) t.Installed++;
            else if (DpContentStates.PairInProgress(state, message)) t.InProgress++;
        }

        var rows = new List<LiveRow>();
        long failedPairs = 0;
        foreach (var id in order)
        {
            var t = byPackage[id];
            failedPairs += t.Failed;
            if (t.Failed == 0 && t.InProgress == 0) continue;
            var (status, text) = t.Failed > 0 ? (LiveStatus.Failed, "Failed") : (LiveStatus.Warning, "In progress");
            var name = nameOf.TryGetValue(id, out var n) && n.Length > 0 ? n : DpContentAggregator.UnknownName(id);
            rows.Add(new LiveRow(id, status, text,
            [
                name, ContentType(t.PackageType), id, N(t.Total), N(t.Installed), N(t.InProgress), N(t.Failed),
                Pct(t.Total > 0 ? Math.Round(t.Installed * 100.0 / t.Total, 1, MidpointRounding.AwayFromZero) : 0)
            ]));
        }
        var counts = Counts(LiveView.Content, complete, c => c switch
        {
            LiveMetrics.ContentIssues => rows.Count,
            LiveMetrics.ContentFailedPairs => failedPairs,
            _ => null
        });
        return (rows, counts);
    }

    // ---- Distribution points and site health ----------------------------------------------------

    public static (LiveStatus Status, string Text) SiteStatus(int value) => value switch
    {
        0 => (LiveStatus.Ok, "OK"),
        1 => (LiveStatus.Warning, "Warning"),
        2 => (LiveStatus.Failed, "Critical"),
        _ => (LiveStatus.Unknown, "Unknown")
    };

    public static string ComponentState(int value) => value switch
    {
        0 => "Stopped",
        1 => "Started",
        2 => "Paused",
        3 => "Installing",
        4 => "Re-installing",
        5 => "De-installing",
        _ => "Unknown (" + N(value) + ")"
    };

    private static string YesNo(string value) => string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) || value == "1" ? "Yes" : "No";

    public static (IReadOnlyList<LiveRow> Rows, LiveCounts Counts) DistributionPoints(HygieneTable dps, HygieneTable status, bool complete)
    {
        // The summarizer has one row per storage object of a server; the worst status counts.
        var worst = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in status.Records)
        {
            var server = DpContentAggregator.ServerOf(s.S("SiteSystem")).Trim();
            var value = s.I("Status", -1);
            if (server.Length == 0 || value < 0) continue;
            if (!worst.TryGetValue(server, out var w) || value > w) worst[server] = value;
        }
        var rows = new List<LiveRow>(dps.Rows.Count);
        var keys = new Keys();
        int critical = 0, warning = 0, unknown = 0;
        foreach (var d in dps.Records)
        {
            var server = d.S("ServerName").Trim();
            if (server.Length == 0) server = DpContentAggregator.ServerOf(d.S("NALPath"));
            var value = worst.TryGetValue(server, out var w) ? w : -1;
            var (st, text) = value < 0 ? (LiveStatus.Unknown, "No status") : SiteStatus(value);
            if (st == LiveStatus.Failed) critical++;
            else if (st == LiveStatus.Warning) warning++;
            else if (st == LiveStatus.Unknown) unknown++;
            var key = d.S("NALPath") is { Length: > 0 } nal ? nal.ToUpperInvariant() : server.ToUpperInvariant();
            rows.Add(new LiveRow(keys.Next(key), st, text, [server, d.S("SiteCode"), text, YesNo(d.S("IsPullDP")), YesNo(d.S("IsPXE")), d.S("ResourceType")]));
        }
        var counts = Counts(LiveView.DistributionPoints, complete, c => c switch
        {
            LiveMetrics.DPTotal => rows.Count,
            LiveMetrics.DPOffline => unknown > 0 ? null : critical,
            LiveMetrics.DPDegraded => unknown > 0 ? null : warning,
            _ => null
        });
        return (rows, counts);
    }

    public static (IReadOnlyList<LiveRow> Rows, LiveCounts Counts) SiteHealth(HygieneTable components, HygieneTable systems, bool complete)
    {
        var rows = new List<LiveRow>(components.Rows.Count + systems.Rows.Count);
        var keys = new Keys();
        int ok = 0, warning = 0, critical = 0, unknown = 0;
        void Count(LiveStatus s)
        {
            switch (s)
            {
                case LiveStatus.Ok: ok++; break;
                case LiveStatus.Warning: warning++; break;
                case LiveStatus.Failed: critical++; break;
                default: unknown++; break;
            }
        }
        foreach (var c in components.Records)
        {
            var (st, text) = SiteStatus(c.I("Status", -1));
            Count(st);
            var name = c.S("ComponentName");
            var machine = c.S("MachineName");
            rows.Add(new LiveRow(keys.Next("C|" + name + "|" + machine), st, text,
                [name, "Component", machine, text, ComponentState(c.I("State", -1)), c.S("LastStarted").Replace('T', ' ')]));
        }
        foreach (var s in systems.Records)
        {
            var (st, text) = SiteStatus(s.I("Status", -1));
            Count(st);
            var server = DpContentAggregator.ServerOf(s.S("SiteSystem")).Trim();
            var role = s.S("Role");
            rows.Add(new LiveRow(keys.Next("S|" + role + "|" + server.ToUpperInvariant()), st, text, [role, "Site system", server, text, "", ""]));
        }
        var counts = Counts(LiveView.SiteHealth, complete, c => unknown > 0 ? null : c switch
        {
            LiveMetrics.SiteOK => ok,
            LiveMetrics.SiteWarning => warning,
            LiveMetrics.SiteCritical => critical,
            _ => null
        });
        return (rows, counts);
    }

    // ---- SQL views -----------------------------------------------------------------------------

    /// <summary>A UTC time from the site database as local text; empty for an empty or unreadable value.</summary>
    public static string LocalTime(string utcText)
    {
        if (!DateTime.TryParseExact(utcText, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) return "";
        return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Health(int? value) => value switch
    {
        1 => "Healthy",
        2 => "Unhealthy",
        _ => "Not evaluated"
    };

    private static string Active(int? value) => value switch
    {
        1 => "Active",
        0 => "Inactive",
        _ => "Unknown"
    };

    /// <summary>Client health rows: ResourceID, Name0, LastEvaluationHealthy, ClientActiveStatus, ClientStateDescription, then six times, version, OS.</summary>
    public static (IReadOnlyList<LiveRow> Rows, LiveCounts Counts) Clients(IReadOnlyList<string[]> data, bool complete)
    {
        var rows = new List<LiveRow>(data.Count);
        var keys = new Keys();
        int healthy = 0, unhealthy = 0, inactive = 0;
        foreach (var v in data)
        {
            string C(int i) => i < v.Length ? v[i] ?? "" : "";
            var health = NullableInt(C(2));
            var active = NullableInt(C(3));
            if (health == 1) healthy++;
            if (health == 2) unhealthy++;
            if (active == 0) inactive++;
            var (st, text) = health == 2 ? (LiveStatus.Failed, "Unhealthy")
                : active == 0 ? (LiveStatus.Warning, "Inactive")
                : health == 1 ? (LiveStatus.Ok, "Healthy")
                : (LiveStatus.Unknown, "Not evaluated");
            rows.Add(new LiveRow(keys.Next(C(0)), st, text,
            [
                C(1), Health(health), Active(active), C(4), LocalTime(C(5)), LocalTime(C(6)), LocalTime(C(7)), LocalTime(C(8)), LocalTime(C(9)), LocalTime(C(10)), C(11), C(12)
            ]));
        }
        var counts = Counts(LiveView.ClientHealth, complete, c => c switch
        {
            LiveMetrics.ClientHealthy => healthy,
            LiveMetrics.ClientUnhealthy => unhealthy,
            LiveMetrics.ClientInactive => inactive,
            _ => null
        });
        return (rows, counts);
    }

    /// <summary>Inactive device rows: ResourceID, Name0, LastActiveTime, LastDDR, DaysSinceContact, OS, version.</summary>
    public static (IReadOnlyList<LiveRow> Rows, LiveCounts Counts) Inactive(IReadOnlyList<string[]> data, bool complete)
    {
        var rows = new List<LiveRow>(data.Count);
        var keys = new Keys();
        foreach (var v in data)
        {
            string C(int i) => i < v.Length ? v[i] ?? "" : "";
            var days = NullableInt(C(4));
            var (st, text) = days is null ? (LiveStatus.Failed, "No contact on record")
                : days > InactiveFailedDays ? (LiveStatus.Failed, "Over " + N(InactiveFailedDays) + " days")
                : (LiveStatus.Warning, "Past the threshold");
            rows.Add(new LiveRow(keys.Next(C(0)), st, text, [C(1), LocalTime(C(2)), LocalTime(C(3)), days is { } d ? N(d) : "", C(5), C(6)]));
        }
        var counts = Counts(LiveView.InactiveDevices, complete, c => c == LiveMetrics.InactiveDevices ? rows.Count : null);
        return (rows, counts);
    }
}
