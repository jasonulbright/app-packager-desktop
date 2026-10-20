using System.Globalization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>The Installer Analysis folders under the per-user data root.</summary>
public static class InstallerAnalysisFolders
{
    public const string FolderName = "InstallerAnalysis";

    public static string Root() => Path.Combine(AppDataPaths.Root, FolderName);

    public static string Temp() => Path.Combine(Root(), "Temp");

    public static string Reports() => Path.Combine(Root(), "Reports");
}

/// <summary>
/// Per-session extraction folders under &lt;data root&gt;\InstallerAnalysis\Temp. A session
/// folder holds only what the analysis extracted; nothing in it is ever started. A
/// linked root or session folder is refused, and a release deletes the folder without
/// following any link inside it.
/// </summary>
public sealed class AnalysisSandbox : IAnalysisSandbox
{
    private static readonly Regex SessionName = new(@"^\d{8}-\d{6}-[0-9a-f]{8}$", RegexOptions.Compiled);

    private readonly string? _root;
    private readonly string _container;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <param name="root">The folder that holds the sessions; the default is the data root's InstallerAnalysis\Temp.</param>
    public AnalysisSandbox(string? root = null, TimeProvider? time = null)
    {
        _root = root is null ? null : Path.GetFullPath(root);
        _container = root is null ? "" : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_root!)) ?? _root!;
        _time = time ?? TimeProvider.System;
    }

    public string Root => _root ?? InstallerAnalysisFolders.Temp();

    public string NewSession()
    {
        var root = Root;
        RefuseLinks(root);
        Directory.CreateDirectory(root);
        RefuseLinks(root);
        var name = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var session = Path.Combine(root, name);
        Directory.CreateDirectory(session);
        lock (_gate) _sessions.Add(session);
        return session;
    }

    public void Release(string session)
    {
        if (string.IsNullOrWhiteSpace(session)) return;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session));
        if (!IsSessionFolder(full)) throw new InstallerAnalysisException("The folder is not an analysis session folder.");
        DeleteTree(full);
        lock (_gate) _sessions.Remove(full);
    }

    public void ReleaseAll()
    {
        string[] sessions;
        lock (_gate) sessions = [.. _sessions];
        foreach (var session in sessions)
        {
            try { Release(session); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void CleanOldSessions(TimeSpan olderThan)
    {
        var root = Root;
        if (!Directory.Exists(root) || AppDataPaths.IsReparsePointDirectory(root)) return;
        var cutoff = _time.GetUtcNow().UtcDateTime - olderThan;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!SessionName.IsMatch(Path.GetFileName(folder))) continue;
            lock (_gate) if (_sessions.Contains(folder)) continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) > cutoff && Directory.GetCreationTimeUtc(folder) > cutoff) continue;
                DeleteTree(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>True when <paramref name="path"/> is the sandbox root or lies inside it.</summary>
    public bool Contains(string path) => AppDataPaths.IsWithin(path, Root);

    private bool IsSessionFolder(string full) =>
        string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)), StringComparison.OrdinalIgnoreCase)
        && SessionName.IsMatch(Path.GetFileName(full));

    private void RefuseLinks(string root)
    {
        var container = _root is null ? AppDataPaths.Root : _container;
        if (DataFolderLinks.HasLink(root, container))
            throw new InstallerAnalysisException("The analysis temp folder is a junction or symbolic link; it was not used.");
    }

    /// <summary>
    /// Deletes a folder tree. A junction or symbolic link inside it is removed as a link,
    /// never followed, so nothing outside the session is touched.
    /// </summary>
    internal static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var info = new DirectoryInfo(folder);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete();
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) directory.Delete();
                else DeleteTree(directory.FullName);
            }
            else
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        info.Delete();
    }
}

/// <summary>
/// The path rules every extraction applies to an archive entry name before anything is
/// written: relative only, no parent segments, no device names, no trailing dots or
/// spaces, no alternate data streams, bounded length.
/// </summary>
public static class SandboxPath
{
    public const int MaxSegmentLength = 200;
    public const int MaxRelativeLength = 400;

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"
    };

    /// <summary>
    /// The entry name as a relative Windows path, or null with the reason when the name is
    /// refused. Forward slashes become backslashes; empty and "." segments are dropped.
    /// </summary>
    public static string? Normalize(string entryName, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(entryName)) { reason = "The entry has no name."; return null; }
        if (entryName.IndexOf('\0') >= 0) { reason = "The entry name contains a null character."; return null; }
        var name = entryName.Replace('/', '\\');
        if (name.StartsWith(@"\\", StringComparison.Ordinal) || name.StartsWith('\\') || (name.Length >= 2 && name[1] == ':'))
        {
            reason = "The entry name is an absolute path.";
            return null;
        }
        var segments = new List<string>();
        foreach (var raw in name.Split('\\'))
        {
            if (raw.Length == 0 || raw == ".") continue;
            if (raw == "..") { reason = "The entry name leaves the extraction folder."; return null; }
            if (raw.EndsWith('.') || raw.EndsWith(' ')) { reason = "An entry name segment ends with a dot or a space."; return null; }
            if (raw.Length > MaxSegmentLength) { reason = "An entry name segment is too long."; return null; }
            if (raw.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || raw.Any(c => c < 32))
            {
                reason = "The entry name contains a character Windows does not allow.";
                return null;
            }
            var stem = raw.Split('.')[0].TrimEnd(' ');
            if (DeviceNames.Contains(stem)) { reason = "The entry name is a device name."; return null; }
            segments.Add(raw);
        }
        if (segments.Count == 0) { reason = "The entry has no name."; return null; }
        var relative = string.Join('\\', segments);
        if (relative.Length > MaxRelativeLength) { reason = "The entry name is too long."; return null; }
        return relative;
    }

    /// <summary>
    /// The full target path of an entry under <paramref name="folder"/>, or null with the
    /// reason. The result is checked to stay inside the folder after full-path resolution,
    /// and no existing folder on the way may be a junction or symbolic link.
    /// </summary>
    public static string? Resolve(string folder, string entryName, out string reason)
    {
        var relative = Normalize(entryName, out reason);
        if (relative is null) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "The entry name leaves the extraction folder.";
            return null;
        }
        var current = Path.GetDirectoryName(target);
        while (current is not null && current.Length > root.Length)
        {
            if (AppDataPaths.IsReparsePointDirectory(current) || File.Exists(current))
            {
                reason = "A folder on the entry path is a link or a file.";
                return null;
            }
            current = Path.GetDirectoryName(current);
        }
        if (AppDataPaths.IsReparsePointDirectory(root)) { reason = "The extraction folder is a junction or symbolic link."; return null; }
        return target;
    }
}
