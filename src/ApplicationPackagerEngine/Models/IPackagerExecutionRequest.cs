namespace ApplicationPackagerEngine.Models;

/// <summary>Packager metadata and run-local state needed by a packaging operation.</summary>
public interface IPackagerExecutionRequest
{
    string Uid { get; }
    bool IsJsonDefined { get; }
    string? DefinitionPath { get; }
    string LatestVersion { get; }
    string? StagedVersion { get; set; }
    string? LocalSourceStagedVersion { get; set; }
    string? StagedManifestPath { get; set; }
    string VendorUrl { get; }
    string FullPath { get; }
    string Script { get; }
}
