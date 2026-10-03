namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The flat Citrix Workspace block an older settings.json carries. It never
/// reached a staged install, and an unchecked box cannot be told apart from
/// one left alone, so only a checked box or a chosen value carries over.
/// </summary>
public sealed class LegacyCwaInstallSettings
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

    /// <summary>
    /// The nested settings this block maps to. "Auto" was the default and
    /// "None" the empty choice, so both map to not set; the Add Store choices
    /// Admin and User name no documented ALLOWADDSTORE value and map to not
    /// set; Save Password Off and On map to N and A. Storage Resync has no
    /// documented switch and is dropped.
    /// </summary>
    public CitrixWorkspaceSwitches ToSwitches()
    {
        static bool? Checked(bool value) => value ? true : null;
        static string? Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        return new CitrixWorkspaceSwitches
        {
            Store = new CwaStore { Name = Text(StoreName), Url = Text(StoreUrl) },
            Installation = new CwaInstallation
            {
                CleanInstall = Checked(Clean),
                IncludeSSON = Checked(SingleSignOn),
                EnableSSON = Checked(SingleSignOn),
                AppProtection = Checked(AppProtection),
                SessionPreLaunch = Checked(PreLaunch),
                SelfServiceMode = Checked(SelfService)
            },
            Plugins = new CwaPlugins
            {
                MSTeamsPlugin = Checked(PluginTeams),
                ZoomPlugin = Checked(PluginZoom),
                WebExPlugin = Checked(PluginWebEx),
                UberAgent = Checked(PluginUber),
                UberAgentSkipUpgrade = Checked(PluginUberSkip),
                EPAClient = Checked(PluginEpa)
            },
            UpdateAndTelemetry = new CwaUpdateAndTelemetry
            {
                AutoUpdateCheck = AutoUpdate?.Trim().ToLowerInvariant() is "manual" or "disabled"
                    ? AutoUpdate.Trim().ToLowerInvariant()
                    : null
            },
            StorePolicy = new CwaStorePolicy
            {
                AllowAddStore = null,
                AllowSavePwd = SavePassword?.Trim() switch
                {
                    "Off" => "N",
                    "On" => "A",
                    _ => null
                }
            }
        };
    }
}
