using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>One entry of a 7-Zip technical listing.</summary>
internal sealed record SevenZipEntry(string Path, long Size, bool IsDirectory, bool IsLink);

/// <summary>
/// Runs an installed 7z.exe to list or extract an archive. The child's output is read as
/// it arrives and the run ends (with its whole process tree) at the entry limit, the
/// time limit, the size limit or a cancel. Arguments are passed one by one, archive
/// names are matched literally (-spd), and standard input is closed so a password
/// prompt ends the run instead of waiting.
/// </summary>
internal sealed class SevenZipTool(string exePath)
{
    public string ExePath { get; } = exePath;

    /// <summary>
    /// Parses "7z l -slt" output: blocks of "Name = Value" lines after the "----------"
    /// separator, one block per entry. Lines the parser does not know are ignored.
    /// </summary>
    public static List<SevenZipEntry> ParseTechnicalListing(IEnumerable<string> lines, int maxEntries)
    {
        var entries = new List<SevenZipEntry>();
        var inEntries = false;
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (block.Count == 0) return;
            if (block.TryGetValue("Path", out var path) && path.Length > 0)
            {
                if (entries.Count >= maxEntries)
                    throw new InstallerAnalysisException($"The archive has more than {maxEntries:N0} entries; it was not listed.");
                long.TryParse(block.GetValueOrDefault("Size", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                var attributes = block.GetValueOrDefault("Attributes", "");
                var isDirectory = block.GetValueOrDefault("Folder", "") == "+" || attributes.StartsWith('D');
                var isLink = attributes.Contains('L') || block.ContainsKey("Symbolic Link") || block.ContainsKey("Hard Link");
                entries.Add(new SevenZipEntry(path, Math.Max(0, size), isDirectory, isLink));
            }
            block.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!inEntries)
            {
                if (line.StartsWith("----------", StringComparison.Ordinal)) inEntries = true;
                continue;
            }
            if (line.Length == 0) { Flush(); continue; }
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq <= 0)
            {
                if (line.EndsWith(" =", StringComparison.Ordinal)) block[line[..^2].Trim()] = "";
                continue;
            }
            var key = line[..eq].Trim();
            if (key == "Path" && block.ContainsKey("Path")) Flush();
            block[key] = line[(eq + 3)..];
        }
        Flush();
        return entries;
    }

    public async Task<List<SevenZipEntry>> ListAsync(string archive, AnalysisLimits limits, CancellationToken ct)
    {
        var lines = new List<string>();
        var entryCount = 0;
        var result = await RunAsync(["l", "-slt", "-sccUTF-8", "--", archive], line =>
        {
            if (line.StartsWith("Path = ", StringComparison.Ordinal) && ++entryCount > limits.MaxEntries + 1)
                return false;
            lines.Add(line);
            return true;
        }, null, 0, limits.Timeout, ct).ConfigureAwait(false);
        if (result.StoppedByLimit)
            throw new InstallerAnalysisException($"The archive has more than {limits.MaxEntries:N0} entries; it was not listed.");
        if (result.TimedOut) throw new InstallerAnalysisException("Listing the payload stopped at the time limit.");
        var entries = ParseTechnicalListing(lines, limits.MaxEntries);
        if (result.ExitCode >= 2 && entries.Count == 0)
            throw new InstallerAnalysisException("7-Zip cannot open this file as an archive; it has no payload to list.");
        return entries;
    }

    /// <summary>
    /// Extracts the named entries (all entries when <paramref name="names"/> is null) with
    /// their folders into <paramref name="outputFolder"/>. The folder's size is watched while
    /// 7-Zip runs, and the run ends when it passes <paramref name="maxBytes"/>.
    /// </summary>
    public async Task<(int ExitCode, bool OverLimit, bool TimedOut)> ExtractAsync(
        string archive, IReadOnlyList<string>? names, string outputFolder, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(outputFolder);
        string? listFile = null;
        try
        {
            var args = new List<string> { "x", "-y", "-bd", "-spd", "-sccUTF-8", "-o" + outputFolder };
            if (names is not null)
            {
                listFile = Path.Combine(outputFolder, "apd-7z-" + Guid.NewGuid().ToString("N") + ".txt");
                await File.WriteAllLinesAsync(listFile, names, new UTF8Encoding(false), ct).ConfigureAwait(false);
                args.Add("-scsUTF-8");
                args.Add("-i@" + listFile);
            }
            args.Add("--");
            args.Add(archive);
            var result = await RunAsync(args, null, outputFolder, maxBytes, timeout, ct).ConfigureAwait(false);
            return (result.ExitCode, result.StoppedByLimit, result.TimedOut);
        }
        finally
        {
            if (listFile is not null) try { File.Delete(listFile); } catch (IOException) { }
        }
    }

    private sealed record RunResult(int ExitCode, bool TimedOut, bool StoppedByLimit);

    private async Task<RunResult> RunAsync(IReadOnlyList<string> args, Func<string, bool>? onLine, string? watchFolder, long maxBytes,
        TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        var stopped = false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);

        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (stopped) continue;
                if (onLine is not null && !onLine(line))
                {
                    stopped = true;
                    BoundedProcessRunner.Terminate(process);
                }
            }
        }, CancellationToken.None);
        var watcher = watchFolder is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!process.HasExited && !stopped)
            {
                try { await Task.Delay(250, bounded.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (FolderBytes(watchFolder) > maxBytes)
                {
                    stopped = true;
                    BoundedProcessRunner.Terminate(process);
                }
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            BoundedProcessRunner.Terminate(process);
            await Observe(reader, stderr, watcher).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new RunResult(-1, TimedOut: true, StoppedByLimit: stopped);
        }
        await Observe(reader, stderr, watcher).ConfigureAwait(false);
        if (watchFolder is not null && FolderBytes(watchFolder) > maxBytes) stopped = true;
        return new RunResult(process.ExitCode, TimedOut: false, StoppedByLimit: stopped);
    }

    private static async Task Observe(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    internal static long FolderBytes(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                total += file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}
