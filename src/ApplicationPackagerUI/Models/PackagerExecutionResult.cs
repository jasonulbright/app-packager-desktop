namespace ApplicationPackagerUI.Models;

public sealed class PackagerExecutionResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public string OutLogPath { get; init; } = "";
    public string ErrLogPath { get; init; } = "";
    public string StructuredLogPath { get; init; } = "";

    /// <summary>Outcome of the .intunewin build for this run; empty when the step never ran.</summary>
    public string IntuneWinNote { get; init; } = "";

    /// <summary>Outcome of the Intune publish for this run; empty when the step never ran.</summary>
    public string IntunePublishNote { get; init; } = "";

    /// <summary>
    /// Set when the run emitted the same-version conflict marker instead of
    /// packaging. Null on every other outcome.
    /// </summary>
    public ExistingApplicationConflict? Conflict { get; init; }
}

/// <summary>The application and version a Skip run declined to overwrite.</summary>
public sealed record ExistingApplicationConflict(string ApplicationName, string Version);
