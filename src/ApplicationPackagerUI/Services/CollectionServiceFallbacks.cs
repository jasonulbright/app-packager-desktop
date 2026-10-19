using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class CollectionComplianceRegistration
{
    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<ICollectionService, UnavailableCollectionService>();
        services.TryAddSingleton<IQueryTemplateStore, UnavailableQueryTemplateStore>();
        services.TryAddSingleton<ICollectionReportWriter, UnavailableCollectionReportWriter>();
    }
}

internal static class Unavailable
{
    public const string Message = "The collection services are not part of this build.";
}

internal sealed class UnavailableCollectionService : ICollectionService
{
    private static Task<T> Fail<T>() => Task.FromException<T>(new CollectionServiceException(Unavailable.Message));

    public Task<IReadOnlyList<CollectionRow>> ListCollectionsAsync(CancellationToken ct) => Fail<IReadOnlyList<CollectionRow>>();
    public Task<CollectionRow?> GetCollectionAsync(string collectionId, CancellationToken ct) => Fail<CollectionRow?>();
    public Task<CollectionDetail> GetDetailAsync(string collectionId, int memberPage, CancellationToken ct) => Fail<CollectionDetail>();
    public Task<WqlResult> ValidateWqlAsync(string expression, CancellationToken ct) => Fail<WqlResult>();
    public Task<WqlResult> PreviewWqlAsync(string expression, int maxRows, CancellationToken ct) => Fail<WqlResult>();
    public Task<IReadOnlyList<SavedQuery>> ListSavedQueriesAsync(CancellationToken ct) => Fail<IReadOnlyList<SavedQuery>>();
    public Task<CollectionChange> PrepareAsync(CollectionChange request, CancellationToken ct) => Fail<CollectionChange>();

    public Task<IReadOnlyList<CollectionOutcome>> ApplyAsync(IReadOnlyList<CollectionChange> changes, IProgress<CollectionOutcome> progress, CancellationToken ct) =>
        Fail<IReadOnlyList<CollectionOutcome>>();
}

internal sealed class UnavailableQueryTemplateStore : IQueryTemplateStore
{
    public IReadOnlyList<QueryTemplate> All() => [];
    public string? Message => Unavailable.Message;
}

internal sealed class UnavailableCollectionReportWriter : ICollectionReportWriter
{
    public string WriteCsv(IReadOnlyList<CollectionRow> rows) => throw new CollectionServiceException(Unavailable.Message);
    public string WriteHtml(IReadOnlyList<CollectionRow> rows) => throw new CollectionServiceException(Unavailable.Message);
}
