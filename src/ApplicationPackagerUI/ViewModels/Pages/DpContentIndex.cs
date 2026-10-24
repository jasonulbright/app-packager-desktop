using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The loaded snapshot with its lookups: the content by PackageID, the DPs by NALPath and the pairs
/// of each content and of each DP. One pass over each list builds it, so its cost grows linearly
/// with the number of rows.
/// </summary>
public sealed class DpContentIndex
{
    public DpContentIndex(DpContentSnapshot snapshot)
    {
        Snapshot = snapshot;
        foreach (var content in snapshot.Content) ContentById.TryAdd(content.PackageId, content);
        foreach (var dp in snapshot.Dps) DpByNal.TryAdd(dp.NalPath, dp);
        foreach (var pair in snapshot.Pairs)
        {
            Add(PairsByContent, pair.PackageId, pair);
            Add(PairsByDp, pair.NalPath, pair);
            if (pair.IsIssue) Issues.Add(pair);
        }
    }

    private static void Add(Dictionary<string, List<PairRow>> map, string key, PairRow pair)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(pair);
    }

    public DpContentSnapshot Snapshot { get; }
    public Dictionary<string, ContentRow> ContentById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DpRow> DpByNal { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PairRow>> PairsByContent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<PairRow>> PairsByDp { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every pair whose state is not Installed or Content Valid.</summary>
    public List<PairRow> Issues { get; } = [];

    public IReadOnlyList<PairRow> PairsOf(ContentRow content) => PairsByContent.TryGetValue(content.PackageId, out var list) ? list : [];

    public IReadOnlyList<PairRow> PairsOn(DpRow dp) => PairsByDp.TryGetValue(dp.NalPath, out var list) ? list : [];

    /// <summary>
    /// The number of DPs that still hold each content of <paramref name="removed"/> after these pairs
    /// are removed: the other pairs of the content that are installed and carry no error message, as
    /// the service counts them.
    /// </summary>
    public Dictionary<string, int> RemainingAfterRemoval(IReadOnlyList<PairRow> removed)
    {
        var keys = new HashSet<string>(removed.Select(DpContentLabels.Key), StringComparer.Ordinal);
        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in removed.Select(p => p.PackageId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pairs = PairsByContent.TryGetValue(packageId, out var list) ? list : [];
            remaining[packageId] = pairs.Count(p => !keys.Contains(DpContentLabels.Key(p)) && DpContentLabels.IsOk(p.State) && !p.HasErrorMessage);
        }
        return remaining;
    }
}
