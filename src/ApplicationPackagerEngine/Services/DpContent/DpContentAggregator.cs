using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Builds the snapshot from what the site returned, in time linear in the number of rows: every
/// lookup is a dictionary keyed by PackageID or by NAL path without case.
/// </summary>
internal static partial class DpContentAggregator
{
    [GeneratedRegex(@"\\\\([^\\""\]\[]+)\\?\s*$")]
    private static partial Regex NalTailRegex();

    /// <summary>
    /// The server name a NAL path ends with, such as DP01.contoso.com for
    /// ["Display=\\DP01.contoso.com\"]MSWNET:["SMS_SITE=PS1"]\\DP01.contoso.com\; the whole path when it has none.
    /// </summary>
    public static string ServerOf(string nal)
    {
        var m = NalTailRegex().Match(nal ?? "");
        return m.Success ? m.Groups[1].Value : nal ?? "";
    }

    /// <summary>A cloud distribution point; the ResourceType text is not verified on a site with one.</summary>
    public static bool IsCloud(string resourceType) => (resourceType ?? "").Contains("Azure", StringComparison.OrdinalIgnoreCase);

    public static string TypeOfPackageType(int packageType) => packageType switch
    {
        0 => DpContentTypes.Package,
        3 => DpContentTypes.DriverPackage,
        4 => DpContentTypes.TaskSequence,
        5 => DpContentTypes.SoftwareUpdatePackage,
        8 => DpContentTypes.Application,
        257 => DpContentTypes.OsImage,
        258 => DpContentTypes.BootImage,
        259 => DpContentTypes.OsUpgradePackage,
        _ => DpContentTypes.Unknown
    };

    public static string UnknownName(string packageId) => $"unknown content ({packageId})";

    public static string Key(string packageId, string nal) => (packageId ?? "").ToUpperInvariant() + "|" + (nal ?? "").ToUpperInvariant();

