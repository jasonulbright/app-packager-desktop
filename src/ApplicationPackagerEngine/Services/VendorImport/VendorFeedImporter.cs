namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>StageFolder: another Id, but the same stage folder (Application and Architecture) as a definition that exists or that comes earlier in the document.</summary>
public enum ConflictKind { None, Shipped, User, AnotherFeed, StageFolder }

/// <summary>A definition of the document and what the import would do with it. The default for a conflict is to skip.</summary>
public sealed record ImportCandidate(ValidatedDefinition Definition, ConflictKind Conflict, string? ConflictFeedId)
{
    public bool Importable => !Definition.Blocked && Conflict == ConflictKind.None;
}

/// <summary>Everything that the preview shows. Nothing is written until <see cref="VendorFeedImporter.Import"/> runs.</summary>
public sealed record ImportPreview(
    FeedDocument Document, ValidatedFeed Feed, string TrustState, IReadOnlyList<ImportCandidate> Candidates, string? Refusal)
{
    public string TrustLabel => FeedTrust.Label(TrustState);
    public bool Refused => Refusal is not null || Feed.Refused;
}

/// <summary>What the administrator confirmed in the preview.</summary>
public sealed record ImportConfirmation(
    string Vendor, string FeedId, bool ConfirmedNotVerified, IReadOnlySet<string> ReplaceShippedIds);

/// <summary>
/// The anonymous import of a feed (step 1): fetch, validate, check the pin, list the conflicts; and, after the confirmation, store. A pin
/// that does not match stops the import and never falls back to the state "Not verified". The document is data and not trust: a
/// definition that carries code is blocked in every state.
/// </summary>
public sealed class VendorFeedImporter(
    FeedFetcher fetcher, ImportedDefinitionStore store, Func<ISet<string>> shippedIds, Func<ISet<string>> userIds, TimeProvider? clock = null,
    Func<ISet<string>>? stageFolders = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ImportPreview> PreviewAsync(Uri address, string? pinnedSha256, CancellationToken ct)
    {
        var document = await fetcher.FetchAsync(address, ct).ConfigureAwait(false);
        var feed = FeedValidator.Validate(document.Bytes);

        var pin = pinnedSha256?.Trim();
        string trust;
        string? refusal = null;
        if (!string.IsNullOrEmpty(pin))
        {
            trust = FeedTrust.PinnedDigest;
            if (!string.Equals(pin, document.Sha256, StringComparison.OrdinalIgnoreCase))
                refusal = $"The SHA-256 of the document is {document.Sha256}, and the pinned value is {pin.ToUpperInvariant()}. The import is stopped; it does not fall back to \"{FeedTrust.NotVerified}\".";
        }
        else
        {
            trust = FeedTrust.NotVerified;
        }

        var shipped = new HashSet<string>(shippedIds(), StringComparer.OrdinalIgnoreCase);
        var user = new HashSet<string>(userIds(), StringComparer.OrdinalIgnoreCase);
        var otherFeeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stored in store.Feeds())
            foreach (var id in stored.Source.Ids) otherFeeds.TryAdd(id, stored.Source.FeedId);

        // Two definitions with one stage folder would share a stage: an imported definition with the Application and Architecture of
        // a shipped one would stage into the folder of the shipped application. The folder names of the shipped and the user definitions
        // come from the caller; those of the stored feeds are read here; and the earlier definition of the document keeps its folder.
        var folders = new HashSet<string>(stageFolders?.Invoke() ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var stored in store.Feeds())
            foreach (var file in stored.DefinitionFiles)
                if (StageFolderOfFile(file) is { } name) folders.Add(name);

        var candidates = feed.Definitions.Select(d =>
        {
            if (user.Contains(d.Id)) return new ImportCandidate(d, ConflictKind.User, null);
            if (otherFeeds.TryGetValue(d.Id, out var other)) return new ImportCandidate(d, ConflictKind.AnotherFeed, other);
            if (shipped.Contains(d.Id)) return new ImportCandidate(d, ConflictKind.Shipped, null);
            if (!d.Blocked && !folders.Add(StageFolder.Name(d.Definition))) return new ImportCandidate(d, ConflictKind.StageFolder, null);
            return new ImportCandidate(d, ConflictKind.None, null);
        }).ToList();
        return new ImportPreview(document, feed, trust, candidates, refusal);
    }

    private static string? StageFolderOfFile(string file)
    {
        try
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<Models.AppDefinition>(File.ReadAllText(file),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return d is null ? null : StageFolder.Name(d);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Stores the definitions that can be imported and the ones whose shipped Id the administrator confirmed to replace. A document in the
    /// state "Not verified" needs the confirmation of that state each time.
    /// </summary>
    /// <exception cref="InvalidOperationException">The preview is refused, or a confirmation is missing.</exception>
    public FeedSource Import(ImportPreview preview, ImportConfirmation confirmation)
    {
        if (preview.Refused) throw new InvalidOperationException(preview.Refusal ?? "The document is refused: " + preview.Feed.Findings.First(f => f.Severity == FindingSeverity.Blocking).Message);
        if (preview.TrustState == FeedTrust.NotVerified && !confirmation.ConfirmedNotVerified)
            throw new InvalidOperationException($"The document is in the state \"{FeedTrust.NotVerifiedWording}\". Confirm that state to import it.");
        if (!FeedValidator.IsValidFeedId(confirmation.FeedId))
            throw new InvalidOperationException($"The FeedId '{confirmation.FeedId}' is not 3 to 64 characters of a-z, 0-9 and '-', or it is a Windows device name.");
        if (string.IsNullOrWhiteSpace(confirmation.Vendor) || confirmation.Vendor.Length > 100 || confirmation.Vendor.Any(char.IsControl))
            throw new InvalidOperationException("The vendor name is empty, longer than 100 characters, or holds a control character.");
        // A feed that is stored under this FeedId is never replaced by an import: its definitions would be deleted. Updating a stored
        // feed is a later step (the update with its diff).
        if (store.ReadSource(confirmation.FeedId) is not null)
            throw new InvalidOperationException($"The FeedId '{confirmation.FeedId}' holds a feed already. Choose another FeedId, or update that feed (a later release).");

        var take = preview.Candidates
            .Where(c => !c.Definition.Blocked && (c.Conflict == ConflictKind.None
                || (c.Conflict == ConflictKind.Shipped && confirmation.ReplaceShippedIds.Contains(c.Definition.Id, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        if (take.Count == 0) throw new InvalidOperationException("No definition can be imported: every one is blocked or in conflict.");

        var replaced = take.Where(c => c.Conflict == ConflictKind.Shipped).Select(c => c.Definition.Id).ToList();
        var source = new FeedSource(
            preview.Document.SafeAddress, confirmation.Vendor.Trim(), confirmation.FeedId, _clock.GetUtcNow(), preview.Document.Sha256,
            preview.Document.Size, FeedTrust.Label(preview.TrustState),
            preview.TrustState == FeedTrust.PinnedDigest ? preview.Document.Sha256 : null, null, null,
            take.Select(c => c.Definition.Id).ToList(), replaced, []);
        store.Write(source, take.Select(c => c.Definition).ToList());
        return source;
    }
}
