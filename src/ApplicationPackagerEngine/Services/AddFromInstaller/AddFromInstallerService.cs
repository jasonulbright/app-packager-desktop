using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.AddFromInstaller;

/// <summary>
/// The one analysis Add from file and Add from web call. The Installer Analysis service reads the file (bounded,
/// sandboxed, off the calling thread, with the BitRock project and Burn bundle readers); this service turns its
/// report into a proposal with an evidence level per value and writes the reviewed proposal as a user definition.
/// </summary>
public sealed class AddFromInstallerService : IAddFromInstallerService
{
    public const string NoSilentSwitchReason = "No silent switch could be derived.";

    public const string NoUninstallReason =
        "No silent uninstall could be derived. The generated uninstall script uninstalls nothing and exits with code 1. Enter the uninstall command or the silent uninstall arguments before you deploy.";
    public const string DefaultDetectionReason = "Detection needs review: no detection rule could be derived from the installer.";
    public const string PatchesExSource = "https://learn.microsoft.com/windows/win32/api/msi/nf-msi-msienumpatchesexw";
    public const string UninstallKeySource = "https://learn.microsoft.com/windows/win32/msi/uninstall-registry-key";
    public const string RegistryViewSource = "https://learn.microsoft.com/windows/win32/winprog64/registry-redirector";
    public const string AddAppxPackageSource = "https://learn.microsoft.com/powershell/module/appx/add-appxpackage";
    public const string PatchXmlSource = "https://learn.microsoft.com/windows/win32/msi/extracting-patch-information-as-xml";
    public const string RemoveProvisionedPackageSource = "https://learn.microsoft.com/powershell/module/dism/remove-appxprovisionedpackage";

    private const string ArpRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int ManifestLimitBytes = 4 * 1024 * 1024;

    /// <summary>The folder under the download root that holds one folder of eight hex digits per fetched installer.</summary>
    private const string FetchFolderName = "AddFromWeb";

    /// <summary>The folders FetchInstallerAsync returned and DiscardFetched has not deleted; only these are deleted.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _fetchedFolders = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Accepted = [".msi", ".exe", ".msp", ".msix", ".appx", ".msixbundle", ".appxbundle"];

    private readonly IInstallerAnalysisService _analysis;
    private readonly DroppedInstallerAnalysisService _deployment;
    private readonly AnalysisLimits _limits;
    private readonly DownloadService _downloads;
    /// <summary>The package reader; a test sets one that builds chains against its own root.</summary>
    internal MsixPackageReader Msix { get; set; } = new();

    public AddFromInstallerService(IInstallerAnalysisService analysis, DroppedInstallerAnalysisService deployment,
        AnalysisLimits? limits = null, DownloadService? downloads = null)
    {
        _analysis = analysis;
        _deployment = deployment;
        _limits = limits ?? AnalysisLimits.Default;
        _downloads = downloads ?? new DownloadService();
    }

    public IReadOnlyList<string> AcceptedExtensions => Accepted;

