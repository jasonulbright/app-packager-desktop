namespace ApplicationPackagerEngine.Services;

public enum RunTargetKind { Collection, Device, Pattern, List }

/// <summary>
/// What the operator typed as the target of a run. <paramref name="Text"/> is a collection ID or exact
/// name (Collection), one device name (Device), a name with * or ? wildcards (Pattern), or names
/// separated by commas, semicolons or lines (List).
/// </summary>
public sealed record RunTarget(RunTargetKind Kind, string Text);

/// <summary>
/// The devices a target resolved to, read from the site in bulk.
/// </summary>
/// <param name="CollectionId">The collection of a Collection target; null for the other kinds.</param>
/// <param name="Description">Plain text such as "Collection All Systems (SMS00001)" or "List of 3 names".</param>
/// <param name="IsBuiltInCollection">True for a collection whose ID starts with SMS: a run needs <see cref="PivotsChange.EveryDeviceAcknowledged"/>.</param>
/// <param name="Unmatched">List and Device targets: the names the site holds no device for.</param>
/// <param name="Truncated">True when the site holds more matching devices than <see cref="Devices"/> lists; <see cref="ResolvedTargets.TotalMatched"/> then says how many.</param>
public sealed record ResolvedTargets(
    IReadOnlyList<ResolvedDevice> Devices,
    string? CollectionId,
    string Description,
    bool IsBuiltInCollection,
    IReadOnlyList<string> Unmatched,
    bool Truncated)
{
    public RunTargetKind Kind { get; init; }

    /// <summary>The target text as resolved; Apply resolves a Collection target again from it.</summary>
    public string Text { get; init; } = "";

    /// <summary>The number of matching devices the site holds; equal to the device count unless truncated.</summary>
    public int TotalMatched { get; init; }

    /// <summary>The collection name of a Collection target; empty otherwise.</summary>
    public string CollectionName { get; init; } = "";

    public DateTime ResolvedUtc { get; init; }

    /// <summary>The devices without a ConfigMgr client: a run skips them.</summary>
    public int NonClientCount => Devices.Count(d => !d.IsClient);

    /// <summary>
    /// True when the target reaches every client of the site: its client count is at least the
    /// site's client count, or a collection has as many members as All Systems. A run then needs
    /// <see cref="PivotsChange.EveryDeviceAcknowledged"/>, as for a built-in collection.
    /// </summary>
    public bool CoversEverySiteDevice { get; init; }

    /// <summary>The number of devices with a ConfigMgr client that the site holds, read with the target; 0 when not read.</summary>
    public int SiteClientCount { get; init; }

    /// <summary>True when a run on the target needs the box "This runs on every device of the site".</summary>
    public bool ReachesEveryDevice => IsBuiltInCollection || CoversEverySiteDevice;
}

/// <param name="IsOnline">The site's fast channel state of the client; null when the site does not say.</param>
public sealed record ResolvedDevice(int ResourceId, string Name, bool? IsOnline = null)
{
    /// <summary>False for a resource that has no ConfigMgr client; a run skips it.</summary>
    public bool IsClient { get; init; } = true;
}

/// <summary>A script as the site holds it (SMS_Scripts).</summary>
/// <param name="State">Waiting, Approved or Declined; see <see cref="SiteScriptStates"/>.</param>
/// <param name="Hash">The SHA-256 of the script text as read, lower-case hex; empty when the text was not read.</param>
/// <param name="Text">The script text; null when the list was read without it.</param>
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
    string? Text)
{
    /// <summary>The ApprovalState of the site: 0 waiting, 1 declined, 3 approved.</summary>
    public int ApprovalState { get; init; }

    /// <summary>The site's comment on the last approval decision.</summary>
    public string Comment { get; init; } = "";

    public DateTime? LastUpdatedUtc { get; init; }

    /// <summary>True when the site's parameter definitions could not be read; the script cannot run from this page.</summary>
    public bool ParametersUnreadable { get; init; }

    /// <summary>The ScriptHash the site holds (SHA-256 that the site computes); a change of the text changes it.</summary>
    public string SiteHash { get; init; } = "";

    public bool IsApproved => State == SiteScriptStates.Approved;
}

