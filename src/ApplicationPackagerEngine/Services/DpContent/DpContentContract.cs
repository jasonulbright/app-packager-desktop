namespace ApplicationPackagerEngine.Services;

/// <summary>One distribution point with the rollup of its (DP, content) rows.</summary>
/// <param name="NalPath">The full ServerNALPath the site returns; it identifies the DP exactly.</param>
/// <param name="CountsKnown">False when the site returned no status row for the DP; the counts are then not known.</param>
public sealed record DpRow(string DpName, string NalPath, string SiteCode, bool IsPullDp, int Total, int Installed, int InProgress, int Failed,
    double PercentOk, double TotalSizeGb, bool CountsKnown);

/// <summary>One content object with the rollup of its (DP, content) rows.</summary>
/// <param name="ContentType">Application, Package, SoftwareUpdatePackage, BootImage, OsImage, DriverPackage or TaskSequence.</param>
/// <param name="DeploymentCount">The number of deployments that use the content; -1 when not known.</param>
public sealed record ContentRow(string PackageId, string Name, string ContentType, double SizeMb, int DpsTargeted, int Installed, int InProgress, int Failed,
    double PercentOk, int DeploymentCount);

/// <summary>One (DP, content) status row; the pair is the PackageID and the full ServerNALPath.</summary>
public sealed record PairRow(string PackageId, string ContentName, string ContentType, string DpName, string NalPath, int State, string StateName,
    string SourceVersion, DateTime? SummaryDate);

/// <param name="Truncated">True when the site returned more rows than the service reads; <paramref name="Note"/> says how many.</param>
public sealed record DpContentSnapshot(IReadOnlyList<DpRow> Dps, IReadOnlyList<ContentRow> Content, IReadOnlyList<PairRow> Pairs, bool Truncated, string? Note);

public enum DpContentChangeKind { Redistribute, Remove, Validate, DistributeAgain }

/// <param name="RemovalConfirmed">The second confirmation of a removal; the service refuses a removal without it.</param>
/// <param name="LastCopyAcknowledged">The operator accepted that a content has no DP left after the removal.</param>
/// <param name="Impact">Plain lines filled by PrepareAsync.</param>
/// <param name="Fingerprint">The state PrepareAsync read; ApplyAsync compares it.</param>
public sealed record DpContentChange(DpContentChangeKind Kind, IReadOnlyList<PairRow> Pairs, bool RemovalConfirmed, bool LastCopyAcknowledged,
    IReadOnlyList<string> Impact, IReadOnlyList<PreflightCheck> Checks, string? Fingerprint);

/// <param name="Result">Requested, Confirmed, Unchanged, Failed or Skipped.</param>
/// <param name="StateAfter">The state the read-back found; null when not read.</param>
public sealed record DpContentPairOutcome(PairRow Pair, string Result, string Message, int? StateAfter);

/// <param name="Message">Plain text; it starts with 'Skipped' when nothing was attempted.</param>
/// <param name="UndoChange">For a removal that changed the site: Distribute again of the pairs that were Installed before.</param>
public sealed record DpContentOutcome(DpContentChange Change, bool Succeeded, string Message, IReadOnlyList<DpContentPairOutcome> Pairs, DpContentChange? UndoChange);

public interface IDpContentService
{
    /// <summary>One bulk read set: the DPs, the content objects of every type and every (DP, content) row.</summary>
    Task<DpContentSnapshot> LoadAsync(IProgress<string> progress, CancellationToken ct);

    /// <summary>Reads the current state of the pairs again, drops stale pairs, and fills Impact, Checks and Fingerprint.</summary>
    Task<DpContentChange> PrepareAsync(DpContentChange request, CancellationToken ct);

    Task<IReadOnlyList<DpContentOutcome>> ApplyAsync(IReadOnlyList<DpContentChange> changes, IProgress<DpContentOutcome> progress, CancellationToken ct);
}

/// <summary>The rows of one view of the page, as the report writer receives them.</summary>
/// <param name="Note">A line the report header holds, for example the truncation note; null for none.</param>
public sealed record DpContentReport(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? Note);

public interface IDpContentReportWriter
{
    /// <summary>Writes a new CSV file and returns its path.</summary>
    string WriteCsv(DpContentReport report);

    /// <summary>Writes a new HTML file and returns its path.</summary>
    string WriteHtml(DpContentReport report);
}

/// <summary>One line of the DP content audit log.</summary>
/// <param name="Action">Redistribute, Remove, Validate or DistributeAgain.</param>
/// <param name="Result">Success, Partial, Failed or Refused.</param>
/// <param name="PackageIds">The PackageIDs of the pairs, separated by "; ".</param>
/// <param name="DpNames">The DP names of the pairs, separated by "; ".</param>
public sealed record DpContentAuditRecord(DateTime Timestamp, string User, string Action, string Result, int PairCount, int Succeeded, int Failed, int Skipped,
    string PackageIds, string DpNames, string Message)
{
    /// <summary>True when the hash chain of the line holds, false when it does not or the line has none; null for a record that was not read from a log.</summary>
    public bool? Verified { get; init; }

    /// <summary>True when the line read from the log carries a Chain property, intact or not.</summary>
    public bool HasChain { get; init; }

    /// <summary>The file line number of the first Chain that does not match, when the line is at or after it.</summary>
    public long? ChainBrokenAt { get; init; }
}

public interface IDpContentAuditLog
{
    AuditProbe Probe();
    AuditWrite Append(DpContentAuditRecord record);

    /// <summary>The last <paramref name="max"/> records, oldest first; a line that cannot be read is skipped.</summary>
    IReadOnlyList<DpContentAuditRecord> Read(int max);

    string Path { get; }
}
