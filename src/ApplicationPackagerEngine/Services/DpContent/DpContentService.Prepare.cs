using ApplicationPackagerEngine.Services.Logging;
using System.Globalization;

namespace ApplicationPackagerEngine.Services;

public sealed partial class DpContentService
{
    public static class CheckNames
    {
        public const string Pairs = "Pairs";
        public const string AuditLog = "AuditLog";
        public const string LastCopy = "LastCopy";
        public const string Deployments = "Deployments";
        public const string PullDp = "PullDp";
        public const string Dropped = "Dropped";
    }

    internal const string DeploymentKinds =
        "Counted: application deployments of the application that owns the content, package and task sequence deployments of the content, and deployments of task sequences that reference the content or use it as boot image. Not counted: software update deployments and application dependencies.";

    public async Task<DpContentChange> PrepareAsync(DpContentChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dropped = new List<DpContentPairOutcome>();
        var wanted = new List<PairRow>();
        var keys = new HashSet<DpPairKey>();
        foreach (var pair in request.Pairs ?? [])
        {
            if (pair is null) continue;
            if (!PackageIdRegex().IsMatch(pair.PackageId ?? "") || string.IsNullOrWhiteSpace(pair.NalPath))
            {
                dropped.Add(Skip(pair, "the pair does not name a valid PackageID and distribution point."));
                continue;
            }
            if (keys.Add(pair.Key)) wanted.Add(pair);
        }

        var checks = new List<PreflightCheck>();
        if (wanted.Count > MaxPairsPerRun)
        {
            checks.Add(new PreflightCheck(CheckNames.Pairs, DeploymentCheckResults.Fail,
                $"The plan has {Count(wanted.Count, "pair")}. A run takes at most {MaxPairsPerRun.ToString(CultureInfo.InvariantCulture)} pairs. Filter the list and run it in parts."));
            return request with { Checks = checks, Impact = [], Fingerprint = null, Dropped = dropped, ContentImpact = [], NeedsLastCopyAcknowledgement = false };
        }

        var read = wanted.Count == 0 ? null : await ReadStateAsync(wanted.Select(p => p.PackageId), request.Kind == DpContentChangeKind.Remove, ct).ConfigureAwait(false);
        var now = read is null ? new Dictionary<DpPairKey, PairRow>() : DpContentAggregator.Pairs(read).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var dps = read is null ? new Dictionary<string, DpContentSiteRead.Dp>(StringComparer.OrdinalIgnoreCase) : DpContentAggregator.DpsByNal(read);
        var instances = read?.Instances.Select(i => new DpPairKey(i.PackageId, i.Nal)).ToHashSet() ?? [];

        var kept = new List<PairRow>();
        foreach (var pair in wanted)
        {
            now.TryGetValue(pair.Key, out var site);
            dps.TryGetValue(pair.NalPath, out var dp);
            var reason = DropReason(request.Kind, pair, site, dp, instances.Contains(pair.Key));
            if (reason is not null)
            {
                dropped.Add(Skip(pair, reason, site?.State));
                continue;
            }
            kept.Add(request.Kind == DpContentChangeKind.DistributeAgain
                ? pair with { DpName = dp!.Name.Length > 0 ? dp.Name : pair.DpName }
                : pair with
                {
                    State = site!.State,
                    StateName = site.StateName,
                    SourceVersion = site.SourceVersion,
                    SummaryDate = site.SummaryDate,
                    DpName = site.DpName,
                    ContentType = pair.ContentType is { Length: > 0 } t && t != DpContentTypes.Unknown ? t : site.ContentType,
                    MessageState = site.MessageState,
                    MessageId = site.MessageId
                });
        }

        var impact = ContentImpactOf(request.Kind, kept, now.Values, read);
        var lastCopy = request.Kind == DpContentChangeKind.Remove && impact.Any(IsLastCopy);
        checks.AddRange(ChecksOf(request.Kind, kept, dropped, impact, dps, read));
        var lines = ImpactLines(request.Kind, kept, impact, dropped);
        string? fingerprint = null;
        if (kept.Count > 0)
        {
            fingerprint = FingerprintOf(request.Kind, kept);
            Remember(fingerprint, new Prepared(request.Kind, kept.ToDictionary(p => p.Key), _utcNow()));
        }
        _log?.Info(LogArea, $"Prepared {request.Kind}: {kept.Count} pairs kept, {dropped.Count} left out.");
        return request with
        {
            Pairs = kept,
            Impact = lines,
            Checks = checks,
            Fingerprint = fingerprint,
            Dropped = dropped,
            ContentImpact = impact,
            NeedsLastCopyAcknowledgement = lastCopy,
            RemovalConfirmed = false,
            LastCopyAcknowledged = false
        };
    }

