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
    public string DownloadRoot { get; set; } = @"C:\temp\ap";
    public string PackagersRoot { get; set; } = "";

    // Deployment Type defaults
    public int EstimatedRuntimeMins { get; set; } = 15;
    public int MaximumRuntimeMins { get; set; } = 30;

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
}

/// <summary>Deployment targets a Package run can write to.</summary>
public static class DeploymentTargets
{
    public const string Mecm = "MECM";
    public const string MecmAndIntune = "MECMAndIntune";
    public const string IntuneOnly = "IntuneOnly";

    /// <summary>Maps an unknown or absent stored value onto MECM only.</summary>
    public static string Normalize(string? value) => value switch
    {
        MecmAndIntune => MecmAndIntune,
        IntuneOnly => IntuneOnly,
        _ => Mecm
    };

    public static bool PublishesToIntune(string? value) => Normalize(value) != Mecm;

    public static bool SkipsMecm(string? value) => Normalize(value) == IntuneOnly;
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
