namespace ApplicationPackagerUI.Models;

public partial class PackagerInfo : ObservableObject
{
    // Mutable — bound to DataGrid, updated during operations
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _currentVersion = "";
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private DateTime? _lastChecked;

    /// <summary>Intune build and publish outcome for the most recent Package run.</summary>
    [ObservableProperty] private string _intuneNote = "";

    // Immutable metadata — set once during discovery
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

    /// <summary>
    /// Priority for auto-check ordering. Lower = checked first.
    /// Browsers and security-critical apps get priority 0.
    /// Apps with no LastChecked or oldest LastChecked come next.
    /// </summary>
    public int CheckPriority
    {
        get
        {
            // Priority 0: Browsers and security-critical
            var name = Application.ToLowerInvariant();
            if (name.Contains("chrome") || name.Contains("edge") || name.Contains("firefox") ||
                name.Contains("brave") || name.Contains("acrobat") || name.Contains("reader"))
                return 0;

            // Priority 1: Never checked
            if (LastChecked is null) return 1;

            // Priority 2+: Oldest checked first (days since last check)
            return 2 + (int)(DateTime.Now - LastChecked.Value).TotalDays;
        }
    }
}
