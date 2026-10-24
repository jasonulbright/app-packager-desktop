using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class PivotsScriptsRegistration
{
    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<IPivotsScriptsService, UnavailableService>();
        services.TryAddSingleton<IPivotsSessionStore, UnavailableSessions>();
        services.TryAddSingleton<IPivotsAuditLog, UnavailableAuditLog>();
        services.TryAddSingleton<IPivotsReportWriter, UnavailableReports>();
        services.TryAddSingleton<IPivotsLibrary, EmptyLibrary>();
    }

    private sealed class UnavailableService : IPivotsScriptsService
    {
        public Task<ResolvedTargets> ResolveTargetsAsync(RunTarget target, CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<IReadOnlyList<SiteScript>> LoadScriptsAsync(CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<SiteScript?> LoadScriptAsync(string guid, CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<ScriptCheck> CheckScriptAsync(string text, CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<PivotsChange> PrepareAsync(PivotsChange request, CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<PivotsOutcome> ApplyAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<PivotsOutcome> WaitForResultsAsync(string operationId, IProgress<PivotsOutcome> progress, CancellationToken ct) => throw new PivotsServiceException(NotAvailable);
        public Task<string> TestConnectionAsync(CancellationToken ct) => Task.FromResult(NotAvailable);
    }

    private sealed class UnavailableSessions : IPivotsSessionStore
    {
        public void Save(PivotsSessionRecord record) => throw new PivotsServiceException(NotAvailable);
        public void Update(PivotsSessionRecord record) => throw new PivotsServiceException(NotAvailable);
        public IReadOnlyList<PivotsSessionSummary> List(int max) => [];
        public PivotsSessionRecord? Read(string id) => null;
        public string Folder => "";
    }

    private sealed class UnavailableAuditLog : IPivotsAuditLog
    {
        public AuditProbe Probe() => new(false, NotAvailable);
        public AuditWrite Append(PivotsAuditRecord r) => new(false, NotAvailable);
        public IReadOnlyList<PivotsAuditRecord> Read(int max) => [];
        public string Path => "";
    }

    private sealed class UnavailableReports : IPivotsReportWriter
    {
        public string WriteCsv(PivotsReport report) => throw new PivotsServiceException(NotAvailable);
        public string WriteJson(PivotsReport report) => throw new PivotsServiceException(NotAvailable);
        public string Folder => "";
    }

    private sealed class EmptyLibrary : IPivotsLibrary
    {
        public IReadOnlyList<PivotsLibraryItem> Load(string? userFolder) => [];
    }
}
