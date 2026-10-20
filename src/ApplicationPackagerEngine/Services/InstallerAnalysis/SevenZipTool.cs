using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>One entry of a 7-Zip technical listing.</summary>
internal sealed record SevenZipEntry(string Path, long Size, bool IsDirectory, bool IsLink);

/// <summary>
/// Runs an installed 7z.exe to list or extract an archive. The child's output is read as
/// it arrives in bounded lines and the run ends (with its whole process tree) at the entry
/// limit, the output limit, the time limit, the size limit or a cancel. Arguments are passed
/// one by one, archive names are matched literally (-spd), and standard input is closed so a
/// password prompt ends the run instead of waiting.
/// </summary>
internal sealed class SevenZipTool(string exePath)
{
    /// <summary>
    /// The oldest 7-Zip that extracts: 25.00 fixes CVE-2025-11001 and CVE-2025-11002 and 25.01
    /// fixes CVE-2025-55188; all three let an archive with a link write outside the target folder.
    /// </summary>
    public static readonly Version MinimumExtractionVersion = new(25, 1);

    /// <summary>The longest output line kept; a longer line ends the listing.</summary>
    public const int MaxLineChars = 32 * 1024;

    /// <summary>The default limit on the characters one listing may print.</summary>
    public const long DefaultMaxOutputChars = 128L * 1024 * 1024;

    private const int WatchIntervalMilliseconds = 50;

    public string ExePath { get; } = exePath;

    public long MaxOutputChars { get; init; } = DefaultMaxOutputChars;

    /// <summary>The major and minor file version of the executable, or null when it has none.</summary>
    public static Version? ReadVersion(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0) return null;
            return new Version(info.FileMajorPart, info.FileMinorPart);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds entries from "7z l -slt" output fed one line at a time: blocks of "Name = Value"
    /// lines after the "----------" separator, one block per entry. Lines the parser does not
    /// know are ignored.
    /// </summary>
    internal sealed class ListingParser(int maxEntries)
    {
        private readonly Dictionary<string, string> _block = new(StringComparer.OrdinalIgnoreCase);
        private bool _inEntries;

        public List<SevenZipEntry> Entries { get; } = [];

        public void Feed(string raw)
        {
            var line = raw.TrimEnd('\r');
            if (!_inEntries)
            {
                if (line.StartsWith("----------", StringComparison.Ordinal)) _inEntries = true;
                return;
            }
            if (line.Length == 0) { Flush(); return; }
            var eq = line.IndexOf(" =", StringComparison.Ordinal);
            if (eq <= 0) return;
            var key = line[..eq].Trim();
            var value = line.Length > eq + 3 ? line[(eq + 3)..] : "";
            if (key == "Path" && _block.ContainsKey("Path")) Flush();
            _block[key] = value;
        }

        public List<SevenZipEntry> Finish()
        {
            Flush();
            return Entries;
        }

