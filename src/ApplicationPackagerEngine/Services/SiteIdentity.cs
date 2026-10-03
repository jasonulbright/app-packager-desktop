namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The ConfigMgr site a set of version results describes. Whitespace and case
/// differences in stored values name the same site; a raw string compare would report a
/// site change and discard results that still apply.
/// </summary>
public static class SiteIdentity
{
    public static string Key(string? siteCode, string? providerMachine) =>
        $"{(siteCode ?? "").Trim()}|{(providerMachine ?? "").Trim()}".ToUpperInvariant();
}
