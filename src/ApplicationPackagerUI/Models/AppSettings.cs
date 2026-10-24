using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Models;

public sealed class AppSettings
{
    public string CompanyName { get; set; } = "";

    public string SiteCode { get; set; } = "MCM";

    /// <summary>
    /// True once the operator saved the site code. Null in a file written before the
    /// flag existed; SettingsService reads such a file's stored SiteCode as saved.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SiteCodeSaved { get; set; }
    /// <summary>
    /// SMS provider / site server host for the generated CMSite drive.
    /// Empty falls back to the local machine, which only reaches the site
    /// when the console host is itself the provider.
    /// </summary>
    public string ProviderMachine { get; set; } = "";
    public string FileShareRoot { get; set; } = @"\\fileserver\sccm$";
    /// <summary>
    /// Folder shape under &lt;share&gt;\Applications: Nested is Vendor\App\Version,
    /// Flat is Vendor-App-Version in one level.
    /// </summary>
    public string ContentLayout { get; set; } = ContentLayouts.Nested;
    public string DownloadRoot { get; set; } = DefaultDownloadRoot();

    // Empty when the data root is refused, so settings still load and the stage reports
    // the unset folder.
    private static string DefaultDownloadRoot()
    {
        try { return AppDataPaths.DefaultStagingRoot; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or InvalidOperationException) { return ""; }
    }

    /// <summary>
    /// True once the staging default of earlier versions was replaced. Null in a file
    /// written before the flag existed; SettingsService then moves a DownloadRoot still
    /// at that default to the per-user staging folder.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? StagingRootMigrated { get; set; }
    public string PackagersRoot { get; set; } = "";

    public int EstimatedRuntimeMins { get; set; } = 15;
    public int MaximumRuntimeMins { get; set; } = 30;

    /// <summary>Allow or Deny: the deployment type's fallback to the site default boundary group.</summary>
    public string ContentFallback { get; set; } = DeploymentTypeContentOptions.Allow;

    /// <summary>Download or DoNothing for a client on a neighbor or default boundary group.</summary>
    public string SlowNetworkDeploymentMode { get; set; } = DeploymentTypeContentOptions.Download;

    /// <summary>The default title mode IncludeVersion for an application whose profile and per-app entry set none.</summary>
    public bool IncludeVersionInTitle { get; set; }

    /// <summary>The default title mode NoVersion; applies only while IncludeVersionInTitle is off.</summary>
    public bool RemoveVersionFromTitle { get; set; }

    public ContentDistributionSettings ContentDistribution { get; set; } = new();

    public OneClickSettings OneClick { get; set; } = new();

    public string[] HiddenApplications { get; set; } = [];

    /// <summary>Definition ids that Discover unmanaged software no longer offers. Disjoint from <see cref="DiscoveryEnabled"/>.</summary>
    public string[] DiscoveryDismissed { get; set; } = [];

    /// <summary>
    /// Definition ids that Discover unmanaged software made visible. The first-run hidden-list
    /// import never hides them again. Disjoint from <see cref="DiscoveryDismissed"/>.
    /// </summary>
    public string[] DiscoveryEnabled { get; set; } = [];

    /// <summary>SQL Server instance of the ConfigMgr site database; empty uses the server of the site.</summary>
    public string DiscoverySqlServer { get; set; } = "";

    /// <summary>ConfigMgr site database name; empty uses CM_ and the site code.</summary>
    public string DiscoverySqlDatabase { get; set; } = "";

    // Per-app version check state (Script name → last checked + latest version)
    public Dictionary<string, AppCheckState> AppStates { get; set; } = [];

    /// <summary>
    /// When true, skip the installer settings review dialog before staging.
    /// Individual apps with ReviewBeforeStage=true will still prompt.
    /// </summary>
    public bool SkipConfigReview { get; set; }

    /// <summary>
    /// Chosen install parameters per definition (definition Id → parameter key → value),
    /// edited under Settings. They pre-fill the review dialog and apply as they are when
    /// the dialog is skipped.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> InstallerSettingValues { get; set; } = [];

    public string M365Channel { get; set; } = "MonthlyEnterprise";

    /// <summary>
    /// Citrix Workspace switch settings in the citrix-workspace-switches.json
    /// shape. Null in a file written before the shape existed; SettingsService
    /// fills it from the legacy Cwa block on load.
    /// </summary>
    public CitrixWorkspaceSwitches? CitrixSwitches { get; set; }

