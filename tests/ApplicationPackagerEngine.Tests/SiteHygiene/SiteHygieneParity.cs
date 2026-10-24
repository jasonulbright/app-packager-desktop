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
