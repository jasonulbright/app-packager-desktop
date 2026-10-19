using System.Diagnostics;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <param name="ExitCode">The exit code; null when the run was ended by its timeout or a cancel.</param>
/// <param name="Output">The first lines the builder wrote to its output and error streams.</param>
public sealed record CatalogBuilderRun(int? ExitCode, bool TimedOut, bool Cancelled, IReadOnlyList<string> Output);

/// <summary>Runs one catalog builder in a child Windows PowerShell.</summary>
public interface ICatalogBuilderRunner
{
    /// <summary>
    /// Runs <paramref name="builder"/> (the embedded bytes of <paramref name="entry"/>'s builder)
    /// with <paramref name="arguments"/> as separate arguments, calls <paramref name="line"/> for
    /// each output line as it arrives, and ends the whole process tree on timeout or cancel.
    /// Throws <see cref="CatalogBuilderRefusedException"/> before anything starts when the run
    /// is not safe.
    /// </summary>
    Task<CatalogBuilderRun> RunAsync(CatalogEntry entry, byte[] builder, IReadOnlyList<string> arguments, Action<string> line, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A builder run that was not started; the message is plain text for the operator.</summary>
public sealed class CatalogBuilderRefusedException(string message) : Exception(message);

/// <summary>
/// Extracts the builder to a new per-run folder under the data root, checks the SHA-256 of the
/// written file against the catalog immediately before the start, runs it with
/// -NoProfile -NonInteractive -ExecutionPolicy Bypass -File in a kill-on-close job and removes
/// the folder afterwards. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never
/// through one command string.
/// </summary>
public sealed class CatalogBuilderRunner(string? catalogFolder = null) : ICatalogBuilderRunner
{
    public const string RunFolderPrefix = "run-";

    /// <summary>The longest time one builder may run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The number of output lines kept for the outcome.</summary>
    public const int MaxOutputLines = 500;

    public const int MaxLineLength = 1000;

    public const string LinkMessage = "The catalog run folder is a junction or symbolic link, so the builder was not started.";
    public const string HashMessage = "The extracted builder does not match the shipped catalog, so it was not started.";
    public const string ContainmentMessage = "The builder could not be started in a contained process, so it was stopped before it ran.";
    public const string StartMessage = "Windows PowerShell 5.1 could not start the builder.";

    public static string DefaultFolder() => Path.Combine(CollectionsFolders.Root(), "Catalog");

    /// <summary>The full path of Windows PowerShell 5.1.</summary>
    internal string PowerShellPath { get; init; } = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    internal Func<Process, IDisposable?> AssignJob { get; init; } = KillOnCloseJob.TryAssign;

    /// <summary>Called with the extracted file's path before the hash check.</summary>
    internal Action<string>? Extracted { get; init; }

    /// <summary>Called with the process ID once the builder started.</summary>
    internal Action<int>? Started { get; init; }

    public async Task<CatalogBuilderRun> RunAsync(CatalogEntry entry, byte[] builder, IReadOnlyList<string> arguments, Action<string> line, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(line);
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(CatalogService.Sha256(builder), entry.BuilderSha256, StringComparison.OrdinalIgnoreCase))
            throw new CatalogBuilderRefusedException(HashMessage);

        var root = catalogFolder ?? DefaultFolder();
        var container = DataFolderLinks.ContainerOf(root, catalogFolder is null);
        if (DataFolderLinks.HasLink(root, container)) throw new CatalogBuilderRefusedException(LinkMessage);
        Directory.CreateDirectory(root);
        var runFolder = Path.Combine(root, RunFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runFolder);
        try
        {
            if (DataFolderLinks.HasLink(runFolder, container)) throw new CatalogBuilderRefusedException(LinkMessage);
            var scriptName = entry.BuilderResource.StartsWith(CatalogService.BuilderPrefix, StringComparison.Ordinal)
                ? entry.BuilderResource[CatalogService.BuilderPrefix.Length..]
                : "";
            if (scriptName.Length == 0 || scriptName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || scriptName.Contains(".."))
                throw new CatalogBuilderRefusedException(HashMessage);
            var scriptPath = Path.Combine(runFolder, scriptName);
            await using (var file = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.WriteAsync(builder, CancellationToken.None).ConfigureAwait(false);
            Extracted?.Invoke(scriptPath);

            // The file is opened without sharing for the check and the start, so it cannot change between the two.
            using var held = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var onDisk = new byte[held.Length];
            await held.ReadExactlyAsync(onDisk, CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(CatalogService.Sha256(onDisk), entry.BuilderSha256, StringComparison.OrdinalIgnoreCase))
                throw new CatalogBuilderRefusedException(HashMessage);

            return await StartAsync(scriptPath, arguments, line, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            RemoveRunFolder(runFolder);
        }
    }

    private async Task<CatalogBuilderRun> StartAsync(string scriptPath, IReadOnlyList<string> arguments, Action<string> line, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }.Concat(arguments))
            startInfo.ArgumentList.Add(argument);
        WindowsPowerShellHost.ApplyLogonModulePath(startInfo);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CatalogBuilderRefusedException(StartMessage);
        }
        var job = AssignJob(process);
        if (job is null)
        {
            BoundedProcessRunner.Terminate(process);
            throw new CatalogBuilderRefusedException(ContainmentMessage);
        }
        using (job)
        {
            Started?.Invoke(process.Id);
            try { process.StandardInput.Close(); } catch (IOException) { }

            var kept = new List<string>();
            void Take(string? text)
            {
                if (text is null) return;
                var shown = text.Length > MaxLineLength ? text[..MaxLineLength] : text;
                lock (kept)
                {
                    if (kept.Count < MaxOutputLines) kept.Add(shown);
                }
                try { line(shown); } catch (Exception) { }
            }
            var stdout = ReadLinesAsync(process.StandardOutput, Take);
            var stderr = ReadLinesAsync(process.StandardError, Take);

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
                await Drain(stdout, stderr).ConfigureAwait(false);
                return new CatalogBuilderRun(null, !ct.IsCancellationRequested, ct.IsCancellationRequested, Snapshot(kept));
            }
            job.Dispose();
            await Drain(stdout, stderr).ConfigureAwait(false);
            return new CatalogBuilderRun(process.ExitCode, false, false, Snapshot(kept));
        }
    }

    private static IReadOnlyList<string> Snapshot(List<string> kept)
    {
        lock (kept) return kept.ToList();
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string?> take)
    {
        try
        {
            string? text;
            while ((text = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                take(text);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>A descendant outside the job can hold the pipes open; the wait for both streams is bounded.</summary>
    private static async Task Drain(Task stdout, Task stderr)
    {
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static void RemoveRunFolder(string runFolder)
    {
        try
        {
            if (!Directory.Exists(runFolder) || AppDataPaths.IsReparsePointDirectory(runFolder)) return;
            foreach (var attempt in Enumerable.Range(0, 5))
            {
                try
                {
                    Directory.Delete(runFolder, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(200);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
