using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// How far a proposed field can be trusted.
/// </summary>
public enum AnalysisConfidence
{
    /// <summary>Read from an authoritative source — the MSI Property table.</summary>
    Confirmed,
    /// <summary>Inferred from an engine convention or a PE resource; an operator must confirm it.</summary>
    Predicted
}

/// <summary>One proposed value with its provenance.</summary>
public sealed class ProposedField
{
    public string Value { get; init; } = "";
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Predicted;
    /// <summary>Where the value came from, for operator-facing diagnostics.</summary>
    public string Source { get; init; } = "";

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public static ProposedField Confirmed(string? value, string source) =>
        new() { Value = value ?? "", Confidence = AnalysisConfidence.Confirmed, Source = source };

    public static ProposedField Predicted(string? value, string source) =>
        new() { Value = value ?? "", Confidence = AnalysisConfidence.Predicted, Source = source };

    public static ProposedField Empty { get; } = new() { Source = "none" };
}

/// <summary>
/// The AppDefinition-shaped proposal produced from one dropped installer.
/// </summary>
public sealed class DroppedInstallerAnalysis
{
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }

    /// <summary>Detected engine: NativeMSI, NSIS, InnoSetup, WixBurn, Squirrel, Velopack, and so on.</summary>
    public string Engine { get; init; } = "Unknown";
    /// <summary>Deployment-type kind the manifest records: MSI or EXE.</summary>
    public string InstallerType { get; init; } = "EXE";
    public string Architecture { get; init; } = "";
    /// <summary>"User" when the predicted uninstall key lives under HKCU, else "Machine".</summary>
    public string InstallContext { get; init; } = "Machine";

    /// <summary>Package id of a format that names itself, such as a Velopack nuspec id.</summary>
    public string PackageId { get; init; } = "";
    /// <summary>
    /// The package's own version where it differs from the version the product
    /// registers: a Velopack nuspec keeps prerelease and build labels that the
    /// Add/Remove Programs entry drops.
    /// </summary>
    public string PackageVersion { get; init; } = "";

    public ProposedField AppName { get; init; } = ProposedField.Empty;
    public ProposedField Publisher { get; init; } = ProposedField.Empty;
    public ProposedField SoftwareVersion { get; init; } = ProposedField.Empty;
    public ProposedField ProductCode { get; init; } = ProposedField.Empty;
    public ProposedField InstallArgs { get; init; } = ProposedField.Empty;
    public ProposedField UninstallArgs { get; init; } = ProposedField.Empty;
    public ProposedField UninstallCommand { get; init; } = ProposedField.Empty;
    public ProposedField UninstallRegistryKey { get; init; } = ProposedField.Empty;

    public string InstallCommand { get; init; } = "";
    public string Notes { get; init; } = "";
    public string DetectionHint { get; init; } = "";

    /// <summary>Detection proposal in stage-manifest shape.</summary>
    public StageDetection Detection { get; init; } = new();

    /// <summary>
    /// Install modes the compiled script accepts (CurrentUser, AllUsers), in
    /// that order. One entry when the installer has no mode switch; empty
    /// when the engine has no script reader.
    /// </summary>
    public IReadOnlyList<string> InstallModes { get; init; } = [];
    /// <summary>The mode the installer takes without a switch.</summary>
    public string DefaultInstallMode { get; init; } = "";
    /// <summary>The resolved branch per install mode, keyed like InstallModes.</summary>
    public IReadOnlyDictionary<string, InstallBranch> ModeBranches { get; init; } = new Dictionary<string, InstallBranch>();

    /// <summary>True when the operator can choose System or User for this drop.</summary>
    public bool HasInstallModeChoice => InstallModes.Count > 1;

    /// <summary>
    /// Identity confidence for the drop as a whole. MSI identity is authoritative;
    /// every other engine yields predictions.
    /// </summary>
    public AnalysisConfidence Confidence => AppName.Confidence == AnalysisConfidence.Confirmed
        && SoftwareVersion.Confidence == AnalysisConfidence.Confirmed
            ? AnalysisConfidence.Confirmed
            : AnalysisConfidence.Predicted;

    /// <summary>
    /// True when an operator must confirm the predicted values before the drop
    /// may be staged. Packaging a wrong silent switch deploys a broken app.
    /// </summary>
    public bool RequiresConfirmation => Confidence == AnalysisConfidence.Predicted;

    /// <summary>Fields an operator should review, in display order.</summary>
    public IEnumerable<(string Name, ProposedField Field)> Fields()
    {
        yield return (nameof(AppName), AppName);
        yield return (nameof(Publisher), Publisher);
        yield return (nameof(SoftwareVersion), SoftwareVersion);
        yield return (nameof(ProductCode), ProductCode);
        yield return (nameof(InstallArgs), InstallArgs);
        yield return (nameof(UninstallArgs), UninstallArgs);
        yield return (nameof(UninstallCommand), UninstallCommand);
        yield return (nameof(UninstallRegistryKey), UninstallRegistryKey);
    }
}

