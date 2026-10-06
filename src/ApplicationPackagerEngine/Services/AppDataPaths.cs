using System.Runtime.InteropServices;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Per-user data folders. APP_PACKAGER_DATA_ROOT supplies a parent for the AppPackager
/// and SRLPackr folders; each Windows user's data is isolated beneath Users\&lt;SID&gt;.
/// </summary>
public static class AppDataPaths
{
    public const string DataRootVariable = "APP_PACKAGER_DATA_ROOT";

    private static string? Override
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DataRootVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
        }
    }

    /// <summary>The per-user folder that holds settings.json, condition templates and the default Workbench root.</summary>
    public static string Root => ValidateUserDataDirectory(Override is { } root
        ? ScopeRootToCurrentUser(root)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager"));

    /// <summary>The default staging folder. It inherits the per-user access list of <see cref="Root"/>.</summary>
    public static string DefaultStagingRoot => Path.Combine(Root, "Staging");

    /// <summary>The staging default of earlier versions; folders under it inherit the drive root's access list.</summary>
    public const string LegacyStagingRoot = @"C:\temp\ap";

    public static bool IsLegacyStagingRoot(string? root) =>
        string.Equals((root ?? "").Trim().TrimEnd('\\', '/'), LegacyStagingRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>User-authored definitions and icon overrides inside the per-user Workbench store.</summary>
    public static string UserDefinitionsRoot => Workbench.WorkbenchPaths.UserDefinitionsRoot;

    /// <summary>The per-user folder that holds session logs, the icon cache, history and downloaded tools.</summary>
    public static string LegacyRoot => ValidateUserDataDirectory(Override is { } root
        ? Path.Combine(ScopeRootToCurrentUser(root), "SRLPackr")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRLPackr"));

    /// <summary>
    /// Treats an explicit shared data location as a container, placing this Windows
    /// user's files under Users\&lt;SID&gt;. Reapplying it to an already-scoped path is safe.
    /// </summary>
    public static string ScopeRootToCurrentUser(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("The current Windows user SID could not be resolved for per-user data isolation.");

        var parent = Path.GetDirectoryName(fullRoot);
        if (string.Equals(Path.GetFileName(fullRoot), sid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(parent), "Users", StringComparison.OrdinalIgnoreCase))
            return EnsureOutsideProgramFiles(fullRoot);

        return EnsureOutsideProgramFiles(Path.Combine(fullRoot, "Users", sid));
    }

    public static string EnsureOutsideProgramFiles(string path)
    {
        RejectDeviceNamespacePath(path);
        var fullPath = Path.GetFullPath(path);
        RejectDeviceNamespacePath(fullPath);
        foreach (var specialFolder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var protectedRoot = Environment.GetFolderPath(specialFolder);
            if (string.IsNullOrWhiteSpace(protectedRoot)) continue;
            var shortProtectedRoot = GetShortPath(protectedRoot)
                ?? throw new UnauthorizedAccessException("APD could not verify the Program Files path boundary.");
            if (IsWithin(fullPath, protectedRoot)
                || IsWithin(fullPath, shortProtectedRoot)
                || ResolvesThroughReparsePointInto(fullPath, protectedRoot))
                throw new UnauthorizedAccessException("APD user data cannot be stored under Program Files.");
        }
        return fullPath;
    }

    private static void RejectDeviceNamespacePath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("APD user data cannot use a Windows device namespace path.");
    }

    private static string? GetShortPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        return length is 0 or >= 32768 ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);

    private static bool ResolvesThroughReparsePointInto(string path, string protectedRoot)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                var resolved = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null && IsWithin(resolved.FullName, protectedRoot))
                    return true;
            }

            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static string ValidateUserDataDirectory(string path)
    {
        var fullPath = EnsureOutsideProgramFiles(path);
        EnsureDirectoryIsNotReparsePoint(fullPath);
        return fullPath;
    }

    public static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The user data folder cannot be a junction or symbolic link: {path}");
    }

    public static bool IsWithin(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
