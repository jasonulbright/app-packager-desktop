using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Services;

public sealed partial class InstallerAnalysisService
{
    public async Task<IReadOnlyList<RegistryWrite>> ReadRegistryWritesAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        (await ReadRegistryAsync(path, limits, ct).ConfigureAwait(false)).Writes;

    public async Task<RegistryReadResult> ReadRegistryAsync(string path, AnalysisLimits limits, CancellationToken ct)
    {
        var full = CheckFile(path, limits);
        return await Bounded(token => ReadRegistryCoreAsync(full, limits, token), limits, ct,
            "Reading the registry settings stopped at the time limit.", "The registry settings could not be read; the file may be damaged.").ConfigureAwait(false);
    }

    private async Task<RegistryReadResult> ReadRegistryCoreAsync(string path, AnalysisLimits limits, CancellationToken ct)
    {
        var engine = Identify(path, limits, "read");
        var notes = new List<string>();
        switch (engine)
        {
            case "NativeMSI":
            {
                try
                {
                    using var db = MsiDatabase.Open(path);
                    var (rows, truncated) = MsiTables.Registry(db, limits.MaxEntries, ct);
                    if (truncated) notes.Add($"Only the first {limits.MaxEntries:N0} registry rows are listed.");
                    if (rows.Any(r => r.Hive == "HKMU")) notes.Add("HKMU is HKLM for a per-machine install and HKCU for a per-user install.");
                    return new RegistryReadResult(rows, notes);
                }
                catch (InvalidDataException)
                {
                    return new RegistryReadResult([], ["The MSI database could not be read; it may be damaged."]);
                }
            }
            case "NSIS":
            {
                var (rows, note) = NsisRegistryReader.Read(path, limits.MaxEntries, "", ct);
                if (note.Length > 0) notes.Add(note);
                if (rows.Count > 0 && rows.Count < limits.MaxEntries)
                    rows.AddRange(await UninstallerRowsAsync(path, limits.MaxEntries - rows.Count, limits, notes, ct).ConfigureAwait(false));
                if (rows.Any(r => r.Hive == "SHCTX")) notes.Add("SHCTX is HKLM after SetShellVarContext all and HKCU otherwise.");
                return new RegistryReadResult(rows, notes);
            }
            case "InnoSetup":
            {
                var (rows, note) = InnoSetupReader.ReadRegistryEntries(path, limits.MaxEntries, ct);
                if (note.Length > 0) notes.Add(note);
                if (rows.Any(r => r.Hive == "HKA")) notes.Add("HKA is HKLM for an administrative install and HKCU for a per-user install.");
                return new RegistryReadResult(rows, notes);
            }
            default:
                return new RegistryReadResult([], ["Registry settings are read from MSI, NSIS and Inno Setup installers."]);
        }
    }

    /// <summary>
    /// The registry changes of the NSIS uninstaller. 7-Zip rebuilds the uninstaller from the
    /// installer's data block; it is extracted to a private sandbox session, read and deleted.
    /// </summary>
    private async Task<List<RegistryWrite>> UninstallerRowsAsync(string path, int maxRows, AnalysisLimits limits, List<string> notes, CancellationToken ct)
    {
        var uninstaller = Path.GetFileName(NsisScriptReader.Read(path).UninstallerPath.Trim('"'));
        if (string.IsNullOrEmpty(uninstaller) || uninstaller.Contains('$')) return [];
        var exe = _findSevenZip();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            notes.Add("The uninstaller's registry changes are read with 7-Zip, which is not installed.");
            return [];
        }
        string session;
        try { session = _sandbox.NewSession(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallerAnalysisException) { return []; }
        try
        {
            var payload = await ListPayloadCoreAsync(path, "NSIS", limits, ct).ConfigureAwait(false);
            var entry = payload.FirstOrDefault(e => !e.IsDirectory && string.Equals(Path.GetFileName(e.Path.Replace('/', '\\')), uninstaller, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return [];
            var extracted = await ExtractEntryCoreAsync(path, "NSIS", entry, session, limits, ct).ConfigureAwait(false);
            var (rows, note) = NsisRegistryReader.Read(extracted, maxRows, "Uninstaller", ct);
            if (rows.Count == 0 && note.Length > 0) notes.Add("Uninstaller: " + note);
            return rows;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            notes.Add("The uninstaller's registry changes could not be read.");
            return [];
        }
        finally
        {
            try { _sandbox.Release(session); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallerAnalysisException) { }
        }
    }

    public async Task<IReadOnlyList<InterestingString>> ReadStringsAsync(string path, AnalysisLimits limits, CancellationToken ct)
    {
        var full = CheckFile(path, limits);
        return await Bounded(token => Task.FromResult(InstallerStringScanner.Scan(full, limits.MinStringLength, limits.MaxStrings, limits.MaxFileBytes, token)),
            limits, ct, "The string scan stopped at the time limit.", "The strings could not be read from the file.").ConfigureAwait(false);
    }
}
