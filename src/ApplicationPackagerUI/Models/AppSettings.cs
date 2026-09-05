using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Models;

public sealed class AppSettings
{
    // Organization
    public string CompanyName { get; set; } = "";

    // MECM Site
    public string SiteCode { get; set; } = "MCM";
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
    public string DownloadRoot { get; set; } = @"C:\temp\ap";
    public string PackagersRoot { get; set; } = "";

    // Deployment Type defaults
    public int EstimatedRuntimeMins { get; set; } = 15;
    public int MaximumRuntimeMins { get; set; } = 30;

    // Steps after the MECM application exists
    public ContentDistributionSettings ContentDistribution { get; set; } = new();

    // One Click: tracked applications and what a run does with an update
    public OneClickSettings OneClick { get; set; } = new();

    // Application visibility
    public string[] HiddenApplications { get; set; } = [];

    // Per-app version check state (Script name → last checked + latest version)
    public Dictionary<string, AppCheckState> AppStates { get; set; } = [];

    // Acquisition preference
    public string AcquisitionMethod { get; set; } = "StaticUrl"; // "StaticUrl" or "Winget"

    // Auto-check on launch
    public bool AutoCheckLatestOnLaunch { get; set; } = true;

    // Staging behavior
    /// <summary>
    /// When true, skip the installer settings review dialog before staging.
    /// Individual apps with ReviewBeforeStage=true will still prompt.
    /// </summary>
    public bool SkipConfigReview { get; set; }

    // Per-app: M365
    public string M365Channel { get; set; } = "MonthlyEnterprise";

    // Per-app: Citrix Workspace
    public CwaInstallSettings Cwa { get; set; } = new();

    // Intune / deployment target
    public IntuneSettings Intune { get; set; } = new();

    // Per-app deployment conditions and command overrides (definition Id → entry)
    public Dictionary<string, DeploymentConditionEntry> DeploymentConditions { get; set; } = [];
    public Dictionary<string, CommandOverrideEntry> CommandOverrides { get; set; } = [];

    // UI
    public string Theme { get; set; } = "Dark";
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public bool PaneOpen { get; set; } = true;

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

    /// <summary>
    /// Under an MECM-only target, a successful Package also builds the .intunewin and
    /// stores it beside the network content version folder. Targets that publish to
    /// Intune build it regardless.
    /// </summary>
    public bool CreateIntuneWin { get; set; }
}

/// <summary>
/// One Click runs a version check over the tracked applications and, per the
/// action, stages or stages and packages the ones with a newer version. An
/// application checked within its cadence is skipped unless the run forces it.
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

    public static bool IsArchitecture(string? value) => value is X64 or Arm64;
    public static bool IsNetwork(string? value) => value is VpnOnly or OnSiteOnly;
    public static bool IsSplit(string? value) => value is not null && Splits.Contains(value);
}

public sealed class AppCheckState
{
    public DateTime? LastChecked { get; set; }
    public string LatestVersion { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
}

public sealed class CwaInstallSettings
{
    public string StoreName { get; set; } = "";
    public string StoreUrl { get; set; } = "";
    public bool Clean { get; set; }
    public bool SingleSignOn { get; set; }
    public bool AppProtection { get; set; }
    public bool PreLaunch { get; set; }
    public bool SelfService { get; set; }
    public bool PluginTeams { get; set; }
    public bool PluginZoom { get; set; }
    public bool PluginWebEx { get; set; }
    public bool PluginUber { get; set; }
    public bool PluginUberSkip { get; set; }
    public bool PluginEpa { get; set; }
    public bool PluginStorageResync { get; set; }
    public string AutoUpdate { get; set; } = "Auto";
    public string AddStorePolicy { get; set; } = "None";
    public string SavePassword { get; set; } = "None";
}
