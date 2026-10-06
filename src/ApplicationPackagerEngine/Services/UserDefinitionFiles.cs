using System.Text.Json;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>Creates new user-authored definition files without allowing path escape or overwrite.</summary>
public static class UserDefinitionFiles
{
    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

    public static string GetPath(string definitionsRoot, string applicationName)
    {
        var root = AppDataPaths.EnsureOutsideProgramFiles(definitionsRoot);
        var fileName = SanitizeFileName(applicationName) + ".json";
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var prefix = Path.EndsInDirectorySeparator(trimmedRoot) ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The definition filename resolves outside the user definitions folder.");
        return path;
    }

    public static string SaveNew(string definitionsRoot, string applicationName, AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var root = AppDataPaths.EnsureOutsideProgramFiles(definitionsRoot);
        Directory.CreateDirectory(root);
        if (AppDataPaths.IsWithin(root, AppDataPaths.Root))
            AppDataPaths.EnsureDirectoryIsNotReparsePoint(AppDataPaths.Root);
        AppDataPaths.EnsureDirectoryIsNotReparsePoint(root);

        var target = GetPath(root, applicationName);
        var temp = Path.Combine(root, $".{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(definition, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target);
            return target;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string SanitizeFileName(string applicationName)
    {
        var safe = new string((applicationName ?? "")
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (safe.Length == 0)
            throw new InvalidOperationException("The application name contains no letters or numbers for a definition filename.");
        if (ReservedDeviceNames.Contains(safe)) safe = "app-" + safe;
        return safe;
    }

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(["con", "prn", "aux", "nul"], StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= 9; i++)
        {
            names.Add($"com{i}");
            names.Add($"lpt{i}");
        }
        return names;
    }
}
