namespace ApplicationPackagerEngine.Services;

public sealed record DiscoveryPackager(string Id, string Vendor, string Application, string CMName,
                                       string ArpName, string? ArpVersion, string? ArpArch);

public sealed record DiscoveryRequest(string SiteCode, string ProviderMachineName, string ServerOverride,
                                      string DatabaseOverride, IReadOnlyList<DiscoveryPackager> Packagers,
                                      IReadOnlyCollection<string> HiddenIds, IReadOnlyCollection<string> DismissedIds,
                                      int TimeoutSeconds = 600);

public sealed record DiscoveryCandidate(string Id, string CMName, string Vendor, string Application, int ClientCount);

public sealed record DiscoveryResult(IReadOnlyList<DiscoveryCandidate> Candidates, int ClientTotal,
                                     DateTime? InventoryNewest, string StatusText);

public sealed class DiscoveryException : Exception
{
    public DiscoveryException(string message) : base(message) { }
}

public interface IDiscoveryService
{
    Task<DiscoveryResult> ScanAsync(DiscoveryRequest request, CancellationToken ct);
}
