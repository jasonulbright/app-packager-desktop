using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The plan text of the DP Content Manager page, made from the prepared change.</summary>
public static class DpContentText
{
    /// <summary>The check names the service gives the remaining-DP check; a failed one is what the last-copy box accepts.</summary>
    public static readonly IReadOnlyList<string> LastCopyChecks = ["LastCopy", "RemainingDps"];

    public const string Pass = "Pass";
    public const string AsyncNote =
        "The site processes each request later. Requested means the site accepted the request; Confirmed means a read-back shows the expected state. A Requested pair is not confirmed.";
    public const string RemoveNote =
        "Clients that use these DPs cannot get the content from them after the removal. Undo last change can distribute again the pairs that were Installed: the content is copied again and that can take a long time. Pairs that were not Installed are not restored.";
    public const string DistributeAgainNote =
        "The content is copied again to each DP, and that can take a long time. Pairs that were not Installed before the removal are not restored.";
    public const string ValidateNote = "Each DP checks the content files against the content library. A validation does not change the content.";
    public const string RedistributeNote = "The site copies the content to each DP again.";
    public const string NothingLeft = "Every pair changed since the list was read, so nothing is left to change. Select Refresh, then try again.";

    public static string ActionName(DpContentChangeKind kind) => kind switch
    {
        DpContentChangeKind.Redistribute => "Redistribute",
        DpContentChangeKind.Remove => "Remove from DP",
        DpContentChangeKind.Validate => "Validate",
        DpContentChangeKind.DistributeAgain => "Distribute again",
        _ => kind.ToString()
    };

    public static string Title(DpContentChangeKind kind) => kind switch
    {
        DpContentChangeKind.Redistribute => "Redistribute content to distribution points",
        DpContentChangeKind.Remove => "Remove content from distribution points",
        DpContentChangeKind.Validate => "Validate content on distribution points",
        DpContentChangeKind.DistributeAgain => "Undo: distribute the removed content again",
        _ => kind.ToString()
    };

