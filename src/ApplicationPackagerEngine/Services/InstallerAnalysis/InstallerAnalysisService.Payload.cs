namespace ApplicationPackagerEngine.Services;

public sealed partial class InstallerAnalysisService
{
    private static readonly Dictionary<string, (string Type, string Description)> InnerKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".msi"] = ("MSI", "Windows Installer database"),
        [".msp"] = ("MSP", "Windows Installer patch"),
        [".cab"] = ("CAB", "Cabinet archive"),
        [".exe"] = ("EXE", "Executable"),
        [".zip"] = ("ZIP", "Zip archive"),
        [".nupkg"] = ("NUPKG", "NuGet or Chocolatey package"),
        [".appx"] = ("APPX", "AppX package"),
        [".appxbundle"] = ("APPX", "AppX bundle"),
        [".msix"] = ("MSIX", "MSIX package"),
        [".msixbundle"] = ("MSIX", "MSIX bundle"),
        [".7z"] = ("7Z", "7-Zip archive"),
        [".intunewin"] = ("INTUNEWIN", "Intune Win32 package")
    };

    public async Task<IReadOnlyList<PayloadEntry>> ListPayloadAsync(string path, AnalysisLimits limits, CancellationToken ct)
    {
        var full = CheckFile(path, limits);
        return await Bounded(token => ListPayloadCoreAsync(full, Identify(full, limits, "listed"), limits, token), limits, ct,
            "Listing the payload stopped at the time limit.", "The payload could not be listed; the file may be damaged.").ConfigureAwait(false);
    }

    /// <summary>
    /// The entry with the payload kind for its container: an inner installer (MSI, MSP, EXE,
    /// cabinet, package) or a plain file. MSI table streams are not installable and stay files.
    /// </summary>
    internal static PayloadEntry Classify(string name, long size, bool isDirectory, string container)
    {
        if (isDirectory) return new PayloadEntry(name, 0, true, "File", "") { Description = "Folder" };
        var leaf = name.Replace('/', '\\');
        leaf = leaf[(leaf.LastIndexOf('\\') + 1)..].Trim('<', '>');
        var extension = Path.GetExtension(leaf);
        if (!InnerKinds.TryGetValue(extension, out var kind))
            return new PayloadEntry(name, size, false, "File", "");
        var description = kind.Description;
        switch (container)
        {
            case "NativeMSI" when kind.Type == "CAB": description = "MSI Media-table cabinet"; break;
            case "WixBurn" when kind.Type is "MSI" or "EXE" or "MSP": description = "Burn bundle chained package"; break;
            case "WixBurn" when kind.Type == "CAB": description = "Burn container cabinet"; break;
            case "Squirrel" when kind.Type == "NUPKG": description = "Squirrel application package"; break;
            case "NSIS" when kind.Type == "EXE" && System.Text.RegularExpressions.Regex.IsMatch(leaf, "(?i)^uninst|uninst.*\\.exe$"): description = "NSIS uninstaller"; break;
            case "PsadtV3" or "PsadtV4" when kind.Type is "MSI" or "EXE" or "MSP": description = "PSADT Files payload"; break;
            case "Chocolatey" when kind.Type is "MSI" or "EXE": description = "Chocolatey tools payload"; break;
        }
        return new PayloadEntry(name, size, false, "InnerInstaller", kind.Type) { Description = description };
    }

    private async Task<IReadOnlyList<PayloadEntry>> ListPayloadCoreAsync(string path, string engine, AnalysisLimits limits, CancellationToken ct)
    {
        if (engine is "NativeMSI" or "MSP")
        {
            using var db = MsiDatabase.Open(path, patch: engine == "MSP");
            var (rows, truncated) = MsiTables.Streams(db, limits.MaxEntries, ct);
            if (truncated) throw new InstallerAnalysisException($"The package has more than {limits.MaxEntries:N0} streams; it was not listed.");
            return rows.Select(r => Classify(r.Name, r.Size, false, engine)).ToList();
        }
        if (ZipEngines.Contains(engine) || engine == "Velopack")
        {
            using var zip = BoundedZip.Open(path, limits.MaxEntries, "listed", velopack: engine == "Velopack");
            return zip.Entries(limits.MaxEntries)
                .Select(e => Classify(e.FullName, e.Length, BoundedZip.IsDirectory(e), engine))
                .ToList();
        }
        var tool = SevenZip();
        var entries = await tool.ListAsync(path, limits, ct).ConfigureAwait(false);
        return entries.Select(e => Classify(e.Path, e.Size, e.IsDirectory, engine)).ToList();
    }

    private SevenZipTool SevenZip()
    {
        var exe = _findSevenZip();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new InstallerAnalysisException("7-Zip is not installed; the payload of this installer type is listed with 7-Zip. Install 7-Zip to list and extract it.");
        return new SevenZipTool(exe);
    }

    public async Task<string> ExtractEntryAsync(string path, PayloadEntry entry, string sessionFolder, AnalysisLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var full = CheckFile(path, limits);
        var session = CheckSessionFolder(sessionFolder);
        if (entry.IsDirectory) throw new InstallerAnalysisException("A folder cannot be extracted as one entry; use Extract payload.");
        return await Bounded(token => ExtractEntryCoreAsync(full, Identify(full, limits, "extracted"), entry, session, limits, token), limits, ct,
            "The extraction stopped at the time limit.", "The entry could not be extracted; the file may be damaged.").ConfigureAwait(false);
    }

    private string CheckSessionFolder(string sessionFolder)
    {
        if (string.IsNullOrWhiteSpace(sessionFolder)) throw new InstallerAnalysisException("No sandbox session was given.");
        var full = Path.GetFullPath(sessionFolder);
        var root = _sandbox.Root;
        if (root.Length > 0 && !AppDataPaths.IsWithin(full, root))
            throw new InstallerAnalysisException("Entries are extracted only into the analysis sandbox.");
        if (!Directory.Exists(full) || AppDataPaths.IsReparsePointDirectory(full))
            throw new InstallerAnalysisException("The sandbox session folder is missing or is a link.");
        return full;
    }

    private async Task<string> ExtractEntryCoreAsync(string path, string engine, PayloadEntry entry, string session, AnalysisLimits limits, CancellationToken ct)
    {
        if (entry.Size > limits.MaxExtractBytes)
            throw new InstallerAnalysisException("The entry is larger than the extraction limit; it was not extracted.");
        var folder = Path.Combine(session, "e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var target = SandboxPath.Resolve(folder, entry.Path.Trim('<', '>'), out var reason)
            ?? throw new InstallerAnalysisException(reason + " It was not extracted.");

        if (engine is "NativeMSI" or "MSP")
        {
            using var db = MsiDatabase.Open(path, patch: engine == "MSP");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!MsiTables.CopyStream(db, entry.Path, output, limits.MaxExtractBytes, ct))
                    throw new InstallerAnalysisException("The entry is not in the package.");
            }
            return target;
        }
        if (ZipEngines.Contains(engine) || engine == "Velopack")
        {
            using var zip = BoundedZip.Open(path, limits.MaxEntries, "extracted", velopack: engine == "Velopack");
            var zipEntry = zip.Entries(limits.MaxEntries).FirstOrDefault(e => e.FullName == entry.Path)
                ?? throw new InstallerAnalysisException("The entry is not in the package.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                BoundedZip.Copy(zipEntry, output, limits.MaxExtractBytes, limits.MaxCompressionRatio, ct);
            }
            catch
            {
                TryDelete(target);
                throw;
            }
            return target;
        }

        var tool = SevenZip();
        var (exitCode, overLimit, timedOut) = await tool.ExtractAsync(path, [entry.Path], folder, limits.MaxExtractBytes, limits.Timeout, ct).ConfigureAwait(false);
        RemoveLinks(folder);
        if (overLimit)
        {
            AnalysisSandbox.DeleteTree(folder);
            throw new InstallerAnalysisException("The entry expands beyond the extraction limit; it was not extracted.");
        }
        if (timedOut) throw new InstallerAnalysisException("The extraction stopped at the time limit.");
        if (File.Exists(target) && !IsReparsePoint(target)) return target;
        var files = Directory.GetFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint });
        var leaf = Path.GetFileName(target);
        var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), leaf, StringComparison.OrdinalIgnoreCase))
            ?? (files.Length == 1 ? files[0] : null);
        if (match is null)
            throw new InstallerAnalysisException(exitCode >= 2 ? "7-Zip could not extract the entry; the archive may be damaged or encrypted." : "The entry was not found after extraction.");
        return match;
    }

    public async Task<PayloadExtractResult> ExtractPayloadAsync(string path, IReadOnlyList<PayloadEntry>? entries, string targetFolder, AnalysisLimits limits, CancellationToken ct)
    {
        var full = CheckFile(path, limits);
        if (string.IsNullOrWhiteSpace(targetFolder)) throw new InstallerAnalysisException("No target folder was given.");
        var target = Path.GetFullPath(targetFolder);
        var root = _sandbox.Root;
        if (root.Length > 0 && AppDataPaths.IsWithin(target, root))
            throw new InstallerAnalysisException("The analysis sandbox cannot be the target folder; choose another folder.");
        if (!Directory.Exists(target)) throw new InstallerAnalysisException("The target folder does not exist.");
        if (AppDataPaths.IsReparsePointDirectory(target)) throw new InstallerAnalysisException("The target folder is a junction or symbolic link; choose another folder.");
        return await Bounded(token => ExtractPayloadCoreAsync(full, Identify(full, limits, "extracted"), entries, target, limits, token), limits, ct,
            "The extraction stopped at the time limit.", "The payload could not be extracted; the file may be damaged.").ConfigureAwait(false);
    }

    private async Task<PayloadExtractResult> ExtractPayloadCoreAsync(string path, string engine, IReadOnlyList<PayloadEntry>? entries, string target,
        AnalysisLimits limits, CancellationToken ct)
    {
        var baseName = Path.GetFileNameWithoutExtension(path).TrimEnd('.', ' ');
        if (SandboxPath.Normalize(baseName, out _) is null) baseName = "payload";
        var folder = Path.Combine(target, baseName + " payload");
        for (var n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
        {
            if (n > 1000) throw new InstallerAnalysisException("No free folder name was found in the target folder.");
            folder = Path.Combine(target, $"{baseName} payload ({n})");
        }
        Directory.CreateDirectory(folder);
        var skipped = new List<string>();
        var wanted = entries?.Where(e => !e.IsDirectory).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        long bytes = 0;
        var files = 0;

        if (engine is "NativeMSI" or "MSP")
        {
            using var db = MsiDatabase.Open(path, patch: engine == "MSP");
            var (streams, _) = MsiTables.Streams(db, limits.MaxEntries, ct);
            foreach (var (name, size) in streams)
            {
                if (wanted is not null && !wanted.Contains(name)) continue;
                var file = SandboxPath.Resolve(folder, name, out var reason);
                if (file is null) { skipped.Add(name + ": " + reason); continue; }
                if (bytes + size > limits.MaxExtractBytes) throw new InstallerAnalysisException("The payload is larger than the extraction limit; extraction stopped.");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                MsiTables.CopyStream(db, name, output, limits.MaxExtractBytes - bytes, ct);
                bytes += output.Length;
                files++;
            }
            return new PayloadExtractResult(folder, files, bytes, skipped);
        }
        if (ZipEngines.Contains(engine) || engine == "Velopack")
        {
            using var zip = BoundedZip.Open(path, limits.MaxEntries, "extracted", velopack: engine == "Velopack");
            foreach (var entry in zip.Entries(limits.MaxEntries))
            {
                ct.ThrowIfCancellationRequested();
                if (BoundedZip.IsDirectory(entry)) continue;
                if (wanted is not null && !wanted.Contains(entry.FullName)) continue;
                if (BoundedZip.IsSymbolicLink(entry)) { skipped.Add(entry.FullName + ": The entry is a symbolic link."); continue; }
                var file = SandboxPath.Resolve(folder, entry.FullName, out var reason);
                if (file is null) { skipped.Add(entry.FullName + ": " + reason); continue; }
                if (File.Exists(file)) { skipped.Add(entry.FullName + ": The name is used twice in the archive."); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                try
                {
                    using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    bytes += BoundedZip.Copy(entry, output, limits.MaxExtractBytes - bytes, limits.MaxCompressionRatio, ct);
                }
                catch
                {
                    TryDelete(file);
                    throw;
                }
                files++;
            }
            return new PayloadExtractResult(folder, files, bytes, skipped);
        }

        var tool = SevenZip();
        List<string>? names = null;
        if (wanted is not null)
        {
            names = [];
            foreach (var name in wanted)
            {
                if (SandboxPath.Normalize(name, out var reason) is null) skipped.Add(name + ": " + reason);
                else names.Add(name);
            }
            if (names.Count == 0) return new PayloadExtractResult(folder, 0, 0, skipped);
        }
        var (exitCode, overLimit, timedOut) = await tool.ExtractAsync(path, names, folder, limits.MaxExtractBytes, limits.Timeout, ct).ConfigureAwait(false);
        foreach (var link in RemoveLinks(folder)) skipped.Add(Path.GetRelativePath(folder, link) + ": The entry is a link; it was removed.");
        if (overLimit) throw new InstallerAnalysisException("The payload expands beyond the extraction limit; extraction stopped and the partial files are in " + folder + ".");
        if (timedOut) throw new InstallerAnalysisException("The extraction stopped at the time limit.");
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            files++;
            bytes += file.Length;
        }
        if (exitCode >= 2 && files == 0) throw new InstallerAnalysisException("7-Zip could not extract the payload; the archive may be damaged or encrypted.");
        if (exitCode >= 1) skipped.Add("7-Zip reported warnings; some entries may be missing.");
        return new PayloadExtractResult(folder, files, bytes, skipped);
    }

    /// <summary>Deletes every junction, symbolic link and mount point under the folder and returns their paths.</summary>
    private static List<string> RemoveLinks(string folder)
    {
        var removed = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    removed.Add(entry.FullName);
                    entry.Delete();
                }
                else if (entry is DirectoryInfo directory) pending.Push(directory);
            }
        }
        return removed;
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
