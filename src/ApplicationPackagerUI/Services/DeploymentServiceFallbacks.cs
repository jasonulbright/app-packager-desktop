using System.IO;
using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class DeploymentHelperRegistration
{
    private const string NotAvailable = "The deployment services are not part of this build.";

    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<IDeploymentService, UnavailableDeploymentService>();
        services.TryAddSingleton<IDeploymentAuditLog, UnavailableAuditLog>();
        services.TryAddSingleton<IDeploymentTemplateStore, EmptyTemplateStore>();
        services.TryAddSingleton<IDeploymentReportWriter, UnavailableReportWriter>();
    }

    private sealed class UnavailableDeploymentService : IDeploymentService
    {
        public Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct) => throw new DeploymentServiceException(NotAvailable);
        public Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct) => throw new DeploymentServiceException(NotAvailable);
        public Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct) => throw new DeploymentServiceException(NotAvailable);
        public Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct) => throw new DeploymentServiceException(NotAvailable);
        public Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct) => throw new DeploymentServiceException(NotAvailable);
        public Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct) => throw new DeploymentServiceException(NotAvailable);

        public Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct) =>
            throw new DeploymentServiceException(NotAvailable);
    }

    private sealed class UnavailableAuditLog : IDeploymentAuditLog
    {
        public string Path => System.IO.Path.Combine(AppDataPaths.Root, "Deployments", "deployment-audit.jsonl");
        public AuditProbe Probe() => new(false, NotAvailable);
        public AuditWrite Append(DeploymentAuditRecord r) => new(false, NotAvailable);
        public IReadOnlyList<DeploymentAuditRecord> Read(int max) => [];
    }

    private sealed class EmptyTemplateStore : IDeploymentTemplateStore
    {
        public string? Message => null;
        public IReadOnlyList<DeploymentTemplate> All() => [];
        public void Save(DeploymentTemplate t) => throw new DeploymentServiceException(NotAvailable);
        public void Delete(string id) => throw new DeploymentServiceException(NotAvailable);
    }

    private sealed class UnavailableReportWriter : IDeploymentReportWriter
    {
        public string WriteCsv(IReadOnlyList<DeploymentAuditRecord> rows) => throw new IOException(NotAvailable);
        public string WriteHtml(IReadOnlyList<DeploymentAuditRecord> rows) => throw new IOException(NotAvailable);
    }
}
