using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One Add/Remove Programs product row: the distinct clients that report this name,
/// publisher, version and inventory view. <see cref="Clients"/> holds per-scan ordinals and
/// never leaves the matching step.
/// </summary>
internal sealed record InventoryRow(string DisplayName, string Publisher, string Version, string Architecture, IReadOnlySet<int> Clients);

internal sealed record InventorySnapshot(IReadOnlyList<InventoryRow> Rows, int ClientTotal, DateTime? InventoryNewest,
                                         IReadOnlyList<string> Applications);

/// <summary>A packager whose patterns compiled; only hidden, not dismissed packagers with an ArpName get one.</summary>
internal sealed record MatchablePackager(DiscoveryPackager Packager, IReadOnlyList<Regex> Names, IReadOnlyList<Regex> Versions, string? Architecture);

internal static class DiscoveryMatcher
{
    /// <summary>
    /// The packagers that can be candidates: hidden, not dismissed, ArpName not "none".
    /// Throws a <see cref="DiscoveryException"/> naming the packager for a pattern that is
    /// not a valid wildcard.
    /// </summary>
    public static IReadOnlyList<MatchablePackager> Prepare(IReadOnlyList<DiscoveryPackager>? packagers,
        IReadOnlyCollection<string>? hiddenIds, IReadOnlyCollection<string>? dismissedIds)
    {
        var hidden = Keys(hiddenIds);
        var dismissed = Keys(dismissedIds);
        var result = new List<MatchablePackager>();
        foreach (var packager in packagers ?? [])
        {
            if (packager is null) continue;
            var id = packager.Id?.Trim() ?? "";
            if (id.Length == 0 || !hidden.Contains(id) || dismissed.Contains(id)) continue;
            var names = Compile(packager, packager.ArpName, "ArpName");
            if (names.Count == 0) continue;
            var versions = Compile(packager, packager.ArpVersion, "ArpVersion");
            var architecture = string.IsNullOrWhiteSpace(packager.ArpArch) ? null : packager.ArpArch.Trim();
            result.Add(new MatchablePackager(packager, names, versions, architecture));
        }
        return result;
    }

    private static HashSet<string> Keys(IReadOnlyCollection<string>? values) =>
        new((values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);

    private static List<Regex> Compile(DiscoveryPackager packager, string? value, string tag)
    {
        var list = new List<Regex>();
        foreach (var pattern in ArpWildcard.Split(value))
        {
            if (!ArpWildcard.TryCreate(pattern, out var regex))
                throw new DiscoveryException($"The definition for {Label(packager)} has an {tag} pattern that is not a valid wildcard: {pattern}");
            list.Add(regex);
        }
        return list;
    }

    private static string Label(DiscoveryPackager p) =>
        !string.IsNullOrWhiteSpace(p.Application) ? p.Application.Trim() : !string.IsNullOrWhiteSpace(p.CMName) ? p.CMName.Trim() : p.Id;

    /// <summary>
    /// The candidates: a packager that matches at least one row (name pattern, optional
    /// version pattern, optional inventory view) and whose CMName has no current ConfigMgr
    /// Application by <see cref="MecmApplicationTitleRule.IsManaged"/>. The client count is
    /// the number of distinct clients over the matched rows. Sorted by CMName.
    /// </summary>
    public static IReadOnlyList<DiscoveryCandidate> FindCandidates(InventorySnapshot snapshot, IReadOnlyList<MatchablePackager> packagers)
    {
        var rows = snapshot.Rows.Where(r => !string.IsNullOrWhiteSpace(r.DisplayName)).ToList();
        if (rows.Count == 0) return [];
        var applications = snapshot.Applications.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();

        var candidates = new List<DiscoveryCandidate>();
        foreach (var m in packagers)
        {
            var p = m.Packager;
            var cmName = p.CMName?.Trim() ?? "";
            if (cmName.Length > 0 && MecmApplicationTitleRule.IsManaged(cmName, applications)) continue;

            var clients = new HashSet<int>();
            var matched = false;
            try
            {
                foreach (var row in rows)
                {
                    if (m.Architecture is not null && !string.Equals(m.Architecture, row.Architecture, StringComparison.OrdinalIgnoreCase)) continue;
                    if (m.Versions.Count > 0 && !m.Versions.Any(v => v.IsMatch(row.Version))) continue;
                    if (!m.Names.Any(n => n.IsMatch(row.DisplayName))) continue;
                    matched = true;
                    clients.UnionWith(row.Clients);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                throw new DiscoveryException($"Matching the definition for {Label(p)} against inventory took too long. Check its ArpName and ArpVersion patterns.");
            }
            if (!matched) continue;
            candidates.Add(new DiscoveryCandidate(p.Id.Trim(), cmName, p.Vendor ?? "", p.Application ?? "", clients.Count));
        }
        return candidates.OrderBy(c => c.CMName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// One line for the log and the prompt: client total, inventory age and candidate count,
    /// or the no-inventory-data text for zero rows.
    /// </summary>
    public static string StatusText(InventorySnapshot snapshot, int candidateCount, DateTime now)
    {
        if (snapshot.Rows.Count == 0)
            return "No inventory data: the site database has no Add or Remove Programs rows. Check that hardware inventory collects Add or Remove Programs.";

        var clients = snapshot.ClientTotal;
        var clientWord = clients == 1 ? "client" : "clients";
        var age = "age of newest data is unknown";
        if (snapshot.InventoryNewest is { } newest)
        {
            var days = (int)(now.Date - newest.Date).TotalDays;
            age = days <= 0 ? "newest data is from today" : days == 1 ? "newest data is 1 day old" : $"newest data is {days.ToString(CultureInfo.InvariantCulture)} days old";
        }
        var appWord = candidateCount == 1 ? "app" : "apps";
        return $"Inventory covers {clients.ToString(CultureInfo.InvariantCulture)} {clientWord}; {age}. Discovered {candidateCount.ToString(CultureInfo.InvariantCulture)} {appWord}.";
    }
}
