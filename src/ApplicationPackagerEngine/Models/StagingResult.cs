namespace ApplicationPackagerEngine.Models;

public sealed class StagingResult
{
    public bool Success { get; init; }
    public string Version { get; init; } = "";
    public string ContentPath { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public string? ErrorMessage { get; init; }
}
