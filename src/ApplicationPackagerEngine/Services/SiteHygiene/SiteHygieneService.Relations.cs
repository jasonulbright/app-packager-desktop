using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class SiteHygieneService
{
    /// <summary>
    /// One script run with five bulk queries: the direct relations, the dependency flags, the applications
    /// (latest and older revisions) and the deployment types. No application definition is read.
    /// </summary>
    public async Task<RelationInventory> LoadRelationsAsync(CancellationToken ct)
    {
        var parameters = new ConfigMgrParameters { { "Operation", "Read" }, { "Sets", HygieneChecks.RelationSets }, { "MaxRows", MaxRows } };
        HygieneSiteRead read;
        try
        {
            read = await RunAsync(parameters, Connection(), ct, ReadTimeout).ConfigureAwait(false);
        }
        catch (OtherConnectionException)
        {
            throw new HygieneServiceException("The relationships were not read: the settings changed to another site or SMS Provider during the read. Try again.");
        }
        var bad = HygieneChecks.RelationSets.Where(s => !read.IsRead(s)).ToList();
        var failed = bad.Where(s => !read.Tables.TryGetValue(s, out var t) || t.State != HygieneSetStates.Cut).ToList();
        if (failed.Count > 0)
        {
            _log?.Warning(LogArea, $"The relationship read failed for {failed.Count} sets.");
            throw new HygieneServiceException("The application relationships could not be read from ConfigMgr: the read of " + string.Join(", ", failed.Select(Label)) + " failed. Try again; the log has the kind of failure.");
        }
        var inventory = HygieneChecks.Inventory(read);
        if (bad.Count > 0)
        {
            var note = "The list is cut: " + string.Join(", ", bad.Select(s => $"{Label(s)} ({N(read.Tables[s].Rows.Count)} of {N(read.Tables[s].Total)})")) + " were read in part, so edges and health can be missing or wrong.";
            inventory = inventory with { Truncated = true, Note = note };
        }
        _log?.Info(LogArea, $"Relationships: {N(inventory.Edges.Count)} edges, {N(read.Queries)} queries.");
        return inventory;
    }
}

/// <summary>
/// The chains of one relationship kind as a tree, built on demand: the roots are the sources no edge of the
/// kind points to (every source when each one is also a target, as in a pure loop), and the children of a node
/// are its edges. A caller that expands a node already on its path shows it as a loop and stops; that keeps
/// every expansion linear in the edges it shows.
/// </summary>
public static class HygieneRelationTree
{
    public static IReadOnlyList<string> Roots(RelationInventory inventory, string kind)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var edges = inventory.Edges.Where(e => e.Kind == kind).ToList();
        var targets = edges.Where(e => e.ToExists).Select(e => e.ToKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = edges.Select(e => e.FromKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var roots = sources.Where(s => !targets.Contains(s)).ToList();
        if (roots.Count == 0) roots = sources;
        return roots.OrderBy(r => inventory.Apps.TryGetValue(r, out var a) ? a.Name : r, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<RelationEdge> Children(RelationInventory inventory, string kind, string key)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory.Edges.Where(e => e.Kind == kind && string.Equals(e.FromKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The children of every node of one kind, for a caller that expands many nodes.</summary>
    public static ILookup<string, RelationEdge> ChildLookup(RelationInventory inventory, string kind)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return inventory.Edges.Where(e => e.Kind == kind).ToLookup(e => e.FromKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The glyph and text of a node: a check for healthy, a warning for disabled or content-less, a cross for missing or retired.</summary>
    public static (string Glyph, string Text) Health(RelationInventory inventory, string key, bool exists)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (!exists || !inventory.Apps.TryGetValue(key, out var app)) return ("✗", "Missing");
        if (app.IsExpired) return ("✗", "Retired");
        if (!app.IsEnabled) return ("⚠", "Disabled");
        if (!app.HasContent) return ("⚠", "No content");
        return ("✓", RelationHealth.Healthy);
    }
}
