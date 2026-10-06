using System.Runtime.CompilerServices;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Points the test process at a temp folder only the current user and administrators can
/// write. Tests stage under Path.GetTempPath(), and StagingRootGuard refuses a stage root
/// that inherits a write entry for another account from the user's own temp folder.
/// </summary>
internal static class PrivateTempFolder
{
    [ModuleInitializer]
    internal static void Use()
    {
        string folder;
        // A throw here would stop the assembly from loading; the guard tests then report the cause.
        try { folder = StagingRootGuard.Prepare(Path.Combine(Path.GetTempPath(), "apkg-tests-" + Environment.ProcessId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        Environment.SetEnvironmentVariable("TMP", folder);
        Environment.SetEnvironmentVariable("TEMP", folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}
