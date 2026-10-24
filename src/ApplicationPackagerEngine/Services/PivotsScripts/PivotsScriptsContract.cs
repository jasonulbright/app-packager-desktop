namespace ApplicationPackagerEngine.Services;

public enum RunTargetKind { Collection, Device, Pattern, List }

/// <param name="Text">A collection ID or exact name, a device name, a wildcard pattern, or a list of names (one per line).</param>
public sealed record RunTarget(RunTargetKind Kind, string Text);

/// <summary>The devices a target resolves to, read in bulk at one time.</summary>
/// <param name="CollectionId">The collection of a Collection target; null for the other kinds.</param>
/// <param name="IsBuiltInCollection">True for a collection whose ID starts with SMS, such as All Systems.</param>
/// <param name="Unmatched">The names of a list or device target that match no device.</param>
/// <param name="Truncated">True when more devices match than <paramref name="Devices"/> holds.</param>
public sealed record ResolvedTargets(
    IReadOnlyList<ResolvedDevice> Devices,
    string? CollectionId,
    string Description,
    bool IsBuiltInCollection,
    IReadOnlyList<string> Unmatched,
    bool Truncated)
{
    /// <summary>The number of devices that match; equal to the device count unless the result is truncated, -1 when not known.</summary>
    public int MatchedCount { get; init; } = -1;
}

/// <param name="IsOnline">The online state the site holds; null when not known.</param>
public sealed record ResolvedDevice(int ResourceId, string Name, bool? IsOnline = null);

/// <param name="State">Waiting, Approved or Declined (see <see cref="PivotsScriptStates"/>).</param>
/// <param name="Hash">The SHA-256 of the script text as hexadecimal text.</param>
/// <param name="Text">The script text; null when the list read did not include it.</param>
public sealed record SiteScript(
    string Guid,
    string Name,
    string Author,
    string Approver,
    string State,
    int Version,
    string Language,
    string Hash,
    IReadOnlyList<ScriptParameter> Parameters,
    string? Text);

/// <param name="Type">The type the site holds for the parameter, such as System.String or System.Int32.</param>
public sealed record ScriptParameter(string Name, string Type, bool Required, string? Default);

public enum PivotsChangeKind { RunPivot, RunScript, SubmitScript, ApproveScript, DenyScript, RemoveScript }

/// <summary>
/// One action of the page. PrepareAsync re-reads the script and the targets and fills
/// <paramref name="Impact"/>, <paramref name="Checks"/> and <paramref name="Fingerprint"/>; ApplyAsync runs only a prepared change.
/// </summary>
/// <param name="RunConfirmed">RunScript and RunPivot: the second confirmation. The service refuses a run without it.</param>
/// <param name="EveryDeviceAcknowledged">A built-in collection target: the operator accepts that the run reaches every device of the site.</param>
/// <param name="ScriptHash">The SHA-256 of the script text read at Prepare; Apply refuses a script whose text changed.</param>
public sealed record PivotsChange(
    PivotsChangeKind Kind,
    ResolvedTargets? Targets,
    string? QueryText,
    string? ScriptGuid,
    string? ScriptHash,
    IReadOnlyDictionary<string, string>? Parameters,
    string? SubmitName,
    string? SubmitText,
    string? Comment,
    bool RunConfirmed,
    bool EveryDeviceAcknowledged,
    IReadOnlyList<string> Impact,
    IReadOnlyList<PreflightCheck> Checks,
    string? Fingerprint)
{
    /// <summary>The site script as PrepareAsync read it, with its text; null before Prepare and for RunPivot.</summary>
    public SiteScript? Script { get; init; }
}

/// <param name="State">Sent, Running, Completed, Failed, TimedOut or NoResponse (see <see cref="PivotsDeviceStates"/>).</param>
/// <param name="Output">The script output or the query result text of the device.</param>
/// <param name="Message">A plain reason for a failed, timed-out or skipped device; starts with "Skipped" when nothing was sent.</param>
public sealed record DeviceResult(int ResourceId, string Name, string State, string Output, string Message);

/// <summary>One result row of a CMPivot query: the device and the values in the order of <see cref="PivotsOutcome.Columns"/>.</summary>
public sealed record PivotsResultRow(int ResourceId, string Device, IReadOnlyList<string> Values);

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
/// <param name="OperationId">The site operation of a run; null for the script actions and for a run that was not sent.</param>
/// <param name="Partial">True when the result set does not hold a final state for every device.</param>
/// <param name="SessionId">The saved session of a run; null when none was saved.</param>
public sealed record PivotsOutcome(
    PivotsChange Change,
    bool Succeeded,
    string Message,
    string? OperationId,
    IReadOnlyList<DeviceResult> Devices,
    bool Partial,
    string? SessionId)
{
    /// <summary>The column names of the CMPivot result rows; empty for a script run.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The CMPivot result rows of every device that answered.</summary>
    public IReadOnlyList<PivotsResultRow> Rows { get; init; } = [];

    /// <summary>The raw responses of the site, one text per response.</summary>
    public IReadOnlyList<string> Raw { get; init; } = [];

    /// <summary>SubmitScript: the GUID the create call returned, also when a later step failed; null when nothing was created.</summary>
    public string? CreatedScriptGuid { get; init; }
}

public static class PivotsDeviceStates
{
    public const string Sent = "Sent";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string TimedOut = "TimedOut";
    public const string NoResponse = "NoResponse";
    public const string Skipped = "Skipped";
}

