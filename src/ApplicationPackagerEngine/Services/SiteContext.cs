namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The active site of this process (Q-112d). A site-scoped store asks <see cref="Resolve"/> for its path: with a site active
/// the path is below sites\&lt;FOLDER&gt; in the data root, with no site active it is the single-site path of earlier versions.
/// A switch never writes into the previous site folder, because every store reads the path again on each use.
/// A test sets a scope for its own async flow with <see cref="Scope"/> and never touches the process value.
/// </summary>
public static class SiteContext
{
    /// <summary>The areas below a site folder. The names are the contract (site-data-contract, "Layout").</summary>
    public static class Areas
    {
        public const string Results = "results";
        public const string State = "state";
        public const string Rings = @"deployments\rings";
        public const string RingRuns = @"deployments\rings\runs";
        public const string Deployments = "deployments";
        public const string Hygiene = "hygiene";
        public const string DpContent = "dpcontent";
        public const string Pivots = "pivots";
        public const string PivotsSessions = @"pivots\sessions";
        public const string Maintenance = "maintenance";
        public const string MaintenanceReports = @"maintenance\reports";
        public const string Collections = "collections";
        public const string CollectionsReports = @"collections\reports";
    }

    private sealed record State(string? Folder, string? DataRoot, string? EnvRoot = null);

    private static readonly object Gate = new();
    private static State _process = new(null, null);
    private static readonly AsyncLocal<State?> Local = new();

    /// <summary>Raised after the process value changed (not for a test scope).</summary>
    public static event Action? Changed;

    private static string? EnvRoot() => Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    /// <summary>
    /// The state in force. A site that the process activated is valid only for the data root it was activated under: when
    /// APP_PACKAGER_DATA_ROOT changes (tests, the command-line harness) the process falls back to the single-site layout instead of
    /// reading a site folder of another root.
    /// </summary>
    private static State Current
    {
        get
        {
            if (Local.Value is { } scoped) return scoped;
            var process = _process;
            return process.Folder is not null && !string.Equals(process.EnvRoot, EnvRoot(), StringComparison.Ordinal)
                ? new State(null, null)
                : process;
        }
    }

    /// <summary>The folder name of the active site (AB1 or AB1~2), or null when the process uses the single-site layout.</summary>
    public static string? ActiveFolderName => Current.Folder;

    public static bool IsActive => Current.Folder is not null;

    /// <summary>The data root of the site layout for this context.</summary>
    public static string DataRoot => Current.DataRoot ?? SiteDataPaths.DataRoot;

    /// <summary>The folder of the active site, or null when none is active. Creates nothing.</summary>
    public static string? ActiveSiteFolder =>
        Current.Folder is { } f ? SiteDataPaths.GetSiteFolder(f, dataRoot: DataRoot) : null;

    /// <summary>Makes a site the active one for the process. Throws ArgumentException for an invalid folder name.</summary>
    public static void Activate(string folderName, string? dataRoot = null)
    {
        if (!SiteDataPaths.TryParseFolderName(folderName, out var parsed) || parsed is null)
            throw new ArgumentException($"Invalid site folder name: '{folderName}'.", nameof(folderName));
        // Inside a test scope the change stays in the scope; the process value is never touched.
        if (Local.Value is { } scoped)
        {
            Local.Value = new State(parsed.FolderName, dataRoot ?? scoped.DataRoot);
            return;
        }
        lock (Gate) _process = new State(parsed.FolderName, dataRoot ?? _process.DataRoot, EnvRoot());
        Changed?.Invoke();
    }

    /// <summary>Returns the process to the single-site layout.</summary>
    public static void Deactivate()
    {
        if (Local.Value is { } scoped)
        {
            Local.Value = new State(null, scoped.DataRoot);
            return;
        }
        lock (Gate) _process = new State(null, _process.DataRoot, _process.EnvRoot);
        Changed?.Invoke();
    }

    /// <summary>Sets the data root for the process. Used by the host before the first read, and by the command-line harness.</summary>
    public static void UseDataRoot(string? dataRoot)
    {
        if (Local.Value is { } scoped)
        {
            Local.Value = new State(scoped.Folder, dataRoot);
            return;
        }
        lock (Gate) _process = new State(_process.Folder, dataRoot, _process.EnvRoot);
    }

    /// <summary>
    /// Overrides the site for the current async flow until the returned scope is disposed. A null folder means the single-site layout.
    /// </summary>
    public static IDisposable Scope(string? folderName, string? dataRoot = null)
    {
        string? folder = null;
        if (folderName is not null)
        {
            if (!SiteDataPaths.TryParseFolderName(folderName, out var parsed) || parsed is null)
                throw new ArgumentException($"Invalid site folder name: '{folderName}'.", nameof(folderName));
            folder = parsed.FolderName;
        }
        var previous = Local.Value;
        Local.Value = new State(folder, dataRoot);
        return new Restore(previous);
    }

    private sealed class Restore(State? previous) : IDisposable
    {
        public void Dispose() => Local.Value = previous;
    }

    /// <summary>
    /// The path of an area of the active site. With no site active, the single-site path from <paramref name="legacy"/>.
    /// An empty area returns the site folder itself.
    /// </summary>
    public static string Resolve(string area, Func<string> legacy)
    {
        var state = Current;
        if (state.Folder is null) return legacy();
        var site = SiteDataPaths.GetSiteFolder(state.Folder, dataRoot: state.DataRoot ?? SiteDataPaths.DataRoot);
        return area.Length == 0 ? site : Path.Combine(site, area);
    }
}