/// <param name="Type">The site type: System.String or System.Int32.</param>
/// <param name="Default">The site's default value; null when none.</param>
public sealed record ScriptParameter(string Name, string Type, bool Required, string? Default)
{
    public string Description { get; init; } = "";
}

public static class SiteScriptStates
{
    public const string Waiting = "Waiting";
    public const string Approved = "Approved";
    public const string Declined = "Declined";
}

public enum PivotsChangeKind { RunPivot, RunScript, SubmitScript, ApproveScript, DenyScript, RemoveScript }

/// <summary>
/// One action of the page. PrepareAsync reads the script and the targets again, fills
/// <paramref name="Impact"/>, <paramref name="Checks"/> and <paramref name="Fingerprint"/>; ApplyAsync runs only a
/// change a Prepare of this service returned, and refuses when a flag the change needs is false.
/// </summary>
/// <param name="Targets">RunPivot and RunScript: the resolved devices at Prepare.</param>
/// <param name="QueryText">RunPivot: the CMPivot query.</param>
/// <param name="ScriptGuid">RunScript, ApproveScript, DenyScript, RemoveScript: the site script.</param>
/// <param name="ScriptHash">RunScript: the SHA-256 of the script text read at Prepare; Apply refuses when the text changed.</param>
/// <param name="Parameters">RunScript: the parameter values by name, sent to the site as typed values.</param>
/// <param name="SubmitName">SubmitScript: the script name.</param>
/// <param name="SubmitText">SubmitScript: the script text.</param>
/// <param name="Comment">ApproveScript and DenyScript: the comment the site stores with the decision.</param>
/// <param name="RunConfirmed">RunScript: the second confirmation that names the device count. The service refuses a script run without it.</param>
/// <param name="EveryDeviceAcknowledged">A run on a built-in collection or on a target that holds every client of the site: the box "this runs on every device of the site".</param>
/// <param name="Fingerprint">The prepared state; ApplyAsync refuses a change without one.</param>
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
    /// <summary>True when a check failed or could not be made: the change does not run.</summary>
    public bool Blocked => Checks.Any(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown);

    /// <summary>True for a run on client devices.</summary>
    public bool IsRun => Kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript;

    /// <summary>True when the plan needs <see cref="RunConfirmed"/>: every script run.</summary>
    public bool NeedsRunConfirmation => Kind == PivotsChangeKind.RunScript;

    /// <summary>
    /// The operator confirmed the plan of this change. ApplyAsync refuses every kind of change without
    /// it; PrepareAsync clears it. The desktop sets it only from a confirmed change plan.
    /// </summary>
    public bool ChangeConfirmed { get; init; }

    /// <summary>True when the plan needs <see cref="EveryDeviceAcknowledged"/>: a run on a built-in collection or on a target that holds every client of the site.</summary>
    public bool NeedsEveryDeviceAcknowledgement => IsRun && Targets is { ReachesEveryDevice: true };

    /// <summary>The site script read at Prepare (RunScript, ApproveScript, DenyScript, RemoveScript), with its text; null before Prepare.</summary>
    public SiteScript? Script { get; init; }

    /// <summary>The devices a run sends to: the client devices of <see cref="Targets"/> at Prepare.</summary>
    public int DeviceCount { get; init; }

    /// <summary>
    /// SubmitScript: the parameter definitions read from the script's param() block at Prepare, as the
    /// site will store them.
    /// </summary>
    public IReadOnlyList<ScriptParameter> SubmitParameters { get; init; } = [];

    /// <summary>
    /// The time ApplyAsync waits for results; null uses the service default. <see cref="SendOnly"/>
    /// makes ApplyAsync return after the send; the caller then reads the results with
    /// WaitForResultsAsync. The window of the prepared plan applies: a later copy cannot change it.
    /// </summary>
    public TimeSpan? ResultWindow { get; init; }

    /// <summary>The <see cref="ResultWindow"/> of a run that ApplyAsync sends without a wait.</summary>
    public static readonly TimeSpan SendOnly = TimeSpan.Zero;

    /// <summary>True when ApplyAsync sends and returns without a wait: a window of zero or less.</summary>
    public bool IsSendOnly => ResultWindow is { } window && window <= TimeSpan.Zero;
}

