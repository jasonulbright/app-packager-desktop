using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The owner file in each analysis session folder: the process id and start time of the desktop
/// instance that created the session. A process id alone is reused by Windows; the pair is not.
/// </summary>
public static partial class InstallerAnalysisSessions
{
    public const string OwnerFileName = "session-owner.txt";

    [GeneratedRegex(@"^\d{8}-\d{6}-[0-9a-f]{8}$")]
    private static partial Regex SessionName();

    private static readonly Lazy<long> CurrentStartTicks = new(() =>
    {
        using var self = Process.GetCurrentProcess();
        return self.StartTime.ToUniversalTime().Ticks;
    });

    public static void WriteOwner(string session, int processId, long startTicks) =>
        File.WriteAllText(Path.Combine(session, OwnerFileName),
            processId.ToString(CultureInfo.InvariantCulture) + " " + startTicks.ToString(CultureInfo.InvariantCulture));

    /// <summary>Marks the session as owned by this process.</summary>
    public static void MarkOwner(string session) => WriteOwner(session, Environment.ProcessId, CurrentStartTicks.Value);

    /// <summary>The owner written in the session, or null when the file is missing or unreadable.</summary>
    public static (int ProcessId, long StartTicks)? ReadOwner(string session)
    {
        string text;
        try
        {
            var file = new FileInfo(Path.Combine(session, OwnerFileName));
            if (!file.Exists || file.Length > 64 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            text = File.ReadAllText(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return null;
        return (pid, ticks);
    }

    /// <summary>
    /// True while the process with this id and start time runs. A process that cannot be inspected
    /// counts as running, so a session is never removed on a guess.
    /// </summary>
    public static bool IsAlive(int processId, long startTicks)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        using (process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks == startTicks; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return true; }
            catch (NotSupportedException) { return true; }
        }
    }

    /// <summary>
    /// Removes the sessions whose owner process has ended, and the sessions without an owner file
    /// that are older than <paramref name="unmarkedAge"/>. A session whose owner still runs is kept
    /// at any age: it can belong to another instance of the desktop.
    /// </summary>
    public static void CleanAbandoned(IAnalysisSandbox sandbox, Func<int, long, bool> isAlive, TimeSpan unmarkedAge)
    {
        var root = sandbox.Root;
        if (string.IsNullOrEmpty(root))
        {
            sandbox.CleanOldSessions(unmarkedAge);
            return;
        }
        if (!Directory.Exists(root) || AppDataPaths.IsReparsePointDirectory(root)) return;
        var cutoff = DateTime.UtcNow - unmarkedAge;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (!SessionName().IsMatch(Path.GetFileName(folder)) || AppDataPaths.IsReparsePointDirectory(folder)) continue;
            try
            {
                if (ReadOwner(folder) is { } owner)
                {
                    if (isAlive(owner.ProcessId, owner.StartTicks)) continue;
                }
                else if (Directory.GetCreationTimeUtc(folder) > cutoff || Directory.GetLastWriteTimeUtc(folder) > cutoff) continue;
                sandbox.Release(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallerAnalysisException) { }
        }
    }
}

/// <summary>Stops the host at exit with a bounded wait, off the dispatcher so a continuation cannot wait for the blocked UI thread.</summary>
public static class HostShutdown
{
    /// <summary>True when the stop finished within the timeout; the stop token is cancelled at the timeout.</summary>
    public static bool StopAndWait(Func<CancellationToken, Task> stop, TimeSpan timeout)
    {
        // Not disposed: a stop that outlives the wait still reads the token.
        var cancel = new CancellationTokenSource(timeout);
        var running = Task.Run(() => stop(cancel.Token));
        try { return running.Wait(timeout); }
        catch (AggregateException) { return false; }
        finally { cancel.Cancel(); }
    }
}
