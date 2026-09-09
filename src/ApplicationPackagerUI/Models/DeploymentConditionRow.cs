using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Models;

/// <summary>
/// One app's row in the Deployment Conditions grid. Display text is what
/// the grid binds; the stored tokens are recovered on save.
/// </summary>
public partial class DeploymentConditionRow : ObservableObject
{
    public const string ArchitectureAny = "Any";
    public const string ArchitectureX64 = "x64 only";
    public const string ArchitectureArm64 = "ARM64 only";
    public const string NetworkAny = "Any";
    public const string NetworkVpn = "VPN only";
    public const string NetworkOnSite = "On-site only";

    public static readonly string[] ArchitectureOptions =
        [ArchitectureAny, ArchitectureX64, ArchitectureArm64];

    public static readonly string[] NetworkOptions =
        [NetworkAny, NetworkVpn, NetworkOnSite];

    public const string InstallModeDefault = "Default";
    public const string InstallModeSystem = "System";
    public const string InstallModeUser = "User";

    public static readonly string[] InstallModeOptions =
        [InstallModeDefault, InstallModeSystem, InstallModeUser];

    public const string TitleModeDefault = "Packager default";
    public const string TitleModeIncludeVersion = "Include version";
    public const string TitleModeNoVersion = "No version";

    public static readonly string[] TitleModeOptions =
        [TitleModeDefault, TitleModeIncludeVersion, TitleModeNoVersion];

    /// <summary>Definition id; the key the selections persist under.</summary>
    public string DefinitionId { get; init; } = "";
    public string Application { get; init; } = "";
    public string Vendor { get; init; } = "";

    /// <summary>'None' plus whatever splits the definition declares.</summary>
    public string[] VariantOptions { get; init; } = [DeploymentConditionValues.NoSplit];
    public bool VariantCapable => VariantOptions.Length > 1;

    /// <summary>True when the definition declares both install modes, so the Install for cell is live.</summary>
    public bool InstallModeCapable { get; init; }

    [ObservableProperty] private string _architectureDisplay = ArchitectureAny;
    [ObservableProperty] private string _languagesDisplay = "";
    [ObservableProperty] private string _networkDisplay = NetworkAny;
    [ObservableProperty] private string _splitDisplay = DeploymentConditionValues.NoSplit;
    [ObservableProperty] private string _installModeDisplay = InstallModeDefault;
    [ObservableProperty] private string _titleModeDisplay = TitleModeDefault;
    [ObservableProperty] private string _commandInstall = "";
    [ObservableProperty] private string _commandUninstall = "";

    /// <summary>Grid button label: whether this app carries command overrides.</summary>
    public string CommandLabel =>
        CommandInstall.Trim().Length > 0 || CommandUninstall.Trim().Length > 0
            ? "Modified" : "Default";

    partial void OnCommandInstallChanged(string value) => OnPropertyChanged(nameof(CommandLabel));
    partial void OnCommandUninstallChanged(string value) => OnPropertyChanged(nameof(CommandLabel));

    // ── Display ⇄ stored token ───────────────────────────────────

    public static string ArchitectureToDisplay(string? stored) => stored switch
    {
        DeploymentConditionValues.X64 => ArchitectureX64,
        DeploymentConditionValues.Arm64 => ArchitectureArm64,
        _ => ArchitectureAny
    };

    public static string DisplayToArchitecture(string? display) => display switch
    {
        ArchitectureX64 => DeploymentConditionValues.X64,
        ArchitectureArm64 => DeploymentConditionValues.Arm64,
        _ => DeploymentConditionValues.Any
    };

    public static string NetworkToDisplay(string? stored) => stored switch
    {
        DeploymentConditionValues.VpnOnly => NetworkVpn,
        DeploymentConditionValues.OnSiteOnly => NetworkOnSite,
        _ => NetworkAny
    };

    public static string DisplayToNetwork(string? display) => display switch
    {
        NetworkVpn => DeploymentConditionValues.VpnOnly,
        NetworkOnSite => DeploymentConditionValues.OnSiteOnly,
        _ => DeploymentConditionValues.Any
    };

    public static string InstallModeToDisplay(string? stored) => stored switch
    {
        DeploymentConditionValues.AllUsers => InstallModeSystem,
        DeploymentConditionValues.CurrentUser => InstallModeUser,
        _ => InstallModeDefault
    };

    public static string DisplayToInstallMode(string? display) => display switch
    {
        InstallModeSystem => DeploymentConditionValues.AllUsers,
        InstallModeUser => DeploymentConditionValues.CurrentUser,
        _ => ""
    };

    public static string TitleModeToDisplay(string? stored) => stored switch
    {
        ApplicationTitle.IncludeVersionToken => TitleModeIncludeVersion,
        ApplicationTitle.NoVersionToken => TitleModeNoVersion,
        _ => TitleModeDefault
    };

    public static string DisplayToTitleMode(string? display) => display switch
    {
        TitleModeIncludeVersion => ApplicationTitle.IncludeVersionToken,
        TitleModeNoVersion => ApplicationTitle.NoVersionToken,
        _ => ""
    };
}
