using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.VendorImport;

public enum DefinitionChange { Added, Removed, Changed, Unchanged }

/// <summary>What an update does to one definition, and the changes that the administrator must look at before confirming.</summary>
public sealed record DefinitionDiff(
    string Id, DefinitionChange Change, IReadOnlyList<string> ChangedProperties,
    bool HostChanged, bool HashSourceChanged, bool RequirementChanged, bool CodeChanged, bool UnblockLost, bool UnblockKept)
{
    /// <summary>The changes of the list in "Update of a feed", rule 2: a host, a hash source, a requirement, and new or changed code.</summary>
    public bool NeedsAttention => HostChanged || HashSourceChanged || RequirementChanged || CodeChanged || UnblockLost;
}

/// <summary>The new document against the stored feed. Nothing is written until <see cref="VendorFeedUpdater.Apply"/> runs.</summary>
public sealed record UpdatePreview(ImportPreview Preview, FeedSource Old, IReadOnlyList<DefinitionDiff> Diffs, bool OlderThanStored, string? PinNote = null, string? FetchedHost = null)
{
    public IEnumerable<DefinitionDiff> Removed => Diffs.Where(d => d.Change == DefinitionChange.Removed);
}

/// <summary>What the administrator confirmed: the trust state, an older document, the definitions to remove, the replaced shipped Ids, the new unblocks.</summary>
public sealed record UpdateConfirmation(
    bool ConfirmedNotVerified, bool ConfirmedOlder, IReadOnlySet<string> RemoveIds, IReadOnlySet<string> ReplaceShippedIds, IReadOnlySet<string> UnblockIds);

