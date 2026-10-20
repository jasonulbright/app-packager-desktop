using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.InstallerScripts;
using ApplicationPackagerEngine.Services.Signing;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Reads an installer offline and never runs it or anything extracted from it. The deployment
/// proposal comes from <see cref="DroppedInstallerAnalysisService"/>; this service adds the full
/// MSI tables, the format metadata, the payload, the registry changes and the strings. Every read
/// is bounded by <see cref="AnalysisLimits"/>, every failure is a plain message or a plain note,
/// and extraction only writes to a sandbox session or to a folder the user chose.
/// </summary>
public sealed partial class InstallerAnalysisService : IInstallerAnalysisService
{
    private const string ArpHklm64 = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ArpHklm32 = @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ArpHkcu = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly HashSet<string> ZipEngines = new(StringComparer.Ordinal)
    {
        "MSIX", "MsixBundle", "NuGet", "Chocolatey", "Intunewin", "PsadtV3", "PsadtV4", "ZIP"
    };

    private readonly IAnalysisSandbox _sandbox;
    private readonly Func<string?> _findSevenZip;
    private readonly IMsiInspector _msi = new NativeMsiInspector();

    /// <param name="findSevenZip">Locates 7z.exe; null when 7-Zip is not installed.</param>
    public InstallerAnalysisService(IAnalysisSandbox sandbox, Func<string?>? findSevenZip = null)
    {
        _sandbox = sandbox;
        _findSevenZip = findSevenZip ?? ArchiveExtractionService.FindSevenZip;
    }

    public static string TypeDisplayName(string engine) => engine switch
    {
        "NativeMSI" => "MSI",
        "MSP" => "MSP (Windows Installer patch)",
        "InnoSetup" => "Inno Setup",
        "WixBurn" => "WiX Burn bundle",
        "AdvancedInstaller" => "Advanced Installer",
        "SevenZipSfx" => "7-Zip self-extracting archive",
        "WinRarSfx" => "WinRAR self-extracting archive",
        "MSIX" => "MSIX / APPX package",
        "MsixBundle" => "MSIX / APPX bundle",
        "Intunewin" => "Intune Win32 package (.intunewin)",
        "PsadtV3" => "PSAppDeployToolkit 3",
        "PsadtV4" => "PSAppDeployToolkit 4",
        "NuGet" => "NuGet package",
        "Chocolatey" => "Chocolatey package",
        "ZIP" => "Zip archive",
        _ => engine
    };

    public Task<string> ComputeSha256Async(string path, CancellationToken ct) =>
        Task.Run(() =>
        {
            var full = CheckFile(path, null);
            try
            {
                return Sha256(full, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InstallerAnalysisException("The file could not be read to compute its SHA-256; another program may hold it open.", ex);
            }
        }, ct);

    public async Task<AnalysisReport> AnalyzeAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct)
    {
        var full = CheckFile(path, limits);
        return await Bounded(token => AnalyzeCoreAsync(full, limits, progress, token), limits, ct,
            "The analysis stopped at the time limit.", "The file could not be analyzed; it may be damaged or not an installer.").ConfigureAwait(false);
    }

