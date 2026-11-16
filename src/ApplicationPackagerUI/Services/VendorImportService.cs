using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.VendorImport;

namespace ApplicationPackagerUI.Services;

/// <summary>What the import window needs from the import: the preview, the import, and the stored feeds. A test supplies a fake.</summary>
public interface IVendorImportService
{
    Task<ImportPreview> PreviewAsync(Uri address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher = null);
    FeedSource Import(ImportPreview preview, ImportConfirmation confirmation);
    IReadOnlyList<StoredFeed> Feeds();
    void RemoveFeed(string feedId);
    IReadOnlyList<FeedCredentialEntry> Credentials();
    FeedCredentialEntry AddCredential(string id, Uri address, FeedAuthMethod method, string? headerName, string? userName, string secret,
        string? tokenAddress = null, string? clientId = null, string? scope = null, string? thumbprint = null);
    void RotateCredential(string id, string secret);
    void RemoveCredential(string id);
    Task<string> TestAsync(Uri address, CancellationToken ct);
    Task<UpdatePreview> PreviewUpdateAsync(string feedId, Uri? address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher = null);
    FeedSource ApplyUpdate(UpdatePreview update, UpdateConfirmation confirmation);
}

/// <summary>The anonymous import of a vendor feed over the engine: the shipped and the user Ids are read from their folders at each preview.</summary>
public sealed class VendorImportService(ImportedDefinitionStore store, Func<ISet<string>> shippedIds, Func<ISet<string>> userIds, FeedFetcher? fetcher = null,
    Func<IReadOnlyDictionary<string, string>>? stageFolders = null, FeedSignatureVerifier? signatureVerifier = null, FeedCredentialStore? credentials = null)
    : IVendorImportService
{
    private readonly FeedFetcher _fetcher = fetcher ?? new FeedFetcher();
    private readonly VendorFeedImporter _importer = new(fetcher ?? new FeedFetcher(), store, shippedIds, userIds, stageFolders: stageFolders, signatureVerifier: signatureVerifier,
        credentialFor: credentials is null ? null : credentials.For);

    public IReadOnlyList<FeedCredentialEntry> Credentials() => credentials?.Entries() ?? [];

    public FeedCredentialEntry AddCredential(string id, Uri address, FeedAuthMethod method, string? headerName, string? userName, string secret,
        string? tokenAddress = null, string? clientId = null, string? scope = null, string? thumbprint = null) =>
        (credentials ?? throw new InvalidOperationException("No credential store.")).Add(id, address, method, headerName, userName, secret, tokenAddress, clientId, scope, thumbprint);

    public void RotateCredential(string id, string secret) => (credentials ?? throw new InvalidOperationException("No credential store.")).Rotate(id, secret);

    public void RemoveCredential(string id) => credentials?.Remove(id);

    /// <summary>One request to the address with its credential: the status, the final host, the size and the trust state. No header and no secret is shown.</summary>
    public async Task<string> TestAsync(Uri address, CancellationToken ct)
    {
        var document = await _fetcher.FetchAsync(address, ct, credentials?.For(address)).ConfigureAwait(false);
        return $"HTTP 200 from {document.FinalAddress.Host}, {document.Size:N0} bytes, trust state: {FeedTrust.Label(FeedTrust.NotVerified)}.";
    }
    private VendorFeedUpdater? _updater;

    private VendorFeedUpdater Updater => _updater ??= new VendorFeedUpdater(_importer, store);

    public Task<UpdatePreview> PreviewUpdateAsync(string feedId, Uri? address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher = null) =>
        Updater.PreviewAsync(feedId, address, pinnedSha256, ct, pinnedPublisher);

    public FeedSource ApplyUpdate(UpdatePreview update, UpdateConfirmation confirmation) => Updater.Apply(update, confirmation);

    public static VendorImportService CreateDefault() => new(
        ImportedDefinitionStore.Default(),
        () => IdsIn(IconPackService.BundledDefinitionsDirectory()),
        () => IdsIn(AppDataPaths.UserDefinitionsRoot),
        stageFolders: () => FoldersIn(IconPackService.BundledDefinitionsDirectory(), AppDataPaths.UserDefinitionsRoot),
        credentials: FeedCredentialStore.Default());

    public Task<ImportPreview> PreviewAsync(Uri address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher = null) =>
        _importer.PreviewAsync(address, pinnedSha256, ct, pinnedPublisher);

    public FeedSource Import(ImportPreview preview, ImportConfirmation confirmation) => _importer.Import(preview, confirmation);

    public IReadOnlyList<StoredFeed> Feeds() => store.Feeds();

    public void RemoveFeed(string feedId) => store.RemoveFeed(feedId);

    private static IReadOnlyDictionary<string, string> FoldersIn(params string[] roots)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.GetFiles(root, "*.json"))
            {
                try
                {
                    var d = System.Text.Json.JsonSerializer.Deserialize<ApplicationPackagerEngine.Models.AppDefinition>(File.ReadAllText(file), options);
                    if (d is not null && !string.IsNullOrWhiteSpace(d.Application)) names[d.Id] = StageFolder.Name(d);
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
