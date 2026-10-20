namespace ApplicationPackagerEngine.Services;

public sealed record AnalysisLimits(long MaxFileBytes, int MaxEntries, long MaxExtractBytes, int MaxDepth /* 5 */, TimeSpan Timeout, int MaxStrings);

public sealed record AnalysisFact(string Name, string Value, string Confidence /* Confirmed, Predicted, None */, string Source);

public sealed record SignatureFact(string Status, string Subject, string Issuer, string Thumbprint, bool Timestamped);

public sealed record InstallMode(string Name /* PerMachine, PerUser */, string Switch, string InstallFolder, string UninstallCommand, string ArpKey, string Hive, string Context);

public sealed record MetadataBlock(string Title, IReadOnlyList<AnalysisFact> Facts);

public sealed record EffectiveDetection(string ProductCode, string DisplayName, string DisplayVersion, string ArpKey, string Explanation);

public sealed record AnalysisReport(string Path, string FileName, long Size, string Sha256, string Type, string Architecture, SignatureFact? Signature,
    IReadOnlyList<AnalysisFact> Deployment, IReadOnlyList<InstallMode> Modes, IReadOnlyList<MetadataBlock> Metadata,
    IReadOnlyList<AnalysisFact> MsiProperties, EffectiveDetection? PostPatch, IReadOnlyList<string> Notes /* plain: not decoded, encrypted, truncated, limit reached */);

public sealed record PayloadEntry(string Path, long Size, bool IsDirectory, string Kind /* File, InnerInstaller */, string InnerType);

public sealed record RegistryWrite(string Origin /* MsiRegistryTable, NsisScript, InnoSetup */, string Hive, string Key, string Name, string Value, string ValueType, string Condition);

public sealed record InterestingString(string Text, string Encoding /* Ascii, Utf16 */, long Offset, string Category /* Url, Path, Registry, Command, Guid, Version, Other */);

public interface IInstallerAnalysisService
{
    Task<AnalysisReport> AnalyzeAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct);
    Task<IReadOnlyList<PayloadEntry>> ListPayloadAsync(string path, AnalysisLimits limits, CancellationToken ct);      // needs 7-Zip for archives; MSI cab/streams and MSIX/nupkg zip entries without it
    Task<IReadOnlyList<RegistryWrite>> ReadRegistryWritesAsync(string path, AnalysisLimits limits, CancellationToken ct);
    Task<IReadOnlyList<InterestingString>> ReadStringsAsync(string path, AnalysisLimits limits, CancellationToken ct);
    Task<string> ExtractEntryAsync(string path, PayloadEntry entry, string sessionFolder, AnalysisLimits limits, CancellationToken ct); // returns the extracted path inside the sandbox
    Task<string> ComputeSha256Async(string path, CancellationToken ct);
}

public interface IAnalysisSandbox { string NewSession(); void Release(string session); void ReleaseAll(); void CleanOldSessions(TimeSpan olderThan); }

public interface IInstallerReportWriter { string Summary(AnalysisReport r); string Json(AnalysisReport r, bool includeKeyMaterial /* false */); string WriteCsv(AnalysisReport r, IReadOnlyList<RegistryWrite> reg); string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry> payload, IReadOnlyList<RegistryWrite> reg); }
