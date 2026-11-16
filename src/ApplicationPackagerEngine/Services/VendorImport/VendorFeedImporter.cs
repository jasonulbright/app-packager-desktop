namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>StageFolder: another Id, but the same stage folder (Application and Architecture) as a definition that exists or that comes earlier in the document.</summary>
public enum ConflictKind { None, Shipped, User, AnotherFeed, StageFolder }

/// <summary>A definition of the document and what the import would do with it. The default for a conflict is to skip.</summary>
public sealed record ImportCandidate(ValidatedDefinition Definition, ConflictKind Conflict, string? ConflictFeedId)
{
    public bool Importable => !Definition.Blocked && Conflict == ConflictKind.None;

    /// <summary>
    /// True when the only reason for the block is code that can be unblocked: scripts and commands (see <see cref="DefinitionUnblock"/>). A path to a
    /// local file or a bootstrapper stays blocked, and so does any other blocking finding.
    /// </summary>
    public bool CanUnblock => Definition.Blocked && Definition.Findings.Where(f => f.Severity == FindingSeverity.Blocking).All(f => f.Code == "CarriesCode")
        && DefinitionUnblock.CanUnblock(Definition.Definition);
}

/// <summary>Everything that the preview shows. Nothing is written until <see cref="VendorFeedImporter.Import"/> runs.</summary>
public sealed record ImportPreview(
    FeedDocument Document, ValidatedFeed Feed, string TrustState, IReadOnlyList<ImportCandidate> Candidates, string? Refusal,
    string? Signer = null, string? PinnedPublisher = null, string? PinnedDigest = null)
{
    public string TrustLabel => FeedTrust.Label(TrustState);
    public bool Refused => Refusal is not null || Feed.Refused;
}

/// <summary>What the administrator confirmed in the preview.</summary>
public sealed record ImportConfirmation(
    string Vendor, string FeedId, bool ConfirmedNotVerified, IReadOnlySet<string> ReplaceShippedIds, IReadOnlySet<string>? UnblockIds = null);

