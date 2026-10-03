using System.Diagnostics;

namespace ApplicationPackagerEngine.Services;

/// <summary>What one bounded child process produced.</summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Runs a child process with both output streams drained and its lifetime bounded.
///
/// Reading one stream to completion while the other fills its buffer deadlocks, and an
/// unbounded wait on a hung child hangs the caller with it. A run that overruns its own
/// timeout is that run failing; only the caller's own cancellation propagates, so a
/// timeout is never mistaken for the operator stopping a batch.
/// </summary>
public static class BoundedProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        // A redirected standard input is closed at once, so a child that reads it gets end of input instead of waiting.
        if (startInfo.RedirectStandardInput) process.StandardInput.Close();

        // Drained together and never through the caller's token: an abandoned read leaves
        // the child blocked on a full pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            // Observe both readers after terminating, even when the caller cancelled.
            var partialOut = await SafeRead(stdoutTask).ConfigureAwait(false);
            var partialErr = await SafeRead(stderrTask).ConfigureAwait(false);
            // The caller's cancellation is the operator; the timeout is this run's own
            // failure and must not read as an interrupted batch.
            ct.ThrowIfCancellationRequested();
            return new ProcessRunResult(-1, partialOut, partialErr, TimedOut: true);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessRunResult(process.ExitCode, stdout, stderr, TimedOut: false);
    }

    /// <summary>
    /// Ends only this process and whatever it started, then waits for it to go. Disposing
    /// a Process handle does not stop the child, so a caller that merely walks away leaves
    /// it running and still writing.
    /// </summary>
    public static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
        try { process.WaitForExit(10_000); } catch (Exception) { }
    }

    private static async Task<string> SafeRead(Task<string> readTask)
    {
        try { return await readTask.ConfigureAwait(false); }
        catch (Exception) { return ""; }
    }
}
