namespace ApplicationPackagerEngine.Services;

/// <summary>
/// One distribution point with the rollup of its content status rows.
/// <paramref name="NalPath"/> is the NALPath of the distribution point as the site holds it; a pair row
/// of the DP carries the same text as its ServerNALPath.
/// </summary>
/// <param name="CountsKnown">False when the site returned no status row for the DP: the counts read 0 but are not known.</param>
/// <param name="TotalSizeGb">The sum of the source sizes of the content the DP has a status row for.</param>
public sealed record DpRow(
    string DpName,
    string NalPath,
    string SiteCode,
    bool IsPullDp,
    int Total,
    int Installed,
    int InProgress,
    int Failed,
    double PercentOk,
    double TotalSizeGb,
    bool CountsKnown)
{
    /// <summary>True for a cloud distribution point; it is shown, never changed.</summary>
    public bool IsCloud { get; init; }

    /// <summary>Healthy, Failed, InProgress or Empty; see <see cref="DpContentRollup.Status"/>.</summary>
    public string Status => DpContentRollup.Status(Failed, InProgress, Total);
}

/// <summary>
/// One content object with the rollup of its status rows.
/// <paramref name="ContentType"/> is Application, Package, SoftwareUpdatePackage, BootImage, OsImage,
/// OsUpgradePackage, DriverPackage, TaskSequence or Unknown (a status row whose PackageID no content list holds).
/// </summary>
/// <param name="DeploymentCount">The deployments that use the content; -1 when not known or not read.</param>
public sealed record ContentRow(
    string PackageId,
    string Name,
    string ContentType,
    double SizeMb,
    int DpsTargeted,
    int Installed,
    int InProgress,
    int Failed,
    double PercentOk,
    int DeploymentCount = -1)
{
    /// <summary>Healthy, Failed, InProgress or Empty; see <see cref="DpContentRollup.Status"/>.</summary>
    public string Status => DpContentRollup.Status(Failed, InProgress, DpsTargeted);

    /// <summary>The ID of the object that owns the content: the CI_ID of an application, the PackageID otherwise.</summary>
    public string ObjectId { get; init; } = "";
}

/// <summary>
/// One (distribution point, content) status row. A pair is identified by <paramref name="PackageId"/>
/// and the full <paramref name="NalPath"/> (the ServerNALPath of the row), compared exactly.
/// </summary>
/// <param name="State">The State of SMS_PackageStatusDistPointsSummarizer.</param>
/// <param name="SummaryDate">The SummaryDate the site holds, as returned.</param>
public sealed record PairRow(
    string PackageId,
    string ContentName,
    string ContentType,
    string DpName,
    string NalPath,
    int State,
    string StateName,
    string SourceVersion,
    DateTime? SummaryDate)
{
    /// <summary>The key that identifies the pair: PackageID and ServerNALPath, compared exactly.</summary>
    public DpPairKey Key => new(PackageId, NalPath);

    /// <summary>True for a row that is not Installed (state 0) and not Content Monitoring (state 8), or that carries an error message.</summary>
    public bool IsIssue => DpContentStates.IsIssue(State) || HasErrorMessage;

    /// <summary>The site code of the distribution point of the pair.</summary>
    public string SiteCode { get; init; } = "";

    /// <summary>
    /// The MessageState of the last distribution status message of the pair (SMS_DistributionDPStatus);
    /// null when the site holds none. The site writes 1 for success, 2 for in progress and 4 for an error.
    /// </summary>
    public int? MessageState { get; init; }

    /// <summary>The status message ID of the last distribution status message, such as 2330, 2384 or 2306; null when none.</summary>
    public int? MessageId { get; init; }

    /// <summary>
    /// True when the last status message of the pair is an error. The summarizer keeps State 7 (content
    /// updating) while the site retries a distribution that fails, so the message is what shows the failure.
    /// </summary>
    public bool HasErrorMessage => MessageState is 3 or 4;

    /// <summary>True for Install failed, Removal failed, or another state whose last message is an error.</summary>
    public bool IsFailed => DpContentStates.IsFailed(State) || HasErrorMessage;

    /// <summary>True for a state in progress whose last message is not an error.</summary>
    public bool IsInProgress => DpContentStates.IsInProgress(State) && !HasErrorMessage;
}

