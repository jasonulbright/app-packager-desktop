using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>One definition as Discover matches it. <see cref="Id"/> is the hidden-list key.</summary>
public sealed record DiscoveryPackager(string Id, string Vendor, string Application, string CMName,
                                       string ArpName, string? ArpVersion, string? ArpArch);

/// <summary>
/// One scan. Both overrides together skip the SMS Provider; otherwise the site definition
/// supplies what the overrides leave empty, and the database defaults to CM_&lt;SiteCode&gt;.
/// </summary>
public sealed record DiscoveryRequest(string SiteCode, string ProviderMachineName, string ServerOverride,
                                      string DatabaseOverride, IReadOnlyList<DiscoveryPackager> Packagers,
                                      IReadOnlyCollection<string> HiddenIds, IReadOnlyCollection<string> DismissedIds,
                                      int TimeoutSeconds = 600);

/// <summary>A hidden packager installed on <see cref="ClientCount"/> distinct clients with no ConfigMgr Application.</summary>
public sealed record DiscoveryCandidate(string Id, string CMName, string Vendor, string Application, int ClientCount);

/// <summary>Candidates sorted by CMName, the inventory client total and age, and the one-line status.</summary>
public sealed record DiscoveryResult(IReadOnlyList<DiscoveryCandidate> Candidates, int ClientTotal,
                                     DateTime? InventoryNewest, string StatusText);

/// <summary>A scan failure. The message is plain text for the user: no stack trace, no raw exception text.</summary>
public sealed class DiscoveryException : Exception
{
    public DiscoveryException(string message) : base(message) { }
}

public interface IDiscoveryService
{
    /// <summary>
    /// Reads Add/Remove Programs inventory and ConfigMgr Application names from the site
    /// database and returns the candidates. Throws <see cref="DiscoveryException"/> on a
    /// failure and when the scan exceeds the request timeout;
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<DiscoveryResult> ScanAsync(DiscoveryRequest request, CancellationToken ct);
}

public static class DiscoveryPackagers
{
    /// <summary>The Discover view of a definition; an empty CMName falls back to the application name.</summary>
    public static DiscoveryPackager From(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var cmName = string.IsNullOrWhiteSpace(definition.CMName) ? definition.Application : definition.CMName;
        return new DiscoveryPackager(definition.Id, definition.Vendor, definition.Application, cmName.Trim(),
            definition.ArpName?.Trim() ?? "", Optional(definition.ArpVersion), Optional(definition.ArpArch)?.ToLowerInvariant());
    }
}
