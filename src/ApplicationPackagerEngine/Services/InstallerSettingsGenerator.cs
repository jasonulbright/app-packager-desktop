using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Generates default InstallerParameter arrays based on InstallerFramework.
/// Used when a definition has no InstallerSettings — provides sensible defaults
/// with tooltips for each known installer technology.
/// </summary>
public static class InstallerSettingsGenerator
{
    /// <summary>
    /// Returns default parameters for a given installer framework.
    /// Returns null if no meaningful defaults exist for the framework.
    /// </summary>
    public static InstallerParameter[]? GenerateDefaults(string? framework)
    {
        return framework switch
        {
            "NativeMSI" => MsiDefaults(),
            "InnoSetup" => InnoSetupDefaults(),
            "NSIS" => NsisDefaults(),
            "WixBurn" => WixBurnDefaults(),
            "InstallShield" => InstallShieldDefaults(),
            _ => null
        };
    }

    private static InstallerParameter[] MsiDefaults() =>
    [
        new()
        {
            Key = "Quiet", Label = "Silent install", Tooltip = "Run installer with no UI (/qn)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/qn", Group = "Install Behavior"
        },
        new()
        {
            Key = "NoRestart", Label = "Suppress reboot", Tooltip = "Prevent automatic restart after install (/norestart)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/norestart", Group = "Install Behavior"
        },
        new()
        {
            Key = "Logging", Label = "Enable MSI logging", Tooltip = "Write verbose install log to %TEMP% (/l*v)",
            ControlType = "Toggle", DefaultValue = "false", CommandFragment = "/l*v \"%TEMP%\\msi_install.log\"", Group = "Diagnostics"
        }
    ];

    private static InstallerParameter[] InnoSetupDefaults() =>
    [
        new()
        {
            Key = "VerySilent", Label = "Very silent", Tooltip = "No UI at all, not even progress bar (/VERYSILENT)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/VERYSILENT", Group = "Install Behavior"
        },
        new()
        {
            Key = "SuppressMsgBoxes", Label = "Suppress message boxes", Tooltip = "Suppress all message boxes during install (/SUPPRESSMSGBOXES)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/SUPPRESSMSGBOXES", Group = "Install Behavior"
        },
        new()
        {
            Key = "NoRestart", Label = "Suppress reboot", Tooltip = "Do not restart after install (/NORESTART)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/NORESTART", Group = "Install Behavior"
        },
        new()
        {
            Key = "SpDisable", Label = "Skip setup preparation", Tooltip = "Skip the 'Preparing to install' dialog (/SP-)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/SP-", Group = "Install Behavior"
        },
        new()
        {
            Key = "AllUsers", Label = "Install for all users", Tooltip = "Install system-wide for all users (/ALLUSERS)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/ALLUSERS", Group = "Scope"
        },
        new()
        {
            Key = "Language", Label = "Language", Tooltip = "Force installer language (e.g., en for English). Prevents language selection dialog.",
            ControlType = "Dropdown", DefaultValue = "en", Options = ["en", "de", "fr", "es", "it", "pt", "ja", "zh", "ko"],
            CommandFragment = "/LANG={value}", Group = "Localization"
        }
    ];

    private static InstallerParameter[] NsisDefaults() =>
    [
        new()
        {
            Key = "Silent", Label = "Silent install", Tooltip = "Run installer silently (/S). NSIS is case-sensitive — must be uppercase S.",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/S", Group = "Install Behavior"
        },
        new()
        {
            Key = "AllUsers", Label = "Install for all users", Tooltip = "Install system-wide (/allusers). Not supported by all NSIS installers.",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/allusers", Group = "Scope"
        }
    ];

    private static InstallerParameter[] WixBurnDefaults() =>
    [
        new()
        {
            Key = "Quiet", Label = "Silent install", Tooltip = "Run installer silently (/quiet or /install /quiet)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/quiet", Group = "Install Behavior"
        },
        new()
        {
            Key = "NoRestart", Label = "Suppress reboot", Tooltip = "Prevent automatic restart (/norestart)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/norestart", Group = "Install Behavior"
        },
        new()
        {
            Key = "Logging", Label = "Enable logging", Tooltip = "Write install log (/log \"%TEMP%\\burn_install.log\")",
            ControlType = "Toggle", DefaultValue = "false", CommandFragment = "/log \"%TEMP%\\burn_install.log\"", Group = "Diagnostics"
        }
    ];

    private static InstallerParameter[] InstallShieldDefaults() =>
    [
        new()
        {
            Key = "Silent", Label = "Silent install", Tooltip = "Run in unattended mode (--mode unattended)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "--mode unattended", Group = "Install Behavior"
        },
        new()
        {
            Key = "NoUI", Label = "No UI", Tooltip = "Suppress all UI (--unattendedmodeui none)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "--unattendedmodeui none", Group = "Install Behavior"
        }
    ];
}