/// <summary>One device of a run.</summary>
/// <param name="State">Sent, Running, Completed, Failed, TimedOut, NoResponse or Skipped; see <see cref="PivotsDeviceStates"/>.</param>
/// <param name="Output">The output the site holds for the device: CMPivot rows as text, or the script output.</param>
/// <param name="Message">Plain text; the reason for Failed, TimedOut, NoResponse and Skipped.</param>
public sealed record DeviceResult(int ResourceId, string Name, string State, string Output, string Message)
{
    public int? ExitCode { get; init; }

    /// <summary>The result rows of the device: one dictionary per row, column to value as text.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; } = [];

    /// <summary>True when the site says it holds only part of the device's result rows.</summary>
    public bool MoreResults { get; init; }

    public DateTime? LastUpdateUtc { get; init; }

    public bool IsFinal => State is PivotsDeviceStates.Completed or PivotsDeviceStates.Failed or PivotsDeviceStates.TimedOut or PivotsDeviceStates.NoResponse or PivotsDeviceStates.Skipped;
}

public static class PivotsDeviceStates
{
    /// <summary>The site accepted the run; the device has not reported yet.</summary>
    public const string Sent = "Sent";

    /// <summary>The site holds a status row for the device without a final result.</summary>
    public const string Running = "Running";

    public const string Completed = "Completed";
    public const string Failed = "Failed";

    /// <summary>The device reported a start or the site reported a time-out, and no result arrived in the window.</summary>
    public const string TimedOut = "TimedOut";

    /// <summary>The site holds no status row from the device at the end of the window.</summary>
    public const string NoResponse = "NoResponse";

    /// <summary>The run did not send to the device, for example a resource without a client.</summary>
    public const string Skipped = "Skipped";
}

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
/// <param name="OperationId">RunPivot and RunScript: the ClientOperationId the site returned; null when nothing was sent.</param>
/// <param name="Partial">True when some devices have no final result: the wait ended, was stopped or the run was cut.</param>
/// <param name="SessionId">The saved session record of a run; null for the script actions.</param>
public sealed record PivotsOutcome(PivotsChange Change, bool Succeeded, string Message, string? OperationId, IReadOnlyList<DeviceResult> Devices, bool Partial, string? SessionId)
{
    /// <summary>False for a progress report while the run still waits; true for the last report.</summary>
    public bool Final { get; init; } = true;

    /// <summary>True when the request reached the site: a run cannot be taken back after this.</summary>
    public bool Sent { get; init; }

    /// <summary>SubmitScript: the GUID the create call returned; null when nothing was created.</summary>
    public string? CreatedScriptGuid { get; init; }

    /// <summary>
    /// SubmitScript that created a script but could not finish it: the RemoveScript of only the created
    /// GUID, to prepare and confirm as a plan of its own; null otherwise.
    /// </summary>
    public PivotsChange? CleanupChange { get; init; }

    /// <summary>The script as the site holds it after the action (Submit, Approve, Deny); null otherwise.</summary>
    public SiteScript? ScriptAfter { get; init; }

    public DateTime? SentUtc { get; init; }

    /// <summary>The time from the send to the first device result; null when none arrived.</summary>
    public TimeSpan? FirstResultAfter { get; init; }
}

public interface IPivotsScriptsService
{
    /// <summary>Resolves a target to devices with one bulk read; never one call per device.</summary>
    Task<ResolvedTargets> ResolveTargetsAsync(RunTarget target, CancellationToken ct);

    /// <summary>The scripts of the site that belong to no ConfigMgr feature, without their text.</summary>
    Task<IReadOnlyList<SiteScript>> LoadScriptsAsync(CancellationToken ct);

    /// <summary>One site script with its text and parameter definitions; null when the site holds no such script.</summary>
    Task<SiteScript?> LoadScriptAsync(string guid, CancellationToken ct);

    /// <summary>Parses a script in Windows PowerShell 5.1: the syntax errors and the parameter definitions the site would store.</summary>
    Task<ScriptCheck> CheckScriptAsync(string text, CancellationToken ct);