/// <summary>
/// The exact key of a pair: the whole PackageID and the whole NAL path, compared without case and
/// never as a substring. The site returns one NAL path in different letter case from different
/// classes (SMS_DistributionPoint can hold "DISPLAY=\\DP01.CONTOSO.COM\" where the summarizer holds
/// "Display=\\DP01.contoso.com\" for the same pair).
/// </summary>
public readonly record struct DpPairKey(string PackageId, string NalPath)
{
    public bool Equals(DpPairKey other) =>
        string.Equals(PackageId, other.PackageId, StringComparison.OrdinalIgnoreCase) && string.Equals(NalPath, other.NalPath, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(PackageId ?? ""), StringComparer.OrdinalIgnoreCase.GetHashCode(NalPath ?? ""));
}

/// <param name="Truncated">True when the site held more status rows than the snapshot holds; <paramref name="Note"/> then says how many.</param>
public sealed record DpContentSnapshot(
    IReadOnlyList<DpRow> Dps,
    IReadOnlyList<ContentRow> Content,
    IReadOnlyList<PairRow> Pairs,
    bool Truncated,
    string? Note)
{
    /// <summary>The number of status rows the site holds; equal to the pair count unless the snapshot is truncated.</summary>
    public int TotalPairs { get; init; }

    /// <summary>The UTC time the load finished.</summary>
    public DateTime LoadedUtc { get; init; }
}

public enum DpContentChangeKind { Redistribute, Remove, Validate, DistributeAgain }

/// <summary>
/// One change of a plan over a list of pairs. PrepareAsync re-reads the pairs, drops stale ones,
/// fills <paramref name="Impact"/>, <paramref name="Checks"/> and <paramref name="Fingerprint"/>; ApplyAsync runs only the
/// pairs the prepared change names.
/// </summary>
/// <param name="RemovalConfirmed">Remove: the second confirmation. The service refuses a removal without it.</param>
/// <param name="LastCopyAcknowledged">Remove: the operator accepts that a content is left on no distribution point.</param>
/// <param name="Fingerprint">The pair states at Prepare; ApplyAsync refuses a change without one.</param>
public sealed record DpContentChange(
    DpContentChangeKind Kind,
    IReadOnlyList<PairRow> Pairs,
    bool RemovalConfirmed,
    bool LastCopyAcknowledged,
    IReadOnlyList<string> Impact,
    IReadOnlyList<PreflightCheck> Checks,
    string? Fingerprint)
{
    /// <summary>True when a check failed or could not be made: the change does not run.</summary>
    public bool Blocked => Checks.Any(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown);

    /// <summary>True for a removal: it needs <see cref="RemovalConfirmed"/>.</summary>
    public bool NeedsRemovalConfirmation => Kind == DpContentChangeKind.Remove;

    /// <summary>Remove: true when at least one content is left on no distribution point; it needs <see cref="LastCopyAcknowledged"/>.</summary>
    public bool NeedsLastCopyAcknowledgement { get; init; }

    /// <summary>The per-content impact PrepareAsync read; empty before Prepare.</summary>
    public IReadOnlyList<DpContentImpact> ContentImpact { get; init; } = [];

    /// <summary>The pairs PrepareAsync dropped from the request, each with a message that starts with "Skipped".</summary>
    public IReadOnlyList<DpContentPairOutcome> Dropped { get; init; } = [];
}

/// <summary>
/// What a change does to one content.
/// </summary>
/// <param name="DpsNow">The distribution points with a status row for the content now.</param>
/// <param name="DpsRemaining">Remove: the distribution points that still hold the content after the change; DpsNow for the other kinds.</param>
/// <param name="DeploymentCount">The deployments that use the content; -1 when not known.</param>
/// <param name="DeploymentNote">Which deployment kinds the count covers, or why it is not known.</param>
public sealed record DpContentImpact(string PackageId, string Name, string ContentType, int Pairs, int DpsNow, int DpsRemaining, int DeploymentCount, string DeploymentNote);