    /// <summary>
    /// Legacy flat Citrix settings, read so an older settings.json keeps its
    /// choices. Dropped from the file on the next save.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LegacyCwaInstallSettings? Cwa { get; set; }

    /// <summary>Local installer source folder per definition (definition Id → folder).</summary>
    public Dictionary<string, string> LocalSourceFolders { get; set; } = [];

    /// <summary>Chosen side-file paths by side-file key, such as BeyondCompareKeyFile.</summary>
    public Dictionary<string, string> SideFiles { get; set; } = [];

    public IntuneSettings Intune { get; set; } = new();

    /// <summary>The WSUS server for local publishing; an empty server name means not configured.</summary>
    public WsusSettings Wsus { get; set; } = new();

    /// <summary>The CVE lookup preferences.</summary>
    public VulnerabilitySettings Vulnerability { get; set; } = new();

    /// <summary>
    /// The systems the operator publishes to. Null in a file written before the
    /// setting existed; SettingsService infers it on load from the Intune
    /// tenant and the WSUS server.
    /// </summary>
    public SystemsInUse? Systems { get; set; }

    /// <summary>
    /// The global script-signing policy, every switch off by default. A run
    /// snapshot's own policy takes its place for that build.
    /// </summary>
    public ApplicationPackagerEngine.Models.Signing.SigningPolicy ScriptSigning { get; set; } = new();

    // Per-app deployment conditions and command overrides (definition Id → entry)
    public Dictionary<string, DeploymentConditionEntry> DeploymentConditions { get; set; } = [];
    public Dictionary<string, CommandOverrideEntry> CommandOverrides { get; set; } = [];

    public string Theme { get; set; } = "Dark";
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public bool PaneOpen { get; set; } = true;

    /// <summary>The sidebar order and hidden entries. Null in a file written before the sidebar could change; null gives the default sidebar.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SidebarLayout? Sidebar { get; set; }

    /// <summary>The Detection Tester page choices. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DetectionTesterSettings? DetectionTester { get; set; }

    /// <summary>The Maintenance Windows page choices. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MaintenanceWindowsSettings? MaintenanceWindows { get; set; }

    /// <summary>The Collection and Compliance page choices. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CollectionComplianceSettings? CollectionCompliance { get; set; }

    /// <summary>The Installer Analysis page choices and recent files. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public InstallerAnalysisSettings? InstallerAnalysis { get; set; }

    /// <summary>The Deployment Helper page choices and audit log path. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DeploymentHelperSettings? DeploymentHelper { get; set; }

    /// <summary>The DP Content Manager page choices. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DpContentSettings? DpContent { get; set; }

    /// <summary>The Pivots and Scripts page choices. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PivotsScriptsSettings? PivotsScripts { get; set; }

    /// <summary>The Site Hygiene page choices. Null in a file written before the page existed; null gives the page defaults.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SiteHygieneSettings? SiteHygiene { get; set; }

    /// <summary>
    /// Null in a file written before the flag existed. SettingsService stamps it true on load so
    /// an already-configured user never meets the setup window.
    /// </summary>
    public bool? FirstRunCompleted { get; set; }
}

public sealed class IntuneSettings
{
    public string DeploymentTarget { get; set; } = DeploymentTargets.Mecm;
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";

    /// <summary>
    /// Client secret protected with DPAPI for the current Windows user, base64-encoded.
    /// The plaintext secret is never persisted and never logged.
    /// </summary>
    public string ClientSecretProtected { get; set; } = "";
}

/// <summary>
/// One Click opens a plan of the tracked applications, then checks them and, per
/// the action, stages or stages and publishes each to its own destinations. A
/// Report run skips an application checked within its cadence unless forced.
/// </summary>
public sealed class OneClickSettings
{
    /// <summary>Report, Stage, or StageAndPackage.</summary>
    public string Action { get; set; } = OneClickActions.Report;
    public bool ForceOnLaunch { get; set; }
    public int DefaultCadenceDays { get; set; } = 7;
    /// <summary>Definition or packager ids.</summary>
    public string[] TrackedApplications { get; set; } = [];
    /// <summary>Per-application cadence in days where it differs from the default (id → days).</summary>
    public Dictionary<string, int> CadenceOverrides { get; set; } = [];

