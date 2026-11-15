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
    public static string Name(AppDefinition def) => Name(def.Application, def.Architecture);

    public static string Name(string? application, string? architecture)
    {
        var legacy = $"{(application ?? "").Replace(" ", "")}-{architecture ?? ""}";
        return IsPlain(legacy) ? legacy : Sanitize(legacy);
    }

    /// <summary>The stage folder of the definition under <paramref name="root"/>; throws when the result is outside the root.</summary>
    public static string Under(string root, AppDefinition def) => WorkbenchPaths.CombineUnder(root, Name(def));

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
