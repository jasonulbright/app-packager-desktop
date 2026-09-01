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
}
