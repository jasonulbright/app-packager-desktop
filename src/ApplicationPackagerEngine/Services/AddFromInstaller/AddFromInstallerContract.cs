namespace ApplicationPackagerEngine.Services.AddFromInstaller;

/// <summary>How a proposed value was found.</summary>
public enum Evidence
{
    /// <summary>Read from the installer.</summary>
    Confirmed,
    /// <summary>Taken from a table or a documented convention; not read from the installer.</summary>
    Predicted,
    /// <summary>Typed by the user.</summary>
    Manual,
    /// <summary>No value could be derived.</summary>
    None
}

/// <param name="Source">Where the value comes from: an installer field, a table name or a documentation URL.</param>
public sealed record ProposedValue<T>(T Value, Evidence Evidence, string? Source);

public enum DetectionKind { RegistryVersion, MsiProductCode, FileVersion, FileExists, RegistryExists, Compound, None }

/// <param name="Values">The fields of the rule; the key names are in <see cref="DetectionValueKeys"/>.</param>
public sealed record DetectionProposal(
    DetectionKind Kind,
    string Description,
    IReadOnlyDictionary<string, string> Values,
    Evidence Evidence,
    string? Source);

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
    IReadOnlyList<string> Notes);

public sealed record AddSaveResult(string DefinitionPath, string DefinitionId, IReadOnlyList<string> Warnings);

/// <param name="DefinitionsRoot">The folder of the user definitions; an existing file is never overwritten.</param>
/// <param name="AcceptPredicted">The user accepted every Predicted value of the reviewed result.</param>
public sealed record AddSaveOptions(string DefinitionsRoot, bool AcceptPredicted);

/// <param name="Url">The resolved download URL; no version token is left in it.</param>
/// <param name="FileName">The file name to save as, or null to take it from the URL.</param>
/// <param name="DownloadRoot">The download root of the settings; the installer is saved under it once.</param>
/// <param name="Headers">Request headers of the version scraper, or null.</param>
public sealed record DownloadRequest(string Url, string? FileName, string DownloadRoot, IReadOnlyDictionary<string, string>? Headers);

public interface IAddFromInstallerService
{
    Task<AddAnalysisResult> AnalyzeAsync(string installerPath, IProgress<string> progress, CancellationToken ct);

    Task<AddSaveResult> SaveAsync(AddAnalysisResult reviewed, AddSaveOptions options, CancellationToken ct);

    /// <summary>Downloads the installer once and returns its local path.</summary>
    Task<string> FetchInstallerAsync(DownloadRequest request, IProgress<string> progress, CancellationToken ct);
}
