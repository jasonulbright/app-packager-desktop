using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
/// .ps1 file in the per-user run folder, never to the command line, and the file is
/// removed after the run. Standard input is redirected and closed
/// at once, so Read-Host and console reads get end of input instead of waiting for a
/// console nobody can see.
/// </summary>
public sealed partial class WindowsPowerShellDetectionScriptHost(
    int outputLimit = WindowsPowerShellDetectionScriptHost.DefaultOutputLimit,
    string? runFolder = null)
    : IDetectionScriptHost
{
    /// <summary>Puts the started process in a kill-on-close job; null when that is not possible.</summary>
    internal Func<Process, IDisposable?> AssignJob { get; init; } = KillOnCloseJob.TryAssign;

    /// <summary>Called with the script file's path once the file is written, before the process starts.</summary>
    internal Action<string>? ScriptFileCreated { get; init; }

    /// <summary>Called after the script process has exited, before the output drain.</summary>
    internal Action? DrainStarted { get; init; }

    public const string ScriptFilePattern = "ap-detection-*.ps1";

    /// <summary>A script file older than this is left over from a run that never cleaned up.</summary>
    public static readonly TimeSpan StaleScriptAge = TimeSpan.FromDays(1);

    private int _staleSwept;

    public static string DefaultRunFolder() => Path.Combine(DetectionTesterFolders.Root(), "run");

    /// <summary>The number of characters kept of each output stream.</summary>
    public const int DefaultOutputLimit = 64 * 1024;

    public const string RunContext = "Windows PowerShell 5.1, current user, not SYSTEM";

    /// <summary>How long the output reads may continue after the script process has exited.</summary>
    public static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);

    private static readonly Encoding OutputEncoding = new UTF8Encoding(false);

    /// <summary>
    /// Kept on line 1 with the body so error line numbers match the body. A redirected
    /// Windows PowerShell writes in the OEM code page unless the console encoding is set.
    /// </summary>
    private const string Utf8OutputPrefix = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); ";

    public async Task<DetectionScriptRun> RunAsync(string scriptText, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scriptText);
        ct.ThrowIfCancellationRequested();

        var folder = PrepareRunFolder();
        var scriptPath = Path.Combine(folder, $"ap-detection-{Guid.NewGuid():N}.ps1");
        try
        {
            // A BOM makes Windows PowerShell 5.1 read the file as UTF-8; without it the file is read in the ANSI code page.
            await File.WriteAllTextAsync(scriptPath, WithUtf8Output(scriptText), new UTF8Encoding(true), CancellationToken.None)
                .ConfigureAwait(false);
            ScriptFileCreated?.Invoke(scriptPath);

            var startInfo = WindowsPowerShellHost.BuildStartInfo(scriptPath, redirectStandardInput: true);
            startInfo.StandardOutputEncoding = OutputEncoding;
            startInfo.StandardErrorEncoding = OutputEncoding;
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            // Without the job, a descendant that outlives the script cannot be ended by Run's
            // end or by Cancel, so the script is not allowed to continue.
            using var job = AssignJob(process);
            if (job is null)
            {
                BoundedProcessRunner.Terminate(process);
                throw new DetectionScriptRefusedException(DetectionScriptRefusedException.NoContainment);
            }
            try { process.StandardInput.Close(); } catch (IOException) { }

            // Neither reader takes a token: an abandoned read leaves the child blocked on a
            // full pipe, and ending the process is what ends both streams.
            var stdoutTask = StartBoundedRead(process.StandardOutput, outputLimit);
            var stderrTask = StartBoundedRead(process.StandardError, outputLimit);

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                BoundedProcessRunner.Terminate(process);
                job.Dispose();
                ct.ThrowIfCancellationRequested();
                var (partialOut, outCut) = await DrainAsync(stdoutTask, ct).ConfigureAwait(false);
                var (partialErr, errCut) = await DrainAsync(stderrTask, ct).ConfigureAwait(false);
                return new DetectionScriptRun(null, partialOut, partialErr, TimedOut: true, outCut || errCut);
            }

            // A descendant that inherited the output handles keeps both pipes open after
            // the script exits; ending the job closes them, and the drain bound covers a
            // descendant outside the job.
            job.Dispose();
            DrainStarted?.Invoke();
            try
            {
                var (stdout, stdoutCut) = await DrainAsync(stdoutTask, ct).ConfigureAwait(false);
                var (stderr, stderrCut) = await DrainAsync(stderrTask, ct).ConfigureAwait(false);
                return new DetectionScriptRun(process.ExitCode, stdout, stderr, TimedOut: false, stdoutCut || stderrCut);
            }
            catch (OperationCanceledException)
            {
                BoundedProcessRunner.Terminate(process);
                throw;
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"^\s*(using\s|param\s*\(|\[CmdletBinding)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingOnlyStatement();

    /// <summary>
    /// The body with the UTF-8 output statement on its first line. A body with a using
    /// statement or a param block is left as it is: neither may follow another statement,
    /// so that body keeps the OEM output encoding.
    /// </summary>
    internal static string WithUtf8Output(string scriptText) =>
        LeadingOnlyStatement().IsMatch(scriptText) ? scriptText : Utf8OutputPrefix + scriptText;

    /// <summary>
    /// Keeps the first <paramref name="limit"/> characters and drains the rest unread.
    /// The kept text never ends in the first half of a surrogate pair.
    /// </summary>
    private sealed class BoundedText
    {
        private readonly StringBuilder _kept = new();
        public bool Truncated;

        public void Append(char[] buffer, int count, int limit)
        {
            lock (_kept)
            {
                if (Truncated) return;
                var room = limit - _kept.Length;
                if (room >= count) { _kept.Append(buffer, 0, count); return; }
                if (room > 0) _kept.Append(buffer, 0, room);
                if (_kept.Length > 0 && char.IsHighSurrogate(_kept[^1])) _kept.Length--;
                Truncated = true;
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_kept) return (_kept.ToString().TrimEnd('\r', '\n'), Truncated);
        }
    }

    private sealed record BoundedRead(BoundedText Text, Task Completion);

    private static BoundedRead StartBoundedRead(StreamReader reader, int limit)
    {
        var text = new BoundedText();
        async Task ReadAsync()
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
                text.Append(buffer, read, limit);
        }
        return new BoundedRead(text, ReadAsync());
    }

    /// <summary>
    /// The text read so far once the stream ends or <see cref="OutputDrainTimeout"/> passes;
    /// <paramref name="ct"/> ends the wait with <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<(string, bool)> DrainAsync(BoundedRead read, CancellationToken ct)
    {
        try { await read.Completion.WaitAsync(OutputDrainTimeout, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return read.Text.Snapshot();
    }

    /// <summary>
    /// The folder for this run's script file, created when absent. A folder that is a
    /// junction or symbolic link is refused: the script would be written, and run, wherever
    /// the link points.
    /// </summary>
    private string PrepareRunFolder()
    {
        var folder = runFolder ?? DefaultRunFolder();
        var container = DetectionTesterFolders.ContainerOf(folder, runFolder is null);
        if (DetectionTesterFolders.HasLink(folder, container))
            throw new DetectionScriptRefusedException(DetectionScriptRefusedException.RunFolderIsLink);
        Directory.CreateDirectory(folder);
        if (Interlocked.Exchange(ref _staleSwept, 1) == 0) RemoveStaleScripts(folder);
        return folder;
    }

    private static void RemoveStaleScripts(string folder)
    {
        var cutoff = DateTime.UtcNow - StaleScriptAge;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles(ScriptFilePattern,
                         new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                try
                {
                    if (file.LastWriteTimeUtc < cutoff) file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
