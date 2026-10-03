using System.Globalization;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// One bundled file of a profile. Destination is relative to the content root; a file
/// only reaches the endpoint when the install script copies it there.
/// </summary>
public sealed record WorkbenchSourceFile(
    string Asset, string Provenance, string Destination, string Sha256, long Size, bool Linked)
{
    public string SizeText => string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", Size);
    public string ShortHash => Sha256.Length >= 16 ? Sha256[..16] : Sha256;

    public JsonObject ToJson()
    {
        var entry = WorkbenchJson.NewObject();
        entry["Asset"] = Asset;
        entry["Destination"] = Destination;
        entry["Sha256"] = Sha256;
        entry["Size"] = Size;
        entry["Provenance"] = Provenance;
        entry["Linked"] = Linked;
        return entry;
    }

    public static WorkbenchSourceFile FromJson(JsonNode? entry) => new(
        WorkbenchJson.Get(entry, "Asset").Text ?? "",
        WorkbenchJson.Get(entry, "Provenance").Text ?? "",
        WorkbenchJson.Get(entry, "Destination").Text ?? "",
        WorkbenchJson.Get(entry, "Sha256").Text ?? "",
        long.TryParse(WorkbenchJson.Get(entry, "Size").Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
        WorkbenchJson.IsTruthy(WorkbenchJson.Get(entry, "Linked").Value));
}

/// <summary>Builds source file entries from picked files and folders.</summary>
public static class WorkbenchSourceFiles
{
    /// <summary>
    /// One entry for a file, or one per file under a folder with the destination kept
    /// relative under the folder's own name. A reparse point is refused, because its
    /// target can change after the build snapshots it. Under a folder, reparse-point
    /// files and folders are skipped and never entered, so a junction cannot pull in
    /// files from outside the picked folder.
    /// </summary>
    public static IReadOnlyList<WorkbenchSourceFile> FromPath(string path)
    {
        if (WorkbenchJson.IsReparsePoint(path))
            throw new InvalidOperationException("Reparse points are not accepted as source files: " + path);

        if (File.Exists(path)) return [Describe(Path.GetFullPath(path), Path.GetFileName(path))];
        if (!Directory.Exists(path)) return [];

        var root = Path.GetFullPath(path).TrimEnd('\\', '/');
        var folderName = Path.GetFileName(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MatchType = MatchType.Win32
        };
        return Directory.GetFiles(root, "*", options)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                AssertNoReparseAncestor(root, f);
                return Describe(f, Path.Combine(folderName, Path.GetRelativePath(root, f)));
            })
            .ToList();
    }

    /// <summary>
    /// Throws when the file or any folder between it and the picked folder is a reparse
    /// point; a folder swapped for a junction after enumeration is caught here.
    /// </summary>
    public static void AssertNoReparseAncestor(string pickedFolder, string file)
    {
        var rootFull = Path.GetFullPath(pickedFolder).TrimEnd('\\');
        var current = Path.GetFullPath(file);
        while (!string.IsNullOrEmpty(current) && current.TrimEnd('\\').Length > rootFull.Length)
        {
            if (WorkbenchJson.IsReparsePoint(current))
                throw new InvalidOperationException($"Source file '{file}' resolves through a reparse point at '{current}'.");
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>The same entry pointing at another file: new provenance, hash and size, no asset id yet.</summary>
    public static WorkbenchSourceFile Replace(WorkbenchSourceFile entry, string path)
    {
        var replacement = Describe(Path.GetFullPath(path), entry.Destination);
        return replacement with { Linked = entry.Linked };
    }

    public static IReadOnlyList<WorkbenchSourceFile> Read(JsonNode? sourceFiles) =>
        WorkbenchProfileValidator.Items(sourceFiles).Where(e => e is not null).Select(WorkbenchSourceFile.FromJson).ToList();

    public static JsonArray ToJson(IEnumerable<WorkbenchSourceFile> entries) =>
        new(entries.Select(e => (JsonNode?)e.ToJson()).ToArray());

    private static WorkbenchSourceFile Describe(string file, string destination)
    {
        string hash;
        try { hash = WorkbenchJson.FileSha256(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hash = ""; }
        return new WorkbenchSourceFile("", file, destination, hash, new FileInfo(file).Length, Linked: false);
    }
}