    public static bool IsLastCopyCheck(PreflightCheck check) => LastCopyChecks.Contains(check.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A check that stops the plan: a result other than Pass or Warn, except the remaining-DP check, which the last-copy box accepts.</summary>
    public static bool Blocks(PreflightCheck check) =>
        !IsLastCopyCheck(check) && !string.Equals(check.Result, Pass, StringComparison.OrdinalIgnoreCase) && !string.Equals(check.Result, "Warn", StringComparison.OrdinalIgnoreCase);

    public static string Target(PairRow pair) => $"{pair.ContentName} ({pair.PackageId}) on {pair.DpName}";

    private static string Pairs(int count) => DpContentLabels.Count(count, "pair");

    private static string Contents(int count) => DpContentLabels.Count(count, "content");

    /// <summary>
    /// The plan of a prepared change. Every line comes from the pairs the service returned, with the
    /// state it read. The remaining DPs and the deployment count come from the per-content impact of
    /// the prepared change; the loaded rows fill in a content the impact does not name, and add the
    /// pull DP mark. Null with the reason when the change cannot run.
    /// </summary>
    public static (DpContentPlan? Plan, string? Problem) BuildPlan(DpContentChange prepared, DpContentIndex index)
    {
        if (prepared.Pairs.Count == 0)
            return (null, string.Join(" ", prepared.Impact.Prepend(NothingLeft).Concat(prepared.Dropped.Select(LeftOut))));
        var blocking = prepared.Checks.Where(Blocks).ToList();
        if (blocking.Count > 0)
            return (null, "The plan cannot run: " + string.Join(" ", blocking.Select(c => c.Message.Length > 0 ? c.Message : $"{c.Name}: {c.Result}.")));

        var kind = prepared.Kind;
        var removal = kind == DpContentChangeKind.Remove;
        var impact = new Dictionary<string, DpContentImpact>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in prepared.ContentImpact) impact.TryAdd(i.PackageId, i);
        var remaining = removal ? index.RemainingAfterRemoval(prepared.Pairs) : [];
        foreach (var i in impact.Values)
            if (removal) remaining[i.PackageId] = i.DpsRemaining;
        var installedRemoved = prepared.Pairs.Where(p => DpContentLabels.IsOk(p.State) && !p.HasErrorMessage).Select(p => p.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lastCopyContents = remaining.Where(r => r.Value == 0 && installedRemoved.Contains(r.Key)).Select(r => r.Key).ToList();
        var lastCopyCheck = prepared.Checks.Any(c => IsLastCopyCheck(c) && !string.Equals(c.Result, Pass, StringComparison.OrdinalIgnoreCase));
        var needsLastCopy = removal && (lastCopyContents.Count > 0 || lastCopyCheck || prepared.NeedsLastCopyAcknowledgement);

        var action = ActionName(kind);
        var entries = new List<DpContentPlanEntry>(prepared.Pairs.Count);
        foreach (var pair in prepared.Pairs)
        {
            var lines = new List<string> { kind == DpContentChangeKind.DistributeAgain ? $"State before the removal: {pair.StateText}." : $"State now: {pair.StateText}." };
            if (removal && remaining.TryGetValue(pair.PackageId, out var left))
                lines.Add(left == 0 ? "No DP holds the content after this plan." : $"DPs that hold the content after this plan: {DpContentLabels.Number(left)}.");
            var known = index.ContentById.TryGetValue(pair.PackageId, out var content);
            var deployments = impact.TryGetValue(pair.PackageId, out var read) && read.DeploymentCount >= 0 ? read.DeploymentCount : content?.DeploymentCount ?? -1;
            if (known || read is not null)
                lines.Add(deployments < 0 ? "Deployments that use the content: unknown." : $"Deployments that use the content: {DpContentLabels.Number(deployments)}.");
            if (!known) lines.Add("The content is not in the loaded list.");
            if (index.DpByNal.TryGetValue(pair.NalPath, out var dp) && dp.IsPullDp) lines.Add("The DP is a pull DP.");
            entries.Add(new DpContentPlanEntry(new ChangePlanItem(action, Target(pair), string.Join(Environment.NewLine, lines)), pair));
        }

        // The notes box shows the first lines without a scroll, so the checks that need attention and the left-out pairs come first.
        var notes = new List<string>();
        notes.AddRange(prepared.Checks.Where(c => !IsPass(c)).Select(CheckLine));
        notes.AddRange(prepared.Dropped.Select(LeftOut));
        notes.AddRange(prepared.Impact);
        notes.AddRange(prepared.Checks.Where(IsPass).Select(CheckLine));
        notes.Add(kind switch
        {
            DpContentChangeKind.Remove => RemoveNote,
            DpContentChangeKind.DistributeAgain => DistributeAgainNote,
            DpContentChangeKind.Validate => ValidateNote,
            _ => RedistributeNote
        });
        notes.Add(AsyncNote);

        var count = entries.Count;
        string? extra = null;
        string? second = null;
        if (removal)
        {
            var noDp = lastCopyContents.Count;
            extra = noDp > 0
                ? $"Remove {Pairs(count)}: {Contents(noDp)} will have no DP left."
                : $"Remove {Pairs(count)} from distribution points. Clients that use these DPs cannot get the content from them.";
            if (needsLastCopy)
                second = noDp > 0
                    ? $"{Contents(noDp)} become{(noDp == 1 ? "s" : "")} unavailable to every client: no distribution point holds {(noDp == 1 ? "it" : "them")} after this removal."
                    : "A content of this plan becomes unavailable to every client: the site reports that no distribution point holds it after this removal.";
        }

        var plan = new ChangePlan(Title(kind), $"{action} {Pairs(count)}", entries.Select(e => e.Item), isDestructive: removal, hasUndo: false)
        {
            ExtraConfirmation = extra, SecondConfirmation = second, Notes = notes, LogName = action
        };
        return (new DpContentPlan(action, prepared, plan, entries), null);
    }

    private static bool IsPass(PreflightCheck check) => string.Equals(check.Result, Pass, StringComparison.OrdinalIgnoreCase);

    private static string CheckLine(PreflightCheck c) => $"{c.Name}: {c.Result}{(c.Message.Length > 0 ? ". " + c.Message : ".")}";

    /// <summary>The line of a pair the service left out of the plan, with its reason.</summary>
    public static string LeftOut(DpContentPairOutcome dropped) => $"Left out: {Target(dropped.Pair)}: {dropped.Message}";
}

/// <summary>Counts and joined names read from the pairs of an audit record.</summary>
public static class DpContentAuditRecordExtensions
{
    extension(DpContentAuditRecord record)
    {
        public string Action => record.Event;
        public int PairCount => record.Pairs.Count;
        public int Succeeded => record.Pairs.Count(p => p.Result is "Requested" or "Confirmed");
        public int Failed => record.Pairs.Count(p => p.Result == "Failed");
        public int Skipped => record.Pairs.Count(p => p.Result == "Skipped");
        public string PackageIds => string.Join("; ", record.Pairs.Select(p => p.PackageId).Distinct(StringComparer.OrdinalIgnoreCase));
        public string DpNames => string.Join("; ", record.Pairs.Select(p => p.DpName).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
