using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>Runs the check families over read data and gives each of the 57 check IDs a status.</summary>
internal static class HygieneEngine
{
    private static readonly Dictionary<HygieneSeverity, int> Rank = new() { [HygieneSeverity.Error] = 0, [HygieneSeverity.Warning] = 1, [HygieneSeverity.Info] = 2 };

    public static (IReadOnlyList<HygieneFinding> Findings, IReadOnlyList<HygieneCheckStatus> Checks) Run(HygieneContext context, IReadOnlySet<string> areas, bool probeOn, IAppLog? log = null)
    {
        var findings = new List<HygieneFinding>();
        var states = new Dictionary<string, (string State, string? Reason)>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in HygieneChecks.Families)
        {
            if (!family.Areas.Any(areas.Contains))
            {
                var names = string.Join(", ", family.Areas.Select(a => HygieneAreas.All.First(x => x.Id == a).Title));
                foreach (var id in family.CheckIds) states[id] = (HygieneCheckStates.NotSelected, $"The scan did not cover the area {names}.");
                continue;
            }
            if (family.OptionGate?.Invoke(context) is { } off)
            {
                foreach (var id in family.CheckIds) states[id] = (HygieneCheckStates.NotSelected, off);
                continue;
            }
            var reason = MissingReason(context.Data, family.Requires) ?? family.Gate?.Invoke(context);
            if (reason is not null)
            {
                foreach (var id in family.CheckIds) states[id] = (HygieneCheckStates.NotRun, reason);
                continue;
            }
            try
            {
                var produced = family.Run(context).ToList();
                findings.AddRange(produced);
                foreach (var id in family.CheckIds) states[id] = (HygieneCheckStates.Ran, null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log?.Warning(SiteHygieneService.LogArea, $"The check family {string.Join(", ", family.CheckIds)} failed ({ex.GetType().Name}): {ex.Message}");
                foreach (var id in family.CheckIds) states[id] = (HygieneCheckStates.Failed, $"The check stopped with an error ({ex.GetType().Name}). Its findings are missing; the failure is a defect worth reporting.");
            }
        }
        var sorted = findings
            .OrderBy(f => Rank[f.Severity])
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ThenBy(f => f.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.ObjectKey, StringComparer.Ordinal)
            .ToList();
        var counts = sorted.GroupBy(f => f.CheckId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var checks = HygieneCatalog.Checks.Select(c =>
        {
            var (state, why) = states.TryGetValue(c.Id, out var s) ? s : (HygieneCheckStates.NotRun, "The check is not built.");
            return new HygieneCheckStatus(c.Id, c.Title, c.Category, state, state == HygieneCheckStates.Ran && counts.TryGetValue(c.Id, out var n) ? n : 0, why) { Severity = c.Severity };
        }).ToList();
        return (sorted, checks);
    }

    /// <summary>Why a family cannot run: a set it needs failed, was cut or was not read; null when every set was read.</summary>
    internal static string? MissingReason(HygieneSiteRead data, IReadOnlyList<string> requires)
    {
        var parts = new List<string>();
        foreach (var set in requires)
        {
            if (!data.Tables.TryGetValue(set, out var t)) { parts.Add($"{SiteHygieneService.Label(set)} were not read"); continue; }
            if (t.State == HygieneSetStates.Cut)
                parts.Add(HygieneSets.PerObject.Contains(set)
                    ? $"{SiteHygieneService.Label(set)} were read for {SiteHygieneService.N(t.Done)} of {SiteHygieneService.N(t.Total)} objects (cap {SiteHygieneService.N(SiteHygieneService.MaxPerObjectReads)})"
                    : $"{SiteHygieneService.Label(set)} were cut at {SiteHygieneService.N(t.Rows.Count)} of {SiteHygieneService.N(t.Total)} rows");
            else if (t.State != HygieneSetStates.Read) parts.Add($"the read of {SiteHygieneService.Label(set)} failed");
        }
        return parts.Count == 0 ? null : "The check did not run: " + string.Join("; ", parts) + ". Running it over incomplete data would report findings that are not true.";
    }
}