    /// <summary>Reads the script and the targets again, fills Impact, Checks and the Fingerprint.</summary>
    Task<PivotsChange> PrepareAsync(PivotsChange request, CancellationToken ct);

    /// <summary>
    /// Runs a prepared change. A run saves its session and writes its Started audit record before the
    /// first request goes out, then waits for results within the window of the prepared plan and
    /// writes the Finished record; a send-only plan returns after the send and WaitForResultsAsync
    /// writes the Finished record. Cancellation before the send sends nothing, cancellation after the
    /// send ends the wait only.
    /// </summary>
    Task<PivotsOutcome> ApplyAsync(PivotsChange change, IProgress<PivotsOutcome> progress, CancellationToken ct);

    /// <summary>
    /// Waits for the results of a sent run; reads only, sends nothing. A run that this service did not
    /// send (a saved session after a restart) is read from the session store, and the results of the
    /// clients that finished come from the site. The first wait that ends (all results, the window or
    /// a stop) writes the one Finished audit record of the run.
    /// </summary>
    /// <param name="window">The wait time; null uses the window of the run or the service default. The service bounds it.</param>
    /// <param name="sessionId">The saved session of the run, when the caller knows it.</param>
    Task<PivotsOutcome> WaitForResultsAsync(string operationId, IProgress<PivotsOutcome> progress, CancellationToken ct, TimeSpan? window = null, string? sessionId = null);

    /// <summary>Checks the session and the administration service; returns plain text.</summary>
    Task<string> TestConnectionAsync(CancellationToken ct);

    /// <summary>
    /// The app closes while a wait reads results: each such run without its Finished record gets one
    /// now (Partial), with the message that the clients still run the request and that History,
    /// Wait again reads the results later. It sends nothing and returns the number of records written.
    /// </summary>
    int EndWaitsAtExit() => 0;
}

/// <param name="Errors">The parse errors with line and column; empty for a valid script.</param>
/// <param name="Problems">Rules of Run Scripts the parameters break (type, count, quote); the script cannot be submitted while any exists.</param>
public sealed record ScriptCheck(bool Valid, IReadOnlyList<string> Errors, IReadOnlyList<ScriptParameter> Parameters, IReadOnlyList<string> Problems);

/// <summary>
/// The saved record of one run, written before the first request goes out and updated with the
/// responses.
/// </summary>
/// <param name="Parameters">The parameter names of a script run, each with the SHA-256 of its value ("sha256:" and lower-case hex); the values are not stored.</param>
public sealed record PivotsSessionRecord(
    string Id,
    DateTime CreatedUtc,
    string User,
    string Site,
    string Provider,
    PivotsChangeKind Kind,
    string TargetDescription,
    string? CollectionId,
    IReadOnlyList<ResolvedDevice> Devices,
    string? QueryText,
    string? ScriptGuid,
    string? ScriptName,
    string? ScriptHash,
    IReadOnlyDictionary<string, string> Parameters)
{
    public string? OperationId { get; init; }
    public string Status { get; init; } = "Saved";
    public string Message { get; init; } = "";
    public IReadOnlyList<DeviceResult> Results { get; init; } = [];
    public IReadOnlyList<PivotsRawRecord> Raw { get; init; } = [];
    public DateTime? FinishedUtc { get; init; }
    public bool Partial { get; init; }

    /// <summary>True once the Finished audit record of the run is written; a later wait writes none.</summary>
    public bool FinishedRecorded { get; init; }

    /// <summary>True when the site held another script text or version right after the send than the plan.</summary>
    public bool ChangedDuringSend { get; init; }
}

/// <summary>One request or response of a run as the site returned it.</summary>
/// <param name="Phase">Submit, Status or Script.</param>
public sealed record PivotsRawRecord(DateTime Utc, string Phase, int? HttpStatus, string Body);

public sealed record PivotsSessionSummary(string Id, DateTime CreatedUtc, PivotsChangeKind Kind, string TargetDescription, int DeviceCount, string Status, string? OperationId);

