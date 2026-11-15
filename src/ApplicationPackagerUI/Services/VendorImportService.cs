using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerUI.Services;

/// <summary>What the import window needs from the import: the preview, the import, and the stored feeds. A test supplies a fake.</summary>
public interface IVendorImportService
{
    Task<ImportPreview> PreviewAsync(Uri address, string? pinnedSha256, CancellationToken ct);
    FeedSource Import(ImportPreview preview, ImportConfirmation confirmation);
    IReadOnlyList<StoredFeed> Feeds();
    void RemoveFeed(string feedId);
}

/// <summary>The anonymous import of a vendor feed over the engine: the shipped and the user Ids are read from their folders at each preview.</summary>
public sealed class VendorImportService(ImportedDefinitionStore store, Func<ISet<string>> shippedIds, Func<ISet<string>> userIds, FeedFetcher? fetcher = null,
    Func<ISet<string>>? stageFolders = null)
    : IVendorImportService
{
    private readonly VendorFeedImporter _importer = new(fetcher ?? new FeedFetcher(), store, shippedIds, userIds, stageFolders: stageFolders);

    public static VendorImportService CreateDefault() => new(
        ImportedDefinitionStore.Default(),
        () => IdsIn(IconPackService.BundledDefinitionsDirectory()),
        () => IdsIn(AppDataPaths.UserDefinitionsRoot),
        stageFolders: () => FoldersIn(IconPackService.BundledDefinitionsDirectory(), AppDataPaths.UserDefinitionsRoot));

    public Task<ImportPreview> PreviewAsync(Uri address, string? pinnedSha256, CancellationToken ct) =>
        _importer.PreviewAsync(address, pinnedSha256, ct);

    public FeedSource Import(ImportPreview preview, ImportConfirmation confirmation) => _importer.Import(preview, confirmation);

    public IReadOnlyList<StoredFeed> Feeds() => store.Feeds();

    public void RemoveFeed(string feedId) => store.RemoveFeed(feedId);

    private static ISet<string> FoldersIn(params string[] roots)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    var d = System.Text.Json.JsonSerializer.Deserialize<ApplicationPackagerEngine.Models.AppDefinition>(File.ReadAllText(file), options);
                    if (d is not null && !string.IsNullOrWhiteSpace(d.Application)) names.Add(StageFolder.Name(d));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { }
            }
        return names;
    }

    private static ISet<string> IdsIn(string folder)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder)) return ids;
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var property in document.RootElement.EnumerateObject())
                    if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        ids.Add(property.Value.GetString()!);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { }
        }
        return ids;
    }
}
