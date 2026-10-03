namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>Removes the profile-owned files an earlier build of the same profile left in a reused stage root.</summary>
public static class StaleStageArtifacts
{
    /// <summary>The CustomAssets categories a profile owns; generated wrappers and payload are never pruned.</summary>
    public static readonly IReadOnlyList<string> PrunableCategories =
        ["SourceFile", "InstallBefore", "InstallAfter", "UninstallBefore", "UninstallAfter"];

    /// <summary>
    /// A stage folder is reused for the same vendor version, so a source file or hook
    /// removed from the profile would otherwise stay on disk, be hashed into
    /// FileHashes and ship. Every sealed record of this application and profile for
    /// this stage root, other than <paramref name="buildId"/>, names what it placed.
    /// A source file is removed only while its bytes still equal the sealed hash: it
    /// lands at an operator-chosen path that a later vendor release can also use.
    /// Returns the relative paths removed.
    /// </summary>
    public static IReadOnlyList<string> Prune(string stageRoot, string dataRoot, string applicationId, string profileId,
        string? buildId, Action<string>? onLog = null)
    {
        var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
        var removed = new List<string>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in BuildRecords.List(dataRoot, applicationId, profileId))
        {
            if (string.Equals(WorkbenchJson.Get(record, "BuildId").Text, buildId, StringComparison.OrdinalIgnoreCase)) continue;
            var recordRoot = WorkbenchJson.Get(record, "StageRoot").Text;
            if (string.IsNullOrWhiteSpace(recordRoot)) continue;
            if (!string.Equals(Path.GetFullPath(recordRoot).TrimEnd('\\'), rootFull, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var asset in WorkbenchProfileValidator.Items(WorkbenchJson.Get(record, "CustomAssets").Value))
            {
                if (asset is null) continue;
                var category = WorkbenchJson.Get(asset, "Category").Text ?? "";
                if (!PrunableCategories.Contains(category, StringComparer.OrdinalIgnoreCase)) continue;
                var relative = WorkbenchJson.Get(asset, "RelativePath").Text;
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var full = Path.GetFullPath(Path.Combine(rootFull, relative));
                if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (ResolvesThroughReparsePoint(rootFull, full))
                {
                    onLog?.Invoke($"Stage prune kept             : {relative} (the path resolves through a reparse point)");
                    continue;
                }
                if (File.Exists(full))
                {
                    if (string.Equals(category, "SourceFile", StringComparison.OrdinalIgnoreCase))
                    {
                        var sealedHash = WorkbenchJson.Get(asset, "Sha256").Text;
                        if (string.IsNullOrWhiteSpace(sealedHash)
                            || !string.Equals(WorkbenchJson.FileSha256(full), sealedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            onLog?.Invoke($"Stage prune kept             : {relative} (content differs from the sealed source file)");
                            continue;
                        }
                    }
                    File.Delete(full);
                    removed.Add(relative);
                }
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent) && parent.TrimEnd('\\').Length > rootFull.Length) folders.Add(parent);
            }
        }

        foreach (var folder in folders.OrderByDescending(f => f.Length))
        {
            if (!Directory.Exists(folder) || ResolvesThroughReparsePoint(rootFull, folder)) continue;
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                try { Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return removed;
    }

    /// <summary>
    /// True when the path, any folder between it and the stage root, or the root itself is
    /// a reparse point. A text prefix check alone lets a folder replaced by a junction send
    /// the hash and the delete to a file outside the stage root.
    /// </summary>
    public static bool ResolvesThroughReparsePoint(string stageRoot, string path)
    {
        var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current) && current.TrimEnd('\\').Length > rootFull.Length)
        {
            if (WorkbenchJson.IsReparsePoint(current)) return true;
            current = Path.GetDirectoryName(current);
        }
        return WorkbenchJson.IsReparsePoint(rootFull);
    }
}
