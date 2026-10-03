using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>The WSUS server operations the settings page and the WSUS Updates window call.</summary>
public interface IWsusAdminClient
{
    Task<WsusServerStatus> GetStatusAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<WsusServerStatus> CreateSelfSignedCertificateAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<WsusServerStatus> ImportPfxAsync(WsusSettings settings, string pfxPath, string password, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<string> ExportCertificateAsync(WsusSettings settings, string cerPath, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetComputerGroupNamesAsync(WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<IReadOnlyList<WsusPublishedUpdate>> ListAsync(WsusSettings settings, bool includeOtherPublishers = false, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<IReadOnlyList<WsusItemResult>> SetStateAsync(WsusSettings settings, IEnumerable<Guid> packageIds, string action, string? groupName = null, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<IReadOnlyList<WsusItemResult>> ImportCatalogUpdatesAsync(WsusSettings settings, IEnumerable<Guid> updateIds, Action<string>? onProgress = null, CancellationToken ct = default);
}

/// <summary>Publishes one staged installer to WSUS.</summary>
public interface IWsusPublisher
{
    Task<WsusPublishResult> PublishAsync(StageManifest manifest, string contentFolder, WsusSettings settings,
        Action<string>? onProgress = null, CancellationToken ct = default, string? supportUrl = null);
}

/// <summary>
/// The WSUS half of a publish run as a row result: a publish, a refusal with
/// its codes, or a failure with the server's text. Nothing here throws for
/// a WSUS outcome; cancellation still propagates.
/// </summary>
public static class WsusPublishStep
{
    public static async Task<WsusStepResult> RunAsync(IWsusPublisher publisher, StageManifest manifest, string contentFolder,
        WsusSettings settings, Action<string>? onProgress = null, CancellationToken ct = default, string? supportUrl = null)
    {
        try
        {
            var result = await publisher.PublishAsync(manifest, contentFolder, settings, onProgress, ct, supportUrl).ConfigureAwait(false);
            return result.ToStepResult();
        }
        catch (OperationCanceledException) { throw; }
        catch (WsusRefusedException ex)
        {
            var refused = new WsusStepResult(WsusStepStatus.Refused, "", ex.Codes, []);
            return refused with { Message = refused.NotSupportedReason + ". " + ex.Message };
        }
        catch (Exception ex) when (ex is WsusOperationException or WsusPayloadException or WsusRuleException
                                   or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new WsusStepResult(WsusStepStatus.Failed, ex.Message, [], []);
        }
    }
}