/// <summary>Operator edits applied on top of an analysis before staging.</summary>
public sealed class DroppedInstallerOverrides
{
    public string? AppName { get; init; }
    public string? Publisher { get; init; }
    public string? SoftwareVersion { get; init; }
    public string? InstallArgs { get; init; }
    public string? UninstallArgs { get; init; }
    public string? UninstallCommand { get; init; }
    public string[]? RunningProcess { get; init; }
    /// <summary>Null takes the analysis prediction (HKCU key means a per-user install).</summary>
    public string? InstallContext { get; init; }
    /// <summary>
    /// AllUsers or CurrentUser to stage a non-default branch of a switchable
    /// installer. Null or the default mode stages the branch the installer
    /// takes on its own; a mode the script does not offer is refused.
    /// </summary>
    public string? InstallMode { get; init; }
}

/// <summary>
/// Analyses an ad-hoc installer dropped onto the app and proposes the identity,
/// silent switches, and detection rule a package needs. Nothing here writes to disk —
/// the proposal graduates into a <see cref="StageManifest"/> or an
/// <see cref="AppDefinition"/> only after the caller supplies confirmation.
/// </summary>
public sealed class DroppedInstallerAnalysisService
{
    private const string ArpRelative = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ArpHklm64 = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ArpHklm32 = @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ArpHkcu = @"HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly IMsiInspector _msiInspector;
    private readonly InstallerIdentificationService? _identificationService;
    private readonly Func<IInstallerAnalysisService> _fullAnalysis;

    /// <param name="fullAnalysis">The Installer Analysis service a deep analysis runs through; the default uses the data-root sandbox.</param>
    public DroppedInstallerAnalysisService(
        IMsiInspector msiInspector,
        InstallerIdentificationService? identificationService = null,
        Func<IInstallerAnalysisService>? fullAnalysis = null)
    {
        _msiInspector = msiInspector;
        _identificationService = identificationService;
        _fullAnalysis = fullAnalysis ?? (() => new InstallerAnalysisService(new AnalysisSandbox()));
    }

    /// <summary>
    /// Analyses one installer. With <paramref name="deepInspect"/> the analysis is the Add from file analysis:
    /// bounded, sandboxed and off the calling thread, with the format readers of the Installer Analysis
    /// (BitRock project, Burn bundle) applied, and a file type with no defined flow is refused with a plain
    /// <see cref="AddFromInstaller.AddFromInstallerRefusedException"/>. Without it, only the file head and the
    /// compiled script are read.
    /// </summary>
    public async Task<DroppedInstallerAnalysis> AnalyzeAsync(
        string path, bool deepInspect = false, CancellationToken ct = default)
    {
        if (!deepInspect) return await AnalyzeLocalAsync(path, deepInspect: false, ct).ConfigureAwait(false);
        var unified = new AddFromInstaller.AddFromInstallerService(_fullAnalysis(), this);
        var result = await unified.AnalyzeAsync(path, new Progress<string>(), ct).ConfigureAwait(false);
        return result.Deployment ?? throw new InstallerAnalysisException("The deployment fields could not be read from this file.");
    }

