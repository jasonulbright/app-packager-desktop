namespace ApplicationPackagerEngine.Services;

public sealed class DiscoveryService : IDiscoveryService
{
    public Task<DiscoveryResult> ScanAsync(DiscoveryRequest request, CancellationToken ct) =>
        throw new NotImplementedException();
}