    public string? RefusalReason(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath)) return "No file was given.";
        if (Directory.Exists(installerPath)) return "The path is a folder. Add from file takes one installer file.";
        var extension = Path.GetExtension(installerPath).ToLowerInvariant();
        if (Accepted.Contains(extension)) return null;
        return (extension.Length == 0 ? "The file has no extension." : $"Add from file does not take {extension} files.")
            + " Use an .msi, .exe, .msp, .msix, .appx, .msixbundle or .appxbundle file.";
    }

    public async Task<AddAnalysisResult> AnalyzeAsync(string installerPath, IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (RefusalReason(installerPath) is { } reason) throw new AddFromInstallerRefusedException(reason);
        var report = await _analysis.AnalyzeAsync(installerPath, _limits, progress, ct).ConfigureAwait(false);
        var local = report.DeploymentProposal;
        if (local is null || report.Type is "NSIS" or "InnoSetup" or "WixBurn")
        {
            progress.Report("Reading the deployment fields");
            local = await LocalProposalAsync(report.Path, deep: report.Type is "NSIS" or "InnoSetup" or "WixBurn", ct).ConfigureAwait(false) ?? local;
        }
        return await Task.Run(() => Map(report, local), ct).ConfigureAwait(false);
    }

    /// <summary>The local proposal under the analysis time limit; null when it could not be read.</summary>
    private async Task<DroppedInstallerAnalysis?> LocalProposalAsync(string path, bool deep, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_limits.Timeout);
        try
        {
            return await Task.Run(() => _deployment.AnalyzeLocalAsync(path, deep, bounded.Token), bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- mapping ---------------------------------------------------------------------------

    internal AddAnalysisResult Map(AnalysisReport report, DroppedInstallerAnalysis? local)
    {
        var engine = report.Type;
        var notes = new List<string>(report.Notes);
        if (engine == "MSP") return MapPatch(report, local, notes);
        if (engine is "MSIX" or "MsixBundle") return MapPackage(report, local, notes);
        if (local is null) throw new InstallerAnalysisException("The deployment fields could not be read from this file.");

        var drop = DroppedInstallerAnalysisService.Overlay(local, OverlayFrom(report, local));
        if (!string.IsNullOrWhiteSpace(drop.Notes)) notes.Add(drop.Notes);

        var isMsi = drop.InstallerType == "MSI";
        var noSwitch = !isMsi && (!drop.InstallArgs.HasValue || engine is "Unknown" or "SevenZipSfx" or "WinRarSfx");
        var install = noSwitch
            ? new ProposedValue<string>(Value: "", Evidence: Evidence.None, Source: null)
            : FromField(drop.InstallArgs, engine);
        var uninstall = drop.UninstallArgs.HasValue ? FromField(drop.UninstallArgs, engine) : new ProposedValue<string>(Value: "", Evidence: Evidence.None, Source: null);
        if (isMsi && drop.ProductCode.HasValue)
            uninstall = new ProposedValue<string>(Value: $"/x {drop.ProductCode.Value} {drop.UninstallArgs.Value}".Trim(), Evidence: Evidence.Confirmed, Source: "msi-property-table");

        var name = Optional(drop.AppName);
        var version = Optional(drop.SoftwareVersion);
        var detection = ProposeDetection(drop, isMsi, version);
        var reasons = new List<string>();
        if (noSwitch) reasons.Add(NoSilentSwitchReason);
        if (!isMsi && !HasUninstall(uninstall.Value, drop.UninstallCommand.HasValue ? drop.UninstallCommand.Value : null)) reasons.Add(NoUninstallReason);
        AddCriticalReasons(reasons, name, version, noSwitch ? null : install, detection);

        return new AddAnalysisResult(
            FilePath: report.Path,
            InstallerType: drop.InstallerType,
            Framework: FrameworkValue(engine),
            Vendor: Optional(drop.Publisher),
            Name: name,
            Version: version,
            InstallArguments: install,
            UninstallArguments: uninstall,
            InstallContext: new ProposedValue<string>(Value: drop.InstallContext, Evidence: ContextEvidence(report, drop), Source: ContextSource(report)),
            Detection: detection,
            NeedsReview: reasons.Count > 0,
            ReviewReason: reasons.Count > 0 ? string.Join(" ", reasons.Distinct()) : null,
            Notes: notes.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList())
        {
            InstallerTypeName = report.TypeName,
            Architecture = Architecture(report, drop.Architecture),
            UninstallCommand = drop.UninstallCommand.HasValue ? FromField(drop.UninstallCommand, engine) : null,
            UninstallRegistryKey = drop.UninstallRegistryKey.HasValue ? FromField(drop.UninstallRegistryKey, engine) : null,
            ProductCode = drop.ProductCode.HasValue ? Optional(drop.ProductCode) : null,
            InstallModes = drop.InstallModes,
            DefaultInstallMode = drop.DefaultInstallMode.Length > 0 ? drop.DefaultInstallMode : null,
            Sha256 = report.Sha256,
            FileSize = report.Size,
            Deployment = drop
        };
    }

    /// <summary>The values the Installer Analysis format readers read better than the generic proposal.</summary>
    private static DeploymentOverlay OverlayFrom(AnalysisReport report, DroppedInstallerAnalysis local)
    {
        ProposedField? Better(string name, ProposedField current)
        {
            var fact = report.Deployment.FirstOrDefault(f => f.Name == name);
            if (fact is null || fact.Value.Length == 0 || fact.Value == current.Value) return null;
            return fact.Confidence == "Confirmed" ? ProposedField.Confirmed(fact.Value, fact.Source) : ProposedField.Predicted(fact.Value, fact.Source);
        }

        var isMsi = local.InstallerType == "MSI";
        var key = isMsi ? null : Better("UninstallRegistryKey", local.UninstallRegistryKey);
        var context = report.Deployment.FirstOrDefault(f => f.Name == "InstallContext")?.Value;
        var bitRockMode = report.Type == "BitRock" ? report.Modes.FirstOrDefault(m => m.IsDefault) : null;
        ProposedField? installArgs = null;
        if (bitRockMode is { Switch.Length: > 0 })
        {
            var handled = report.Metadata.SelectMany(b => b.Facts).FirstOrDefault(f => f.Name == "Unattended mode")?.Confidence == "Confirmed";
            installArgs = handled ? ProposedField.Confirmed(bitRockMode.Switch, "bitrock-project") : ProposedField.Predicted(bitRockMode.Switch, "switch-table:BitRock");
        }
        return new DeploymentOverlay
        {
            AppName = Better("DisplayName", local.AppName),
            Publisher = Better("Publisher", local.Publisher),
            SoftwareVersion = Better("DisplayVersion", local.SoftwareVersion),
            UninstallCommand = isMsi ? null : Better("UninstallCommand", local.UninstallCommand),
            UninstallRegistryKey = key,
            InstallArgs = installArgs,
            InstallContext = key is null ? null : context == "PerUser" ? "User" : context is null ? null : "Machine",
            Architecture = string.IsNullOrEmpty(report.Architecture) ? null : report.Architecture,
            DetectionHint = string.IsNullOrEmpty(report.DetectionHint) ? null : report.DetectionHint
        };
    }

    private static ProposedValue<string> FrameworkValue(string engine) => engine == "Unknown"
        ? new ProposedValue<string>(Value: engine, Evidence: Evidence.None, Source: null)
        : new ProposedValue<string>(Value: engine, Evidence: Evidence.Confirmed, Source: "installer-signature");

    /// <summary>
    /// The MSI Template names the platform of the product. The PE header of an EXE names the platform of the installer
    /// program only: a 32-bit installer can install a 64-bit product, so that value is predicted.
    /// </summary>
    private static ProposedValue<string>? Architecture(AnalysisReport report, string architecture) =>
        string.IsNullOrEmpty(architecture) ? null : report.Type == "NativeMSI"
            ? new ProposedValue<string>(Value: architecture, Evidence: Evidence.Confirmed, Source: "msi-summary-information")
            : new ProposedValue<string>(Value: architecture, Evidence: Evidence.Predicted, Source: "pe-header: the platform of the installer program");

    private static Evidence ContextEvidence(AnalysisReport report, DroppedInstallerAnalysis drop)
    {
        var fact = report.Deployment.FirstOrDefault(f => f.Name == "InstallContext");
        return fact is null ? Evidence.Predicted : ToEvidence(fact.Confidence);
    }

    private static string? ContextSource(AnalysisReport report) =>
        report.Deployment.FirstOrDefault(f => f.Name == "InstallContext")?.Source is { Length: > 0 } source ? source : "analysis";

    internal static Evidence ToEvidence(string? confidence) => confidence switch
    {
        "Confirmed" => Evidence.Confirmed,
        "Predicted" => Evidence.Predicted,
        _ => Evidence.None
    };

    private static ProposedValue<string?> Optional(ProposedField field) => !field.HasValue
        ? new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null)
        : new ProposedValue<string?>(Value: field.Value, Evidence: field.Confidence == AnalysisConfidence.Confirmed ? Evidence.Confirmed : Evidence.Predicted, Source: field.Source);

    /// <summary>A table value carries the documentation URL of its table entry as its source when the entry has one.</summary>
    private static ProposedValue<string> FromField(ProposedField field, string engine)
    {
        var source = field.Source;
        if (source.StartsWith("switch-table:", StringComparison.Ordinal) && SilentSwitchDatabase.Lookup(engine).Source is { Length: > 0 } documented)
            source = documented;
        return new ProposedValue<string>(Value: field.Value, Evidence: field.Confidence == AnalysisConfidence.Confirmed ? Evidence.Confirmed : Evidence.Predicted, Source: source);
    }

    /// <summary>A source that names neither the file structure nor documentation: a bare table or an engine convention.</summary>
    internal static bool IsUndocumented(string? source) =>
        string.IsNullOrWhiteSpace(source)
        || source.StartsWith("switch-table", StringComparison.Ordinal)
        || source.StartsWith("engine-convention", StringComparison.Ordinal);

    /// <summary>
    /// True when the generated EXE uninstall has something to run: an uninstall command, or silent uninstall
    /// arguments for the uninstaller of the Add/Remove Programs entry.
    /// </summary>
    private static bool HasUninstall(string? uninstallArguments, string? uninstallCommand) =>
        !string.IsNullOrWhiteSpace(uninstallArguments) || !string.IsNullOrWhiteSpace(uninstallCommand);

    private static void AddCriticalReasons(List<string> reasons, ProposedValue<string?> name, ProposedValue<string?> version,
        ProposedValue<string>? install, DetectionProposal detection)
    {
        if (name.Evidence == Evidence.None) reasons.Add("The application name could not be read.");
        else if (name.Evidence == Evidence.Predicted && IsUndocumented(name.Source)) reasons.Add("The application name is predicted, not read from the installer.");
        if (version.Evidence == Evidence.None) reasons.Add("The version could not be read.");
        else if (version.Evidence == Evidence.Predicted && IsUndocumented(version.Source)) reasons.Add("The version is predicted, not read from the installer.");
        if (install is { Evidence: Evidence.Predicted } && IsUndocumented(install.Source))
            reasons.Add("The silent switches come from a table with no documented source; test them before you deploy.");
        if (detection.IsDefault) reasons.Add(DefaultDetectionReason);
        else if (detection.Evidence == Evidence.Predicted)
            reasons.Add(IsUndocumented(detection.Source)
                ? "Detection needs review: the uninstall key is predicted from the installer type, not read from the installer."
                : "Detection needs review: the rule is predicted, not read from the installer.");
    }

    // ---- detection -------------------------------------------------------------------------

    private static DetectionProposal ProposeDetection(DroppedInstallerAnalysis drop, bool isMsi, ProposedValue<string?> version)
    {
        var d = drop.Detection;
        if (d.Type != "RegistryKeyValue" || string.IsNullOrWhiteSpace(d.RegistryKeyRelative))
            return DetectionProposal.NoneDerived("No detection rule could be derived from the installer. Enter a rule before you deploy.");

        var hive = d.IsCurrentUserHive ? "HKCU" : "HKLM";
        var view = d.Is64Bit ? "64-bit" : "32-bit";
        var keyField = drop.UninstallRegistryKey;
        var keyEvidence = isMsi ? Evidence.Confirmed : keyField.HasValue
            ? (keyField.Confidence == AnalysisConfidence.Confirmed ? Evidence.Confirmed : Evidence.Predicted)
            : Evidence.Predicted;
        var keySource = isMsi ? "msi-property-table" : keyField.HasValue ? keyField.Source : "engine-convention:" + drop.Engine;
        var values = new Dictionary<string, string>
        {
            ["Hive"] = hive,
            ["Key"] = d.RegistryKeyRelative,
            ["Is64Bit"] = d.Is64Bit ? "true" : "false"
        };

        if (isMsi && drop.ProductCode.HasValue)
        {
            values["ProductCode"] = drop.ProductCode.Value;
            values["ValueName"] = "DisplayVersion";
            values["Operator"] = "IsEquals";
            values["Expected"] = version.Value ?? "";
            return new DetectionProposal(Kind: DetectionKind.MsiProductCode,
                Description: $"The Add/Remove Programs key of ProductCode {drop.ProductCode.Value} in {hive} ({view} view), DisplayVersion equals {version.Value}.",
                Values: values, Evidence: Evidence.Confirmed, Source: "msi-property-table");
        }

        if (string.IsNullOrWhiteSpace(version.Value))
        {
            return new DetectionProposal(Kind: DetectionKind.RegistryExists,
                Description: $@"The key {hive}\{d.RegistryKeyRelative} ({view} view) exists.",
                Values: values, Evidence: keyEvidence, Source: keySource);
        }

        values["ValueName"] = d.ValueName ?? "DisplayVersion";
        values["Operator"] = d.Operator ?? "IsEquals";
        values["Expected"] = version.Value!;
        var evidence = keyEvidence == Evidence.Confirmed && version.Evidence == Evidence.Confirmed ? Evidence.Confirmed : Evidence.Predicted;
        return new DetectionProposal(Kind: DetectionKind.RegistryVersion,
            Description: $@"The value {values["ValueName"]} of {hive}\{d.RegistryKeyRelative} ({view} view) equals {version.Value}.",
            Values: values, Evidence: evidence, Source: keySource);
    }

    /// <summary>Two or more registry or file clauses joined by And or Or.</summary>
    public static DetectionProposal Compound(string connector, params DetectionProposal[] clauses)
    {
        if (connector is not ("And" or "Or")) throw new ArgumentException("The connector is And or Or.", nameof(connector));
        if (clauses.Length < 2) throw new ArgumentException("A compound rule has two or more clauses.", nameof(clauses));
        if (clauses.Any(c => c.Kind is DetectionKind.Compound or DetectionKind.None or DetectionKind.MsiPatchCode or DetectionKind.MsixPackage))
            throw new ArgumentException("A compound rule takes registry and file clauses only.", nameof(clauses));
        var evidence = clauses.All(c => c.Evidence == Evidence.Confirmed) ? Evidence.Confirmed
            : clauses.Any(c => c.Evidence == Evidence.Manual) ? Evidence.Manual : Evidence.Predicted;
        return new DetectionProposal(Kind: DetectionKind.Compound,
            Description: string.Join(connector == "And" ? " And " : " Or ", clauses.Select(c => "(" + c.Description + ")")),
            Values: new Dictionary<string, string> { ["Connector"] = connector },
            Evidence: evidence, Source: string.Join("; ", clauses.Select(c => c.Source).Where(s => !string.IsNullOrEmpty(s)).Distinct()))
        {
            Clauses = clauses
        };
    }

    // ---- MSI patch -------------------------------------------------------------------------

    private AddAnalysisResult MapPatch(AnalysisReport report, DroppedInstallerAnalysis? local, List<string> notes)
    {
        var facts = report.Metadata.Where(b => b.Title.StartsWith("MSI patch", StringComparison.Ordinal)).SelectMany(b => b.Facts).ToList();
        string Fact(string name) => facts.FirstOrDefault(f => f.Name == name)?.Value ?? "";
        var patchCode = Fact("Primary PatchCode");
        var targets = Fact("Target ProductCodes").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ProposedCommand.IsProductCode).ToList();
        var postPatch = report.PostPatch;
        var productCode = postPatch?.ProductCode is { Length: > 0 } pc ? pc : targets.FirstOrDefault() ?? "";
        var title = Fact("DisplayName");
        var targetName = Fact("TargetProductName");
        var postVersion = postPatch?.DisplayVersion is { Length: > 0 } v ? v : Fact("Post-patch version");
        var warning = productCode.Length > 0
            ? $"The patch applies only to an installed base product. Deploy it only to devices that have the product with ProductCode {productCode}."
            : "The patch applies only to an installed base product. The patch names no target product; deploy it only to devices that have the base product.";
        notes.Add(warning);

        var detection = PatchDetection(patchCode, productCode, postPatch, postVersion);
        var name = title.Length > 0
            ? new ProposedValue<string?>(Value: title, Evidence: Evidence.Confirmed, Source: "msi-patch-metadata")
            : targetName.Length > 0
                ? new ProposedValue<string?>(Value: targetName, Evidence: Evidence.Confirmed, Source: "msi-patch-metadata")
                : new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null);
        var vendor = Fact("ManufacturerName") is { Length: > 0 } maker
            ? new ProposedValue<string?>(Value: maker, Evidence: Evidence.Confirmed, Source: "msi-patch-metadata")
            : new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null);
        var fromXml = facts.FirstOrDefault(f => f.Name == "Post-patch version") is { Confidence: "Confirmed", Source: "msi-patch-xml" } xmlFact && xmlFact.Value == postVersion;
        var version = postVersion.Length == 0
            ? new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null)
            : fromXml
                ? new ProposedValue<string?>(Value: postVersion, Evidence: Evidence.Confirmed, Source: PatchXmlSource)
                : new ProposedValue<string?>(Value: postVersion, Evidence: Evidence.Predicted, Source: "msi-patch-metadata: the version in parentheses in the patch DisplayName");
        var switches = SilentSwitchDatabase.Lookup("MSP");
        var uninstallCommand = patchCode.Length > 0 && productCode.Length > 0 && ProposedCommand.IsProductCode(patchCode)
            ? new ProposedValue<string>(Value: $"msiexec.exe /package {productCode} /uninstall {patchCode} /qn /norestart", Evidence: Evidence.Predicted, Source: SilentSwitchDatabase.MspUninstallSource)
            : null;
        if (uninstallCommand is null) notes.Add("No uninstall command is proposed: the patch code or the target ProductCode was not read.");
        if (Fact("AllowRemoval") == "0") notes.Add("The patch is not uninstallable (AllowRemoval 0). Only an uninstall of the base product removes it.");

        var reasons = new List<string>();
        AddCriticalReasons(reasons, name, version, null, detection);

        return new AddAnalysisResult(
            FilePath: report.Path,
            InstallerType: "MSP",
            Framework: new ProposedValue<string>(Value: "MSP", Evidence: Evidence.Confirmed, Source: "installer-signature"),
            Vendor: vendor,
            Name: name,
            Version: version,
            InstallArguments: new ProposedValue<string>(Value: switches.InstallArgs ?? "", Evidence: Evidence.Predicted, Source: SilentSwitchDatabase.MspSource),
            UninstallArguments: new ProposedValue<string>(Value: switches.UninstallArgs ?? "", Evidence: Evidence.Predicted, Source: SilentSwitchDatabase.MspUninstallSource),
            InstallContext: new ProposedValue<string>(Value: "Machine", Evidence: Evidence.Predicted, Source: "msi-patch: the context of the base product"),
            Detection: detection,
            NeedsReview: reasons.Count > 0,
            ReviewReason: reasons.Count > 0 ? string.Join(" ", reasons.Distinct()) : null,
            Notes: notes.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList())
        {
            InstallerTypeName = report.TypeName,
            UninstallCommand = uninstallCommand,
            ProductCode = productCode.Length > 0 ? new ProposedValue<string?>(Value: productCode, Evidence: Evidence.Confirmed, Source: "msi-summary-information") : null,
            Sha256 = report.Sha256,
            FileSize = report.Size,
            Deployment = local,
            Patch = new PatchFacts(PatchCode: patchCode, Title: title, TargetProductCodes: targets, TargetProductName: targetName.Length > 0 ? targetName : null,
                PostPatchVersion: postVersion.Length > 0 ? postVersion : null, BaseProductWarning: warning)
            {
                Classification = Fact("Classification") is { Length: > 0 } c ? c : null,
                AllowRemoval = Fact("AllowRemoval") is { Length: > 0 } a ? a : null
            }
        };
    }

    private static DetectionProposal PatchDetection(string patchCode, string productCode, EffectiveDetection? postPatch, string postVersion)
    {
        if (ProposedCommand.IsProductCode(patchCode) && ProposedCommand.IsProductCode(productCode))
        {
            return new DetectionProposal(Kind: DetectionKind.MsiPatchCode,
                Description: $"Windows Installer reports patch {patchCode} as applied to product {productCode}.",
                Values: new Dictionary<string, string> { ["PatchCode"] = patchCode, ["ProductCode"] = productCode },
                Evidence: Evidence.Predicted, Source: PatchesExSource);
        }
        if (postPatch is { ArpKey.Length: > 0 } && postVersion.Length > 0)
        {
            var key = postPatch.ArpKey;
            var hive = key.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? "HKCU" : "HKLM";
            var relative = key[(key.IndexOf(":\\", StringComparison.Ordinal) + 2)..];
            var wow = relative.Contains(@"WOW6432Node\", StringComparison.OrdinalIgnoreCase);
            relative = relative.Replace(@"WOW6432Node\", "", StringComparison.OrdinalIgnoreCase);
            return new DetectionProposal(Kind: DetectionKind.RegistryVersion,
                Description: $@"The value DisplayVersion of {hive}\{relative} is {postVersion} or later.",
                Values: new Dictionary<string, string>
                {
                    ["Hive"] = hive, ["Key"] = relative, ["Is64Bit"] = wow ? "false" : "true",
                    ["ValueName"] = "DisplayVersion", ["Operator"] = "GreaterEquals", ["Expected"] = postVersion
                },
                Evidence: Evidence.Predicted, Source: UninstallKeySource);
        }
        return DetectionProposal.NoneDerived("No detection rule could be derived from the patch. Enter a rule before you deploy.");
    }

    // ---- MSIX and APPX ---------------------------------------------------------------------

    private AddAnalysisResult MapPackage(AnalysisReport report, DroppedInstallerAnalysis? local, List<string> notes)
    {
        var bundle = report.Type == "MsixBundle";
        var facts = report.Metadata.Where(b => b.Title is "MSIX manifest" or "MSIX bundle manifest").SelectMany(b => b.Facts).ToList();
        string Fact(string name) => facts.FirstOrDefault(f => f.Name == name)?.Value ?? "";
        var identity = Fact("Identity Name");
        var publisher = Fact("Identity Publisher");
        var version = Fact("Identity Version");
        var architecture = Fact("ProcessorArchitecture");
        var source = bundle ? "appx-bundle-manifest" : "appx-manifest";

        var (trust, subject, thumbprint) = ReadTrust(report.Path, notes);
        var trustWarning = trust switch
        {
            PackageTrust.Unsigned => "The package is not signed. Windows does not install an unsigned package. Sign the package before you deploy it.",
            PackageTrust.Invalid => "The package signature is not valid. Do not deploy this package.",
            PackageTrust.Untrusted => "This computer does not trust the certificate that signed the package. Each target device must trust the certificate, or the install fails.",
            PackageTrust.Unknown => "The package signature could not be checked.",
            PackageTrust.Expired => "The certificate that signed the package expired, and the signature has no valid timestamp from before that date. Windows does not install the package. Sign the package again before you deploy it.",
            _ => null
        };
        if (trustWarning is not null) notes.Add(trustWarning);
        var dependencies = bundle ? [] : ReadDependencies(report.Path, notes);
        if (dependencies.Count > 0)
            notes.Add("The package needs these packages on the device: " + string.Join(", ", dependencies) + ". The deployment does not install them.");

        var nameValid = ProposedCommand.IsMsixName(identity);
        var detection = nameValid
            ? new DetectionProposal(Kind: DetectionKind.MsixPackage,
                Description: $"The package {identity} is provisioned on the device.",
                Values: new Dictionary<string, string> { ["PackageName"] = identity, ["Publisher"] = publisher },
                Evidence: Evidence.Confirmed, Source: source)
            : DetectionProposal.NoneDerived("The package identity name is not valid, so no detection rule could be derived.");

        var reasons = new List<string>();
        var (architectureValue, architectureNote) = PackageArchitecture(architecture);
        if (architectureNote is not null) notes.Add(architectureNote);
        if (architectureValue is { Length: > 0 } && !IsSupportedArchitecture(architectureValue)) reasons.Add(architectureNote!);
        if (trust is not PackageTrust.Trusted) reasons.Add(trustWarning!);
        if (dependencies.Count > 0) reasons.Add("The package needs other packages that the deployment does not install.");
        var displayName = report.Deployment.FirstOrDefault(f => f.Name == "DisplayName")?.Value ?? identity;
        var name = displayName.Length > 0 ? new ProposedValue<string?>(Value: displayName, Evidence: Evidence.Confirmed, Source: source) : new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null);
        var versionValue = version.Length > 0 ? new ProposedValue<string?>(Value: version, Evidence: Evidence.Confirmed, Source: source) : new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null);
        AddCriticalReasons(reasons, name, versionValue, null, detection);
        var vendorName = report.Deployment.FirstOrDefault(f => f.Name == "Publisher")?.Value ?? publisher;

        return new AddAnalysisResult(
            FilePath: report.Path,
            InstallerType: "MSIX",
            Framework: new ProposedValue<string>(Value: report.Type, Evidence: Evidence.Confirmed, Source: "installer-signature"),
            Vendor: vendorName.Length > 0 ? new ProposedValue<string?>(Value: vendorName, Evidence: Evidence.Confirmed, Source: source) : new ProposedValue<string?>(Value: null, Evidence: Evidence.None, Source: null),
            Name: name,
            Version: versionValue,
            InstallArguments: new ProposedValue<string>(Value: "", Evidence: Evidence.Predicted, Source: SilentSwitchDatabase.MsixProvisionSource),
            UninstallArguments: new ProposedValue<string>(Value: "", Evidence: Evidence.Predicted, Source: SilentSwitchDatabase.MsixProvisionSource),
            InstallContext: new ProposedValue<string>(Value: "Machine", Evidence: Evidence.Predicted, Source: SilentSwitchDatabase.MsixProvisionSource),
            Detection: detection,
            NeedsReview: reasons.Count > 0,
            ReviewReason: reasons.Count > 0 ? string.Join(" ", reasons.Distinct()) : null,
            Notes: notes.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList())
        {
            InstallerTypeName = report.TypeName,
            Architecture = architectureValue.Length > 0 ? new ProposedValue<string>(Value: architectureValue, Evidence: Evidence.Confirmed, Source: source) : null,
            UninstallCommand = report.Deployment.FirstOrDefault(f => f.Name == "UninstallCommand") is { Value.Length: > 0 } u
                ? new ProposedValue<string>(Value: u.Value, Evidence: Evidence.Predicted, Source: RemoveProvisionedPackageSource) : null,
            ProductCode = identity.Length > 0 ? new ProposedValue<string?>(Value: identity, Evidence: Evidence.Confirmed, Source: source) : null,
            Sha256 = report.Sha256,
            FileSize = report.Size,
            Deployment = local,
            Package = new PackageFacts(IdentityName: identity, Publisher: publisher, Version: version, Architecture: architecture, Dependencies: dependencies, IsBundle: bundle, Trust: trust)
            {
                PublisherDisplayName = Fact("PublisherDisplayName") is { Length: > 0 } p ? p : null,
                PackageFamilyName = Fact("PackageFamilyName") is { Length: > 0 } f ? f : null,
                SignerSubject = subject,
                SignerThumbprint = thumbprint,
                TrustWarning = trustWarning
            }
        };
    }

    private static bool IsSupportedArchitecture(string architecture)
    {
        try { InstallerArchitectures.Resolve(architecture); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The definition architecture of a manifest architecture. A neutral package runs on every architecture and takes x64.
    /// A value outside x86, x64 and ARM64 stays as declared with a note, because the stage refuses it.
    /// </summary>
    private static (string Value, string? Note) PackageArchitecture(string declared)
    {
        var value = declared.Trim();
        if (value.Length == 0) return ("", null);
        if (value.Equals("neutral", StringComparison.OrdinalIgnoreCase))
            return (InstallerArchitectures.X64, "The package architecture is neutral. The definition uses x64.");
        return IsSupportedArchitecture(value)
            ? (InstallerArchitectures.Resolve(value), null)
            : (value, $"The package architecture {value} is not supported. App Packager supports the architectures x86, x64 and ARM64.");
    }

    private (PackageTrust Trust, string? Subject, string? Thumbprint) ReadTrust(string path, List<string> notes)
    {
        try
        {
            var signature = Msix.ReadSignature(path);
            if (signature is null) return (PackageTrust.Unsigned, null, null);
            var trust = !signature.SignatureValid ? PackageTrust.Invalid
                : signature.ChainTrusted ? PackageTrust.Trusted
                : signature.CertificateExpired ? PackageTrust.Expired
                : PackageTrust.Untrusted;
            if (trust == PackageTrust.Trusted && signature.Timestamp is { } stamped)
                notes.Add($"The signing certificate is outside its validity period now. The signature has a timestamp of {stamped:yyyy-MM-dd} inside that period, so Windows accepts the signature.");
            return (trust, signature.Subject, signature.Thumbprint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or InstallerAnalysisException)
        {
            notes.Add("The package signature could not be read.");
            return (PackageTrust.Unknown, null, null);
        }
    }

    private List<string> ReadDependencies(string path, List<string> notes)
    {
        try
        {
            using var zip = BoundedZip.Open(path, _limits.MaxEntries, "read");
            var text = zip.ReadText("AppxManifest.xml", ManifestLimitBytes);
            if (string.IsNullOrEmpty(text)) return [];
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ManifestLimitBytes };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var root = XDocument.Load(reader).Root;
            return root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Dependencies")?.Elements()
                .Where(e => e.Name.LocalName == "PackageDependency")
                .Select(e => ((string?)e.Attribute("Name") ?? "") + ((string?)e.Attribute("MinVersion") is { Length: > 0 } min ? " " + min + " or later" : ""))
                .Where(s => s.Length > 0).Take(50).ToList() ?? [];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or InstallerAnalysisException)
        {
            notes.Add("The package dependencies could not be read.");
            return [];
        }
    }

    // ---- save ------------------------------------------------------------------------------

    public Task<AddSaveResult> SaveAsync(AddAnalysisResult reviewed, AddSaveOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            var effective = WithWizardValues(reviewed, options);
            var definition = BuildDefinition(effective, options, warnings);
            if (IsLocalFileDefinition(definition)) definition.LocalInstaller = LocalInstallerOf(effective);
            var name = definition.Application;
            string target;
            try { target = UserDefinitionFiles.GetPath(options.DefinitionsFolder, name); }
            catch (InvalidOperationException ex) { throw new AddFromInstallerRefusedException(ex.Message); }
            if (File.Exists(target))
                throw new AddFromInstallerRefusedException($"A definition file named {Path.GetFileName(target)} already exists. Change the application name and save again.");
            ct.ThrowIfCancellationRequested();
            string path;
            try { path = UserDefinitionFiles.SaveNew(options.DefinitionsFolder, name, definition); }
            catch (IOException) when (File.Exists(target))
            {
                throw new AddFromInstallerRefusedException($"A definition file named {Path.GetFileName(target)} already exists. Change the application name and save again.");
            }
            var reasons = ReviewReasonsAfterEdit(effective);
            return new AddSaveResult(FilePath: path, DefinitionId: definition.Id, Warnings: warnings.Distinct().ToList())
            {
                NeedsReview = reasons.Count > 0,
                ReviewReason = reasons.Count > 0 ? string.Join(" ", reasons.Distinct()) : null
            };
        }, ct);
    }

    /// <summary>The analysed file as the stage of a definition with no download source finds it: full path, SHA-256 and reviewed version.</summary>
    private static LocalInstallerConfig LocalInstallerOf(AddAnalysisResult r)
    {
        var path = Path.GetFullPath(r.FilePath);
        if (!File.Exists(path))
            throw new AddFromInstallerRefusedException($"The installer file is not found: {path}. Open the installer file again.");
        var sha256 = StagingService.ComputeSha256(path);
        if (!string.IsNullOrWhiteSpace(r.Sha256) && !string.Equals(sha256, r.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new AddFromInstallerRefusedException($"The installer file changed after the analysis: {path}. Open the installer file again.");
        return new LocalInstallerConfig { Path = path, Sha256 = sha256, Version = r.Version.Value?.Trim() ?? "" };
    }

    /// <summary>The review state of the saved values: a value the user entered counts as reviewed.</summary>
    private static List<string> ReviewReasonsAfterEdit(AddAnalysisResult r)
    {
        var reasons = new List<string>();
        if (r.InstallerType is not ("MSI" or "MSIX" or "MSP") && string.IsNullOrWhiteSpace(r.InstallArguments.Value)) reasons.Add(NoSilentSwitchReason);
        if (r.InstallerType is not ("MSI" or "MSIX" or "MSP") && !HasUninstall(r.UninstallArguments.Value, r.UninstallCommand?.Value)) reasons.Add(NoUninstallReason);
        AddCriticalReasons(reasons, r.Name, r.Version, r.InstallerType is "MSIX" or "MSP" ? null : r.InstallArguments, r.Detection);
        if (r.Package is { Trust: not PackageTrust.Trusted, TrustWarning: { } warning }) reasons.Add(warning);
        return reasons;
    }

    private const string WizardSource = "add-from-web-wizard";

    /// <summary>
    /// The reviewed proposal with each wizard value in place of the analysis value of the same field. A field the
    /// review edited (evidence Manual) keeps the edit.
    /// </summary>
    internal static AddAnalysisResult WithWizardValues(AddAnalysisResult r, AddSaveOptions o)
    {
        static bool Take(string? wizard, Evidence reviewed) => !string.IsNullOrWhiteSpace(wizard) && reviewed != Evidence.Manual;
        static ProposedValue<string?> Optional(string value) => new(Value: value.Trim(), Evidence: Evidence.Manual, Source: WizardSource);
        static ProposedValue<string> Required(string value) => new(Value: value, Evidence: Evidence.Manual, Source: WizardSource);
        var e = r;
        if (Take(o.ApplicationName, r.Name.Evidence)) e = e with { Name = Optional(o.ApplicationName!) };
        if (Take(o.Vendor, r.Vendor.Evidence)) e = e with { Vendor = Optional(o.Vendor!) };
        if (o.InstallArgs is not null && r.InstallArguments.Evidence != Evidence.Manual) e = e with { InstallArguments = Required(o.InstallArgs) };
        if (o.UninstallArgs is not null && r.UninstallArguments.Evidence != Evidence.Manual) e = e with { UninstallArguments = Required(o.UninstallArgs) };
        if (o.InstallContext is "Machine" or "User" && r.InstallContext.Evidence != Evidence.Manual) e = e with { InstallContext = Required(o.InstallContext) };
        if (Take(o.Architecture, r.Architecture?.Evidence ?? Evidence.None)) e = e with { Architecture = Required(o.Architecture!.Trim()) };
        return e;
    }

    /// <summary>The definition the reviewed proposal describes. Throws a plain refusal for a value the stage cannot use.</summary>
    internal AppDefinition BuildDefinition(AddAnalysisResult r, AddSaveOptions options, List<string> warnings)
    {
        var name = r.Name.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new AddFromInstallerRefusedException("Enter the application name.");
        var version = r.Version.Value?.Trim();
        if (string.IsNullOrWhiteSpace(version)) throw new AddFromInstallerRefusedException("Enter the version.");
        try { UserDefinitionFiles.SanitizeFileName(name); }
        catch (InvalidOperationException) { throw new AddFromInstallerRefusedException("The application name must contain a letter or a number."); }

        AppDefinition definition = r.InstallerType switch
        {
            "MSP" => PatchDefinition(r, name),
            "MSIX" => PackageDefinition(r, name),
            _ => InstallerDefinition(r, options, name, version)
        };

        definition.Vendor = string.IsNullOrWhiteSpace(r.Vendor.Value) ? "Unknown" : r.Vendor.Value.Trim();
        definition.Application = name;
        definition.CMName = string.IsNullOrWhiteSpace(options.CMName) ? name : options.CMName.Trim();
        if (r.InstallArguments.Evidence == Evidence.Manual)
        {
            definition.InstallArgs = r.InstallArguments.Value;
            definition.InstallerSettings = null;
        }
        if (r.UninstallArguments.Evidence == Evidence.Manual) definition.UninstallArgs = RestoreProductCodeToken(r, r.UninstallArguments.Value);
        if (r.Architecture is { Evidence: Evidence.Manual, Value.Length: > 0 }) definition.Architecture = r.Architecture.Value;
        try { definition.Architecture = InstallerArchitectures.Resolve(definition.Architecture); }
        catch (InvalidOperationException)
        {
            throw new AddFromInstallerRefusedException(
                $"The architecture {definition.Architecture} is not supported. App Packager supports the architectures x86, x64 and ARM64.");
        }
        if (!string.IsNullOrWhiteSpace(options.InstallerType)) definition.InstallerType = options.InstallerType.Trim();
        if (!string.IsNullOrWhiteSpace(options.InstallerFramework) && r.Framework.Evidence != Evidence.Manual) definition.InstallerFramework = options.InstallerFramework.Trim();
        // The analysed file name carries the analysed version; with no typed name the stage takes the name from the resolved download URL.
        if (!string.IsNullOrWhiteSpace(options.InstallerFileName)) definition.InstallerFileName = options.InstallerFileName.Trim();
        else if (!string.IsNullOrWhiteSpace(options.DownloadUrl)) definition.InstallerFileName = null;
        if (!string.IsNullOrWhiteSpace(options.DownloadUrl)) definition.DownloadUrl = options.DownloadUrl.Trim();
        if (options.VersionScraper is not null) definition.VersionScraper = options.VersionScraper;
        if (!string.IsNullOrWhiteSpace(options.VendorUrl)) definition.VendorUrl = options.VendorUrl.Trim();
        if (options.RunningProcess is { Count: > 0 }) definition.RunningProcess = [.. options.RunningProcess];

        foreach (var (label, value) in new (string, ProposedValue<string?>)[] { ("name", r.Name), ("version", r.Version), ("publisher", r.Vendor) })
            if (value.NotVerified && !string.IsNullOrEmpty(value.Value)) warnings.Add($"The {label} is not verified: it is predicted, not read from the installer.");
        if (r.InstallArguments.Evidence == Evidence.Predicted && r.InstallerType is not ("MSIX" or "MSI"))
            warnings.Add("The silent install arguments are not verified. Test the install on a test device before you deploy.");
        if (r.Detection.IsDefault) warnings.Add("Detection needs review: the definition has no detection rule. Add a rule in the Workbench before you deploy.");
        else if (r.Detection.Evidence == Evidence.Predicted) warnings.Add("The detection rule is not verified. Test it on a test device before you deploy.");
        if (r.Patch is not null) warnings.Add(r.Patch.BaseProductWarning);
        if (r.Package?.TrustWarning is { } trust) warnings.Add(trust);
        return definition;
    }

    /// <summary>
    /// An MSI uninstall names the product by the {ProductCode} token. A literal code belongs to one version and the next
    /// version has another code, so the literal code of the analysed product becomes the token.
    /// </summary>
    private static string RestoreProductCodeToken(AddAnalysisResult r, string arguments)
    {
        if (r.InstallerType != "MSI") return arguments;
        var code = r.Deployment?.ProductCode is { HasValue: true } field ? field.Value : r.ProductCode?.Value;
        return string.IsNullOrEmpty(code) ? arguments : arguments.Replace(code, "{ProductCode}", StringComparison.OrdinalIgnoreCase);
    }

    private AppDefinition InstallerDefinition(AddAnalysisResult r, AddSaveOptions options, string name, string version)
    {
        var drop = r.Deployment ?? throw new AddFromInstallerRefusedException("The analysis has no deployment fields. Analyze the installer again.");
        var isMsi = r.InstallerType == "MSI";
        if (!isMsi && string.IsNullOrWhiteSpace(r.InstallArguments.Value))
            throw new AddFromInstallerRefusedException("Enter the silent install arguments. " + NoSilentSwitchReason);
        var overrides = new DroppedInstallerOverrides
        {
            AppName = name,
            Publisher = r.Vendor.Value,
            SoftwareVersion = version,
            InstallArgs = r.InstallArguments.Value,
            UninstallArgs = isMsi ? null : r.UninstallArguments.Value,
            UninstallCommand = r.UninstallCommand?.Value,
            InstallContext = r.InstallContext.Value is "User" or "Machine" ? r.InstallContext.Value : null,
            InstallMode = options.InstallMode,
            RunningProcess = options.RunningProcess?.ToArray()
        };
        AppDefinition definition;
        try { definition = _deployment.BuildDefinition(drop, overrides, confirmed: true); }
        catch (InvalidOperationException ex) { throw new AddFromInstallerRefusedException(ex.Message); }

        var modeChanged = !string.IsNullOrWhiteSpace(options.InstallMode)
            && !string.Equals(options.InstallMode, drop.DefaultInstallMode, StringComparison.OrdinalIgnoreCase);
        // The proposal describes the default mode. After a mode change only a rule the user edited replaces the rule of the chosen mode.
        if (!r.Detection.IsDefault && (!modeChanged || r.Detection.Evidence == Evidence.Manual))
        {
            var expectedToken = DroppedInstallerAnalysisService.ExpectedValueFor(drop, version, version);
            definition.Detection = ToDetectionConfig(r.Detection, isMsi && drop.ProductCode.HasValue ? drop.ProductCode.Value : null,
                r.Version.Evidence == Evidence.Confirmed ? expectedToken : null, version);
        }
        else if (r.Detection.IsDefault)
        {
            definition.Detection = new DetectionConfig { RegistryHive = null, RegistryKeyTemplate = null };
        }
        return definition;
    }

    private static AppDefinition PatchDefinition(AddAnalysisResult r, string name)
    {
        var fileName = Path.GetFileName(r.FilePath);
        var detection = r.Detection.Kind == DetectionKind.MsiPatchCode
            ? PatchDetectionConfig(r.Detection.Values["ProductCode"], r.Detection.Values["PatchCode"])
            : r.Detection.IsDefault ? new DetectionConfig { RegistryHive = null } : ToDetectionConfig(r.Detection, null, null, r.Version.Value ?? "");
        string? uninstall = null;
        if (r.UninstallCommand?.Value is { Length: > 0 } command)
        {
            var parts = Regex.Match(command, @"/package\s+(\{[0-9A-Fa-f-]{36}\})\s+/uninstall\s+(\{[0-9A-Fa-f-]{36}\})");
            if (parts.Success)
                uninstall = $"$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/package', '{parts.Groups[1].Value}', '/uninstall', '{parts.Groups[2].Value}', '/qn', '/norestart') -Wait -PassThru -NoNewWindow";
        }
        return new AppDefinition
        {
            Schema = 1,
            Id = Guid.NewGuid().ToString("N")[..16],
            Application = name,
            Architecture = "x64",
            InstallerType = "MSP",
            InstallerFramework = "MSP",
            InstallContext = "Machine",
            InstallerFileName = fileName,
            InstallArgs = r.InstallArguments.Value,
            UninstallArgs = "",
            UninstallCommand = uninstall,
            RunningProcess = [],
            Detection = detection
        };
    }

    /// <summary>
    /// A PowerShell detection that asks Windows Installer for the applied patches of the product in the machine context.
    /// The machine context takes an empty user SID string; Windows PowerShell 5.1 throws on $null. The RecordList and
    /// Patch members are read through InvokeMember because the PowerShell COM adapter returns no PatchCode for them.
    /// A failed query writes standard error and exits 1, which ConfigMgr reports as detection state Unknown.
    /// </summary>
    internal static DetectionConfig PatchDetectionConfig(string productCode, string patchCode)
    {
        if (!ProposedCommand.IsProductCode(productCode) || !ProposedCommand.IsProductCode(patchCode))
            throw new AddFromInstallerRefusedException("The patch code or the product code is not a GUID.");
        var script = string.Join("\r\n",
            "try {",
            "    $installer = New-Object -ComObject WindowsInstaller.Installer",
            $"    $patches = $installer.GetType().InvokeMember('PatchesEx', 'GetProperty', $null, $installer, @('{productCode}', '', 4, 1))",
            "    $count = $patches.GetType().InvokeMember('Count', 'GetProperty', $null, $patches, $null)",
            "    for ($i = 0; $i -lt $count; $i++) {",
            "        $patch = $patches.GetType().InvokeMember('Item', 'GetProperty', $null, $patches, @($i))",
            "        $code = $patch.GetType().InvokeMember('PatchCode', 'GetProperty', $null, $patch, $null)",
            $"        if ($code -eq '{patchCode}') {{ Write-Output 'Installed'; break }}",
            "    }",
            "} catch {",
            "    [Console]::Error.WriteLine('The Windows Installer patch query failed: ' + $_.Exception.Message)",
            "    exit 1",
            "}");
        return new DetectionConfig { Type = "Script", ScriptLanguage = "PowerShell", ScriptText = script, RegistryHive = null, ValueName = null, Operator = null };
    }

    private static AppDefinition PackageDefinition(AddAnalysisResult r, string name)
    {
        var architecture = r.Architecture?.Value is { Length: > 0 } arch ? arch : "x64";
        return new AppDefinition
        {
            Schema = 1,
            Id = Guid.NewGuid().ToString("N")[..16],
            Application = name,
            Architecture = architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? "ARM64" : architecture,
            InstallerType = "MSIX",
            InstallerFramework = "MSIX",
            InstallContext = "Machine",
            InstallerFileName = Path.GetFileName(r.FilePath),
            InstallArgs = "",
            UninstallArgs = "",
            RunningProcess = [],
            Detection = new DetectionConfig { Type = "RegistryKey", Is64Bit = true, RegistryHive = null, ValueName = null, Operator = null }
        };
    }

    /// <summary>
    /// The definition rule of a proposal. A registry rule with an empty key is refused. A ProductCode key of an MSI
    /// becomes the {ProductCode} template; <paramref name="expectedToken"/> replaces the expected version when set.
    /// </summary>
    public static DetectionConfig ToDetectionConfig(DetectionProposal p, string? msiProductCode, string? expectedToken, string version)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.IsDefault) throw new AddFromInstallerRefusedException("The detection rule is empty. Enter a rule before you save.");
        string V(string key) => p.Values.TryGetValue(key, out var value) ? value : "";
        string Key()
        {
            var key = V("Key").Trim();
            if (key.Length == 0) throw new AddFromInstallerRefusedException("The detection rule has no registry key. Enter a key or choose another rule.");
            return key;
        }
        bool Is64() => !string.Equals(V("Is64Bit"), "false", StringComparison.OrdinalIgnoreCase);
        string Hive() => V("Hive") is "HKCU" ? "HKCU" : "HKLM";
        string Expected()
        {
            var expected = expectedToken is not null && V("Expected") == version ? expectedToken : V("Expected");
            if (string.IsNullOrWhiteSpace(expected)) throw new AddFromInstallerRefusedException("The version rule has no expected version. Enter a version or choose another rule.");
            return expected;
        }

        switch (p.Kind)
        {
            case DetectionKind.MsiProductCode:
            {
                var key = Key();
                if (msiProductCode is not null && key.EndsWith(@"\" + msiProductCode, StringComparison.OrdinalIgnoreCase))
                    key = key[..^msiProductCode.Length] + "{ProductCode}";
                return new DetectionConfig
                {
                    Type = "RegistryKeyValue", RegistryHive = Hive(), RegistryKeyTemplate = key, ValueName = V("ValueName") is { Length: > 0 } n ? n : "DisplayVersion",
                    Operator = V("Operator") is { Length: > 0 } o ? o : "IsEquals", PropertyType = "Version", Is64Bit = Is64(), ExpectedValue = Expected()
                };
            }
            case DetectionKind.RegistryVersion:
                return new DetectionConfig
                {
                    Type = "RegistryKeyValue", RegistryHive = Hive(), RegistryKeyTemplate = Key(), ValueName = V("ValueName") is { Length: > 0 } n2 ? n2 : "DisplayVersion",
                    Operator = V("Operator") is { Length: > 0 } o2 ? o2 : "IsEquals", PropertyType = "Version", Is64Bit = Is64(), ExpectedValue = Expected()
                };
            case DetectionKind.RegistryExists:
                return new DetectionConfig { Type = "RegistryKey", RegistryHive = Hive(), RegistryKeyTemplate = Key(), ValueName = null, Operator = null, Is64Bit = Is64() };
            case DetectionKind.FileVersion:
            case DetectionKind.FileExists:
            {
                var folder = V("FilePath").Trim();
                var file = V("FileName").Trim();
                if (folder.Length == 0 || file.Length == 0) throw new AddFromInstallerRefusedException("The file rule needs a folder and a file name.");
                var versioned = p.Kind == DetectionKind.FileVersion;
                return new DetectionConfig
                {
                    Type = "File", RegistryHive = null, ValueName = null, FilePath = folder, FileName = file, Is64Bit = Is64(),
                    PropertyType = versioned ? "Version" : null,
                    Operator = versioned ? (V("Operator") is { Length: > 0 } o3 ? o3 : "GreaterEquals") : null,
                    ExpectedValue = versioned ? Expected() : null
                };
            }
            case DetectionKind.Compound:
                return new DetectionConfig
                {
                    Type = "Compound", RegistryHive = null, ValueName = null, Operator = null,
                    Connector = V("Connector") is "Or" ? "Or" : "And",
                    Clauses = [.. p.Clauses.Select(c => ToDetectionConfig(c, msiProductCode, expectedToken, version))]
                };
            case DetectionKind.MsiPatchCode:
                return PatchDetectionConfig(V("ProductCode"), V("PatchCode"));
            case DetectionKind.MsixPackage:
                return new DetectionConfig { Type = "RegistryKey", Is64Bit = true, RegistryHive = null, ValueName = null, Operator = null };
            default:
                throw new AddFromInstallerRefusedException("The detection rule is empty. Enter a rule before you save.");
        }
    }

    /// <summary>True for a definition with no download source: no download URL, no scraper URL and no URL query.</summary>
    public static bool IsLocalFileDefinition(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return string.IsNullOrWhiteSpace(definition.DownloadUrl)
            && string.IsNullOrWhiteSpace(definition.VersionScraper?.Url)
            && string.IsNullOrWhiteSpace(definition.VersionScraper?.DownloadUrlQuery);
    }

    // ---- download --------------------------------------------------------------------------

    /// <summary>The download URL with its version tokens replaced, as the stage resolves them.</summary>
    public static string ResolveDownloadUrl(string template, string displayVersion, string? rawVersion = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var url = template
            .Replace("{display_version}", displayVersion, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", rawVersion ?? displayVersion, StringComparison.OrdinalIgnoreCase);
        return StagingService.ApplyDashedVersion(url, displayVersion);
    }

    public async Task<string> FetchInstallerAsync(DownloadRequest request, IProgress<string> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (string.IsNullOrWhiteSpace(request.DownloadRoot))
            throw new AddFromInstallerRefusedException("Set the download folder in Settings first.");
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new AddFromInstallerRefusedException("The download URL is not a valid http or https URL.");
        if (request.Url.Contains('{'))
            throw new AddFromInstallerRefusedException("The download URL still has a version token. Resolve the version first.");
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? Path.GetFileName(uri.AbsolutePath) : request.FileName.Trim();
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            throw new AddFromInstallerRefusedException("The download URL names no installer file. Enter the installer file name.");
        try { fileName = StagingService.AssertPlainFileName(fileName); }
        catch (InvalidOperationException) { throw new AddFromInstallerRefusedException("The installer file name is not a plain file name."); }
        if (RefusalReason(fileName) is { } reason) throw new AddFromInstallerRefusedException(reason);

        var folder = Path.Combine(request.DownloadRoot, FetchFolderName, Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(folder, fileName);
        Directory.CreateDirectory(folder);
        try
        {
            await FetchToAsync(request, target, fileName, progress, ct).ConfigureAwait(false);
_fetchedFolders[Path.GetFullPath(folder)] = 0;
            return target;
        }
        catch
        {
            DeleteFolder(folder);
            throw;
        }
    }

    private async Task FetchToAsync(DownloadRequest request, string target, string fileName, IProgress<string> progress, CancellationToken ct)
    {
        void Report(string line) => progress.Report(line);
        var limit = _limits.MaxFileBytes;
        try
        {
            try
            {
                var cacheRoot = Workbench.WorkbenchPaths.GetSharedDownloadCacheRoot(request.DownloadRoot);
                await SharedDownloadCache.FetchAsync(_downloads, request.Url, target, cacheRoot, request.Headers, Report, ct, maxBytes: limit).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await _downloads.DownloadAsync(request.Url, target, onProgress: Report, ct: ct, headers: request.Headers, maxBytes: limit).ConfigureAwait(false);
            }
        }
        catch (DownloadTooLargeException)
        {
            throw new AddFromInstallerRefusedException($"The download is larger than the limit of {limit} bytes.");
        }
        var type = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".msi" => "MSI",
            ".msp" => "MSP",
            ".exe" => "EXE",
            _ => "MSIX"
        };
        try { StagingService.ValidateInstallerMagic(target, type); }
        catch (InvalidOperationException ex)
        {
            throw new AddFromInstallerRefusedException("The download is not an installer: " + ex.Message);
        }
    }

    public void DiscardFetched(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath)) return;
        var folder = Path.GetDirectoryName(Path.GetFullPath(installerPath));
        if (folder is not null && _fetchedFolders.ContainsKey(folder) && DeleteFolder(folder)) _fetchedFolders.TryRemove(folder, out _);
    }

    /// <summary>True when the folder is gone.</summary>
    private static bool DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

}