    private async Task<AnalysisReport> AnalyzeCoreAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct)
    {
        var notes = new List<string>();
        var metadata = new List<MetadataBlock>();
        var fileName = Path.GetFileName(path);
        var size = new FileInfo(path).Length;

        progress.Report("Computing the SHA-256 hash");
        var sha = Sha256(path, ct);

        progress.Report("Checking the signature");
        ct.ThrowIfCancellationRequested();
        var signature = ReadSignature(path, notes);
        ct.ThrowIfCancellationRequested();

        progress.Report("Identifying the installer type");
        ct.ThrowIfCancellationRequested();
        var engine = Identify(path, limits, "read");
        var isPe = IsPe(path);
        var executionLevel = isPe ? Safe(() => PeResources.ReadRequestedExecutionLevel(path), "") : "";
        if (isPe) metadata.Add(VersionResourceBlock(path));

        progress.Report("Reading the deployment fields");
        DroppedInstallerAnalysis? drop = null;
        try
        {
            drop = await new DroppedInstallerAnalysisService(_msi).AnalyzeAsync(path, deepInspect: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            notes.Add("The deployment fields could not be read from this file.");
        }

        var msiProperties = new List<AnalysisFact>();
        EffectiveDetection? postPatch = null;
        var overrides = new DeploymentOverrides();
        IReadOnlyList<PayloadEntry>? payload = null;

        void AddBlock(Func<MetadataBlock> read, string failure)
        {
            ct.ThrowIfCancellationRequested();
            try { metadata.Add(read()); }
            catch (OutOfMemoryException) { notes.Add(TooLargeNote); }
            catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add(failure); }
        }

        switch (engine)
        {
            case "NativeMSI":
                progress.Report("Reading the MSI tables");
                ReadMsi(path, limits, msiProperties, metadata, notes, ct);
                break;
            case "MSP":
                progress.Report("Reading the patch metadata");
                postPatch = ReadMsp(path, [], metadata, notes, ct);
                break;
            case "NSIS":
                progress.Report("Reading the NSIS script");
                AddBlock(() => NsisBlock(path, notes, ct), "The NSIS script header could not be read.");
                break;
            case "InnoSetup":
                progress.Report("Reading the Inno Setup header");
                AddBlock(() => InnoBlock(path, notes, ct), "The Inno Setup header could not be read.");
                break;
            case "Velopack":
                AddBlock(() => VelopackBlock(path, notes), "The Velopack package inside the setup was not read.");
                break;
            case "WixBurn":
                AddBlock(() => BurnBlock(path, overrides, notes), "The WiX Burn bundle header could not be read.");
                break;
            case "BitRock":
                progress.Report("Reading the BitRock project");
                AddBlock(() => BitRockBlock(path, overrides, notes, ct), "The BitRock project could not be read.");
                break;
        }
        if (ZipEngines.Contains(engine))
        {
            progress.Report("Reading the package manifest");
            ReadZipPackage(path, engine, limits, metadata, notes, overrides, ct);
        }

        if (engine is "SevenZipSfx" or "WinRarSfx" or "ZIP" or "WixBurn" or "Unknown" or "InstallShield" or "AdvancedInstaller")
        {
            postPatch = await PostPatchFromPayloadAsync(path, engine, limits, progress, metadata, notes, list => payload = list, ct).ConfigureAwait(false);
        }

        progress.Report("Building the report");
        var deployment = DeploymentFacts(drop, engine, fileName, overrides, msiProperties, notes);
        var modes = Modes(drop, engine, fileName, msiProperties, overrides);
        var architecture = !string.IsNullOrEmpty(overrides.Architecture) ? overrides.Architecture : drop?.Architecture ?? "";
        var hint = !string.IsNullOrEmpty(overrides.DetectionHint) ? overrides.DetectionHint : DetectionHint(drop, engine, deployment);
        if (!isPe && engine is not ("NativeMSI" or "MSP") && !ZipEngines.Contains(engine) && engine != "Unknown")
            notes.Add("The file is not a Windows executable.");
        if (engine == "Unknown") notes.Add("The installer type was not recognized.");

        return new AnalysisReport(path, fileName, size, sha, engine, architecture, signature, deployment, modes, metadata,
            msiProperties, postPatch, notes.Distinct().ToList())
        {
            DetectionHint = hint,
            RequestedExecutionLevel = executionLevel,
            TypeName = TypeDisplayName(engine),
            Payload = payload
        };
    }

    /// <summary>Values a format reader knows better than the generic proposal.</summary>
    private sealed class DeploymentOverrides
    {
        public string DisplayName = "";
        public string DisplayVersion = "";
        public string Publisher = "";
        public string ProductCode = "";
        public string ArpKey = "";
        public string ArpKeySource = "";
        public string InstallCommand = "";
        public string UninstallCommand = "";
        public string Architecture = "";
        public string DetectionHint = "";
        public string Source = "";
        public string InstallContext = "";
        public string Notes = "";

        /// <summary>The one install mode the format reader resolved; null when the generic proposal supplies the modes.</summary>
        public InstallMode? Mode;

        /// <summary>Deployment field names whose override value is a prediction.</summary>
        public readonly HashSet<string> PredictedFields = new(StringComparer.Ordinal);

        /// <summary>True when the reader found the values a command is built from invalid; no command is proposed.</summary>
        public bool CommandsRefused;
    }

    // Without ALLUSERS, Windows Installer writes the uninstall key under HKLM but registers the
    // product for the installing account only (UserData of that SID, AssignmentType 0).
    private const string InstallingAccountContext = "ARP key under HKLM; the product is registered for the installing account only (no ALLUSERS property)";
    private const string InstallingAccountNote = "The MSI has no ALLUSERS property: detection by the uninstall key under HKLM is right, but per-user shortcuts and files land in the installing account's profile (the SYSTEM profile when the deployment runs as SYSTEM). Add ALLUSERS=1 to the install command for a per-machine install.";

    private static bool NoAllUsers(List<AnalysisFact> msiProperties) =>
        msiProperties.Count > 0 && !msiProperties.Any(p => p.Name == "ALLUSERS")
        && msiProperties.FirstOrDefault(p => p.Name == "MSIINSTALLPERUSER")?.Value != "1";

    private const string UnsafeCommandNote = "A proposed command line was dropped: text read from the installer holds characters that a command line cannot carry safely.";
    private const string ProductCodeNote = "The ProductCode is not a GUID; no msiexec uninstall command is proposed.";

    /// <summary>
    /// Builds the deployment fields. Commands a format reader generates (overrides) are Predicted and
    /// their inputs were validated by that reader; every other command passes
    /// <see cref="ProposedCommand.IsSafeCommandLine"/> or is dropped with a note.
    /// </summary>
    private List<AnalysisFact> DeploymentFacts(DroppedInstallerAnalysis? drop, string engine, string fileName, DeploymentOverrides o, List<AnalysisFact> msiProperties, List<string> notes)
    {
        string Checked(string command)
        {
            if (command.Length == 0 || ProposedCommand.IsSafeCommandLine(command)) return command;
            notes.Add(UnsafeCommandNote);
            return "";
        }

        var facts = new List<AnalysisFact>();
        AnalysisFact From(string name, ProposedField? field, string overrideValue = "", string overrideSource = "")
        {
            if (!string.IsNullOrEmpty(overrideValue)) return new AnalysisFact(name, overrideValue, o.PredictedFields.Contains(name) ? "Predicted" : "Confirmed", overrideSource);
            if (field is null || !field.HasValue) return new AnalysisFact(name, "", "None", field?.Source ?? "none");
            return new AnalysisFact(name, field.Value, field.Confidence.ToString(), field.Source);
        }

        facts.Add(From("DisplayName", drop?.AppName, o.DisplayName, o.Source));
        facts.Add(From("DisplayVersion", drop?.SoftwareVersion, o.DisplayVersion, o.Source));
        facts.Add(From("Publisher", drop?.Publisher, o.Publisher, o.Source));
        facts.Add(From("ProductCode", drop?.ProductCode, o.ProductCode, o.Source));
        var upgrade = msiProperties.FirstOrDefault(p => p.Name == "UpgradeCode");
        if (upgrade is not null) facts.Add(new AnalysisFact("UpgradeCode", upgrade.Value, "Confirmed", "msi-property-table"));

        var install = o.InstallCommand;
        var installSource = o.Source;
        var installConfidence = "Predicted";
        if (string.IsNullOrEmpty(install) && drop is not null && !o.CommandsRefused)
        {
            installSource = drop.InstallArgs.Source;
            installConfidence = drop.InstallArgs.Confidence.ToString();
            install = Checked(engine is "NativeMSI" or "MSP" || string.IsNullOrWhiteSpace(drop.InstallArgs.Value)
                ? drop.InstallCommand
                : "\"" + fileName + "\" " + drop.InstallArgs.Value);
        }
        facts.Add(new AnalysisFact("InstallCommand", install ?? "", string.IsNullOrEmpty(install) ? "None" : installConfidence, installSource));

        string uninstall;
        string uninstallConfidence;
        string uninstallSource;
        if (!string.IsNullOrEmpty(o.UninstallCommand)) { uninstall = o.UninstallCommand; uninstallConfidence = "Predicted"; uninstallSource = o.Source; }
        else if (o.CommandsRefused) { uninstall = ""; uninstallConfidence = "None"; uninstallSource = o.Source; }
        else if (engine == "NativeMSI" && drop is { ProductCode.HasValue: true })
        {
            var valid = ProposedCommand.IsProductCode(drop.ProductCode.Value);
            if (!valid) notes.Add(ProductCodeNote);
            uninstall = valid ? $"msiexec.exe /x \"{drop.ProductCode.Value}\" /qn /norestart" : "";
            uninstallConfidence = "Confirmed";
            uninstallSource = "msi-property-table";
        }
        else
        {
            uninstall = Checked(drop?.UninstallCommand.Value ?? "");
            uninstallConfidence = drop?.UninstallCommand.Confidence.ToString() ?? "None";
            uninstallSource = drop?.UninstallCommand.Source ?? "none";
        }
        facts.Add(new AnalysisFact("UninstallCommand", uninstall, string.IsNullOrEmpty(uninstall) ? "None" : uninstallConfidence, uninstallSource));

        if (!string.IsNullOrEmpty(o.ArpKey))
            facts.Add(new AnalysisFact("UninstallRegistryKey", o.ArpKey, o.ArpKeySource == "engine-convention" ? "Predicted" : "Confirmed", o.ArpKeySource));
        else
            facts.Add(From("UninstallRegistryKey", drop?.UninstallRegistryKey));
        if (o.InstallContext.Length > 0)
            facts.Add(new AnalysisFact("InstallContext", o.InstallContext, o.ArpKeySource == "engine-convention" ? "Predicted" : "Confirmed", o.ArpKeySource));
        else if (engine == "NativeMSI" && drop is { InstallContext: "Machine" } && NoAllUsers(msiProperties))
        {
            facts.Add(new AnalysisFact("InstallContext", InstallingAccountContext, "Confirmed", "msi-property-table"));
            notes.Add(InstallingAccountNote);
        }
        else
            facts.Add(new AnalysisFact("InstallContext", drop is null ? "" : drop.InstallContext == "User" ? "PerUser" : "PerMachine",
                drop is null ? "None" : "Predicted", drop is null ? "none" : "analysis"));
        if (o.Notes.Length > 0)
            facts.Add(new AnalysisFact("Notes", o.Notes, "Predicted", o.Source));
        else if (drop is not null && !string.IsNullOrWhiteSpace(drop.Notes))
            facts.Add(new AnalysisFact("Notes", drop.Notes, "Predicted", "analysis"));
        return facts;
    }

    private static List<InstallMode> Modes(DroppedInstallerAnalysis? drop, string engine, string fileName, List<AnalysisFact> msiProperties, DeploymentOverrides o)
    {
        var modes = new List<InstallMode>();
        if (o.Mode is not null)
        {
            modes.Add(o.Mode);
            return modes;
        }
        if (drop is null) return modes;
        static string Checked(string command) => ProposedCommand.IsSafeCommandLine(command) ? command : "";
        foreach (var name in drop.InstallModes)
        {
            if (!drop.ModeBranches.TryGetValue(name, out var b)) continue;
            var isDefault = string.Equals(name, drop.DefaultInstallMode, StringComparison.OrdinalIgnoreCase);
            var modeSwitch = Regex.Match(b.InstallArgs, @"(?i)(/allusers|/currentuser)").Value;
            modes.Add(new InstallMode(name == "AllUsers" ? "PerMachine" : "PerUser", modeSwitch, b.InstallDirWindows, Checked(b.SilentUninstallCommand),
                b.UninstallRegistryKey, b.RegistryHive, b.InstallContext)
            {
                IsDefault = isDefault,
                View = b.RegistryView,
                InstallCommand = Checked("\"" + fileName + "\" " + b.InstallArgs)
            });
        }
        if (modes.Count == 0 && engine == "NativeMSI" && drop.ProductCode.HasValue && ProposedCommand.IsProductCode(drop.ProductCode.Value))
        {
            var perUser = drop.InstallContext == "User";
            var key = drop.UninstallRegistryKey.Value;
            var context = perUser ? "PerUser" : NoAllUsers(msiProperties) ? InstallingAccountContext : "PerMachine";
            modes.Add(new InstallMode(perUser ? "PerUser" : "PerMachine", "", "", $"msiexec.exe /x \"{drop.ProductCode.Value}\" /qn /norestart",
                key, perUser ? "HKCU" : "HKLM", context)
            {
                IsDefault = true,
                View = perUser ? "" : key.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? "32" : "64",
                InstallCommand = drop.InstallCommand
            });
            var allUsers = msiProperties.FirstOrDefault(p => p.Name == "ALLUSERS")?.Value;
            if (allUsers == "2" && !perUser)
            {
                var userKey = ArpHkcu + "\\" + drop.ProductCode.Value;
                modes.Add(new InstallMode("PerUser", "ALLUSERS=2 MSIINSTALLPERUSER=1", "", $"msiexec.exe /x \"{drop.ProductCode.Value}\" /qn /norestart",
                    userKey, "HKCU", "PerUser")
                {
                    InstallCommand = $"msiexec.exe /i \"{fileName}\" ALLUSERS=2 MSIINSTALLPERUSER=1 /qn /norestart"
                });
            }
        }
        return modes;
    }

    private static string DetectionHint(DroppedInstallerAnalysis? drop, string engine, List<AnalysisFact> deployment)
    {
        var key = deployment.FirstOrDefault(f => f.Name == "UninstallRegistryKey")?.Value ?? "";
        if (engine is "NSIS" or "InnoSetup" or "WixBurn" or "Squirrel" or "Velopack" && key.Length > 0)
        {
            var context = key.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? " Per-user: evaluate in the user context."
                : key.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase) ? " The key is in the 32-bit registry view." : "";
            return $"Registry key {key}, value DisplayVersion.{context}";
        }
        return drop?.DetectionHint ?? "";
    }

    // ---- shared helpers ------------------------------------------------------------------

    private static readonly HashSet<string> ZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".nupkg", ".msix", ".appx", ".msixbundle", ".appxbundle", ".intunewin"
    };

    /// <summary>
    /// The engine name. A zip-based file is first checked against the entry and central
    /// directory limits, so no reader loads an oversized central directory.
    /// </summary>
    private static string Identify(string path, AnalysisLimits limits, string verb)
    {
        if (ZipExtensions.Contains(Path.GetExtension(path))) ZipCentralDirectory.Check(path, limits.MaxEntries, verb);
        return InstallerSignatureScanner.Identify(path);
    }

    private static string CheckFile(string path, AnalysisLimits? limits)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InstallerAnalysisException("No file was given.");
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InstallerAnalysisException("The path is not a valid file path.");
        }
        if (Directory.Exists(full)) throw new InstallerAnalysisException("The path is a folder; open one installer file.");
        if (!File.Exists(full)) throw new InstallerAnalysisException("The file does not exist.");
        long length;
        try { length = new FileInfo(full).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallerAnalysisException("The file could not be read.");
        }
        if (limits is not null && length > limits.MaxFileBytes)
            throw new InstallerAnalysisException($"The file is larger than the analysis limit of {FormatBytes(limits.MaxFileBytes)}.");
        if (length == 0) throw new InstallerAnalysisException("The file is empty.");
        return full;
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the time limit and translates every failure into a
    /// plain <see cref="InstallerAnalysisException"/>; only the caller's own cancel passes through.
    /// </summary>
    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> work, AnalysisLimits limits, CancellationToken ct, string timeoutMessage, string failureMessage)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(limits.Timeout);
        var task = Task.Run(() => work(bounded.Token), bounded.Token);
        try
        {
            return await task.WaitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Settle(task).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await Settle(task).ConfigureAwait(false);
            throw new InstallerAnalysisException(timeoutMessage);
        }
        catch (InstallerAnalysisException)
        {
            throw;
        }
        catch (OutOfMemoryException ex)
        {
            throw new InstallerAnalysisException(TooLargeNote, ex);
        }
        catch (Exception ex)
        {
            throw new InstallerAnalysisException(failureMessage, ex);
        }
    }

    /// <summary>The longest wait after a cancel for the work to observe its token; a native call that takes no token can outlast it.</summary>
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(10);

    private static async Task Settle(Task task)
    {
        try { await task.WaitAsync(CancelGrace).ConfigureAwait(false); }
        catch (Exception) { }
    }

    internal const string TooLargeNote = "A part of the file is too large to read in memory; it may be damaged.";

    private static string Sha256(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static SignatureFact ReadSignature(string path, List<string> notes)
    {
        try
        {
            var (error, signer, timestamper) = AuthenticodeNative.VerifyFile(path, cacheOnly: true);
            using (signer)
            using (timestamper)
            {
                // TRUST_E_SUBJECT_FORM_UNKNOWN: no signature format is registered for this file type.
                var status = error == 0x800B0003 ? "NotSupportedFileFormat" : AuthenticodeVerifier.StatusText(unchecked((int)error));
                return new SignatureFact(status, signer?.Subject ?? "", signer?.Issuer ?? "",
                    signer?.Thumbprint ?? "", timestamper is not null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add("The signature could not be checked.");
            return new SignatureFact("Unknown", "", "", "", false);
        }
    }

    private static bool IsPe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return stream.Length >= 64 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (IOException) { return false; }
    }

    private static MetadataBlock VersionResourceBlock(string path)
    {
        var facts = new List<AnalysisFact>();
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            void Add(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) facts.Add(new AnalysisFact(name, value.Trim(), "Confirmed", "pe-version-info"));
            }
            Add("ProductName", info.ProductName);
            Add("ProductVersion", info.ProductVersion);
            Add("FileVersion", info.FileVersion);
            Add("CompanyName", info.CompanyName);
            Add("FileDescription", info.FileDescription);
            Add("OriginalFilename", info.OriginalFilename);
            Add("LegalCopyright", info.LegalCopyright);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        facts.Add(new AnalysisFact("PE architecture", InstallerSignatureScanner.ReadPeArchitecture(path), "Confirmed", "pe-header"));
        return new MetadataBlock("Version resource", facts);
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { return fallback; }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes"
    };

    private static AnalysisFact Fact(string name, string? value, string source, string confidence = "Confirmed") =>
        new(name, value ?? "", string.IsNullOrEmpty(value) ? "None" : confidence, source);
}
