namespace ApplicationPackagerEngine.Services;

/// <summary>The Detection Tester folders under the per-user data root.</summary>
internal static class DetectionTesterFolders
{
    public const string FolderName = "DetectionTester";

    public static string Root() => Path.Combine(AppDataPaths.Root, FolderName);

    /// <summary>
    /// True when <paramref name="folder"/>, or an existing directory between it and
    /// <paramref name="container"/> (not included), is a junction or symbolic link. A
    /// directory created or written through such a link lands wherever the link points.
    /// </summary>
    public static bool HasLink(string folder, string container)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container));
        while (!string.IsNullOrEmpty(current) && !string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
        {
            if (AppDataPaths.IsReparsePointDirectory(current)) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    /// <summary>The folder a caller supplied is checked by itself; a default folder up to the data root.</summary>
    public static string ContainerOf(string folder, bool isDefault) =>
        isDefault ? AppDataPaths.Root : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))) ?? folder;
}
