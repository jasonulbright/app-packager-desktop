using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class DpContentRegistration
{
    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<IDpContentService, UnavailableDpContentService>();
        services.TryAddSingleton<IDpContentAuditLog, UnavailableDpContentAuditLog>();
        services.TryAddSingleton<IDpContentReportWriter, UnavailableDpContentReports>();
    }

    private const string NotAvailable = "The DP content service is not available in this build.";

    private sealed class UnavailableDpContentService : IDpContentService
    {
        public Task<DpContentSnapshot> LoadAsync(IProgress<string> progress, CancellationToken ct) => throw new NotSupportedException(NotAvailable);
        public Task<DpContentChange> PrepareAsync(DpContentChange request, CancellationToken ct) => throw new NotSupportedException(NotAvailable);
        public Task<IReadOnlyList<DpContentOutcome>> ApplyAsync(IReadOnlyList<DpContentChange> changes, IProgress<DpContentOutcome> progress, CancellationToken ct) =>
            throw new NotSupportedException(NotAvailable);
    }

    private sealed class UnavailableDpContentAuditLog : IDpContentAuditLog
    {
        public AuditProbe Probe() => new(false, NotAvailable);
        public AuditWrite Append(DpContentAuditRecord record) => new(false, NotAvailable);
        public IReadOnlyList<DpContentAuditRecord> Read(int max) => [];
        public string Path => "";
    }

    private sealed class UnavailableDpContentReports : IDpContentReportWriter
    {
        public string WriteCsv(DpContentReport report) => throw new NotSupportedException(NotAvailable);
        public string WriteHtml(DpContentReport report) => throw new NotSupportedException(NotAvailable);
    }
}