    /// <summary>
    /// True for a content type that Distribute again can copy to one distribution point. A task
    /// sequence is not one: Start-CMContentDistribution -TaskSequenceId distributes every content
    /// the task sequence references and creates no row for the task sequence itself.
    /// </summary>
    internal static bool CanDistributeAgain(string? contentType) => contentType is not (null or "" or DpContentTypes.TaskSequence or DpContentTypes.Unknown);

    /// <summary>A removed pair that Undo distributes again: installed before, with no error message, of a type Distribute again can copy.</summary>
    internal static bool IsRestorable(PairRow pair) => IsInstalled(pair) && CanDistributeAgain(pair.ContentType);

    /// <summary>A pair that holds a usable copy: installed, with no error message.</summary>
    internal static bool IsInstalled(PairRow pair) => DpContentStates.IsOk(pair.State) && !pair.HasErrorMessage;

    /// <summary>A content the removal leaves with no installed copy while it takes one: a content that was never installed loses nothing.</summary>
    private static bool IsLastCopy(DpContentImpact impact) => impact.DpsRemaining == 0 && impact.RemovesInstalledCopy;

    /// <summary>
    /// The text of a passed last-copy check. A content that is installed on no distribution point
    /// now and loses no installed copy passes, and the text says so instead of naming other copies.
    /// </summary>
    internal static string LastCopyPassText(IReadOnlyList<DpContentImpact> impact)
    {
        var none = impact.Where(i => i.DpsRemaining == 0 && !i.RemovesInstalledCopy).ToList();
        if (none.Count == 0) return "Every content stays installed on at least one other distribution point.";
        var names = string.Join(", ", none.Take(10).Select(i => $"{i.Name} ({i.PackageId})")) + (none.Count > 10 ? ", ..." : "");
        var them = none.Count == 1 ? "it" : "them";
        var where = $"{Count(none.Count, "content")} {(none.Count == 1 ? "is" : "are")} installed on no distribution point now and after the removal: {names}. Clients cannot get {them} before or after the removal.";
        return none.Count == impact.Count
            ? "The removal takes no installed copy. " + where
            : $"The removal takes no installed copy of {Count(none.Count, "content")}. {where} Every other content stays installed on at least one other distribution point.";
    }

    private static DpContentPairOutcome Skip(PairRow pair, string reason, int? stateNow = null) =>
        new(pair, DpContentResults.Skipped, SkippedPrefix + reason, stateNow);

    /// <summary>Why a requested pair is left out of the plan; null when it stays.</summary>
    private static string? DropReason(DpContentChangeKind kind, PairRow pair, PairRow? site, DpContentSiteRead.Dp? dp, bool hasInstance)
    {
        if (dp is not null && DpContentAggregator.IsCloud(dp.ResourceType)) return "the distribution point is a cloud distribution point; this page shows it but does not change it.";
        if (kind == DpContentChangeKind.DistributeAgain)
        {
            if (site is not null || hasInstance) return $"the content is on the distribution point now ({site?.StateName ?? "listed"}); nothing to distribute again.";
            if (dp is null) return "the distribution point is no longer in the site's list of distribution points.";
            if (!CanDistributeAgain(pair.ContentType))
                return "the content type cannot be distributed from this page.";
            return null;
        }
        if (site is null) return "the site no longer lists the pair. Refresh the list.";
        if (site.State != pair.State) return $"the state changed from {DpContentStates.Name(pair.State)} to {site.StateName} since the list was loaded. Refresh and plan again.";
        return kind switch
        {
            DpContentChangeKind.Redistribute when site.State is DpContentStates.RemovalPending or DpContentStates.RemovalRetrying => "the content is being removed from the distribution point.",
            DpContentChangeKind.Validate when !DpContentStates.IsOk(site.State) => $"only installed content can be validated; the pair is {site.StateName}.",
            DpContentChangeKind.Remove when site.State is DpContentStates.RemovalPending or DpContentStates.RemovalRetrying => "the removal is already pending.",
            _ => null
        };
    }

