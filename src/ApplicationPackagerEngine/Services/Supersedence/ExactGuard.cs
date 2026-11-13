using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Supersedence;

/// <summary>
/// IsEquals hides nothing: a clause that says 3.7.7 does not find a product that reports 3.7.7.1234. So Exact is applied to a clause only
/// when a value that was read from the installed product equals the expected value. The installed value is the ProductVersion of
/// an MSI for the clause on the Windows Installer product, and for any other clause the value that a test install recorded for the title
/// and the version. Without a value, or with a different one, Exact is blocked for the clause.
/// </summary>
public sealed class ExactGuard
{
    private readonly Func<StageManifest, string?> _msiProductVersion;
    private readonly Dictionary<string, string> _recorded;
    private readonly ISet<string> _accepted;

    /// <param name="msiProductVersion">Reads the ProductVersion of the staged MSI of a manifest; null when it is not an MSI or unreadable.</param>
    /// <param name="recorded">Installed values by <see cref="RecordKey"/>.</param>
    /// <param name="accepted">Accepted blocks by <see cref="RecordKey"/>.</param>
    public ExactGuard(Func<StageManifest, string?> msiProductVersion, IReadOnlyDictionary<string, string>? recorded, IEnumerable<string>? accepted)
    {
        _msiProductVersion = msiProductVersion;
        _recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A settings file can hold two keys that differ only in letter case; the later one wins and the guard never throws.
        foreach (var pair in recorded ?? new Dictionary<string, string>()) _recorded[pair.Key] = pair.Value;
        _accepted = new HashSet<string>(accepted ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The key of a recorded installed value or of an accepted block: the title and the version, so a new version needs its own.</summary>
    public static string RecordKey(string titleKey, string version) => $"{titleKey.Trim()}|{version.Trim()}";

    /// <summary>The text compare of the specification: the separators + and - become a dot, and no zero part is ignored.</summary>
    public static bool SameVersionText(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => value.Trim().Replace('+', '.').Replace('-', '.');

    /// <summary>True for the registry clause on the Windows Installer product of an MSI: Uninstall\{ProductCode}, DisplayVersion.</summary>
    public static bool IsMsiProductClause(StageManifest manifest, StageDetection clause) =>
        string.Equals(manifest.InstallerType, "MSI", StringComparison.OrdinalIgnoreCase)
        && clause.Type.Equals("RegistryKeyValue", StringComparison.OrdinalIgnoreCase)
        && string.Equals(clause.ValueName, "DisplayVersion", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(manifest.ProductCode)
        && (clause.RegistryKeyRelative ?? "").Contains(manifest.ProductCode, StringComparison.OrdinalIgnoreCase);

    public ExactGuardVerdict Evaluate(StageManifest manifest, StageDetection clause, string? titleKey)
    {
        var expected = clause.ExpectedValue ?? "";
        var key = titleKey is null ? null : RecordKey(titleKey, manifest.SoftwareVersion);
        string? installed;
        string source;
        if (IsMsiProductClause(manifest, clause))
        {
            installed = _msiProductVersion(manifest);
            source = "the ProductVersion of the MSI";
        }
        else
        {
            installed = key is not null && _recorded.TryGetValue(key, out var value) ? value : null;
            source = "the value that a test install recorded";
        }

        if (!string.IsNullOrWhiteSpace(installed) && SameVersionText(installed, expected)) return ExactGuardVerdict.Allowed;

        var reason = string.IsNullOrWhiteSpace(installed)
            ? $"No installed value exists for {VersionRuleApplier.Describe(clause)} (version {manifest.SoftwareVersion}). Run the test install of the title to record it."
            : $"The installed value '{installed}' ({source}) differs from the expected value '{expected}' of {VersionRuleApplier.Describe(clause)}.";
        return key is not null && _accepted.Contains(key)
            ? new ExactGuardVerdict(ClauseResult.ExactAcceptedBlock, reason)
            : new ExactGuardVerdict(ClauseResult.ExactBlocked, reason);
    }
}
