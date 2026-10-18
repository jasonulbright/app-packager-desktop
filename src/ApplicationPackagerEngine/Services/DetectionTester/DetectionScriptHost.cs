using System.Diagnostics;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>Runs one detection script body and reports what it printed and how it ended.</summary>
public interface IDetectionScriptHost
{
    /// <summary>
    /// The timeout returns a run with <see cref="DetectionScriptRun.TimedOut"/> set; only
    /// <paramref name="ct"/> throws <see cref="OperationCanceledException"/>. Both end the
    /// process tree first.
    /// </summary>
    Task<DetectionScriptRun> RunAsync(string scriptText, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Runs the body in a child Windows PowerShell 5.1 as the current user. The body goes to a
/// temporary .ps1 file, never to the command line. Standard input is redirected and closed
/// at once, so Read-Host and console reads get end of input instead of waiting for a
/// console nobody can see.
/// </summary>
public sealed class WindowsPowerShellDetectionScriptHost(int outputLimit = WindowsPowerShellDetectionScriptHost.DefaultOutputLimit)
    : IDetectionScriptHost
{
    /// <summary>The number of characters kept of each output stream.</summary>
    public const int DefaultOutputLimit = 64 * 1024;

    public const string RunContext = "Windows PowerShell 5.1, current user, not SYSTEM";

    public async Task<DetectionScriptRun> RunAsync(string scriptText, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scriptText);
        ct.ThrowIfCancellationRequested();

        var scriptPath = Path.Combine(Path.GetTempPath(), $"ap-detection-{Guid.NewGuid():N}.ps1");
        // A BOM makes Windows PowerShell 5.1 read the file as UTF-8; without it the file is read in the ANSI code page.
        await File.WriteAllTextAsync(scriptPath, scriptText, new UTF8Encoding(true), CancellationToken.None);
        try
        {
            using var process = new Process { StartInfo = WindowsPowerShellHost.BuildStartInfo(scriptPath, redirectStandardInput: true) };
            process.Start();
            try { process.StandardInput.Close(); } catch (IOException) { }

            // Neither reader takes a token: an abandoned read leaves the child blocked on a
            // full pipe, and ending the process is what ends both streams.
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, outputLimit);
            var stderrTask = ReadBoundedAsync(process.StandardError, outputLimit);

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                BoundedProcessRunner.Terminate(process);
                var (partialOut, outCut) = await SafeAsync(stdoutTask).ConfigureAwait(false);
                var (partialErr, errCut) = await SafeAsync(stderrTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return new DetectionScriptRun(null, partialOut, partialErr, TimedOut: true, outCut || errCut);
            }

            var (stdout, stdoutCut) = await stdoutTask.ConfigureAwait(false);
            var (stderr, stderrCut) = await stderrTask.ConfigureAwait(false);
            return new DetectionScriptRun(process.ExitCode, stdout, stderr, TimedOut: false, stdoutCut || stderrCut);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Keeps the first <paramref name="limit"/> characters and drains the rest unread.</summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var kept = new StringBuilder();
        var truncated = false;
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            var room = limit - kept.Length;
            if (room >= read) { kept.Append(buffer, 0, read); continue; }
            if (room > 0) kept.Append(buffer, 0, room);
            truncated = true;
        }
        return (kept.ToString().TrimEnd('\r', '\n'), truncated);
    }

    private static async Task<(string, bool)> SafeAsync(Task<(string, bool)> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception) { return ("", false); }
    }
}
