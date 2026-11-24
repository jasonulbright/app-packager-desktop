using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.VendorImport;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The folder of a stage under the download root: <c>&lt;Application without spaces&gt;-&lt;Architecture&gt;</c>. The name comes from a
/// definition, and a definition can come from a vendor feed, so a name that would leave the download root (a dot segment, a rooted path, a
/// drive colon, a device name) is turned into a plain name and the result is checked to stay under the root.
/// A name that is safe keeps the folder that it always had (so "GN Audio A/S" still stages under GNAudioA\S-x64 and existing stages are found).
/// </summary>
public static class StageFolder
{
    public static string Name(AppDefinition def) => Name(def.FolderApplication(), def.Architecture);

    public static string Name(string? application, string? architecture)
    {
        var legacy = $"{(application ?? "").Replace(" ", "")}-{architecture ?? ""}";
        return IsPlain(legacy) ? legacy : Sanitize(legacy);
    }

    /// <summary>The stage folder of the definition under <paramref name="root"/>; throws when the result is outside the root.</summary>
    public static string Under(string root, AppDefinition def) => WorkbenchPaths.CombineUnder(root, Name(def));

    /// <summary>
    /// The refusal for a stage whose manifest belongs to another application, or null. Two definitions can share one folder name (an
    /// imported definition with the Application of a shipped one), so a stage found in the folder must carry the identity of the
    /// definition that packages it. A manifest without an identity (an older stage) is accepted.
    /// </summary>
    public static string? IdentityRefusal(AppDefinition def, string? manifestApplicationId)
    {
        if (string.IsNullOrWhiteSpace(manifestApplicationId)) return null;
        var expected = StagingService.CatalogApplicationId(def);
        if (expected.Length == 0) return null;
        // A stage carries catalog:Id, custom:Id or imported:Id (a run with a snapshot uses the origin of the row). The Id must be the
        // Id of this definition, and an imported definition never takes over the stage of a catalog or custom one, or the other way round.
        var colon = manifestApplicationId.IndexOf(':');
        var prefix = colon < 0 ? "" : manifestApplicationId[..colon];
        var name = colon < 0 ? manifestApplicationId : manifestApplicationId[(colon + 1)..];
        var sameName = string.Equals(name, def.Id, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, Path.GetFileNameWithoutExtension(def.Id), StringComparison.OrdinalIgnoreCase);
        var importedStage = string.Equals(prefix, Models.Workbench.ApplicationIds.ImportedPrefix, StringComparison.OrdinalIgnoreCase);
        var importedDefinition = def.SourceOrigin == Models.Workbench.ApplicationOrigin.Imported;
        if (sameName && importedStage == importedDefinition) return null;
        return $"The stage in this folder belongs to {manifestApplicationId}, not to {expected}. Run Stage again for {def.Application}.";
    }

    /// <summary>The application part of an installer file name: no space, bracket or character that a file name cannot hold.</summary>
    public static string FileStem(string? application)
    {
        var text = (application ?? "").Replace(" ", "").Replace("(", "").Replace(")", "");
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var stem = new string(chars).Trim('.', ' ');
        return stem.Length == 0 ? "_" : stem;
    }

    // Plain: every segment (a "/" or a "\" has always nested) is a name that Windows stores as it is, and the path is not rooted.
    private static bool IsPlain(string name)
    {
        if (name.Length == 0 || Path.IsPathRooted(name)) return false;
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in name.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment.Trim('.').Length == 0) return false;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            if (segment.IndexOfAny(invalid) >= 0) return false;
            if (FeedValidator.IsDeviceName(segment)) return false;
        }
        return true;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var text = new string(chars).Trim('.', ' ');
        while (text.Contains("..", StringComparison.Ordinal)) text = text.Replace("..", "_", StringComparison.Ordinal);
        if (text.Length == 0) return "_";
        return FeedValidator.IsDeviceName(text) ? "_" + text : text;
    }
}
