using System.Collections.Concurrent;
using System.Diagnostics;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The child processes a run started itself (curl.exe, packager scripts, PowerShell), by
/// process id. A cancelled run ends its own children through its token; this list lets the
/// app end the ones still running when the window closes, and never touches a process the
/// run did not start.
/// </summary>
public static class RunChildProcesses
{
    private static readonly ConcurrentDictionary<int, Process> Live = new();

    /// <summary>Process ids of the children that are still being tracked.</summary>
    public static IReadOnlyCollection<int> Ids => [.. Live.Keys];

    /// <summary>Tracks a process this run just started; it is dropped when it exits. Returns the same process.</summary>
    public static Process Track(Process process)
    {
        try
        {
            var id = process.Id;
            Live[id] = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Live.TryRemove(id, out _);
            if (process.HasExited) Live.TryRemove(id, out _);
        }
        catch (Exception) { }
        return process;
    }

    /// <summary>Ends every tracked child and its own children, by process id. Safe to call twice.</summary>
    public static void KillAll()
    {
        foreach (var id in Live.Keys)
        {
            if (!Live.TryRemove(id, out var process)) continue;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Waits for a child to exit for at most <paramref name="timeout"/>. A cancelled token or
    /// an overrun ends the child and its tree at once. Returns true when the child exited;
    /// false when it was killed for overrunning; throws when the caller's token was cancelled.
    /// </summary>
    public static async Task<bool> WaitOrKillAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            BoundedProcessRunner.Terminate(process);
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
