using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>One Add or Remove Programs entry read from this computer.</summary>
/// <param name="Source">"HKLM 64-bit", "HKLM 32-bit" or "HKCU".</param>
/// <param name="KeyPath">The full key path with its hive prefix, for example HKLM\SOFTWARE\...\Uninstall\{GUID}.</param>
public sealed record InstalledApplication(
    string Name,
    string Publisher,
    string Version,
    string InstallDate,
    string Source,
    string KeyPath,
    string UninstallString,
    string QuietUninstallString,
    string InstallLocation);

public interface IInstalledApplicationsService
{
    /// <summary>
    /// The entries of the three uninstall keys, sorted by name; an entry in two hives appears once for each hive.
    /// An entry with SystemComponent=1 is left out unless <paramref name="includeSystemComponents"/> is true.
    /// </summary>
    IReadOnlyList<InstalledApplication> Enumerate(bool includeSystemComponents = false);
}

public enum DetectionClauseKind
{
    RegistryKeyValue,
    RegistryKey,
    File,
    Script,
    Compound
}

public enum RegistryViewChoice
{
    Registry64,
    Registry32
}

/// <param name="View">
/// The registry view and file system view of a single registry or file clause. It replaces
/// <see cref="StageDetection.Is64Bit"/> of that clause. The clauses of a compound detection
/// keep their own views.
/// </param>
/// <param name="ScriptTimeout">The limit for each script clause; zero or less selects the default.</param>
public sealed record DetectionTestRequest(
    StageDetection Detection,
    bool Negative,
    RegistryViewChoice View,
    TimeSpan ScriptTimeout);

/// <param name="Detected">True only when the detection evaluated to detected.</param>
/// <param name="Passed">
/// Detected, or not detected when the request is negative. A clause that could not be
/// evaluated (unsupported operator or kind, read failure, script timeout) fails the test
/// in both modes.
/// </param>
/// <param name="RunContext">The account and host that ran the test, as plain text.</param>
public sealed record DetectionTestResult(
    bool Detected,
    bool Passed,
    string Summary,
    IReadOnlyList<DetectionClauseResult> Clauses,
    string? ScriptStdout,
    string? ScriptStderr,
    int? ScriptExitCode,
    string RunContext);

/// <param name="Detected">True only when this clause evaluated to detected.</param>
/// <param name="Detail">The value read, the comparison and its outcome, or the reason the clause could not be evaluated.</param>
public sealed record DetectionClauseResult(string Description, bool Detected, string Detail);

public interface IDetectionTestService
{
    /// <summary>
    /// Evaluates the detection on this computer. Cancellation ends a running script's
    /// process tree and throws <see cref="OperationCanceledException"/>; the script
    /// timeout does not throw and returns a failed result.
    /// </summary>
    Task<DetectionTestResult> RunAsync(DetectionTestRequest request, CancellationToken ct);
}

public sealed record DetectionHistoryEntry(
    Guid Id,
    DateTimeOffset Time,
    string Kind,
    string Description,
    bool Negative,
    bool Passed,
    string Summary,
    StageDetection Detection);

public interface IDetectionHistoryStore
{
    /// <summary>The stored entries, newest first.</summary>
    IReadOnlyList<DetectionHistoryEntry> All();

    void Add(DetectionHistoryEntry entry);

    void Clear();
}

public interface IDetectionReportWriter
{
    /// <summary>Writes the entries to a new CSV file and returns its path.</summary>
    string WriteCsv(IReadOnlyList<DetectionHistoryEntry> entries);

    /// <summary>Writes the entries to a new HTML file and returns its path.</summary>
    string WriteHtml(IReadOnlyList<DetectionHistoryEntry> entries);
}
