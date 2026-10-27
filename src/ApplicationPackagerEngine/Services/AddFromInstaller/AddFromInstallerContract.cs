namespace ApplicationPackagerEngine.Services.AddFromInstaller;

/// <summary>Where a proposed value comes from.</summary>
public enum Evidence
{
    /// <summary>Read from the installer file.</summary>
    Confirmed,
    /// <summary>Taken from a switch table, an engine convention or vendor documentation; not read from the file.</summary>
    Predicted,
    /// <summary>Entered by the user.</summary>
    Manual,
    /// <summary>No value could be derived.</summary>
    None
}

/// <summary>One proposed value. Source names the table, the file structure or the documentation URL the value comes from.</summary>
public sealed record ProposedValue<T>(T Value, Evidence Evidence, string? Source)
{
    /// <summary>True when the value is not verified: Predicted, or None.</summary>
    public bool NotVerified => Evidence is Evidence.Predicted or Evidence.None;
}

public enum DetectionKind
{
    /// <summary>A registry value compared as a version (the Add/Remove Programs DisplayVersion).</summary>
    RegistryVersion,
    /// <summary>The Add/Remove Programs key of an MSI ProductCode.</summary>
    MsiProductCode,
    FileVersion,
    FileExists,
    RegistryExists,
    /// <summary>Two or more clauses joined by And or Or.</summary>
    Compound,
    /// <summary>An MSI patch code applied to its target product.</summary>
    MsiPatchCode,
    /// <summary>An MSIX or APPX package identity.</summary>
    MsixPackage,
    None
}

/// <summary>
/// A proposed detection rule. Values keys: Hive (HKLM or HKCU), Key (relative to the hive), ValueName, Operator,
/// Expected, Is64Bit (true or false), FilePath, FileName, ProductCode, PatchCode, PackageName, Connector.
/// A registry proposal never carries an empty Key.
/// </summary>
public sealed record DetectionProposal(DetectionKind Kind, string Description, IReadOnlyDictionary<string, string> Values, Evidence Evidence, string? Source)
{
    /// <summary>The clauses of a Compound proposal; empty otherwise.</summary>
    public IReadOnlyList<DetectionProposal> Clauses { get; init; } = [];

    private readonly bool _isDefault;

    /// <summary>True when no rule could be derived and the proposal is the default placeholder; always true for None and for a Compound with no clauses.</summary>
    public bool IsDefault
    {
        get => _isDefault || Kind == DetectionKind.None || (Kind == DetectionKind.Compound && Clauses.Count == 0);
        init => _isDefault = value;
    }

    public static DetectionProposal NoneDerived(string reason) =>
        new(Kind: DetectionKind.None, Description: reason, Values: new Dictionary<string, string>(), Evidence: Evidence.None, Source: null) { IsDefault = true };
}

/// <summary>Facts of an MSI patch (.msp).</summary>
public sealed record PatchFacts(string PatchCode, string Title, IReadOnlyList<string> TargetProductCodes, string? TargetProductName, string? PostPatchVersion, string BaseProductWarning)
{
    public string? Classification { get; init; }
    public string? AllowRemoval { get; init; }
}

public enum PackageTrust
{
    /// <summary>Signed, the signature verifies and the certificate chains to a root this machine trusts.</summary>
    Trusted,
    /// <summary>Signed and the signature verifies, but this machine does not trust the certificate.</summary>
    Untrusted,
    /// <summary>The package carries no signature.</summary>
    Unsigned,
    /// <summary>The signature does not verify against the package contents.</summary>
    Invalid,
    Unknown
}

