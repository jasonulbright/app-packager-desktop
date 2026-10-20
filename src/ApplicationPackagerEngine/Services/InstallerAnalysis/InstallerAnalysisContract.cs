namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Bounds for one analysis. Every parser, listing, extraction and string scan stops at
/// these limits and reports the stop as a plain note or a plain failure.
/// </summary>
public sealed record AnalysisLimits(long MaxFileBytes, int MaxEntries, long MaxExtractBytes, int MaxDepth, TimeSpan Timeout, int MaxStrings)
{
    /// <summary>
    /// The highest expanded-to-compressed ratio an archive entry may have before it is refused.
    /// Deflate reaches about 1030 on uniform data, so the limit sits well below that maximum.
    /// </summary>
    public int MaxCompressionRatio { get; init; } = DefaultCompressionRatio;

    public const int DefaultCompressionRatio = 200;

    /// <summary>Shortest printable run the string scan reports.</summary>
    public int MinStringLength { get; init; } = 6;

    public static AnalysisLimits Default { get; } = new(
        MaxFileBytes: 8L * 1024 * 1024 * 1024,
        MaxEntries: 100_000,
        MaxExtractBytes: 4L * 1024 * 1024 * 1024,
        MaxDepth: 5,
        Timeout: TimeSpan.FromMinutes(5),
        MaxStrings: 20_000);
}

/// <summary>One labelled value. Confidence is Confirmed, Predicted or None.</summary>
public sealed record AnalysisFact(string Name, string Value, string Confidence, string Source);

/// <summary>Authenticode facts. Status uses the Get-AuthenticodeSignature names (Valid, NotSigned, HashMismatch, NotTrusted, UnknownError).</summary>
public sealed record SignatureFact(string Status, string Subject, string Issuer, string Thumbprint, bool Timestamped);

/// <summary>One install branch resolved end to end. Name is PerMachine or PerUser.</summary>
public sealed record InstallMode(string Name, string Switch, string InstallFolder, string UninstallCommand, string ArpKey, string Hive, string Context)
{
    /// <summary>True for the branch the installer takes without a mode switch.</summary>
    public bool IsDefault { get; init; }

    /// <summary>"64" or "32" for an HKLM key; empty otherwise.</summary>
    public string View { get; init; } = "";

    /// <summary>The full silent install command line of this branch.</summary>
    public string InstallCommand { get; init; } = "";
}

/// <summary>A titled group of format-specific facts.</summary>
public sealed record MetadataBlock(string Title, IReadOnlyList<AnalysisFact> Facts);

/// <summary>The Add/Remove Programs entry a base MSI writes once a patch applies.</summary>
public sealed record EffectiveDetection(string ProductCode, string DisplayName, string DisplayVersion, string ArpKey, string Explanation)
{
    /// <summary>The base package version before the patch; empty when no base MSI was read.</summary>
    public string BaseVersion { get; init; } = "";

    /// <summary>The patch file name the target comes from.</summary>
    public string PatchFile { get; init; } = "";
}

/// <summary>
/// The result of <see cref="IInstallerAnalysisService.AnalyzeAsync"/>. Notes are plain sentences:
/// not decoded, encrypted, truncated, limit reached.
/// </summary>
public sealed record AnalysisReport(string Path, string FileName, long Size, string Sha256, string Type, string Architecture, SignatureFact? Signature,
    IReadOnlyList<AnalysisFact> Deployment, IReadOnlyList<InstallMode> Modes, IReadOnlyList<MetadataBlock> Metadata,
    IReadOnlyList<AnalysisFact> MsiProperties, EffectiveDetection? PostPatch, IReadOnlyList<string> Notes)
{
    /// <summary>A detection suggestion keyed to the installer type.</summary>
    public string DetectionHint { get; init; } = "";

    /// <summary>The manifest requestedExecutionLevel of an executable; empty otherwise.</summary>
    public string RequestedExecutionLevel { get; init; } = "";

    /// <summary>The installer type for display (Type is the stable identifier: NativeMSI, InnoSetup, ...).</summary>
    public string TypeName { get; init; } = "";

    /// <summary>The payload list when the analysis already listed it (to find an inner patch); null otherwise.</summary>
    public IReadOnlyList<PayloadEntry>? Payload { get; init; }
}

/// <summary>One payload entry. Kind is File or InnerInstaller; InnerType names the inner format (MSI, MSP, EXE, CAB, ...).</summary>
public sealed record PayloadEntry(string Path, long Size, bool IsDirectory, string Kind, string InnerType)
{
    /// <summary>What the entry is inside its container, for display.</summary>
    public string Description { get; init; } = "";
}

/// <summary>
/// One registry change an installer makes. Origin is MsiRegistryTable, NsisScript or InnoSetup.
/// Values keep the installer's own notation ([PROPERTY], $INSTDIR, {app}).
/// </summary>
public sealed record RegistryWrite(string Origin, string Hive, string Key, string Name, string Value, string ValueType, string Condition)
{
    /// <summary>write, create key, delete value, delete key.</summary>
    public string Action { get; init; } = "write";

