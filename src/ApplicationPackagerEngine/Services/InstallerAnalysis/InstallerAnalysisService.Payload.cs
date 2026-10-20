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
            return zip.Entries(limits.MaxEntries).Select(e =>
            {
                if (BoundedZip.IsSymbolicLink(e)) return new PayloadEntry(e.FullName, e.Length, false, "Link", "") { Description = LinkDescription };
                var isDirectory = BoundedZip.IsDirectory(e);
                // The Windows attribute byte keeps FILE_ATTRIBUTE_REPARSE_POINT; the entry data is the reparse buffer, not a link and not an installer.
                if ((e.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    return new PayloadEntry(e.FullName, isDirectory ? 0 : e.Length, isDirectory, "File", "")
                    { Description = isDirectory ? StoredFolderDescription : StoredFileDescription };
                return Classify(e.FullName, e.Length, isDirectory, engine);
            }).ToList();
        }
        var tool = SevenZip();
        var entries = await tool.ListAsync(path, limits, ct).ConfigureAwait(false);
        return entries.Select(e => e.IsLink
            ? new PayloadEntry(e.Path, e.Size, false, "Link", "") { Description = LinkDescription }
            : Classify(e.Path, e.Size, e.IsDirectory, engine)).ToList();
    }

    private const string LinkDescription = "Link; it is not extracted";
    private const string StoredFileDescription = "Stored as a plain file by the archive format; links are not created";
    private const string StoredFolderDescription = "Stored as a plain folder by the archive format; links are not created";

    private static bool IsBuiltInContainer(string engine) => engine is "NativeMSI" or "MSP" or "Velopack" || ZipEngines.Contains(engine);

    private SevenZipTool SevenZip()
    {
        var exe = _findSevenZip();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new SevenZipNotFoundException();
        return new SevenZipTool(exe);
    }

    /// <summary>
    /// The 7-Zip used to extract. A version older than <see cref="SevenZipTool.MinimumExtractionVersion"/>
    /// can write outside the target through a link in the archive, so it is refused; listing still uses it.
    /// </summary>
    private SevenZipTool SevenZipForExtraction()
    {
        var tool = SevenZip();
        var version = SevenZipTool.ReadVersion(tool.ExePath);
        var minimum = SevenZipTool.MinimumExtractionVersion;
        if (version is null)
            throw new InstallerAnalysisException($"The version of 7-Zip ({tool.ExePath}) could not be read; extraction is refused. Install 7-Zip {minimum.Major}.{minimum.Minor:00} or later from https://www.7-zip.org. Listing and analysis still work.");
        if (version < minimum)
            throw new InstallerAnalysisException($"7-Zip {version.Major}.{version.Minor:00} ({tool.ExePath}) is older than {minimum.Major}.{minimum.Minor:00} and can write outside the extraction folder through a link in an archive; extraction is refused. Install 7-Zip {minimum.Major}.{minimum.Minor:00} or later from https://www.7-zip.org. Listing and analysis still work.");
        return tool;
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
        SevenZipTool? tool = null;
        if (!IsBuiltInContainer(engine))
        {
            tool = SevenZipForExtraction();
            var listed = (await tool.ListAsync(path, limits, ct).ConfigureAwait(false)).FirstOrDefault(e => e.Path == entry.Path)
                ?? throw new InstallerAnalysisException("The entry is not in the archive.");
            if (listed.IsLink) throw new InstallerAnalysisException("The entry is a link; it is not extracted.");
            if (listed.Size > limits.MaxExtractBytes)
                throw new InstallerAnalysisException("The entry is larger than the extraction limit; it was not extracted.");
        }
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

        var (exitCode, overLimit, timedOut) = await tool!.ExtractAsync(path, [entry.Path], null, folder, limits.MaxExtractBytes, limits.Timeout, ct).ConfigureAwait(false);
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
        for (var ancestor = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target)); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if (AppDataPaths.IsReparsePointDirectory(ancestor))
                throw new InstallerAnalysisException("A folder on the path of the target folder is a junction or symbolic link; choose another folder.");
        }
        return await Bounded(token => ExtractPayloadCoreAsync(full, Identify(full, limits, "extracted"), entries, target, limits, token), limits, ct,
            "The extraction stopped at the time limit.", "The payload could not be extracted; the file may be damaged.").ConfigureAwait(false);
    }

    private async Task<PayloadExtractResult> ExtractPayloadCoreAsync(string path, string engine, IReadOnlyList<PayloadEntry>? entries, string target,
        AnalysisLimits limits, CancellationToken ct)
    {
        var skipped = new List<string>();
        var wanted = entries?.Where(e => !e.IsDirectory).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        long bytes = 0;
        var files = 0;

        // 7-Zip archives are listed first: links and refused names are left out, and the listed
        // sizes are checked against the limit before anything is written.
        SevenZipTool? tool = null;
        List<string>? names = null;
        var excluded = new List<string>();
        if (!IsBuiltInContainer(engine))
        {
            tool = SevenZipForExtraction();
            var listing = await tool.ListAsync(path, limits, ct).ConfigureAwait(false);
            var selected = listing.Where(e => (!e.IsDirectory || e.IsLink) && (wanted is null || wanted.Contains(e.Path))).ToList();
            if (wanted is not null)
                foreach (var missing in wanted.Except(selected.Select(e => e.Path), StringComparer.Ordinal)) skipped.Add(missing + ": The entry is not in the archive.");
            names = wanted is null ? null : [];
            long listedBytes = 0;
            foreach (var e in selected)
            {
                string? reason = null;
                if (e.IsLink) reason = "The entry is a link; it is not extracted.";
                else if (SandboxPath.Normalize(e.Path, out var why) is null) reason = why;
                if (reason is not null)
                {
                    skipped.Add(e.Path + ": " + reason);
                    excluded.Add(e.Path);
                    continue;
                }
                listedBytes += e.Size;
                names?.Add(e.Path);
            }
            if (listedBytes > limits.MaxExtractBytes)
                throw new InstallerAnalysisException($"The payload is larger than the extraction limit of {FormatBytes(limits.MaxExtractBytes)}; nothing was extracted.");
        }

        var baseName = Path.GetFileNameWithoutExtension(path).TrimEnd('.', ' ');
        if (SandboxPath.Normalize(baseName, out _) is null) baseName = "payload";
        var folder = CreatePayloadFolder(target, baseName);

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

        if (names is { Count: 0 }) return new PayloadExtractResult(folder, 0, 0, skipped);
        var (exitCode, overLimit, timedOut) = await tool!.ExtractAsync(path, names, names is null ? excluded : null, folder, limits.MaxExtractBytes, limits.Timeout, ct).ConfigureAwait(false);
        foreach (var link in RemoveLinks(folder)) skipped.Add(Path.GetRelativePath(folder, link) + ": The entry is a link; it was removed.");
        if (overLimit)
        {
            // 7-Zip sets each file to its declared size before it writes, so a stopped run can leave large files behind.
            AnalysisSandbox.DeleteTree(folder);
            throw new InstallerAnalysisException("The payload expands beyond the extraction limit; extraction stopped and the partial files were removed.");
        }
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);

    /// <summary>
    /// Creates "&lt;name&gt; payload" in <paramref name="target"/>, or "&lt;name&gt; payload (n)" when the name
    /// is taken. CreateDirectoryW fails on an existing name, so two concurrent extractions never
    /// share one folder.
    /// </summary>
    internal static string CreatePayloadFolder(string target, string baseName)
    {
        for (var n = 1; n <= 1000; n++)
        {
            var folder = Path.Combine(target, n == 1 ? baseName + " payload" : $"{baseName} payload ({n})");
            if (CreateDirectoryNative(folder, IntPtr.Zero)) return folder;
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            // ERROR_ALREADY_EXISTS (183) and ERROR_FILE_EXISTS (80): the name is taken, by a folder or a file.
            if (error is not 183 and not 80)
                throw new InstallerAnalysisException("The payload folder could not be created in the target folder.");
        }
        throw new InstallerAnalysisException("No free folder name was found in the target folder.");
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