public static class PivotsScriptStates
{
    public const string Waiting = "Waiting";
    public const string Approved = "Approved";
    public const string Declined = "Declined";
}

public interface IPivotsScriptsService
{
    /// <summary>Resolves a target with bulk reads: one query per collection for its members, never one call per device.</summary>
    Task<ResolvedTargets> ResolveTargetsAsync(RunTarget target, CancellationToken ct);

    Task<IReadOnlyList<SiteScript>> LoadScriptsAsync(CancellationToken ct);

    /// <summary>Re-reads the script and the targets, fills Impact, Checks, the Fingerprint and the Script.</summary>
    Task<PivotsChange> PrepareAsync(PivotsChange request, CancellationToken ct);

    /// <summary>Sends the prepared change. A run returns the devices as Sent with the OperationId; the results come from WaitForResultsAsync.</summary>
    Task<PivotsOutcome> ApplyAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct);

    /// <summary>Polls the site for the results of a run for the default window.</summary>
    Task<PivotsOutcome> WaitForResultsAsync(string operationId, IProgress<PivotsOutcome> progress, CancellationToken ct);

    /// <summary>
    /// Polls the site for the results of a run for at most <paramref name="window"/>. A cancel ends the
    /// wait: the service returns the partial result set or throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PivotsOutcome> WaitForResultsAsync(string operationId, TimeSpan window, IProgress<PivotsOutcome> progress, CancellationToken ct) =>
        WaitForResultsAsync(operationId, progress, ct);

    /// <summary>Checks the syntax of a PowerShell script text; an empty list when the text parses.</summary>
    Task<IReadOnlyList<string>> CheckSyntaxAsync(string text, CancellationToken ct) =>
        throw new NotSupportedException("The syntax check is not available.");

    Task<string> TestConnectionAsync(CancellationToken ct);
}

/// <summary>A saved run as History lists it.</summary>
public sealed record PivotsSessionSummary(string SessionId, DateTime Started, PivotsChangeKind Kind, string Description, int DeviceCount, string State);

/// <summary>A saved run: written before the first request goes out, then with the responses.</summary>
public sealed record PivotsSession(
    string SessionId,
    DateTime Started,
    PivotsChangeKind Kind,
    string Description,
    ResolvedTargets? Targets,
    string? QueryText,
    string? ScriptGuid,
    string? ScriptHash,
    IReadOnlyDictionary<string, string>? Parameters,
    PivotsOutcome? Outcome);

public interface IPivotsSessionStore
{
    /// <summary>Saves the session as one JSON file and returns its path.</summary>
    string Save(PivotsSession session);

    /// <summary>The newest <paramref name="max"/> sessions, newest first.</summary>
    IReadOnlyList<PivotsSessionSummary> List(int max);

    /// <summary>The whole session; null when the file is gone or cannot be read.</summary>
    PivotsSession? Read(string sessionId);

    string Folder { get; }
}

/// <summary>One exported view: the column names and the text of each cell, as the page shows them.</summary>
/// <param name="Partial">The rows come from a partial result set; <paramref name="Note"/> then says so.</param>
public sealed record PivotsReport(string View, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Partial, string? Note)
{
    /// <summary>The filters of the view as text; empty when no filter is set.</summary>
    public string Filter { get; init; } = "";
}

public interface IPivotsReportWriter
{
    string WriteCsv(PivotsReport report);
    string WriteJson(PivotsReport report);
    string Folder { get; }
}

/// <summary>One line of the Pivots and Scripts audit log, with the hash chain of the shared audit file.</summary>
/// <param name="Event">RunPivot, RunScript, SubmitScript, ApproveScript, DenyScript or RemoveScript.</param>
/// <param name="Phase">Started or Finished.</param>
public sealed record PivotsAuditRecord(
    DateTime Timestamp,
    string User,
    string Event,
    string Phase,
    string Result,
    string RunId,
    int DeviceCount,
    string Target,
    string? ScriptGuid,
    string? ScriptHash,
    string Message)
{
    public string Source { get; init; } = "";
    public string? SessionId { get; init; }
    public bool? Verified { get; init; }
    public bool HasChain { get; init; }
    public bool AfterChainStart { get; init; }
    public long? ChainBrokenAt { get; init; }
}

public interface IPivotsAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(PivotsAuditRecord record);

    /// <summary>The last <paramref name="max"/> records, oldest first.</summary>
    IReadOnlyList<PivotsAuditRecord> Read(int max);

    string Path { get; }

    bool OlderRecordsDropped => false;
}

public enum PivotsLibraryKind { Query, Script }

/// <param name="BuiltIn">True for a sample that ships with the app; false for a file of the user library folder.</param>
/// <param name="Verified">True when a built-in sample matches its recorded hash.</param>
public sealed record PivotsLibraryItem(PivotsLibraryKind Kind, string Name, string Text, bool BuiltIn, bool Verified);

public interface IPivotsLibrary
{
    /// <summary>The built-in samples that pass their hash check, then the files of the user folder when one is set.</summary>
    IReadOnlyList<PivotsLibraryItem> Load(PivotsLibraryKind kind, string? userFolder);
}

/// <summary>A plain refusal or failure of the Pivots and Scripts services; the message is safe to show.</summary>
public sealed class PivotsScriptsServiceException(string message) : Exception(message);
