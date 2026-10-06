using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;

namespace ApplicationPackagerUI.Models;

public partial class PackagerInfo : ObservableObject, IPackagerExecutionRequest
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _currentVersion = "";
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _status = "Ready";
    /// <summary>The full text behind a short status, shown as the status tooltip; null when the status says it all.</summary>
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private DateTime? _lastChecked;

    partial void OnStatusChanged(string value) => StatusDetail = null;

    /// <summary>Intune build and publish outcome for the most recent Package run.</summary>
    [ObservableProperty] private string _intuneNote = "";

    /// <summary>Internal unique identifier. Never displayed to user.</summary>
    public string Uid { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Application { get; init; } = "";
    public string CMName { get; init; } = "";
    public string Script { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string VendorUrl { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>True if backed by a JSON definition (engine DLL). False if PS1 script.</summary>
    public bool IsJsonDefined { get; init; }
    /// <summary>Path to the .json definition file (null for PS1 scripts).</summary>
    public string? DefinitionPath { get; init; }
    /// <summary>Workbench namespace for a JSON definition: shipped/overridden catalog or user-authored custom.</summary>
    public ApplicationOrigin DefinitionOrigin { get; init; } = ApplicationOrigin.Catalog;

    /// <summary>
    /// Stage manifest of an ad-hoc dropped installer. Set, the row packages straight from
    /// that manifest instead of resolving one from a definition and a version folder.
    /// </summary>
    public string? StagedManifestPath { get; set; }

    /// <summary>
    /// Version folder of the last successful definition stage. With no LatestVersion
    /// known, Package publishes this version.
    /// </summary>
    public string? StagedVersion { get; set; }

    /// <summary>
    /// Version folder of the last successful stage in this session when it staged from a
    /// local installer, null otherwise. A local installer stages under its own file
    /// version, which can differ from LatestVersion; Package publishes this version then.
    /// </summary>
    public string? LocalSourceStagedVersion { get; set; }

    public bool IsRunnable => true;
}
