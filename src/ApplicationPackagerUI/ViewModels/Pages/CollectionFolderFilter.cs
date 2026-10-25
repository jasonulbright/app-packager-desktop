namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The folder filter of the collection pages. A folder entry matches the collections of that
/// folder and of every folder below it.
/// </summary>
public static class CollectionFolderFilter
{
    public const string SubFoldersSuffix = " (and sub-folders)";

    /// <summary>
    /// Replaces every entry after the first ("All folders") with each folder path and all its
    /// ancestors, and keeps <paramref name="current"/> when no collection has that folder now.
    /// </summary>
    public static void Fill(IList<Choice<string>> choices, IEnumerable<string> folderPaths, string current)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in folderPaths.Select(Normalize).Where(p => p.Length > 0))
        {
            paths.Add(path);
            for (var cut = path.LastIndexOf('\\'); cut > 0; cut = path.LastIndexOf('\\', cut - 1))
                paths.Add(path[..cut]);
        }
        while (choices.Count > 1) choices.RemoveAt(1);
        foreach (var path in paths)
            choices.Add(new Choice<string>(path, paths.Any(p => IsBelow(p, path)) ? path + SubFoldersSuffix : path));
        if (current.Length > 0 && !paths.Contains(current)) choices.Add(new Choice<string>(current, current));
    }

    /// <summary>True when <paramref name="filter"/> is empty, or <paramref name="folderPath"/> is that folder or a folder below it.</summary>
    public static bool Matches(string filter, string folderPath)
    {
        var wanted = Normalize(filter);
        if (wanted.Length == 0) return true;
        var path = Normalize(folderPath);
        return string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase) || IsBelow(path, wanted);
    }

    private static bool IsBelow(string path, string folder) =>
        path.Length > folder.Length + 1 && path[folder.Length] == '\\' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? path) => (path ?? "").Trim().Trim('\\');
}
