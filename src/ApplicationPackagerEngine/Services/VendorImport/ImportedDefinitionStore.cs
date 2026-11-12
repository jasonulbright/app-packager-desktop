using System.Text.Json;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>The trust state of a document and the exact wording that every surface uses for it.</summary>
public static class FeedTrust
{
    public const string PinnedDigest = "Pinned digest";
    public const string SignedByPinnedPublisher = "Signed by pinned publisher";
    public const string NotVerified = "Not verified";

    /// <summary>The wording of the state without a pin. It matches the payload check of the stage ("Payload SHA256 NOT verified").</summary>
    public const string NotVerifiedWording = "Definition document NOT verified";

    /// <summary>The label that the preview, the source record, the log and the grid use for a trust state.</summary>
    public static string Label(string state) => state == NotVerified ? NotVerifiedWording : state;
}

/// <summary>
/// The source record that is written beside the stored definitions of a feed. It holds no secret and no query. It has no Id and is never
/// read as a definition.
/// </summary>
public sealed record FeedSource(
    string FinalAddress, string Vendor, string FeedId, DateTimeOffset FetchedAt, string Sha256, int Size, string TrustState,
    string? PinnedDigest, string? PinnedPublisher, string? Signer, IReadOnlyList<string> Ids, IReadOnlyList<string> ReplacedShippedIds,
    IReadOnlyList<string> UnblockedDigests);

/// <summary>A feed on disk: its record and the files of its definitions.</summary>
public sealed record StoredFeed(string Folder, FeedSource Source, IReadOnlyList<string> DefinitionFiles);

/// <summary>
/// The third definition folder: <c>&lt;root&gt;\&lt;FeedId&gt;\&lt;Id&gt;.json</c> and <c>source.json</c>. A feed is built in a staging folder and
/// swapped in, with <c>source.json</c> written last, so a failure leaves the earlier state and no folder whose record disagrees with its
/// files. The swap is two renames, so <see cref="Recover"/> restores the old copy when a run stopped between them.
/// </summary>
public sealed class ImportedDefinitionStore(string root)
{
    public const string SourceFileName = "source.json";
    private const string StagingPrefix = ".staging-";
    private const string OldInfix = ".old-";

    private static readonly JsonSerializerOptions SourceJson = new() { WriteIndented = true };

    public string Root { get; } = root;

    public static ImportedDefinitionStore Default() => new(Path.Combine(AppDataPaths.Root, "ImportedDefinitions"));

    private string FolderOf(string feedId)
    {
        if (!FeedValidator.IsValidFeedId(feedId)) throw new ArgumentException($"The FeedId '{feedId}' is not allowed.", nameof(feedId));
        return Path.Combine(Root, feedId);
    }

    /// <summary>Restores the old copy of a feed whose swap stopped between the two renames, and removes what a stopped run left.</summary>
    public void Recover()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var directory in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal))
            {
                Directory.Delete(directory, recursive: true);
                continue;
            }
            var at = name.IndexOf(OldInfix, StringComparison.Ordinal);
            if (at <= 0) continue;
            var feed = Path.Combine(Root, name[..at]);
            if (Directory.Exists(feed)) Directory.Delete(directory, recursive: true);
            else Directory.Move(directory, feed);
        }
    }

    /// <summary>Writes the definitions of a feed and its source record. A failure keeps the earlier state of the feed.</summary>
    public void Write(FeedSource source, IReadOnlyList<ValidatedDefinition> definitions)
    {
        var folder = FolderOf(source.FeedId);
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string? old = null;
        try
        {
            foreach (var definition in definitions)
            {
                if (definition.Blocked) throw new InvalidOperationException($"The definition '{definition.Id}' is blocked and is not written.");
                var file = Path.GetFullPath(Path.Combine(staging, definition.Id + ".json"));
                // The file name is built from the validated Id only; it must stay inside the staging folder.
                if (!file.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The Id '{definition.Id}' does not give a file inside the folder of the feed.");
                var temp = file + ".tmp";
                File.WriteAllText(temp, definition.Json);
                File.Move(temp, file);
            }
            var record = source with { Ids = definitions.Select(d => d.Id).ToList() };
            var sourceTemp = Path.Combine(staging, SourceFileName + ".tmp");
            File.WriteAllText(sourceTemp, JsonSerializer.Serialize(record, SourceJson));
            File.Move(sourceTemp, Path.Combine(staging, SourceFileName));

            if (Directory.Exists(folder))
            {
                old = folder + OldInfix + Guid.NewGuid().ToString("N");
                Directory.Move(folder, old);
            }
            Directory.Move(staging, folder);
            // The new copy is in place. A lock on the old copy (a scanner, an indexer) must not report a successful import as a failure;
            // Recover removes what is left at the next start.
            if (old is not null)
            {
                try { Directory.Delete(old, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { old = null; }
            }
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            // The swap stopped after the first rename: the old copy goes back.
            if (old is not null && Directory.Exists(old) && !Directory.Exists(folder)) Directory.Move(old, folder);
            throw;
        }
    }

    public FeedSource? ReadSource(string feedId)
    {
        var path = Path.Combine(FolderOf(feedId), SourceFileName);
        return File.Exists(path) ? JsonSerializer.Deserialize<FeedSource>(File.ReadAllText(path)) : null;
    }

    /// <summary>The stored feeds, the earlier import first: the loaders read the folders in this order, not by name.</summary>
    public IReadOnlyList<StoredFeed> Feeds()
    {
        if (!Directory.Exists(Root)) return [];
        var feeds = new List<StoredFeed>();
        foreach (var directory in Directory.GetDirectories(Root))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.Contains(OldInfix, StringComparison.Ordinal) || !FeedValidator.IsValidFeedId(name)) continue;
            var path = Path.Combine(directory, SourceFileName);
            if (!File.Exists(path)) continue;
            FeedSource? source;
            try { source = JsonSerializer.Deserialize<FeedSource>(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { continue; }
            if (source is null) continue;
            // source.json is not a definition; only the files that the record lists are definitions, and a listed Id must be a valid Id
            // (the record is read from disk and is not trusted to name a path).
            source = source with { Ids = (source.Ids ?? []).Where(FeedValidator.IsValidDefinitionId).ToList() };
            var files = source.Ids.Select(id => Path.Combine(directory, id + ".json")).Where(File.Exists).ToList();
            feeds.Add(new StoredFeed(directory, source, files));
        }
        return feeds.OrderBy(f => f.Source.FetchedAt).ThenBy(f => f.Source.FeedId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Deletes the folder of a feed after the caller confirmed it. Stages that exist stay.</summary>
    public void RemoveFeed(string feedId)
    {
        var folder = FolderOf(feedId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
