using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// What a publish does with the updates already on the server under its
/// identity. Existing is the newest update of the same type and version
/// that is not expired; Earlier holds the lower versions of the same type;
/// RetiredCount counts the updates of the retired type Application that are
/// not expired; LeftAlone names the rest with the reason.
/// </summary>
public sealed record WsusSupersedenceDecision(
    WsusUpdateCandidate? Existing,
    IReadOnlyList<WsusUpdateCandidate> Earlier,
    int RetiredCount,
    IReadOnlyList<(WsusUpdateCandidate Update, string Reason)> LeftAlone);

public static class WsusSupersedence
{
    /// <summary>
    /// Only a lower version is superseded: a build of an older version,
    /// published after a newer one, must not retire the newer update. A
    /// version that cannot be compared is left alone. An update under one of
    /// <paramref name="formerTags"/> counts as this identity's.
    /// </summary>
    public static WsusSupersedenceDecision Decide(IEnumerable<WsusUpdateCandidate> candidates, string identityTag, string version,
        IReadOnlyCollection<string>? formerTags = null)
    {
        WsusUpdateCandidate? existing = null;
        var earlier = new List<WsusUpdateCandidate>();
        var leftAlone = new List<(WsusUpdateCandidate, string)>();
        var retired = 0;
        foreach (var candidate in candidates)
        {
            var parsed = WsusIdentity.ParseIdentity(candidate.Description);
            if (parsed is null || (parsed.IdentityTag != identityTag && formerTags?.Contains(parsed.IdentityTag) != true)) continue;
            if (parsed.PackageType != WsusSettings.UpdatePackageType)
            {
                if (!candidate.Expired) retired++;
                continue;
            }
            if (candidate.Expired) continue;
            var order = WsusIdentity.CompareVersion(parsed.Version, version);
            if (order == 0)
            {
                if (existing is null || (candidate.Created ?? DateTimeOffset.MinValue) > (existing.Created ?? DateTimeOffset.MinValue)) existing = candidate;
                continue;
            }
            if (order == -1) { earlier.Add(candidate); continue; }
            leftAlone.Add((candidate, order is null ? "its version cannot be compared" : "it carries a newer version"));
        }
        return new WsusSupersedenceDecision(existing, earlier, retired, leftAlone);
    }
}