    private static List<DpContentImpact> ContentImpactOf(DpContentChangeKind kind, IReadOnlyList<PairRow> kept, IEnumerable<PairRow> now, DpContentSiteRead? read)
    {
        var removed = kind == DpContentChangeKind.Remove ? kept.Select(p => p.Key).ToHashSet() : [];
        var rowsByContent = now.GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var list = new List<DpContentImpact>();
        foreach (var group in kept.GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            var rows = rowsByContent.TryGetValue(group.Key, out var r) ? r : [];
            var dpsNow = rows.Count;
            var remaining = kind == DpContentChangeKind.Remove
                ? rows.Count(p => !removed.Contains(p.Key) && DpContentStates.IsOk(p.State) && !p.HasErrorMessage)
                : kind == DpContentChangeKind.DistributeAgain ? dpsNow + group.Count() : dpsNow;
            var deployments = -1;
            var note = "Not read for this kind of change.";
            if (kind == DpContentChangeKind.Remove)
            {
                if (read is { DeploymentsRead: true } && read.Deployments.TryGetValue(group.Key, out var n))
                {
                    deployments = n;
                    note = DeploymentKinds;
                }
                else note = "The deployments that use the content could not be read.";
            }
            var first = group.First();
            list.Add(new DpContentImpact(first.PackageId.ToUpperInvariant(), first.ContentName, first.ContentType, group.Count(), dpsNow, remaining, deployments, note)
            {
                RemovesInstalledCopy = kind == DpContentChangeKind.Remove && group.Any(IsInstalled)
            });
        }
        return list;
    }