public interface IPivotsSessionStore
{
    /// <summary>Writes a new session file; throws <see cref="PivotsServiceException"/> when it cannot. A run does not send without it.</summary>
    void Save(PivotsSessionRecord record);

    /// <summary>Replaces a session file with a later state of the same run.</summary>
    void Update(PivotsSessionRecord record);

    /// <summary>The newest <paramref name="max"/> sessions, newest first; an unreadable file is skipped.</summary>
    IReadOnlyList<PivotsSessionSummary> List(int max);

    /// <summary>One session; null when the file does not exist or cannot be read.</summary>
    PivotsSessionRecord? Read(string id);

    /// <summary>The newest session of the run with this operation ID among the newest <paramref name="max"/> sessions; null when none holds it.</summary>
    PivotsSessionRecord? FindByOperation(string operationId, int max) =>
        List(max).FirstOrDefault(s => s.OperationId == operationId) is { } summary ? Read(summary.Id) : null;

    string Folder { get; }
}

/// <summary>Result rows as the page shows them.</summary>
/// <param name="View">Results, Devices, Raw, Pipeline or History.</param>
/// <param name="Partial">The rows come from a run without a result for every device; the writer refuses it without a <paramref name="Note"/>.</param>
public sealed record PivotsReport(string View, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Partial, string? Note)
{
    public string Filter { get; init; } = "";
}

public interface IPivotsReportWriter
{
    string WriteCsv(PivotsReport report);
    string WriteJson(PivotsReport report);
    string Folder { get; }
}

/// <summary>
/// One line of the Pivots and Scripts audit log. A run writes a Started record before the first
/// request goes out (a failed write blocks the run) and one Finished record when its first wait ends
/// (Partial for a stop); a script action writes Started before and Finished after its call.
/// </summary>
/// <param name="Event">RunPivot, RunScript, SubmitScript, ApproveScript, DenyScript or RemoveScript.</param>
/// <param name="Phase">Started or Finished.</param>
/// <param name="Result">Started: empty. Finished: Succeeded, Partial, Failed or Skipped.</param>
/// <param name="Target">The target description; empty for a script action.</param>
/// <param name="TextHash">The SHA-256 of the query or the script text.</param>
public sealed record PivotsAuditRecord(
    DateTime Timestamp,
    string User,
    string Event,
    string Phase,
    string Result,
    string RunId,
    string Target,
    int DeviceCount,
    string? ScriptGuid,
    string? TextHash,
    string? OperationId,
    string Message)
{
    public string Source { get; init; } = "";
    public string? SessionId { get; init; }

    /// <summary>True, false or null as <see cref="DeploymentAuditRecord.Verified"/>: the hash chain of the line.</summary>
    public bool? Verified { get; init; }

    public bool HasChain { get; init; }
    public bool AfterChainStart { get; init; }
    public long? ChainBrokenAt { get; init; }
}

public interface IPivotsAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(PivotsAuditRecord r);

    /// <summary>The last <paramref name="max"/> records, oldest first; a line that cannot be read is skipped.</summary>
    IReadOnlyList<PivotsAuditRecord> Read(int max);

    /// <summary>Every record of one saved session in the whole log, oldest first; the read is not limited to the last records.</summary>
    IReadOnlyList<PivotsAuditRecord> ReadSession(string sessionId) => Read(int.MaxValue).Where(r => r.SessionId == sessionId).ToList();

    string Path { get; }

    bool OlderRecordsDropped => false;
}

/// <summary>A query or script of a library.</summary>
/// <param name="Kind">Query or Script.</param>
/// <param name="BuiltIn">True for an item shipped in the app; false for a file of the user library folder.</param>
public sealed record PivotsLibraryItem(string Name, string Kind, string Text, bool BuiltIn, string Hash);

public interface IPivotsLibrary
{
    /// <summary>The built-in items, checked by hash, and the items of the user folder when one is set and readable.</summary>
    IReadOnlyList<PivotsLibraryItem> Load(string? userFolder);
}

/// <summary>A plain refusal or failure of the Pivots and Scripts services; the message is safe to show.</summary>
public sealed class PivotsServiceException(string message) : Exception(message);
