namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>One imported definition that a loader may use: its Id, its file, the feed it came from, and whether it replaces a shipped one.</summary>
public sealed record ResolvedImportedDefinition(string Id, string Path, FeedSource Feed, bool ReplacesShipped);

/// <summary>
/// Decides which stored imported definitions the three loaders (catalog loader, Workbench discovery, command-line build) use, so that they
/// cannot disagree. A user definition with the same Id wins over an imported one. An imported definition with the Id of a shipped definition
/// replaces it only when the administrator confirmed that Id at import (it is in the replaced list of the source record). The earlier feed
/// wins when two feeds hold the same Id. The origin comes from the folder and the source record, never from a property of the definition.
/// </summary>
public static class ImportedDefinitionResolver
{
    public static IReadOnlyList<ResolvedImportedDefinition> Resolve(
        string? importedRoot, IReadOnlySet<string> shippedIds, IReadOnlySet<string> userIds)
    {
        if (string.IsNullOrWhiteSpace(importedRoot) || !Directory.Exists(importedRoot)) return [];
        var accepted = new List<ResolvedImportedDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in new ImportedDefinitionStore(importedRoot).Feeds())
        {
            var replaced = new HashSet<string>(feed.Source.ReplacedShippedIds ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var file in feed.DefinitionFiles)
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                // The record and the file name are read from disk and are not trusted: the Id inside the file must be the one that the
                // record lists, so that the three loaders agree and a file cannot take the Id of another application.
                if (!string.Equals(IdInside(file), id, StringComparison.OrdinalIgnoreCase)) continue;
                if (userIds.Contains(id)) continue;
                var replacesShipped = shippedIds.Contains(id);
                if (replacesShipped && !replaced.Contains(id)) continue;
                if (!seen.Add(id)) continue;
                accepted.Add(new ResolvedImportedDefinition(id, file, feed.Source, replacesShipped));
            }
        }
        return accepted;
    }

    private static string? IdInside(string file)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return property.Value.GetString();
            return null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
