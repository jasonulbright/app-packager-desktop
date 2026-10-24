using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class SiteHygieneRegistration
{
    public const string NotAvailable = "The Site Hygiene service is not available in this build.";

    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<ISiteHygieneService, UnavailableSiteHygieneService>();
        services.TryAddSingleton<IHygieneSuppressionStore, EmptySuppressionStore>();
        services.TryAddSingleton<IHygieneScanStore, EmptyScanStore>();
        services.TryAddSingleton<IHygieneReportWriter, UnavailableReportWriter>();
    }

    private sealed class UnavailableSiteHygieneService : ISiteHygieneService
    {
        public Task<HygieneScanResult> ScanAsync(HygieneScanOptions options, IProgress<string> progress, CancellationToken ct) => throw new SiteHygieneServiceException(NotAvailable);
        public Task<RelationInventory> LoadRelationsAsync(CancellationToken ct) => throw new SiteHygieneServiceException(NotAvailable);
        public Task<HygieneChange> PrepareAsync(HygieneChange request, CancellationToken ct) => throw new SiteHygieneServiceException(NotAvailable);
        public Task<HygieneOutcome> ApplyAsync(HygieneChange change, IProgress<HygieneOutcome> progress, CancellationToken ct) => throw new SiteHygieneServiceException(NotAvailable);
    }

    private sealed class EmptySuppressionStore : IHygieneSuppressionStore
    {
        public IReadOnlyList<Suppression> Load() => [];
        public void Add(Suppression suppression) => throw new SiteHygieneServiceException(NotAvailable);
        public void Remove(string checkId, string objectKey) => throw new SiteHygieneServiceException(NotAvailable);
    }

    private sealed class EmptyScanStore : IHygieneScanStore
    {
        public void Save(HygieneScanResult result) { }
        public HygieneScanResult? Load() => null;
        public IReadOnlySet<string> LastKeys() => new HashSet<string>();
    }

    private sealed class UnavailableReportWriter : IHygieneReportWriter
    {
        public string WriteCsv(HygieneReport report) => throw new SiteHygieneServiceException(NotAvailable);
        public string WriteHtml(HygieneReport report) => throw new SiteHygieneServiceException(NotAvailable);
        public string Folder => "";
    }
}