/// <summary>
/// The anonymous import of a feed (step 1): fetch, validate, check the pin, list the conflicts; and, after the confirmation, store. A pin
/// that does not match stops the import and never falls back to the state "Not verified". The document is data and not trust: a
/// definition that carries code is blocked in every state.
/// </summary>
public sealed class VendorFeedImporter(
    FeedFetcher fetcher, ImportedDefinitionStore store, Func<ISet<string>> shippedIds, Func<ISet<string>> userIds, TimeProvider? clock = null,
    Func<IReadOnlyDictionary<string, string>>? stageFolders = null, FeedSignatureVerifier? signatureVerifier = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly FeedSignatureVerifier _signatureVerifier = signatureVerifier ?? new FeedSignatureVerifier();

    public Task<ImportPreview> PreviewAsync(Uri address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher = null) =>
        PreviewCoreAsync(address, pinnedSha256, ct, pinnedPublisher, updatingFeedId: null);

    /// <param name="updatingFeedId">The feed that an update replaces: its own definitions are no conflict for the new document.</param>
    internal async Task<ImportPreview> PreviewCoreAsync(Uri address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher, string? updatingFeedId)
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

        // A pinned publisher: the detached signature (.p7s at the address with .p7s added, on the same host) must be valid for the exact
        // bytes and made by that publisher. A failure stops the import; it never falls back to a weaker state.
        string? signer = null;
        string? publisherPin = null;
        if (!string.IsNullOrWhiteSpace(pinnedPublisher))
        {
            if (!PublisherPin.TryParse(pinnedPublisher, out var parsed, out var parseError))
            {
                refusal ??= parseError + $" The import is stopped; it does not fall back to \"{FeedTrust.NotVerified}\".";
            }
            else
            {
                publisherPin = parsed.Text;
                try
                {
                    var signature = await fetcher.FetchSignatureAsync(document.FinalAddress, ct).ConfigureAwait(false);
                    var result = _signatureVerifier.Verify(document.Bytes, signature.Bytes, parsed, _clock.GetUtcNow());
                    if (result.Valid) { trust = FeedTrust.SignedByPinnedPublisher; signer = result.SignerText; }
                    else refusal ??= $"The signature of the document is refused: {result.Error} The import is stopped; it does not fall back to \"{FeedTrust.NotVerified}\".";
                }
                catch (FeedFetchException ex)
                {
                    refusal ??= $"The signature file could not be fetched: {ex.Message} The import is stopped; it does not fall back to \"{FeedTrust.NotVerified}\".";
                }
            }
        }

        var shipped = new HashSet<string>(shippedIds(), StringComparer.OrdinalIgnoreCase);
        var user = new HashSet<string>(userIds(), StringComparer.OrdinalIgnoreCase);
        var otherFeeds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stored in store.Feeds().Where(s => s.Source.FeedId != updatingFeedId))
            foreach (var id in stored.Source.Ids) otherFeeds.TryAdd(id, stored.Source.FeedId);

        // Two definitions with one stage folder would share a stage: an imported definition with the Application and Architecture of
        // a shipped one would stage into the folder of the shipped application. The folder names of the shipped and the user definitions
        // come from the caller; those of the stored feeds are read here; and the earlier definition of the document keeps its folder.
        var folderOfId = new Dictionary<string, string>(stageFolders?.Invoke() ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(folderOfId.Values, StringComparer.OrdinalIgnoreCase);
        foreach (var stored in store.Feeds().Where(s => s.Source.FeedId != updatingFeedId))
            foreach (var file in stored.DefinitionFiles)
                if (StageFolderOfFile(file) is { } name) folders.Add(name);

        var candidates = feed.Definitions.Select(d =>
        {
            if (user.Contains(d.Id)) return new ImportCandidate(d, ConflictKind.User, null);
            if (otherFeeds.TryGetValue(d.Id, out var other)) return new ImportCandidate(d, ConflictKind.AnotherFeed, other);
            if (shipped.Contains(d.Id))
            {
                // A replacement takes over the folder of the shipped definition; it must not take the folder of another one.
                if (d.Blocked && !new ImportCandidate(d, ConflictKind.Shipped, null).CanUnblock) return new ImportCandidate(d, ConflictKind.Shipped, null);
                var own = StageFolder.Name(d.Definition);
                var others = folderOfId.Where(p => !string.Equals(p.Key, d.Id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value);
                if (others.Contains(own, StringComparer.OrdinalIgnoreCase)) return new ImportCandidate(d, ConflictKind.StageFolder, null);
                folders.Add(own);
                return new ImportCandidate(d, ConflictKind.Shipped, null);
            }
            if (!d.Blocked && !folders.Add(StageFolder.Name(d.Definition))) return new ImportCandidate(d, ConflictKind.StageFolder, null);
            return new ImportCandidate(d, ConflictKind.None, null);
        }).ToList();
        return new ImportPreview(document, feed, trust, candidates, refusal, signer, publisherPin, string.IsNullOrEmpty(pin) ? null : document.Sha256);
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

        var unblock = confirmation.UnblockIds ?? new HashSet<string>();
        foreach (var id in unblock)
        {
            var candidate = preview.Candidates.FirstOrDefault(c => string.Equals(c.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
            if (candidate is null || !candidate.CanUnblock)
                throw new InvalidOperationException($"The definition '{id}' cannot be unblocked: only scripts and commands can be unblocked, never a path to a local file or a bootstrapper.");
        }
        var take = preview.Candidates
            .Where(c => (!c.Definition.Blocked || (c.CanUnblock && unblock.Contains(c.Definition.Id, StringComparer.OrdinalIgnoreCase))) && (c.Conflict == ConflictKind.None
                || (c.Conflict == ConflictKind.Shipped && confirmation.ReplaceShippedIds.Contains(c.Definition.Id, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        if (take.Count == 0) throw new InvalidOperationException("No definition can be imported: every one is blocked or in conflict.");

        // A definition that was unblocked takes its stage folder now: it must not share one with another definition that is taken or that exists.
        var takenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in take)
            if (!takenFolders.Add(StageFolder.Name(c.Definition.Definition)))
                throw new InvalidOperationException($"The definition '{c.Definition.Id}' has the same stage folder (Application and Architecture) as another definition of this import.");
        var replacedIds = take.Where(c => c.Conflict == ConflictKind.Shipped).Select(c => c.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in stageFolders?.Invoke() ?? new Dictionary<string, string>())
            if (!replacedIds.Contains(pair.Key)) existing.Add(pair.Value);
        foreach (var stored in store.Feeds())
            foreach (var file in stored.DefinitionFiles)
                if (StageFolderOfFile(file) is { } folderName) existing.Add(folderName);
        foreach (var c in take.Where(c => c.Definition.Blocked))
            if (existing.Contains(StageFolder.Name(c.Definition.Definition)))
                throw new InvalidOperationException($"The unblocked definition '{c.Definition.Id}' has the same stage folder (Application and Architecture) as a definition that exists.");

        var replaced = take.Where(c => c.Conflict == ConflictKind.Shipped).Select(c => c.Definition.Id).ToList();
        var source = new FeedSource(
            preview.Document.SafeAddress, confirmation.Vendor.Trim(), confirmation.FeedId, _clock.GetUtcNow(), preview.Document.Sha256,
            preview.Document.Size, FeedTrust.Label(preview.TrustState),
            preview.PinnedDigest, preview.PinnedPublisher, preview.Signer,
            take.Select(c => c.Definition.Id).ToList(), replaced,
            take.Where(c => c.Definition.Blocked).Select(c => c.Definition.Id + ":" + DefinitionUnblock.Digest(c.Definition.Definition)).ToList(), preview.Feed.Updated);
        store.Write(source, take.Select(c => c.Definition).ToList());
        return source;
    }
}