    /// <summary>"64", "32" or empty: the registry view in effect at the change.</summary>
    public string View { get; init; } = "";

    /// <summary>The MSI component, NSIS section or Inno Setup flags the change belongs to.</summary>
    public string Section { get; init; } = "";
}

/// <summary>A printable string found in the file. Encoding is Ascii or Utf16; Category is Url, Path, Registry, Command, Guid, Version or Other.</summary>
public sealed record InterestingString(string Text, string Encoding, long Offset, string Category);

/// <summary>What a payload extraction to a chosen folder wrote.</summary>
public sealed record PayloadExtractResult(string Folder, int Files, long Bytes, IReadOnlyList<string> Skipped);

/// <summary>A failure with a fixed, plain message that the page shows as it is.</summary>
public class InstallerAnalysisException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The payload of this installer type is listed with 7-Zip, and 7-Zip was not found.</summary>
public sealed class SevenZipNotFoundException()
    : InstallerAnalysisException("7-Zip is not installed; the payload of this installer type is listed with 7-Zip. Install 7-Zip to list and extract it.");

/// <summary>The payload of this installer type cannot be listed by any reader the analysis has; the message says why.</summary>
public sealed class PayloadNotListedException(string message) : InstallerAnalysisException(message)
{
    public const string InnoSetupMessage = "Inno Setup compresses its payload into the setup's own data streams, and 7-Zip has no Inno Setup support, so the payload is not listed or extracted. The Overview and Registry Settings tabs read the Inno Setup header.";
}

public interface IInstallerAnalysisService
{
    Task<AnalysisReport> AnalyzeAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct);

    /// <summary>Needs 7-Zip for executable archives; MSI streams and MSIX, nupkg and zip entries are listed without it.</summary>
    Task<IReadOnlyList<PayloadEntry>> ListPayloadAsync(string path, AnalysisLimits limits, CancellationToken ct);

    Task<IReadOnlyList<RegistryWrite>> ReadRegistryWritesAsync(string path, AnalysisLimits limits, CancellationToken ct);

    Task<IReadOnlyList<InterestingString>> ReadStringsAsync(string path, AnalysisLimits limits, CancellationToken ct);

    /// <summary>Returns the extracted path inside the sandbox session folder.</summary>
    Task<string> ExtractEntryAsync(string path, PayloadEntry entry, string sessionFolder, AnalysisLimits limits, CancellationToken ct);

    Task<string> ComputeSha256Async(string path, CancellationToken ct);

    /// <summary>
    /// Extracts the given entries (all entries when null) to a folder the user chose, with the
    /// sandbox path rules. A folder inside the sandbox or a linked folder is refused.
    /// </summary>
    Task<PayloadExtractResult> ExtractPayloadAsync(string path, IReadOnlyList<PayloadEntry>? entries, string targetFolder, AnalysisLimits limits, CancellationToken ct) =>
        throw new NotSupportedException();

    /// <summary>
    /// The plain reason the payload cannot be extracted (7-Zip not found, a format with no payload
    /// reader), or null when extraction can be tried. Reads only the file head; lists nothing.
    /// </summary>
    Task<string?> PayloadUnavailableReasonAsync(string path, AnalysisLimits limits, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>The registry changes with plain notes (format not read, header not decoded, limit reached).</summary>
    async Task<RegistryReadResult> ReadRegistryAsync(string path, AnalysisLimits limits, CancellationToken ct) =>
        new(await ReadRegistryWritesAsync(path, limits, ct).ConfigureAwait(false), []);
}

/// <summary>The registry changes of one installer and the plain notes about what could not be read.</summary>
public sealed record RegistryReadResult(IReadOnlyList<RegistryWrite> Writes, IReadOnlyList<string> Notes);

public interface IAnalysisSandbox
{
    /// <summary>Creates a new empty session folder and returns its path.</summary>
    string NewSession();

    void Release(string session);

    void ReleaseAll();

    void CleanOldSessions(TimeSpan olderThan);

    /// <summary>The folder every session lives under.</summary>
    string Root => "";
}

public interface IInstallerReportWriter
{
    string Summary(AnalysisReport r);

    /// <summary>A ConfigMgr digest. Key material of an .intunewin is never written; the flag is accepted and ignored.</summary>
    string Json(AnalysisReport r, bool includeKeyMaterial);

    /// <summary>Writes a new CSV file and returns its path.</summary>
    string WriteCsv(AnalysisReport r, IReadOnlyList<RegistryWrite> reg);

    /// <summary>Writes a new HTML file and returns its path.</summary>
    string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry> payload, IReadOnlyList<RegistryWrite> reg);

    /// <summary>
    /// Writes a new HTML file and returns its path. A null <paramref name="payload"/> writes the plain
    /// line "The payload was not listed: &lt;reason&gt;." in place of the payload table.
    /// </summary>
    string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry>? payload, IReadOnlyList<RegistryWrite> reg, string notListedReason) =>
        WriteHtml(r, payload ?? [], reg);

    /// <summary>The folder new reports are written to.</summary>
    string Folder => "";
}