/// <param name="Result">Requested, Confirmed, Unchanged, Failed or Skipped (see <see cref="DpContentResults"/>).</param>
/// <param name="StateAfter">The state the last read-back found; null when the pair has no status row or was not read.</param>
public sealed record DpContentPairOutcome(PairRow Pair, string Result, string Message, int? StateAfter);

/// <param name="Message">Plain text without raw exception text; starts with "Skipped" when nothing was attempted.</param>
/// <param name="UndoChange">Remove: the DistributeAgain of the removed pairs that were Installed before; null otherwise.</param>
public sealed record DpContentOutcome(DpContentChange Change, bool Succeeded, string Message, IReadOnlyList<DpContentPairOutcome> Pairs, DpContentChange? UndoChange)
{
    /// <summary>
    /// False for the progress report after a chunk while the change still runs: its pairs are the
    /// pairs done so far with their first result. True for the last report of the change.
    /// </summary>
    public bool Final { get; init; } = true;

    /// <summary>DistributeAgain: a new request for the pairs that ended Unchanged or Failed, to prepare and run again later; null otherwise.</summary>
    public DpContentChange? RetryChange { get; init; }
}

public static class DpContentResults
{
    /// <summary>The site accepted the request; the read-back window ended before the state changed.</summary>
    public const string Requested = "Requested";

    /// <summary>A read-back in the window showed the expected state change.</summary>
    public const string Confirmed = "Confirmed";

    /// <summary>The site accepted the request and the read-back shows the pair as before, with no sign of work.</summary>
    public const string Unchanged = "Unchanged";

    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

public static class DpContentTypes
{
    public const string Application = "Application";
    public const string Package = "Package";
    public const string SoftwareUpdatePackage = "SoftwareUpdatePackage";
    public const string BootImage = "BootImage";
    public const string OsImage = "OsImage";
    public const string DriverPackage = "DriverPackage";
    public const string TaskSequence = "TaskSequence";

    /// <summary>An OS upgrade package (SMS_OperatingSystemInstallPackage).</summary>
    public const string OsUpgradePackage = "OsUpgradePackage";
    public const string Unknown = "Unknown";

    public static readonly IReadOnlyList<string> All = [Application, Package, SoftwareUpdatePackage, BootImage, OsImage, OsUpgradePackage, DriverPackage, TaskSequence, Unknown];
}

/// <summary>The states of SMS_PackageStatusDistPointsSummarizer.</summary>
public static class DpContentStates
{
    public const int Installed = 0;
    public const int InstallPending = 1;
    public const int InstallRetrying = 2;
    public const int InstallFailed = 3;
    public const int RemovalPending = 4;
    public const int RemovalRetrying = 5;
    public const int RemovalFailed = 6;
    public const int ContentUpdating = 7;
    public const int ContentMonitoring = 8;

    public static string Name(int state) => state switch
    {
        Installed => "Installed",
        InstallPending => "Install pending",
        InstallRetrying => "Install retrying",
        InstallFailed => "Install failed",
        RemovalPending => "Removal pending",
        RemovalRetrying => "Removal retrying",
        RemovalFailed => "Removal failed",
        ContentUpdating => "Content updating",
        ContentMonitoring => "Content monitoring",
        _ => "Unknown state " + state.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    public static bool IsOk(int state) => state is Installed or ContentMonitoring;
    public static bool IsFailed(int state) => state is InstallFailed or RemovalFailed;
    public static bool IsInProgress(int state) => state is InstallPending or InstallRetrying or RemovalPending or RemovalRetrying or ContentUpdating;
    public static bool IsIssue(int state) => !IsOk(state);
}

/// <summary>The status of a rollup: Empty with no rows, Failed with a failed row, InProgress with a row in progress, Healthy otherwise.</summary>
public static class DpContentRollup
{
    public const string Healthy = "Healthy";
    public const string Failed = "Failed";
    public const string InProgress = "InProgress";
    public const string Empty = "Empty";