    /// <summary>The pair rows of the summarizer rows, named from the content and DP lists, with the last status message of each pair.</summary>
    public static List<PairRow> Pairs(DpContentSiteRead read)
    {
        var dps = DpsByNal(read);
        var contents = ContentById(read);
        var messages = new Dictionary<string, DpContentSiteRead.Message>(StringComparer.Ordinal);
        foreach (var m in read.Messages) messages[Key(m.PackageId, m.Nal)] = m;
        var pairs = new List<PairRow>(read.Summaries.Count);
        foreach (var s in read.Summaries)
        {
            contents.TryGetValue(s.PackageId, out var content);
            dps.TryGetValue(s.Nal, out var dp);
            messages.TryGetValue(Key(s.PackageId, s.Nal), out var message);
            pairs.Add(new PairRow(s.PackageId, content?.Name ?? UnknownName(s.PackageId), content?.Type ?? TypeOfPackageType(s.PackageType), dp?.Name ?? ServerOf(s.Nal), s.Nal, s.State,
                DpContentStates.Name(s.State), s.SourceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), s.SummaryDate)
            {
                SiteCode = s.SiteCode.Length > 0 ? s.SiteCode : dp?.SiteCode ?? "",
                MessageState = message?.MessageState is { } ms and >= 0 ? ms : null,
                MessageId = message?.MessageId is { } mi and >= 0 ? mi : null
            });
        }
        return pairs;
    }

    public static Dictionary<string, DpContentSiteRead.Dp> DpsByNal(DpContentSiteRead read)
    {
        var map = new Dictionary<string, DpContentSiteRead.Dp>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in read.Dps) map.TryAdd(d.Nal, d);
        return map;
    }

    public static Dictionary<string, DpContentSiteRead.Content> ContentById(DpContentSiteRead read)
    {
        var map = new Dictionary<string, DpContentSiteRead.Content>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in read.Contents) map.TryAdd(c.PackageId, c);
        return map;
    }

    private sealed class Tally
    {
        public int Total, Installed, InProgress, Failed;
        public double SizeKb;

        public void Add(PairRow pair)
        {
            Total++;
            if (pair.IsFailed) Failed++;
            else if (DpContentStates.IsOk(pair.State)) Installed++;
            else if (pair.IsInProgress) InProgress++;
        }
    }

    public static DpContentSnapshot Snapshot(DpContentSiteRead read, IReadOnlyList<PairRow> pairs, int totalPairs, DateTime loadedUtc)
    {
        var contents = ContentById(read);
        var byDp = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var byContent = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, PairRow>(StringComparer.OrdinalIgnoreCase);
        var dpSite = new Dictionary<string, (string Name, string Site)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            if (!byDp.TryGetValue(pair.NalPath, out var d)) byDp[pair.NalPath] = d = new Tally();
            d.Add(pair);
            if (contents.TryGetValue(pair.PackageId, out var content)) d.SizeKb += content.SizeKb;
            if (!byContent.TryGetValue(pair.PackageId, out var c)) byContent[pair.PackageId] = c = new Tally();
            c.Add(pair);
            if (content is null) unknown.TryAdd(pair.PackageId, pair);
            dpSite.TryAdd(pair.NalPath, (pair.DpName, pair.SiteCode));
        }

        var dps = new List<DpRow>(read.Dps.Count);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dp in read.Dps)
        {
            if (!listed.Add(dp.Nal)) continue;
            byDp.TryGetValue(dp.Nal, out var t);
            dps.Add(DpRowOf(dp.Name.Length > 0 ? dp.Name : ServerOf(dp.Nal), dp.Nal, dp.SiteCode, dp.IsPullDp, t) with { IsCloud = IsCloud(dp.ResourceType) });
        }
        foreach (var (nal, t) in byDp)
            if (!listed.Contains(nal))
                dps.Add(DpRowOf(dpSite[nal].Name, nal, dpSite[nal].Site, false, t));

        var truncated = totalPairs > pairs.Count;
        var rows = new List<ContentRow>(read.Contents.Count + unknown.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var content in read.Contents)
        {
            if (!seen.Add(content.PackageId)) continue;
            byContent.TryGetValue(content.PackageId, out var t);
            rows.Add(ContentRowOf(content.PackageId, content.Name, content.Type, content.SizeKb, t) with { ObjectId = content.ObjectId, CountsKnown = !truncated });
        }
        foreach (var (id, pair) in unknown)
            rows.Add(ContentRowOf(id, pair.ContentName, pair.ContentType, 0, byContent[id]) with { ObjectId = "", CountsKnown = !truncated });
        // The status rows are not sorted by DP or by content, so after a cut any rollup can miss a row.
        if (truncated)
            for (var i = 0; i < dps.Count; i++) dps[i] = dps[i] with { CountsKnown = false };

        var note = truncated
            ? $"Truncated: showing {pairs.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} of {totalPairs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} status rows. The counts and the status of each DP and each content are not known; the lists show the rows read."
            : null;
        return new DpContentSnapshot(dps, rows, pairs, truncated, note) { TotalPairs = Math.Max(totalPairs, pairs.Count), LoadedUtc = loadedUtc };
    }

    private static DpRow DpRowOf(string name, string nal, string site, bool pull, Tally? t) =>
        new(name, nal, site, pull, t?.Total ?? 0, t?.Installed ?? 0, t?.InProgress ?? 0, t?.Failed ?? 0, DpContentRollup.PercentOk(t?.Installed ?? 0, t?.Total ?? 0),
            Math.Round((t?.SizeKb ?? 0) / 1024 / 1024, 2, MidpointRounding.AwayFromZero), t is not null);

    private static ContentRow ContentRowOf(string id, string name, string type, double sizeKb, Tally? t) =>
        new(id, name, type, Math.Round(sizeKb / 1024, 2, MidpointRounding.AwayFromZero), t?.Total ?? 0, t?.Installed ?? 0, t?.InProgress ?? 0, t?.Failed ?? 0,
            DpContentRollup.PercentOk(t?.Installed ?? 0, t?.Total ?? 0));
}
