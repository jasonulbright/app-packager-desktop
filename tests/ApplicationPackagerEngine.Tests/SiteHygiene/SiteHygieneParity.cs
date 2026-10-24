using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>Runs the native checks over the CM01 rows and compares them with the source module's findings on the same site.</summary>
internal static class SiteHygieneParity
{
    public static (IReadOnlyList<HygieneFinding> Findings, IReadOnlyList<HygieneCheckStatus> Checks) Native(params string[] areas) =>
        Native(SiteHygieneFixtures.NativeRead(), areas);

    public static (IReadOnlyList<HygieneFinding> Findings, IReadOnlyList<HygieneCheckStatus> Checks) Native(HygieneSiteRead read, params string[] areas)
    {
        var picked = SiteHygieneService.ResolveAreas(areas.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var context = new HygieneContext(read, new HygieneThresholds(), SiteHygieneFixtures.CaptureLocal, "MCM");
        return HygieneEngine.Run(context, picked, probeOn: false);
    }

    /// <summary>Synthetic read data: each set given as its columns and rows, read completely.</summary>
    public static HygieneSiteRead Synthetic(params (string Set, string[] Columns, string[][] Rows)[] sets)
    {
        var read = new HygieneSiteRead { SiteCode = "MCM" };
        foreach (var (set, columns, rows) in sets)
        {
            var table = new HygieneTable(set, columns) { State = HygieneSetStates.Read, Total = rows.Length };
            table.Rows.AddRange(rows);
            read.Tables[set] = table;
        }
        return read;
    }

    /// <summary>Every set every check needs, empty and read, so a synthetic test fills only the sets it judges.</summary>
    public static HygieneSiteRead EmptySite()
    {
        var read = new HygieneSiteRead { SiteCode = "MCM" };
        foreach (var set in HygieneChecks.Families.SelectMany(f => f.Requires).Distinct())
            read.Tables[set] = new HygieneTable(set, []) { State = HygieneSetStates.Read };
        return read;
    }

    public static HygieneSiteRead Put(this HygieneSiteRead read, string set, string[] columns, params string[][] rows)
    {
        var table = new HygieneTable(set, columns) { State = HygieneSetStates.Read, Total = rows.Length };
        table.Rows.AddRange(rows);
        read.Tables[set] = table;
        return read;
    }

    /// <summary>The (check ID, object) pairs of one check ID, from the native findings and from the source findings.</summary>
    public static (SortedSet<string> Native, SortedSet<string> Source) Pairs(IReadOnlyList<HygieneFinding> native, string checkId, Func<HygieneFinding, string> nativeKey, Func<SiteHygieneFixtures.SourceFinding, string> sourceKey)
    {
        var n = new SortedSet<string>(native.Where(f => f.CheckId == checkId).Select(nativeKey), StringComparer.Ordinal);
        var s = new SortedSet<string>(SiteHygieneFixtures.SourceFindings().Where(f => f.CheckId == checkId).Select(sourceKey), StringComparer.Ordinal);
        return (n, s);
    }

    /// <summary>Asserts that the native check finds exactly the objects the source finds, by object key.</summary>
    public static void SameAsSource(IReadOnlyList<HygieneFinding> native, string checkId, Func<SiteHygieneFixtures.SourceFinding, string>? sourceKey = null, Func<HygieneFinding, string>? nativeKey = null)
    {
        var (n, s) = Pairs(native, checkId, nativeKey ?? (f => f.ObjectKey), sourceKey ?? (f => f.ObjectId));
        Assert.True(n.SetEquals(s), $"{checkId}: native [{string.Join("; ", n)}] source [{string.Join("; ", s)}]");
    }
}
