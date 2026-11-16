namespace ApplicationPackagerEngine.Models;

public sealed class StagingResult
{
    public bool Success { get; init; }
    public string Version { get; init; } = "";
    public string ContentPath { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public string? ErrorMessage { get; init; }
    /// <summary>True when the installer came from a local source folder instead of a download.</summary>
    public bool FromLocalSource { get; init; }

    /// <summary>"staging allowed by user acknowledgement" and who accepted it, when the stage ran in a folder that other accounts can write to only because the user allowed it. Null otherwise.</summary>
    public string? StagingNote { get; init; }

    /// <summary>The same result with the staging note.</summary>
    public StagingResult WithStagingNote(string? note) => new()
    {
        Success = Success, Version = Version, ContentPath = ContentPath, ManifestPath = ManifestPath,
        ErrorMessage = ErrorMessage, FromLocalSource = FromLocalSource, StagingNote = note
    };
}