    /// <summary>Applications with destinations of their own (id → set). Any other application follows the default.</summary>
    public Dictionary<string, DestinationSet> Destinations { get; set; } = [];

    /// <summary>
    /// Null in a file written before the default existed; the planner then
    /// reads the saved deployment target.
    /// </summary>
    public DestinationSet? DefaultDestinations { get; set; }

    /// <summary>Skip or Overwrite: a run decides an existing ConfigMgr application by this, without a prompt.</summary>
    public string OnExisting { get; set; } = ApplicationPackagerEngine.Services.OneClick.OneClickOnExisting.Skip;
}

/// <summary>Stored tokens for the One Click action.</summary>
public static class OneClickActions
{
    public const string Report = "Report";
    public const string Stage = "Stage";
    public const string StageAndPackage = "StageAndPackage";

    public static string Normalize(string? value) => value switch
    {
        Stage => Stage,
        StageAndPackage => StageAndPackage,
        _ => Report
    };
}

/// <summary>
/// Optional steps after the MECM application exists: content distribution to a
/// distribution point group, then a deployment to a test collection. The test
/// deployment only runs after a distribution.
/// </summary>
public sealed class ContentDistributionSettings
{
    public bool AutoDistribute { get; set; }
    public string DpGroupName { get; set; } = "";
    public bool DeployToTestCollection { get; set; }
    public string TestCollectionName { get; set; } = "";
    public bool TestCollectionIsUser { get; set; }
    public bool CreateTestCollectionIfMissing { get; set; }
}

/// <summary>
/// One app's deployment condition selections. Architecture and Network
/// carry the stored token, not the display text.
/// </summary>
public sealed class DeploymentConditionEntry
{
    /// <summary>Any, x64, or ARM64.</summary>
    public string Architecture { get; set; } = DeploymentConditionValues.Any;
    public string[] Languages { get; set; } = [];
    /// <summary>Any, VpnOnly, or OnSiteOnly.</summary>
    public string Network { get; set; } = DeploymentConditionValues.Any;
    /// <summary>None, Architecture, Language, or Network.</summary>
    public string Split { get; set; } = DeploymentConditionValues.NoSplit;
    /// <summary>Empty for the installer's own default, else AllUsers or CurrentUser.</summary>
    public string InstallMode { get; set; } = "";
    /// <summary>Empty for the packager default, else IncludeVersion or NoVersion.</summary>
    public string TitleMode { get; set; } = "";
}

/// <summary>One app's install/uninstall command overrides. Empty keeps the shipped default.</summary>
public sealed class CommandOverrideEntry
{
    public string Install { get; set; } = "";
    public string Uninstall { get; set; } = "";
}

/// <summary>Stored tokens for the deployment condition selections.</summary>
public static class DeploymentConditionValues
{
    public const string Any = "Any";
    public const string X64 = "x64";
    public const string Arm64 = "ARM64";
    public const string VpnOnly = "VpnOnly";
    public const string OnSiteOnly = "OnSiteOnly";
    public const string NoSplit = "None";

    public static readonly string[] Splits = ["Architecture", "Language", "Network"];
    public const string AllUsers = "AllUsers";
    public const string CurrentUser = "CurrentUser";
    public static bool IsInstallMode(string? value) => value is AllUsers or CurrentUser;

    public static bool IsTitleMode(string? value) =>
        value is ApplicationTitle.IncludeVersionToken or ApplicationTitle.NoVersionToken;

    public static bool IsArchitecture(string? value) => value is X64 or Arm64;
    public static bool IsNetwork(string? value) => value is VpnOnly or OnSiteOnly;
    public static bool IsSplit(string? value) => value is not null && Splits.Contains(value);
}

public sealed class AppCheckState
{
    public DateTime? LastChecked { get; set; }
    public string LatestVersion { get; set; } = "";
    public string CurrentVersion { get; set; } = "";

    /// <summary>The last One Click stage of this application.</summary>
    public DateTime? LastStaged { get; set; }

    /// <summary>The version last published to each destination (ConfigMgr, WSUS, Intune).</summary>
    public Dictionary<string, ApplicationPackagerEngine.Services.OneClick.OneClickPublishRecord> Published { get; set; } = [];

    /// <summary>The version each destination last refused; a publish to that destination clears it.</summary>
    public Dictionary<string, ApplicationPackagerEngine.Services.OneClick.OneClickRefusalRecord> NotSupported { get; set; } = [];
}
