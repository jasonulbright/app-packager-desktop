namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>Copies a profile file into a stage root under the containment, collision and reparse-point rules.</summary>
public static class StageFileCopier
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destination"/> under
    /// the stage root and returns the written path. A file already at the destination
    /// is replaced only when its SHA-256 equals <paramref name="expectedSha256"/>:
    /// that is this profile's own copy from an earlier stage of the same version,
    /// which the build-record prune misses when no record was written. Anything
    /// else there is a real collision with generated or third-party content.
    /// </summary>
    public static string Copy(string stageRoot, string sourcePath, string destination,
        string? expectedSha256 = null, bool allowOverwrite = false)
    {
        if (Path.IsPathRooted(destination))
            throw new InvalidOperationException($"Source file destination '{destination}' must be relative to the content root.");
        AssertPlainDestination(destination);
        var rootFull = Path.GetFullPath(stageRoot).TrimEnd('\\');
        var targetFull = Path.GetFullPath(Path.Combine(rootFull, destination));
        if (!targetFull.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Source file destination '{destination}' escapes the content root.");
        if (!allowOverwrite && (File.Exists(targetFull) || Directory.Exists(targetFull)))
        {
            var replaceable = !string.IsNullOrWhiteSpace(expectedSha256) && File.Exists(targetFull)
                && string.Equals(WorkbenchJson.FileSha256(targetFull), expectedSha256, StringComparison.OrdinalIgnoreCase);
            if (!replaceable)
                throw new InvalidOperationException($"Source file destination '{destination}' collides with a file already in the stage root.");
        }
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException($"Source file '{sourcePath}' is missing; the build cannot snapshot it.");
        if (WorkbenchJson.IsReparsePoint(sourcePath))
            throw new InvalidOperationException($"Source file '{sourcePath}' is a reparse point; copy the real file into the profile instead.");
        AssertNoReparsePoint(rootFull, targetFull);

        var folder = Path.GetDirectoryName(targetFull)!;
        Directory.CreateDirectory(folder);
        AssertNoReparsePoint(rootFull, folder);
        File.Copy(sourcePath, targetFull, overwrite: true);
        return targetFull;
    }

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>
    /// Throws for a destination with a colon or a device-name segment. A colon names an
    /// alternate data stream (install.ps1:hidden writes into the generated install.ps1,
    /// outside the collision check and the file hashes) or a drive; a device name
    /// such as NUL or COM1, with or without an extension, opens the device.
    /// </summary>
    public static void AssertPlainDestination(string destination)
    {
        if (destination.Contains(':'))
            throw new InvalidOperationException(
                $"Source file destination '{destination}' contains a colon; a destination is a plain relative path without a drive or stream name.");
        foreach (var segment in destination.Split('\\', '/'))
        {
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (DeviceNames.Contains(stem))
                throw new InvalidOperationException(
                    $"Source file destination '{destination}' uses the device name '{segment}'.");
        }
    }

    /// <summary>
    /// Throws when any existing element between the root and the path, or the root
    /// itself, is a reparse point, so a staged file is never written through a link.
    /// </summary>
    public static void AssertNoReparsePoint(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current) && current.TrimEnd('\\').Length > rootFull.Length)
        {
            if (WorkbenchJson.IsReparsePoint(current))
                throw new InvalidOperationException($"Source file destination '{path}' resolves through a reparse point at '{current}'.");
            current = Path.GetDirectoryName(current);
        }
        if (WorkbenchJson.IsReparsePoint(rootFull))
            throw new InvalidOperationException($"Stage root '{rootFull}' is a reparse point.");
    }
}
