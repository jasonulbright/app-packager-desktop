using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

public sealed record InstalledApplication(string Name, string Publisher, string Version, string InstallDate,
    string Source /* "HKLM 64-bit", "HKLM 32-bit", "HKCU" */, string KeyPath, string UninstallString, string QuietUninstallString,
    string InstallLocation);
public interface IInstalledApplicationsService { IReadOnlyList<InstalledApplication> Enumerate(); }

public enum DetectionClauseKind { RegistryKeyValue, RegistryKey, File, Script, Compound }
public sealed record DetectionTestRequest(StageDetection Detection, bool Negative, RegistryViewChoice View, TimeSpan ScriptTimeout);
public enum RegistryViewChoice { Registry64, Registry32 }
public sealed record DetectionTestResult(bool Detected, bool Passed /* Detected, or not Detected when Negative */,
    string Summary, IReadOnlyList<DetectionClauseResult> Clauses, string? ScriptStdout, string? ScriptStderr, int? ScriptExitCode,
    string RunContext /* plain text, for example "Windows PowerShell, current user, not SYSTEM" */);
public sealed record DetectionClauseResult(string Description, bool Detected, string Detail);
public interface IDetectionTestService { Task<DetectionTestResult> RunAsync(DetectionTestRequest request, CancellationToken ct); }

public sealed record DetectionHistoryEntry(Guid Id, DateTimeOffset Time, string Kind, string Description, bool Negative, bool Passed, string Summary, StageDetection Detection);
public interface IDetectionHistoryStore { IReadOnlyList<DetectionHistoryEntry> All(); void Add(DetectionHistoryEntry entry); void Clear(); }
public interface IDetectionReportWriter { string WriteCsv(IReadOnlyList<DetectionHistoryEntry> entries); string WriteHtml(IReadOnlyList<DetectionHistoryEntry> entries); /* returns the written file path */ }
