namespace ApplicationPackagerEngine.Models;

public sealed class PackagerExecutionResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public string OutLogPath { get; init; } = "";
    public string ErrLogPath { get; init; } = "";
    public string StructuredLogPath { get; init; } = "";

    /// <summary>Intune build and publish notes for this run, when those steps were selected.</summary>
    public string IntuneWinNote { get; init; } = "";
    public string IntunePublishNote { get; init; } = "";

    /// <summary>Set when a same-version conflict prevented packaging.</summary>
    public ExistingApplicationConflict? Conflict { get; init; }

    /// <summary>Persistable destination facts; null means that destination was not selected.</summary>
    public Inventory.InventoryOutcome? ConfigMgrOutcome { get; init; }
    public string? ConfigMgrObjectId { get; init; }
    public string? ConfigMgrDetail { get; init; }

    /// <summary>The CI_ID and ModelName of the application the ConfigMgr publish read back; null when not known.</summary>
    public string? ConfigMgrCiId { get; init; }
    public string? ConfigMgrModelName { get; init; }
    public Inventory.InventoryOutcome? IntuneOutcome { get; init; }
    public string? IntuneObjectId { get; init; }
    public string? IntuneDetail { get; init; }
    public string? PublishedVersion { get; init; }
    public string? PublishedBuildId { get; init; }
}

/// <summary>The application and version a Skip run declined to overwrite.</summary>
public sealed record ExistingApplicationConflict(string ApplicationName, string Version, string IncomingVersion);