    private List<PreflightCheck> ChecksOf(DpContentChangeKind kind, IReadOnlyList<PairRow> kept, IReadOnlyList<DpContentPairOutcome> dropped, IReadOnlyList<DpContentImpact> impact,
        IReadOnlyDictionary<string, DpContentSiteRead.Dp> dps, DpContentSiteRead? read)
    {
        var checks = new List<PreflightCheck>
        {
            kept.Count == 0
                ? new PreflightCheck(CheckNames.Pairs, DeploymentCheckResults.Fail, "No pair in the plan can be changed now.")
                : new PreflightCheck(CheckNames.Pairs, DeploymentCheckResults.Pass, $"{Count(kept.Count, "pair")} on {Count(kept.Select(p => p.NalPath.ToUpperInvariant()).Distinct().Count(), "distribution point")}.")
        };
        var probe = _audit.Probe();
        checks.Add(kind == DpContentChangeKind.Remove
            ? new PreflightCheck(CheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Fail,
                probe.Writable ? probe.Message : "The removal is blocked: " + probe.Message)
            : new PreflightCheck(CheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Warn,
                probe.Writable ? probe.Message : "The change can run, but its audit record may not be written: " + probe.Message));
        if (kind == DpContentChangeKind.Remove)
        {
            var last = impact.Where(IsLastCopy).ToList();
            checks.Add(last.Count == 0
                ? new PreflightCheck(CheckNames.LastCopy, DeploymentCheckResults.Pass, LastCopyPassText(impact))
                : new PreflightCheck(CheckNames.LastCopy, DeploymentCheckResults.Warn,
                    $"{Count(last.Count, "content")} will have no distribution point with the content installed: {string.Join(", ", last.Take(10).Select(i => $"{i.Name} ({i.PackageId})"))}{(last.Count > 10 ? ", ..." : "")}. Clients cannot install it until it is distributed again. The removal needs the box that accepts this."));
            var used = impact.Where(i => i.DeploymentCount > 0).ToList();
            var unknown = impact.Count(i => i.DeploymentCount < 0);
            checks.Add(unknown > 0
                ? new PreflightCheck(CheckNames.Deployments, DeploymentCheckResults.Warn, $"The deployments that use {Count(unknown, "content")} could not be read.")
                : used.Count == 0
                    ? new PreflightCheck(CheckNames.Deployments, DeploymentCheckResults.Pass, "No counted deployment uses the content. " + DeploymentKinds)
                    : new PreflightCheck(CheckNames.Deployments, DeploymentCheckResults.Warn,
                        $"{Count(used.Sum(i => i.DeploymentCount), "deployment")} use{(used.Sum(i => i.DeploymentCount) == 1 ? "s" : "")} {Count(used.Count, "content")} of the plan. " + DeploymentKinds));
        }
        var pull = kept.Select(p => p.NalPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => dps.TryGetValue(n, out var d) && d.IsPullDp).ToList();
        var sources = read?.PullSources ?? [];
        var sourceOf = kept.Select(p => p.NalPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => sources.Any(s => string.Equals(s.Source, n, StringComparison.OrdinalIgnoreCase) || string.Equals(DpContentAggregator.ServerOf(s.Source), DpContentAggregator.ServerOf(n), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (pull.Count > 0 || sourceOf.Count > 0)
        {
            var text = new List<string>();
            if (pull.Count > 0) text.Add($"{Count(pull.Count, "distribution point")} of the plan {(pull.Count == 1 ? "is a pull distribution point" : "are pull distribution points")}: {string.Join(", ", pull.Select(DpContentAggregator.ServerOf))}. A pull distribution point copies the content from its source distribution points.");
            if (sourceOf.Count > 0) text.Add($"{Count(sourceOf.Count, "distribution point")} of the plan {(sourceOf.Count == 1 ? "is the source" : "are sources")} of a pull distribution point: {string.Join(", ", sourceOf.Select(DpContentAggregator.ServerOf))}. A pull distribution point cannot copy content its source no longer has.");
            checks.Add(new PreflightCheck(CheckNames.PullDp, DeploymentCheckResults.Warn, string.Join(" ", text)));
        }
        else if (kind == DpContentChangeKind.Remove && read is not null && kept.Count > 0)
            checks.Add(new PreflightCheck(CheckNames.PullDp, DeploymentCheckResults.Pass, "No distribution point of the plan is a pull distribution point or the source of one."));
        if (dropped.Count > 0)
            checks.Add(new PreflightCheck(CheckNames.Dropped, DeploymentCheckResults.Warn, $"{Count(dropped.Count, "pair")} {(dropped.Count == 1 ? "was" : "were")} left out of the plan. The list of left-out pairs says why."));
        return checks;
    }

    private string Window => ReadBackWindow.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);

    private List<string> ImpactLines(DpContentChangeKind kind, IReadOnlyList<PairRow> kept, IReadOnlyList<DpContentImpact> impact, IReadOnlyList<DpContentPairOutcome> dropped)
    {
        var lines = new List<string>();
        var dpCount = Count(kept.Select(p => p.NalPath.ToUpperInvariant()).Distinct().Count(), "distribution point");
        switch (kind)
        {
            case DpContentChangeKind.Redistribute:
                lines.Add($"Redistribute {Count(kept.Count, "pair")} on {dpCount}: the site copies the content from its content library to each distribution point again.");
                lines.Add("There is no Undo: a redistribution adds nothing that needs to be taken back.");
                break;
            case DpContentChangeKind.Validate:
                lines.Add($"Validate {Count(kept.Count, "pair")} on {dpCount}: the site checks the hash of each file of the content on each distribution point.");
                lines.Add("There is no Undo: a validation changes no content.");
                break;
            case DpContentChangeKind.Remove:
                lines.Add($"Remove {Count(kept.Count, "pair")} from {dpCount}: the site deletes the content from each distribution point. Clients that use only these distribution points cannot install the content any more.");
                foreach (var i in impact)
                    lines.Add($"{i.Name} ({i.PackageId}): on {Count(i.DpsNow, "distribution point")} now, installed on {Count(i.DpsRemaining, "distribution point")} after the change; "
                        + (i.DeploymentCount < 0 ? "the deployments that use it are not known." : $"used by {Count(i.DeploymentCount, "counted deployment")}."));
                var installed = kept.Count(IsRestorable);
                var anyInstalled = kept.Any(p => DpContentStates.IsOk(p.State) && !p.HasErrorMessage);
                lines.Add(installed == 0
                    ? anyInstalled ? "Undo restores nothing." : "No pair of the plan is installed now, so Undo restores nothing."
                    : $"Undo distributes the {Count(installed, "pair")} that {(installed == 1 ? "is" : "are")} installed now again: the content is copied again and can take a long time. Pairs that are not installed now are not restored. Run Undo after the site has finished the removal: a request made before that can be ignored by the site.");
                var notRestored = kept.Count(p => DpContentStates.IsOk(p.State) && !p.HasErrorMessage && !CanDistributeAgain(p.ContentType));
                if (notRestored > 0)
                    lines.Add($"Undo does not restore {Count(notRestored, "installed pair")} of a task sequence or of unknown content: distribute that content again from the ConfigMgr console.");
                lines.Add(DeploymentKinds);
                break;
            case DpContentChangeKind.DistributeAgain:
                lines.Add($"Distribute {Count(kept.Count, "pair")} again to {dpCount}: the site copies the content to each distribution point. This can take a long time for large content.");
                lines.Add("A request made while the site still removes the same content from the distribution point can be ignored by the site: the result then reads Unchanged. Run it again later.");
                break;
        }
        lines.Add($"The run works in parts of {ChunkSize.ToString(CultureInfo.InvariantCulture)} pairs and reads every pair again before it changes it. Cancel stops between parts.");
        lines.Add($"Results: Requested means the site accepted the request and works on it; Confirmed means a read-back within {Window} seconds showed the change; Unchanged means the read-back showed no sign of the request.");
        if (dropped.Count > 0) lines.Add($"{Count(dropped.Count, "pair")} {(dropped.Count == 1 ? "is" : "are")} left out.");
        return lines;
    }
}