    /// <summary>
    /// The generic proposal from the file head, the compiled script and the MSI Property table. With
    /// <paramref name="deepInspect"/> and an identification service, the payload is also extracted (bounded,
    /// in the analysis sandbox) for framework manifest data (JetBrains product-info.json, the Burn manifest).
    /// </summary>
    internal async Task<DroppedInstallerAnalysis> AnalyzeLocalAsync(
        string path, bool deepInspect, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Installer not found", path);

        var fullPath = System.IO.Path.GetFullPath(path);
        var fileName = System.IO.Path.GetFileName(fullPath);
        var engine = InstallerSignatureScanner.Identify(fullPath);

        MsiProperties? msi = null;
        var msiArchitecture = "";
        if (engine == "NativeMSI" || engine == "MSP")
        {
            try
            {
                msi = _msiInspector.ReadProperties(fullPath);
                msiArchitecture = _msiInspector.ReadTemplateArchitecture(fullPath);
            }
            catch
            {
                // A file that only looks like an MSI (OLE compound document with an
                // .exe extension) still reaches here; fall back to PE metadata.
                msi = null;
            }
        }

        InstallerMetadata? deepMetadata = null;
        if (deepInspect && _identificationService is not null && msi is null)
        {
            try
            {
                deepMetadata = await _identificationService.ExtractMetadataAsync(fullPath, engine, ct);
            }
            catch
            {
                deepMetadata = null;
            }
        }

        var versionInfo = msi is null ? TryReadVersionInfo(fullPath) : null;
        var isMsi = msi is not null;

        // The compiled script is the authoritative source for an NSIS or Inno
        // Setup drop: it carries the values the installer writes to Add/Remove
        // Programs, so a literal there needs no operator confirmation.
        var script = msi is null ? CompiledScriptFacts.TryRead(engine, fullPath, ct) : null;
        var scriptSource = script is null ? null : CompiledScriptFacts.SourceName(engine);

        // The nuspec inside a Velopack setup is read the same way a compiled
        // script is: it holds the values the product registers, so it outranks
        // the setup stub's own PE resource.
        var velopack = engine == "Velopack" ? VelopackMetadataReader.Read(fullPath) : null;
        if (velopack is { HeaderAvailable: false }) velopack = null;
        const string VelopackSource = "velopack-nuspec";

        var appName = ResolveIdentity(
            msi?.ProductName, deepMetadata?.ProductName,
            versionInfo?.ProductName ?? versionInfo?.FileDescription, isMsi, deepMetadata?.MetadataSource,
            velopack?.DisplayName ?? script?.DisplayName, velopack is null ? scriptSource : VelopackSource);
        var publisher = ResolveIdentity(
            msi?.Manufacturer, deepMetadata?.Manufacturer,
            versionInfo?.CompanyName, isMsi, deepMetadata?.MetadataSource,
            velopack?.Publisher ?? script?.Publisher, velopack is null ? scriptSource : VelopackSource);
        var version = ResolveIdentity(
            msi?.ProductVersion, deepMetadata?.ProductVersion,
            versionInfo?.ProductVersion ?? versionInfo?.FileVersion, isMsi, deepMetadata?.MetadataSource,
            velopack?.DisplayVersion ?? script?.DisplayVersion, velopack is null ? scriptSource : VelopackSource);

        var productCodeValue = !string.IsNullOrEmpty(msi?.ProductCode)
            ? msi!.ProductCode
            : deepMetadata?.ProductCode ?? "";
        var productCode = isMsi && !string.IsNullOrEmpty(msi?.ProductCode)
            ? ProposedField.Confirmed(productCodeValue, "msi-property-table")
            : ProposedField.Predicted(productCodeValue, deepMetadata?.MetadataSource ?? "none");

        var switches = SilentSwitchDatabase.Resolve(engine, fileName, productCodeValue, appName.Value);

        var architecture = !string.IsNullOrEmpty(msiArchitecture)
            ? NormalizeMsiArchitecture(msiArchitecture)
            : InstallerSignatureScanner.ReadPeArchitecture(fullPath);

        // A 32-bit Velopack setup stub can carry an x64 or ARM64 application;
        // the deployment describes the application, not the stub.
        if (NormalizePackageArchitecture(velopack?.Architecture) is { Length: > 0 } payloadArchitecture)
            architecture = payloadArchitecture;

        // The default branch of the compiled script names the key, hive and
        // view the installer registers under; the engine convention is the
        // fallback when there is no script or the key is computed at run time.
        var branch = script?.DefaultBranch;
        var arp = velopack is not null
            ? new UninstallKeyPrediction
            {
                Path = velopack.UninstallRegistryKey,
                Hive = "HKCU",
                Source = VelopackSource,
                Note = velopack.Note
            }
            : branch is { UninstallRegistryKey: { Length: > 0 } }
            ? new UninstallKeyPrediction
            {
                Path = branch.UninstallRegistryKey,
                Hive = branch.RegistryHive,
                Source = scriptSource!,
                Note = branch.UninstallRegistryKeyNote
            }
            : PredictUninstallRegistryKey(engine, appName.Value, productCodeValue, msi, architecture);

        var switchSource = $"switch-table:{engine}";
        var detection = BuildDetection(engine, isMsi, productCodeValue, arp?.Path, appName.Value, version.Value, architecture, msi);
        if (branch is { UninstallRegistryKey: { Length: > 0 } } && detection.Type == "RegistryKeyValue")
        {
            // The script's view beats the PE architecture: a 32-bit stub that runs
            // SetRegView 64 writes to the 64-bit view, and the reverse.
            detection.Is64Bit = branch.Is64BitView;
        }
        var perUser = branch is not null ? branch.InstallContext == "PerUser" : arp?.Hive == "HKCU";

        var installArgs = branch is not null && !string.IsNullOrWhiteSpace(branch.InstallArgs)
            ? ProposedField.Confirmed(branch.InstallArgs, scriptSource!)
            : MakeField(switches.InstallArgs, isMsi, switchSource);
        var uninstallCommand = isMsi
            ? ProposedField.Confirmed("", "msi-uninstalls-by-product-code")
            : velopack is not null
                ? ProposedField.Confirmed(velopack.SilentUninstallCommand, VelopackSource)
            : branch is { SilentUninstallCommand: { Length: > 0 }, InstallDirPredicted: true }
                ? ProposedField.Predicted(branch.SilentUninstallCommand, scriptSource!)
            : branch is { SilentUninstallCommand: { Length: > 0 } }
                ? ProposedField.Confirmed(branch.SilentUninstallCommand, scriptSource!)
                : ProposedField.Predicted(switches.UninstallCommand, switchSource);

        return new DroppedInstallerAnalysis
        {
            Path = fullPath,
            FileName = fileName,
            FileSize = new FileInfo(fullPath).Length,
            Engine = engine,
            InstallerType = isMsi ? "MSI" : "EXE",
            Architecture = architecture,
            InstallContext = perUser ? "User" : "Machine",
            PackageId = velopack?.PackageId ?? "",
            PackageVersion = velopack?.PackageVersion ?? "",
            AppName = appName,
            Publisher = publisher,
            SoftwareVersion = version,
            ProductCode = productCode,
            InstallArgs = installArgs,
            UninstallArgs = MakeField(switches.UninstallArgs, isMsi, switchSource),
            UninstallCommand = uninstallCommand,
            UninstallRegistryKey = arp is null
                ? ProposedField.Empty
                : isMsi || arp.Source == scriptSource
                    ? ProposedField.Confirmed(arp.Path, arp.Source)
                    : ProposedField.Predicted(arp.Path, arp.Source),
            InstallCommand = switches.InstallCommand,
            Notes = string.Join(" ", new[] { switches.Notes, arp?.Note, script?.Note }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
            DetectionHint = script is not null
                ? $"Read from the compiled {engine} script: {arp?.Path ?? "no Add/Remove Programs key"}."
                : DetectionHintFor(engine, productCodeValue, appName.Value),
            Detection = detection,
            InstallModes = script?.InstallModes ?? [],
            DefaultInstallMode = script?.DefaultInstallMode ?? "",
            ModeBranches = script?.Branches ?? new Dictionary<string, InstallBranch>()
        };
    }

    /// <summary>
    /// The branch a requested install mode selects, or null for the default
    /// branch. A mode the script does not offer is refused: staging it would
    /// pass a switch the installer ignores and register under the other hive.
    /// </summary>
    public static InstallBranch? SelectModeBranch(DroppedInstallerAnalysis analysis, string? requestedMode)
    {
        if (string.IsNullOrWhiteSpace(requestedMode)) return null;
        if (string.Equals(requestedMode, analysis.DefaultInstallMode, StringComparison.OrdinalIgnoreCase)) return null;
        if (!analysis.ModeBranches.TryGetValue(requestedMode, out var branch))
            throw new InvalidOperationException(
                $"'{analysis.FileName}' offers no {requestedMode} install mode; the script accepts {(analysis.InstallModes.Count == 0 ? "no mode switch" : string.Join(" and ", analysis.InstallModes))}.");
        return branch;
    }

    /// <summary>
    /// Turns an analysis into a stage manifest. Predicted values must be confirmed first:
    /// staging a wrong silent switch produces a package that fails on every target device.
    /// </summary>
    public StageManifest BuildStageManifest(
        DroppedInstallerAnalysis analysis,
        DroppedInstallerOverrides? overrides = null,
        bool confirmed = false)
    {
        if (analysis.Engine == "MSP")
            throw new InvalidOperationException(
                "MSI patches (.msp) apply against an installed base MSI and cannot be staged as a standalone deployment.");
        if (analysis.Engine is "MSIX" or "MsixBundle")
            throw new InvalidOperationException(
                "An MSIX or APPX package is staged through its definition. Save it as a definition, then stage the definition.");

        if (analysis.RequiresConfirmation && !confirmed)
            throw new InvalidOperationException(
                $"Analysis of '{analysis.FileName}' is predicted, not confirmed. Confirm the proposed values before staging.");

        // A non-default install mode swaps the branch: switch, uninstaller,
        // key, hive, view and context move together, since each follows the
        // folder the branch installs into.
        var modeBranch = SelectModeBranch(analysis, overrides?.InstallMode);
        var branchInstallArgs = modeBranch?.InstallArgs ?? analysis.InstallArgs.Value;
        var branchUninstallCommand = modeBranch is null ? analysis.UninstallCommand.Value : modeBranch.SilentUninstallCommand;
        var installContext = overrides?.InstallContext
            ?? (modeBranch is null ? analysis.InstallContext : modeBranch.InstallContext == "PerUser" ? "User" : "Machine");

        var appName = Pick(overrides?.AppName, analysis.AppName.Value);
        var publisher = Pick(overrides?.Publisher, analysis.Publisher.Value);
        var version = Pick(overrides?.SoftwareVersion, analysis.SoftwareVersion.Value);
        var installArgs = Pick(overrides?.InstallArgs, branchInstallArgs);
        var uninstallArgs = Pick(overrides?.UninstallArgs, analysis.UninstallArgs.Value);
        var uninstallCommand = Pick(overrides?.UninstallCommand, branchUninstallCommand);

        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException("Ad-hoc staging requires an application name.");
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("Ad-hoc staging requires a version.");
        if (analysis.InstallerType != "MSI" && string.IsNullOrWhiteSpace(installArgs))
            throw new InvalidOperationException("Ad-hoc staging requires install arguments for a non-MSI installer.");
        if (string.IsNullOrWhiteSpace(publisher))
            publisher = "Unknown";

        var detection = analysis.Detection;
        if (modeBranch is { UninstallRegistryKey: { Length: > 0 } } && detection.Type == "RegistryKeyValue")
        {
            detection = CloneDetection(detection);
            var relative = StripHive(modeBranch.UninstallRegistryKey);
            var wow = relative.Contains("WOW6432Node\\", StringComparison.OrdinalIgnoreCase);
            detection.RegistryKeyRelative = wow ? relative.Replace("WOW6432Node\\", "", StringComparison.OrdinalIgnoreCase) : relative;
            detection.Is64Bit = !wow && modeBranch.Is64BitView;
            detection.Hive = modeBranch.RegistryHive == "HKCU" ? "CurrentUser" : "LocalMachine";
        }
        if (string.IsNullOrWhiteSpace(detection.RegistryKeyRelative) && detection.Type == "RegistryKeyValue")
        {
            detection = CloneDetection(detection);
            detection.RegistryKeyRelative = $@"{ArpRelative}\{appName}";
        }
        if (detection.Type == "RegistryKeyValue")
        {
            detection = CloneDetection(detection);
            detection.ExpectedValue = version;
        }

        return new StageManifest
        {
            AppName = appName,
            DisplayName = appName,
            Publisher = publisher,
            SoftwareVersion = version,
            InstallerFile = analysis.FileName,
            InstallerType = analysis.InstallerType,
            // The analysis reads the payload's own architecture; an unreadable one keeps
            // the same x64 default the graduated definition takes.
            Architecture = string.IsNullOrEmpty(analysis.Architecture)
                ? InstallerArchitectures.LegacyDefault
                : analysis.Architecture,
            InstallContext = installContext,
            InstallArgs = installArgs,
            UninstallArgs = uninstallArgs,
            UninstallCommand = analysis.InstallerType == "MSI" || string.IsNullOrWhiteSpace(uninstallCommand) ? null : uninstallCommand,
            ProductCode = analysis.ProductCode.HasValue ? analysis.ProductCode.Value : null,
            RunningProcess = overrides?.RunningProcess ?? [],
            Detection = detection
        };
    }

    /// <summary>
    /// Graduates a drop into a catalog definition. Acquisition stays unset — a dropped
    /// file carries no origin URL, so the version scraper and download URL remain the
    /// operator's to fill in before the definition can re-acquire the installer.
    /// </summary>
    public AppDefinition BuildDefinition(
        DroppedInstallerAnalysis analysis,
        DroppedInstallerOverrides? overrides = null,
        bool confirmed = false)
    {
        var manifest = BuildStageManifest(analysis, overrides, confirmed);
        var isMsi = manifest.InstallerType == "MSI";

        var uninstallArgs = isMsi && analysis.ProductCode.HasValue
            ? $"/x {{ProductCode}} {manifest.UninstallArgs}".Trim()
            : manifest.UninstallArgs;

        var registryKeyTemplate = isMsi && analysis.ProductCode.HasValue
            ? $@"{ArpRelative}\{{ProductCode}}"
            : manifest.Detection.RegistryKeyRelative;

        var settings = InstallerSettingsGenerator.AlignWithArgs(
            InstallerSettingsGenerator.GenerateDefaults(analysis.Engine), manifest.InstallArgs, out var remainingArgs);

        return new AppDefinition
        {
            Schema = 1,
            Id = Guid.NewGuid().ToString("N")[..16],
            Vendor = manifest.Publisher,
            Application = manifest.AppName,
            CMName = manifest.AppName,
            Architecture = string.IsNullOrEmpty(analysis.Architecture) ? "x64" : analysis.Architecture,
            InstallerType = manifest.InstallerType,
            InstallerFramework = analysis.Engine,
            InstallContext = manifest.InstallContext,
            InstallerFileName = manifest.InstallerFile,
            InstallArgs = settings is null ? manifest.InstallArgs : remainingArgs,
            UninstallArgs = uninstallArgs,
            // The definition field holds the wrapper's uninstall expression, not a
            // bare command line; a bare line would run without capturing $proc.
            UninstallCommand = string.IsNullOrWhiteSpace(manifest.UninstallCommand)
                || WrapperGeneratorService.UninstallExpressionFromCommandLine(manifest.UninstallCommand) is not { Length: > 0 } expression
                ? null
                : expression,
            InstallerSettings = settings,
            RunningProcess = manifest.RunningProcess ?? [],
            Detection = new DetectionConfig
            {
                Type = manifest.Detection.Type,
                RegistryHive = manifest.Detection.RegistryKeyRelative is null ? null : manifest.Detection.IsCurrentUserHive ? "HKCU" : "HKLM",
                RegistryKeyTemplate = registryKeyTemplate,
                ValueName = manifest.Detection.ValueName,
                Operator = manifest.Detection.Operator,
                FilePath = manifest.Detection.FilePath,
                FileName = manifest.Detection.FileName,
                PropertyType = manifest.Detection.PropertyType,
                Is64Bit = manifest.Detection.Is64Bit,
                ExpectedValue = manifest.Detection.Type == "RegistryKeyValue" ? ExpectedValueFor(analysis, manifest.SoftwareVersion, overrides?.SoftwareVersion) : manifest.Detection.ExpectedValue
            }
        };
    }

    /// <summary>
    /// The DisplayVersion the rule compares. A version read from the MSI Property table or the compiled script
    /// becomes the stage token that reads the same field of the staged payload, so a later version of the same
    /// installer still matches; an edited or predicted version stays a literal.
    /// </summary>
    internal static string ExpectedValueFor(DroppedInstallerAnalysis analysis, string version, string? editedVersion)
    {
        if (!string.IsNullOrWhiteSpace(editedVersion) && editedVersion != analysis.SoftwareVersion.Value) return version;
        if (analysis.SoftwareVersion.Confidence != AnalysisConfidence.Confirmed) return version;
        if (analysis.SoftwareVersion.Source == "msi-property-table") return StageTokens.ProductVersionToken;
        if (analysis.SoftwareVersion.Source == CompiledScriptFacts.SourceName(analysis.Engine)) return StageTokens.HeaderDisplayVersionToken;
        return version;
    }

    /// <summary>
    /// A copy of the proposal with the values a format reader read better (BitRock project, Burn bundle, MSIX
    /// manifest). A new uninstall key moves the detection rule, its hive and view, and the install context with it.
    /// </summary>
    public static DroppedInstallerAnalysis Overlay(DroppedInstallerAnalysis a, DeploymentOverlay o)
    {
        var appName = o.AppName ?? a.AppName;
        var version = o.SoftwareVersion ?? a.SoftwareVersion;
        var key = o.UninstallRegistryKey ?? a.UninstallRegistryKey;
        var architecture = string.IsNullOrEmpty(o.Architecture) ? a.Architecture : o.Architecture;
        var keyMoved = o.UninstallRegistryKey is { HasValue: true } && o.UninstallRegistryKey.Value != a.UninstallRegistryKey.Value;
        var detection = keyMoved && a.Detection.Type == "RegistryKeyValue"
            ? BuildDetection(a.Engine, isMsi: false, a.ProductCode.Value, key.Value, appName.Value, version.Value, architecture, msi: null)
            : CloneDetection(a.Detection);
        if (!keyMoved && detection.Type == "RegistryKeyValue" && o.SoftwareVersion is not null) detection.ExpectedValue = version.Value;
        var context = o.InstallContext ?? (keyMoved ? (key.Value.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? "User" : "Machine") : a.InstallContext);
        return new DroppedInstallerAnalysis
        {
            Path = a.Path,
            FileName = a.FileName,
            FileSize = a.FileSize,
            Engine = a.Engine,
            InstallerType = a.InstallerType,
            Architecture = architecture,
            InstallContext = context,
            PackageId = a.PackageId,
            PackageVersion = a.PackageVersion,
            AppName = appName,
            Publisher = o.Publisher ?? a.Publisher,
            SoftwareVersion = version,
            ProductCode = a.ProductCode,
            InstallArgs = o.InstallArgs ?? a.InstallArgs,
            UninstallArgs = a.UninstallArgs,
            UninstallCommand = o.UninstallCommand ?? a.UninstallCommand,
            UninstallRegistryKey = key,
            InstallCommand = a.InstallCommand,
            Notes = string.Join(" ", new[] { a.Notes, o.Notes }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
            DetectionHint = o.DetectionHint ?? a.DetectionHint,
            Detection = detection,
            InstallModes = keyMoved ? [] : a.InstallModes,
            DefaultInstallMode = keyMoved ? "" : a.DefaultInstallMode,
            ModeBranches = keyMoved ? new Dictionary<string, InstallBranch>() : a.ModeBranches
        };
    }

    private static ProposedField ResolveIdentity(
        string? msiValue, string? metadataValue, string? versionInfoValue, bool isMsi, string? metadataSource,
        string? scriptValue = null, string? scriptSource = null)
    {
        if (!string.IsNullOrWhiteSpace(msiValue))
            return ProposedField.Confirmed(Trim(msiValue), "msi-property-table");
        if (!string.IsNullOrWhiteSpace(scriptValue))
            return ProposedField.Confirmed(Trim(scriptValue), scriptSource ?? "compiled-script");
        if (!string.IsNullOrWhiteSpace(metadataValue))
            return ProposedField.Predicted(Trim(metadataValue), metadataSource ?? "framework-manifest");
        if (!string.IsNullOrWhiteSpace(versionInfoValue))
            return ProposedField.Predicted(Trim(versionInfoValue), "pe-version-info");
        return isMsi ? ProposedField.Confirmed("", "msi-property-table") : ProposedField.Empty;
    }

    /// <summary>
    /// Inno Setup and some MSI authoring tools emit fixed-width space-padded strings in
    /// the PE resource; untrimmed, the padding leaks into the predicted registry key.
    /// </summary>
    private static string Trim(string? value) => (value ?? "").Trim();

    private static ProposedField MakeField(string value, bool isMsi, string source) =>
        isMsi ? ProposedField.Confirmed(value, source) : ProposedField.Predicted(value, source);

    private static string Pick(string? over, string fallback) =>
        string.IsNullOrWhiteSpace(over) ? fallback : over;

    private static FileVersionInfoView? TryReadVersionInfo(string path)
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return new FileVersionInfoView
            {
                ProductName = vi.ProductName,
                FileDescription = vi.FileDescription,
                ProductVersion = vi.ProductVersion,
                FileVersion = vi.FileVersion,
                CompanyName = vi.CompanyName
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a package manifest's architecture name onto the engine's spelling.
    /// Returns an empty string for an absent or unrecognised value, which leaves
    /// the PE architecture in place.
    /// </summary>
    private static string NormalizePackageArchitecture(string? value) => (value ?? "").ToLowerInvariant() switch
    {
        "x86" => "x86",
        "x64" => "x64",
        "arm64" => "ARM64",
        _ => ""
    };

    private static string NormalizeMsiArchitecture(string template) =>
        template.StartsWith("Intel", StringComparison.OrdinalIgnoreCase) || template == "x86"
            ? "x86"
            : template.Equals("Arm64", StringComparison.OrdinalIgnoreCase)
                ? "ARM64"
                : "x64";

    /// <summary>
    /// Predicts the Add/Remove Programs key the installer writes. Returns null when the
    /// engine writes no ARP entry, or when the key name is script-defined and nothing in
    /// the file reveals it — an omitted field reads as a genuine gap, an invented one does not.
    /// </summary>
    public static UninstallKeyPrediction? PredictUninstallRegistryKey(
        string engine, string appName, string productCode, MsiProperties? msi, string architecture)
    {
        var is32Bit = architecture.Equals("x86", StringComparison.OrdinalIgnoreCase);
        var root = is32Bit ? ArpHklm32 : ArpHklm64;

        switch (engine)
        {
            case "NativeMSI":
            case "MSP":
                if (string.IsNullOrEmpty(productCode)) return null;
                if (msi?.IsPerUser == true)
                {
                    return new UninstallKeyPrediction
                    {
                        Path = $@"{ArpHkcu}\{productCode}",
                        Hive = "HKCU",
                        Source = "msi-property-table",
                        Note = "The MSI Property table indicates a per-user install (ALLUSERS / MSIINSTALLPERUSER); the ARP key is under HKCU."
                    };
                }
                return new UninstallKeyPrediction
                {
                    Path = $@"{root}\{productCode}",
                    Hive = "HKLM",
                    Source = "msi-property-table",
                    Note = is32Bit ? "A 32-bit MSI on x64 Windows is redirected to WOW6432Node." : ""
                };

            case "InstallShield":
            case "AdvancedInstaller":
                if (string.IsNullOrEmpty(productCode)) return null;
                return new UninstallKeyPrediction
                {
                    Path = $@"{root}\{productCode}",
                    Hive = "HKLM",
                    Source = $"engine-convention:{engine}",
                    Note = "The EXE wraps an MSI; the ARP key is the wrapped ProductCode."
                };

            case "WixBurn":
                if (!string.IsNullOrEmpty(productCode))
                {
                    return new UninstallKeyPrediction
                    {
                        Path = $@"{root}\{productCode}",
                        Hive = "HKLM",
                        Source = "burn-manifest",
                        Note = "Burn registers the bundle under its BundleId; the inner MSI ProductCode is the fallback target."
                    };
                }
                return null;

            case "Squirrel":
                if (string.IsNullOrEmpty(appName)) return null;
                return new UninstallKeyPrediction
                {
                    Path = $@"{ArpHkcu}\{appName}",
                    Hive = "HKCU",
                    Source = "engine-convention:Squirrel",
                    Note = "Squirrel installs per-user to %LOCALAPPDATA% and registers under HKCU."
                };

            case "Velopack":
                // The key name is the embedded nuspec id; no other part of the
                // file supplies it, and a display title is not a substitute.
                return null;

            case "InnoSetup":
                if (string.IsNullOrEmpty(appName)) return null;
                return new UninstallKeyPrediction
                {
                    Path = $@"{root}\{appName}_is1",
                    Hive = "HKLM",
                    Source = "engine-convention:InnoSetup",
                    Note = "Inno Setup appends \"_is1\" to the script-defined AppId, which often equals AppName. Per-user installs go under HKCU."
                };

            case "NSIS":
            case "BitRock":
            case "PsadtV3":
            case "PsadtV4":
                if (string.IsNullOrEmpty(appName)) return null;
                return new UninstallKeyPrediction
                {
                    Path = $@"{root}\{appName}",
                    Hive = "HKLM",
                    Source = $"engine-convention:{engine}",
                    Note = "The uninstall key name is script-defined; the convention is DisplayName under HKLM. Per-user installs go under HKCU."
                };

            default:
                return null;
        }
    }

    private static StageDetection BuildDetection(
        string engine, bool isMsi, string productCode, string? arpPath,
        string appName, string version, string architecture, MsiProperties? msi)
    {
        if (engine is "MSIX" or "MsixBundle")
        {
            return new StageDetection
            {
                Type = "MSIX",
                FileName = appName,
                Is64Bit = !architecture.Equals("x86", StringComparison.OrdinalIgnoreCase)
            };
        }

        // A predicted key under HKCU is a per-user product; the manifest hive
        // decides which registry the detection reads on the device.
        var hive = arpPath?.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) == true ? "CurrentUser" : "LocalMachine";

        if (isMsi && !string.IsNullOrEmpty(productCode))
        {
            return new StageDetection
            {
                Type = "RegistryKeyValue",
                RegistryKeyRelative = $@"{ArpRelative}\{productCode}",
                ValueName = "DisplayVersion",
                ExpectedValue = version,
                Operator = "IsEquals",
                PropertyType = "Version",
                Is64Bit = msi?.IsPerUser != true && !architecture.Equals("x86", StringComparison.OrdinalIgnoreCase),
                Hive = msi?.IsPerUser == true ? "CurrentUser" : hive
            };
        }

        var relative = "";
        var is64 = !architecture.Equals("x86", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(arpPath))
        {
            relative = StripHive(arpPath);
            // A WOW6432Node prediction means the 32-bit registry view, which the
            // manifest expresses with Is64Bit rather than in the key path.
            if (relative.Contains("WOW6432Node\\", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative.Replace("WOW6432Node\\", "", StringComparison.OrdinalIgnoreCase);
                is64 = false;
            }
        }

        return new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = string.IsNullOrEmpty(relative) ? null : relative,
            ValueName = "DisplayVersion",
            ExpectedValue = version,
            Operator = "IsEquals",
            PropertyType = "Version",
            Is64Bit = is64,
            Hive = hive
        };
    }

    private static string StripHive(string path)
    {
        var index = path.IndexOf(":\\", StringComparison.Ordinal);
        return index < 0 ? path : path[(index + 2)..];
    }

    private static StageDetection CloneDetection(StageDetection source) => new()
    {
        Type = source.Type,
        RegistryKeyRelative = source.RegistryKeyRelative,
        ValueName = source.ValueName,
        ExpectedValue = source.ExpectedValue,
        Operator = source.Operator,
        PropertyType = source.PropertyType,
        FilePath = source.FilePath,
        FileName = source.FileName,
        Is64Bit = source.Is64Bit,
        Hive = source.Hive
    };

    private static string DetectionHintFor(string engine, string productCode, string appName) => engine switch
    {
        "NativeMSI" or "MSP" => string.IsNullOrEmpty(productCode)
            ? "MSI detection: use the ProductCode from the MSI Property table."
            : $"MSI ProductCode detection: {productCode}",
        "NSIS" => @"Registry uninstall key detection (HKLM\...\Uninstall\<DisplayName>) or file-version on the primary EXE.",
        "InnoSetup" => @"Registry uninstall key detection (HKLM\...\Uninstall\<AppId>_is1) or file-version on the primary EXE.",
        "InstallShield" => "Registry uninstall key detection or file-version on the primary EXE.",
        "WixBurn" => @"BundleUpgradeCode or the related MSI ProductCode under HKLM\...\Uninstall.",
        "BitRock" => @"Registry uninstall key detection keyed on DisplayName (BitRock writes HKLM\...\Uninstall\<AppName> by default).",
        "Squirrel" => @"Registry uninstall key in HKCU\...\Uninstall\<AppName> (per-user), or file-version on the installed EXE.",
        "Velopack" => @"Registry uninstall key in HKCU\...\Uninstall\<PackageId> (per-user); DisplayVersion is major.minor.patch without prerelease or build labels.",
        "MSIX" or "MsixBundle" => string.IsNullOrEmpty(appName)
            ? "MSIX family-name detection via Get-AppxPackage."
            : $"MSIX family-name detection via Get-AppxPackage -Name {appName}",
        "PsadtV3" or "PsadtV4" => "Registry uninstall key detection keyed on AppName/AppVersion (configured in the deployment script).",
        "SevenZipSfx" or "WinRarSfx" or "ZIP" => "Not a true installer; detection depends on the extracted payload.",
        _ => "Pick a file-version or registry-uninstall detection per the extracted payload."
    };

    private sealed class FileVersionInfoView
    {
        public string? ProductName { get; init; }
        public string? FileDescription { get; init; }
        public string? ProductVersion { get; init; }
        public string? FileVersion { get; init; }
        public string? CompanyName { get; init; }
    }
}

/// <summary>Values that replace those of a proposal; null keeps the proposal's value.</summary>
public sealed record DeploymentOverlay
{
    public ProposedField? AppName { get; init; }
    public ProposedField? Publisher { get; init; }
    public ProposedField? SoftwareVersion { get; init; }
    public ProposedField? InstallArgs { get; init; }
    public ProposedField? UninstallCommand { get; init; }
    /// <summary>The key with its hive prefix (HKLM:\ or HKCU:\).</summary>
    public ProposedField? UninstallRegistryKey { get; init; }
    /// <summary>Machine or User.</summary>
    public string? InstallContext { get; init; }
    public string? Architecture { get; init; }
    public string? Notes { get; init; }
    public string? DetectionHint { get; init; }
}

/// <summary>A predicted Add/Remove Programs key with the reasoning behind it.</summary>
public sealed class UninstallKeyPrediction
{
    public string Path { get; init; } = "";
    public string Hive { get; init; } = "HKLM";
    public string Source { get; init; } = "";
    public string Note { get; init; } = "";
}