        private void Flush()
        {
            if (_block.Count == 0) return;
            if (_block.TryGetValue("Path", out var path) && path.Length > 0)
            {
                if (Entries.Count >= maxEntries)
                    throw new InstallerAnalysisException($"The archive has more than {maxEntries:N0} entries; it was not listed.");
                long.TryParse(_block.GetValueOrDefault("Size", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                var attributes = _block.GetValueOrDefault("Attributes", "");
                var mode = _block.GetValueOrDefault("Mode", "");
                var isDirectory = _block.GetValueOrDefault("Folder", "") == "+" || attributes.StartsWith('D');
                var isLink = attributes.Contains('L') || mode.StartsWith('l')
                    || _block.GetValueOrDefault("Symbolic Link", "").Length > 0 || _block.GetValueOrDefault("Hard Link", "").Length > 0;
                Entries.Add(new SevenZipEntry(path, Math.Max(0, size), isDirectory, isLink));
            }
            _block.Clear();
        }
    }

    public static List<SevenZipEntry> ParseTechnicalListing(IEnumerable<string> lines, int maxEntries)
    {
        var parser = new ListingParser(maxEntries);
        foreach (var line in lines) parser.Feed(line);
        return parser.Finish();
    }

    public async Task<List<SevenZipEntry>> ListAsync(string archive, AnalysisLimits limits, CancellationToken ct)
    {
        var parser = new ListingParser(limits.MaxEntries);
        InstallerAnalysisException? parseFailure = null;
        var result = await RunAsync(["l", "-slt", "-sccUTF-8", "--", archive], line =>
        {
            if (ct.IsCancellationRequested) return false;
            try { parser.Feed(line); }
            catch (InstallerAnalysisException ex)
            {
                parseFailure = ex;
                return false;
            }
            return true;
        }, null, 0, limits.Timeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (parseFailure is not null) throw parseFailure;
        if (result.LineTooLong) throw new InstallerAnalysisException($"The archive listing has a line longer than {MaxLineChars:N0} characters; it was not listed.");
        if (result.OutputOverLimit) throw new InstallerAnalysisException("The archive listing is larger than the output limit; it was not listed.");
        if (result.TimedOut) throw new InstallerAnalysisException("Listing the payload stopped at the time limit.");
        var entries = parser.Finish();
        if (result.ExitCode >= 2 && entries.Count == 0)
            throw new InstallerAnalysisException("7-Zip cannot open this file as an archive; it has no payload to list.");
        return entries;
    }

    /// <summary>
    /// Extracts the named entries (all entries when <paramref name="names"/> is null), without
    /// the <paramref name="excluded"/> ones, with their folders into <paramref name="outputFolder"/>.
    /// The folder's size is watched while 7-Zip runs, and the run ends when it passes
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public async Task<(int ExitCode, bool OverLimit, bool TimedOut)> ExtractAsync(
        string archive, IReadOnlyList<string>? names, IReadOnlyList<string>? excluded, string outputFolder, long maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(outputFolder);
        var listFiles = new List<string>();
        try
        {
            var args = new List<string> { "x", "-y", "-bd", "-spd", "-sccUTF-8", "-o" + outputFolder };
            async Task AddList(string switchName, IReadOnlyList<string> entries)
            {
                // The list files live outside the output folder so the size watcher does not count them.
                var listFile = Path.Combine(Path.GetTempPath(), "apd-7z-" + Guid.NewGuid().ToString("N") + ".txt");
                listFiles.Add(listFile);
                await File.WriteAllLinesAsync(listFile, entries, new UTF8Encoding(false), ct).ConfigureAwait(false);
                args.Add(switchName + listFile);
            }
            if (names is not null || excluded is { Count: > 0 }) args.Add("-scsUTF-8");
            if (names is not null) await AddList("-i@", names).ConfigureAwait(false);
            if (excluded is { Count: > 0 }) await AddList("-x@", excluded).ConfigureAwait(false);
            args.Add("--");
            args.Add(archive);
            var result = await RunAsync(args, null, outputFolder, maxBytes, timeout, ct).ConfigureAwait(false);
            return (result.ExitCode, result.StoppedByLimit, result.TimedOut);
        }
        finally
        {
            foreach (var listFile in listFiles)
                try { File.Delete(listFile); } catch (IOException) { }
        }
    }

    private sealed record RunResult(int ExitCode, bool TimedOut, bool StoppedByLimit, bool OutputOverLimit, bool LineTooLong);

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
        var outputOverLimit = false;
        var lineTooLong = false;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);

        void Stop()
        {
            stopped = true;
            BoundedProcessRunner.Terminate(process);
        }

        var stderr = DrainAsync(process.StandardError);
        var reader = Task.Run(async () =>
        {
            var buffer = new char[16 * 1024];
            var line = new StringBuilder();
            var overlong = false;
            long total = 0;
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                if (stopped || onLine is null) continue;
                total += read;
                if (total > MaxOutputChars)
                {
                    outputOverLimit = true;
                    Stop();
                    continue;
                }
                for (var i = 0; i < read && !stopped; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        if (overlong) { lineTooLong = true; Stop(); break; }
                        if (!onLine(line.ToString())) { Stop(); break; }
                        line.Clear();
                        continue;
                    }
                    if (line.Length < MaxLineChars) line.Append(c);
                    else overlong = true;
                }
            }
            if (!stopped && onLine is not null)
            {
                if (overlong) lineTooLong = true;
                else if (line.Length > 0) onLine(line.ToString());
            }
        }, CancellationToken.None);
        var watcher = watchFolder is null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!process.HasExited && !stopped)
            {
                try { await Task.Delay(WatchIntervalMilliseconds, bounded.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (FolderBytes(watchFolder) > maxBytes) Stop();
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
            return new RunResult(-1, TimedOut: true, StoppedByLimit: stopped, outputOverLimit, lineTooLong);
        }
        await Observe(reader, stderr, watcher).ConfigureAwait(false);
        var overSize = watchFolder is not null && FolderBytes(watchFolder) > maxBytes;
        return new RunResult(process.ExitCode, TimedOut: false, StoppedByLimit: (stopped && watchFolder is not null) || overSize, outputOverLimit, lineTooLong);
    }

    /// <summary>Reads a stream to its end without keeping it, so a child that writes much to it never blocks.</summary>
    private static Task DrainAsync(StreamReader reader) => Task.Run(async () =>
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) > 0) { }
    });

    private static async Task Observe(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// The bytes in the folder tree. Each size is queried from the file itself: the size a folder
    /// listing reports for a file another process is still writing stays at its old value until
    /// that process closes the file.
    /// </summary>
    internal static long FolderBytes(string folder)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                try
                {
                    file.Refresh();
                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }
}