/// <summary>
/// The manual update of a stored feed (step 4): fetch with the same limits and trust rules, show a diff against the stored definitions, and
/// write only after the confirmation. An unblock of a script or a command survives only while the digest of its text and settings is
/// unchanged. A definition that the update removes stays until the administrator confirms its removal.
/// </summary>
public sealed class VendorFeedUpdater(VendorFeedImporter importer, ImportedDefinitionStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <param name="address">Where to fetch; null takes the stored address (it has no query: the administrator can enter another one).</param>
    public async Task<UpdatePreview> PreviewAsync(string feedId, Uri? address, string? pinnedSha256, CancellationToken ct, string? pinnedPublisher = null)
    {
        var stored = store.Feeds().FirstOrDefault(f => f.Source.FeedId == feedId) ?? throw new InvalidOperationException($"The feed '{feedId}' is not stored.");
        var target = address ?? new Uri(stored.Source.FinalAddress);
        // A pin of the record stays in force: an update never falls back to a weaker state. The administrator pins a new digest or publisher by entering it.
        var digestPin = string.IsNullOrWhiteSpace(pinnedSha256) ? stored.Source.PinnedDigest : pinnedSha256;
        var publisherPin = string.IsNullOrWhiteSpace(pinnedPublisher) ? stored.Source.PinnedPublisher : pinnedPublisher;
        var preview = await importer.PreviewCoreAsync(target, digestPin, ct, publisherPin, feedId).ConfigureAwait(false);
        var pinNote = PinNote(stored.Source, preview);

        var oldById = new Dictionary<string, (string Json, ValidatedDefinition Valid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in stored.DefinitionFiles)
        {
            var json = File.ReadAllText(file);
            try
            {
                var valid = FeedValidator.ValidateStored(json);
                if (JsonNode.Parse(json) is not JsonObject) throw new JsonException("The file is not an object.");
                oldById[Path.GetFileNameWithoutExtension(file)] = (json, valid);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"The stored definition {Path.GetFileName(file)} cannot be read ({ex.Message}). Remove the feed and import it again.");
            }
        }

        var diffs = new List<DefinitionDiff>();
        var digests = new HashSet<string>(stored.Source.UnblockedDigests ?? [], StringComparer.OrdinalIgnoreCase);
        var newIds = new HashSet<string>(preview.Feed.Definitions.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var d in preview.Feed.Definitions)
        {
            var newDigest = d.CarriesCode ? DefinitionUnblock.Digest(d.Definition) : null;
            var kept = newDigest is not null && digests.Contains(d.Id + ":" + newDigest);
            if (!oldById.TryGetValue(d.Id, out var old))
            {
                diffs.Add(new DefinitionDiff(d.Id, DefinitionChange.Added, [], false, false, false, d.CarriesCode, false, false));
                continue;
            }
            var changed = ChangedProperties(old.Json, d.Json);
            var oldHadUnblock = digests.Any(x => x.StartsWith(d.Id + ":", StringComparison.OrdinalIgnoreCase));
            diffs.Add(new DefinitionDiff(
                d.Id, changed.Count == 0 ? DefinitionChange.Unchanged : DefinitionChange.Changed, changed,
                !old.Valid.Hosts.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(d.Hosts.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                HashSource(old.Valid.Definition) != HashSource(d.Definition),
                old.Valid.Definition.RequirePayloadHash != d.Definition.RequirePayloadHash || old.Valid.Definition.RequireValidSignature != d.Definition.RequireValidSignature,
                CodeDigest(old.Valid) != CodeDigest(d),
                UnblockLost: oldHadUnblock && d.CarriesCode && !kept,
                UnblockKept: kept));
        }
        foreach (var (id, old) in oldById.Where(p => !newIds.Contains(p.Key)))
            diffs.Add(new DefinitionDiff(id, DefinitionChange.Removed, [], false, false, false, old.Valid.CarriesCode, false, false));

        var older = stored.Source.Updated is { } was && preview.Feed.Updated is { } now && now < was;
        return new UpdatePreview(preview, stored.Source, diffs, older, pinNote, target.Host);
    }

    private static string? PinNote(FeedSource old, ImportPreview preview)
    {
        var notes = new List<string>();
        if (!string.IsNullOrEmpty(old.PinnedDigest) && !string.Equals(old.PinnedDigest, preview.PinnedDigest, StringComparison.OrdinalIgnoreCase))
            notes.Add($"The pinned digest changes from {old.PinnedDigest} to {preview.PinnedDigest ?? "none"}.");
        if (!string.IsNullOrEmpty(old.PinnedPublisher) && !string.Equals(old.PinnedPublisher, preview.PinnedPublisher, StringComparison.OrdinalIgnoreCase))
            notes.Add($"The pinned publisher changes from {old.PinnedPublisher} to {preview.PinnedPublisher ?? "none"}.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private static string HashSource(Models.AppDefinition d) =>
        $"{d.PayloadSha256}|{d.PayloadSha256Url}|{d.VersionScraper?.PayloadHashQuery}";

    private static string CodeDigest(ValidatedDefinition d) =>
        d.CarriesCode ? string.Join(";", d.CodeProperties) + "|" + DefinitionUnblock.Digest(d.Definition) : "";

    private static List<string> ChangedProperties(string oldJson, string newJson)
    {
        var a = JsonNode.Parse(oldJson)!.AsObject();
        var b = JsonNode.Parse(newJson)!.AsObject();
        var names = a.Select(p => p.Key).Union(b.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        return names.Where(n => a[n]?.ToJsonString() != b[n]?.ToJsonString()).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The definitions of the new document that an update does not apply, with the reason: blocked code (the update view has no unblock; remove the
    /// feed and import it again to unblock new code), or a conflict. A shipped Id that the feed replaced is replaced again, as the administrator confirmed at import.
    /// </summary>
    public static IReadOnlyList<string> NotApplied(UpdatePreview update)
    {
        var replaced = new HashSet<string>(update.Old.ReplacedShippedIds ?? [], StringComparer.OrdinalIgnoreCase);
        var digests = update.Old.UnblockedDigests ?? [];
        var reasons = new List<string>();
        foreach (var c in update.Preview.Candidates)
        {
            var id = c.Definition.Id;
            var stillUnblocked = c.CanUnblock && digests.Contains(id + ":" + DefinitionUnblock.Digest(c.Definition.Definition), StringComparer.OrdinalIgnoreCase);
            if (c.Definition.Blocked && !stillUnblocked) reasons.Add($"{id}: blocked (it carries code that is not unblocked); the stored copy stays");
            else if (c.Conflict == ConflictKind.Shipped && !replaced.Contains(id)) reasons.Add($"{id}: the Id of a shipped definition");
            else if (c.Conflict is ConflictKind.User or ConflictKind.AnotherFeed or ConflictKind.StageFolder) reasons.Add($"{id}: {c.Conflict} conflict");
        }
        return reasons;
    }

    /// <summary>Writes the update. The earlier state stays when anything is missing or fails (the store swaps the folder).</summary>
    /// <exception cref="InvalidOperationException">The preview is refused, or a confirmation is missing.</exception>
    public FeedSource Apply(UpdatePreview update, UpdateConfirmation confirmation)
    {
        var preview = update.Preview;
        if (preview.Refused) throw new InvalidOperationException(preview.Refusal ?? "The document is refused: " + preview.Feed.Findings.First(f => f.Severity == FindingSeverity.Blocking).Message);
        if (preview.TrustState == FeedTrust.NotVerified && !confirmation.ConfirmedNotVerified)
            throw new InvalidOperationException($"The document is in the state \"{FeedTrust.NotVerifiedWording}\". Confirm that state to update the feed; it is asked again each time.");
        if (update.OlderThanStored && !confirmation.ConfirmedOlder)
            throw new InvalidOperationException("The document is older than the stored one (its Updated is earlier). Confirm that you want the older document.");

        var feedId = update.Old.FeedId;
        var stored = store.Feeds().FirstOrDefault(f => f.Source.FeedId == feedId) ?? throw new InvalidOperationException($"The feed '{feedId}' is not stored.");
        var oldDigests = (stored.Source.UnblockedDigests ?? []).ToList();
        var unblock = confirmation.UnblockIds;
        foreach (var id in unblock)
        {
            var candidate = preview.Candidates.FirstOrDefault(c => string.Equals(c.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
            if (candidate is null || !candidate.CanUnblock)
                throw new InvalidOperationException($"The definition '{id}' cannot be unblocked: only scripts and commands can be unblocked, never a path to a local file or a bootstrapper.");
        }

        bool Unblocked(ImportCandidate c) =>
            c.CanUnblock && (unblock.Contains(c.Definition.Id, StringComparer.OrdinalIgnoreCase)
                             || oldDigests.Contains(c.Definition.Id + ":" + DefinitionUnblock.Digest(c.Definition.Definition), StringComparer.OrdinalIgnoreCase));

        var take = preview.Candidates
            .Where(c => (!c.Definition.Blocked || Unblocked(c))
                        && (c.Conflict == ConflictKind.None || (c.Conflict == ConflictKind.Shipped && confirmation.ReplaceShippedIds.Contains(c.Definition.Id, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in take)
            if (!folders.Add(StageFolder.Name(c.Definition.Definition)))
                throw new InvalidOperationException($"The definition '{c.Definition.Id}' has the same stage folder (Application and Architecture) as another definition of this update.");

        // The checks run again at Apply: the store can have changed since the preview (another import, another update).
        var replacedNow = take.Where(c => c.Conflict == ConflictKind.Shipped).Select(c => c.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elsewhere = importer.TakenElsewhere(feedId);
        foreach (var c in take.Where(c => elsewhere.Contains(c.Definition.Id)))
            throw new InvalidOperationException($"The Id '{c.Definition.Id}' is now used by a user definition or by another feed. Check for the update again.");
        var existingFolders = importer.StageFoldersElsewhere(feedId, replacedNow);
        foreach (var c in take)
            if (existingFolders.Contains(StageFolder.Name(c.Definition.Definition)))
                throw new InvalidOperationException($"The definition '{c.Definition.Id}' has the same stage folder (Application and Architecture) as a definition that exists.");

        // An old definition that the update does not replace stays as it is stored, unless the administrator confirmed its removal.
        var takenIds = new HashSet<string>(take.Select(c => c.Definition.Id), StringComparer.OrdinalIgnoreCase);
        var keep = new List<(string Id, string Json)>();
        foreach (var file in stored.DefinitionFiles)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (takenIds.Contains(id) || confirmation.RemoveIds.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            keep.Add((id, File.ReadAllText(file)));
        }
        var keptIds = new HashSet<string>(keep.Select(k => k.Id), StringComparer.OrdinalIgnoreCase);
        // A kept definition comes from the earlier document and keeps the record of the earlier state: the update does not relabel it with a new trust state.
        if (keep.Count > 0 && !string.Equals(update.Old.TrustState, FeedTrust.Label(preview.TrustState), StringComparison.Ordinal))
            throw new InvalidOperationException($"{keep.Count} stored definition(s) would stay from the earlier document ({update.Old.TrustState}), and the new document is in another state ({FeedTrust.Label(preview.TrustState)}). Confirm their removal, or update in the same trust state.");
        foreach (var (keptId, keptJson) in keep)
        {
            var keptFolder = StageFolder.Name(FeedValidator.ValidateStored(keptJson).Definition);
            if (take.Any(c => StageFolder.Name(c.Definition.Definition).Equals(keptFolder, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A definition of the update has the same stage folder (Application and Architecture) as the stored definition '{keptId}', which stays. Confirm the removal of '{keptId}'.");
        }

        var digests = take.Where(c => c.Definition.Blocked).Select(c => c.Definition.Id + ":" + DefinitionUnblock.Digest(c.Definition.Definition))
            .Concat(oldDigests.Where(d => keptIds.Contains(d[..Math.Max(0, d.IndexOf(':'))]))).ToList();
        var replaced = take.Where(c => c.Conflict == ConflictKind.Shipped).Select(c => c.Definition.Id)
            .Concat((stored.Source.ReplacedShippedIds ?? []).Where(keptIds.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var source = update.Old with
        {
            FinalAddress = preview.Document.SafeAddress, FetchedAt = _clock.GetUtcNow(), Sha256 = preview.Document.Sha256, Size = preview.Document.Size,
            TrustState = FeedTrust.Label(preview.TrustState), PinnedDigest = preview.PinnedDigest, PinnedPublisher = preview.PinnedPublisher, Signer = preview.Signer,
            Ids = [], ReplacedShippedIds = replaced, UnblockedDigests = digests, Updated = preview.Feed.Updated ?? update.Old.Updated
        };
        if (take.Count == 0 && keep.Count == 0) throw new InvalidOperationException("The update would leave the feed with no definition. Remove the feed instead.");
        store.Write(source, take.Select(c => c.Definition).ToList(), keep);
        return store.ReadSource(feedId)!;
    }
}
