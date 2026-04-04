namespace ApplicationPackagerUI.Models;

public sealed class AppSettings
{
    // Organization
    public string CompanyName { get; set; } = "";

    // MECM Site
    public string SiteCode { get; set; } = "MCM";
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

    // UI
    public string Theme { get; set; } = "Dark";
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public bool PaneOpen { get; set; } = true;
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
