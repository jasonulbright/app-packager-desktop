namespace ApplicationPackagerUI.Models;

public sealed class PackagerExecutionResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public string OutLogPath { get; init; } = "";
    public string ErrLogPath { get; init; } = "";
    public string StructuredLogPath { get; init; } = "";
}