/// <summary>Facts of an MSIX or APPX package or bundle.</summary>
public sealed record PackageFacts(string IdentityName, string Publisher, string Version, string Architecture, IReadOnlyList<string> Dependencies, bool IsBundle, PackageTrust Trust)
{
    public string? PublisherDisplayName { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? SignerSubject { get; init; }
    public string? SignerThumbprint { get; init; }

    /// <summary>The plain warning for an unsigned, invalid or untrusted package; null when the package is trusted.</summary>
    public string? TrustWarning { get; init; }
}

/// <summary>The proposal for one installer. Every critical value carries its evidence level.</summary>
public sealed record AddAnalysisResult(
    string FilePath,
    string InstallerType,
    ProposedValue<string> Framework,
    ProposedValue<string?> Vendor,
    ProposedValue<string?> Name,
    ProposedValue<string?> Version,
    ProposedValue<string> InstallArguments,
    ProposedValue<string> UninstallArguments,
    ProposedValue<string> InstallContext,
    DetectionProposal Detection,
    bool NeedsReview,
    string? ReviewReason,
    IReadOnlyList<string> Notes)
{
    /// <summary>The installer type for display, such as "Inno Setup" or "WiX Burn bundle".</summary>
    public string? InstallerTypeName { get; init; }

    public ProposedValue<string>? Architecture { get; init; }

    /// <summary>The full silent uninstall command line when one was derived (NSIS, Inno Setup, BitRock, MSIX).</summary>
    public ProposedValue<string>? UninstallCommand { get; init; }

    /// <summary>The Add/Remove Programs key the product writes, with the hive prefix (HKLM:\ or HKCU:\).</summary>
    public ProposedValue<string>? UninstallRegistryKey { get; init; }

    public ProposedValue<string?>? ProductCode { get; init; }

    /// <summary>Install modes the installer accepts (AllUsers, CurrentUser); one or none when it has no mode switch.</summary>
    public IReadOnlyList<string> InstallModes { get; init; } = [];

    public string? DefaultInstallMode { get; init; }

    public string? Sha256 { get; init; }

    public long FileSize { get; init; }

    /// <summary>Set for an MSI patch.</summary>
    public PatchFacts? Patch { get; init; }

    /// <summary>Set for an MSIX or APPX package or bundle.</summary>
    public PackageFacts? Package { get; init; }

    /// <summary>The deployment proposal the stage and Save as Application consume.</summary>
    public DroppedInstallerAnalysis? Deployment { get; init; }
}

/// <summary>
/// Where and how SaveAsync writes the definition. Web fields stay empty for a local file. A wizard value replaces
/// the analysis value of the same field unless the review edited that field (evidence Manual); null keeps the
/// reviewed value.
/// </summary>
public sealed record AddSaveOptions(string DefinitionsFolder)
{
    public string? ApplicationName { get; init; }
    public string? Vendor { get; init; }
    public string? Architecture { get; init; }
    /// <summary>The installer file name override; it may hold {version} tokens.</summary>
    public string? InstallerFileName { get; init; }
    public string? InstallerType { get; init; }
    public string? InstallerFramework { get; init; }
    /// <summary>Machine or User.</summary>
    public string? InstallContext { get; init; }
    /// <summary>Install arguments as the wizard writes them; when set and not edited in the review, the definition carries them as they are and no InstallerSettings.</summary>
    public string? InstallArgs { get; init; }
    public string? UninstallArgs { get; init; }

    public string? DownloadUrl { get; init; }
    public Models.VersionScraperConfig? VersionScraper { get; init; }
    public string? VendorUrl { get; init; }
    public string? CMName { get; init; }

    /// <summary>AllUsers or CurrentUser for an installer with a mode switch; null keeps the default mode.</summary>
    public string? InstallMode { get; init; }

    public IReadOnlyList<string>? RunningProcess { get; init; }
}

public sealed record AddSaveResult(string FilePath, string DefinitionId, IReadOnlyList<string> Warnings)
{
    /// <summary>True when the saved detection or any critical value still needs review.</summary>
    public bool NeedsReview { get; init; }

    public string? ReviewReason { get; init; }
}

/// <summary>A resolved download. Url has no version tokens left; FileName overrides the name taken from the URL.</summary>
public sealed record DownloadRequest(string Url, string? FileName, string DownloadRoot)
{
    public IReadOnlyList<string>? Headers { get; init; }
}

/// <summary>A file Add from file does not accept; the message is plain and shown as it is.</summary>
public sealed class AddFromInstallerRefusedException(string message) : InstallerAnalysisException(message);

public interface IAddFromInstallerService
{
    /// <summary>File extensions with a defined flow, lower case with the dot.</summary>
    IReadOnlyList<string> AcceptedExtensions { get; }

    /// <summary>The plain refusal message for a path with no defined flow, or null when the path is accepted.</summary>
    string? RefusalReason(string installerPath);

    /// <summary>Runs off the calling thread, bounded by the analysis limits; throws <see cref="InstallerAnalysisException"/> with a plain message.</summary>
    Task<AddAnalysisResult> AnalyzeAsync(string installerPath, IProgress<string> progress, CancellationToken ct);

    /// <summary>Writes a new user definition. An existing file is never overwritten.</summary>
    Task<AddSaveResult> SaveAsync(AddAnalysisResult reviewed, AddSaveOptions options, CancellationToken ct);

    /// <summary>Downloads the installer once under the download root and returns its path.</summary>
    Task<string> FetchInstallerAsync(DownloadRequest request, IProgress<string> progress, CancellationToken ct);
}