    public static string Status(int failed, int inProgress, int total) =>
        total <= 0 ? Empty : failed > 0 ? Failed : inProgress > 0 ? InProgress : Healthy;

    /// <summary>The installed share in percent, rounded to one decimal; 0 with no rows.</summary>
    public static double PercentOk(int ok, int total) => total <= 0 ? 0 : Math.Round(ok * 100.0 / total, 1, MidpointRounding.AwayFromZero);
}

public interface IDpContentService
{
    /// <summary>Reads the distribution points, the content objects of every type and all status rows in one bulk read set.</summary>
    Task<DpContentSnapshot> LoadAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>Re-reads the pairs' current state, drops stale pairs, fills Impact, Checks and the Fingerprint.</summary>
    Task<DpContentChange> PrepareAsync(DpContentChange request, CancellationToken ct);

    /// <summary>
    /// Runs the prepared changes in chunks. Cancellation takes effect between chunks; the pairs
    /// not attempted are reported as skipped. A removal carries its undo.
    /// </summary>
    Task<IReadOnlyList<DpContentOutcome>> ApplyAsync(IReadOnlyList<DpContentChange> changes, IProgress<DpContentOutcome> progress, CancellationToken ct);
}

/// <summary>
/// One exported view: the column names and the text of each cell, as the page shows them.
/// </summary>
/// <param name="View">DPs, Content, Status Issues, Content detail or History.</param>
/// <param name="Truncated">The rows come from a truncated load; the writer refuses it without a <paramref name="Note"/>.</param>
public sealed record DpContentReport(string View, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated, string? Note);

public interface IDpContentReportWriter
{
    /// <summary>Writes the report as a new CSV file and returns its path.</summary>
    string WriteCsv(DpContentReport report);

    /// <summary>Writes the report as a new HTML file and returns its path.</summary>
    string WriteHtml(DpContentReport report);

    /// <summary>The folder the reports go to.</summary>
    string Folder { get; }
}

/// <summary>
/// One line of the DP content audit log. A removal writes a Started record before each chunk and a
/// Finished record after it; the other kinds write the Finished record only.
/// </summary>
/// <param name="Event">Redistribute, Remove, Validate or DistributeAgain.</param>
/// <param name="Phase">Started or Finished.</param>
/// <param name="Result">Started: empty. Finished: Succeeded, Partial, Failed or Skipped.</param>
/// <param name="RunId">The same for every record of one ApplyAsync change.</param>
public sealed record DpContentAuditRecord(
    DateTime Timestamp,
    string User,
    string Event,
    string Phase,
    string Result,
    string RunId,
    int Chunk,
    IReadOnlyList<DpContentAuditPair> Pairs,
    string Message)
{
    public string Source { get; init; } = "";

    /// <summary>True, false or null as <see cref="DeploymentAuditRecord.Verified"/>: the hash chain of the line.</summary>
    public bool? Verified { get; init; }

    public bool HasChain { get; init; }
    public bool AfterChainStart { get; init; }
    public long? ChainBrokenAt { get; init; }
}

/// <param name="StateBefore">The state at the re-read before the write; null when not read.</param>
/// <param name="Result">Empty in a Started record.</param>
public sealed record DpContentAuditPair(string PackageId, string ContentName, string DpName, string NalPath, int? StateBefore, string Result);

public interface IDpContentAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(DpContentAuditRecord r);

    /// <summary>The last <paramref name="max"/> records, oldest first; a line that cannot be read is skipped.</summary>
    IReadOnlyList<DpContentAuditRecord> Read(int max);

    string Path { get; }
}

/// <summary>A plain refusal or failure of the DP content services; the message is safe to show.</summary>
public sealed class DpContentServiceException(string message) : Exception(message);
