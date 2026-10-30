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
            Key = "Silent", Label = "Silent install", Tooltip = "Run the setup silently (/s)",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/s", Group = "Install Behavior"
        },
        new()
        {
            Key = "MsiQuiet", Label = "Silent MSI", Tooltip = "Pass /qn to the MSI the setup runs (/v\"/qn\")",
            ControlType = "Toggle", DefaultValue = "true", CommandFragment = "/v\"/qn\"", Group = "Install Behavior"
        }
    ];

    /// <summary>
    /// Matches the parameters to install arguments that already carry their switches. A toggle is on when its
    /// fragment is in the arguments and off otherwise, and the fragment leaves the arguments, so the stage does
    /// not pass a switch twice and a mode switch (/CURRENTUSER) is not joined by the opposite default (/ALLUSERS).
    /// A free-value parameter gets an empty default, so it adds nothing the analysis did not propose.
    /// </summary>
    public static InstallerParameter[]? AlignWithArgs(InstallerParameter[]? parameters, string installArgs, out string remainingArgs)
    {
        remainingArgs = installArgs ?? "";
        if (parameters is null) return null;
        foreach (var parameter in parameters)
        {
            if (parameter.ControlType == "Toggle" && !string.IsNullOrEmpty(parameter.CommandFragment))
            {
                var found = RemoveToken(remainingArgs, parameter.CommandFragment, out var rest);
                parameter.DefaultValue = found ? "true" : "false";
                remainingArgs = rest;
            }
            else
            {
                parameter.DefaultValue = "";
            }
        }
        remainingArgs = System.Text.RegularExpressions.Regex.Replace(remainingArgs, @"\s{2,}", " ").Trim();
        return parameters;
    }

    private static bool RemoveToken(string args, string fragment, out string rest)
    {
        var at = 0;
        while ((at = args.IndexOf(fragment, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + fragment.Length;
            var startsToken = at == 0 || char.IsWhiteSpace(args[at - 1]);
            var endsToken = end == args.Length || char.IsWhiteSpace(args[end]);
            if (startsToken && endsToken)
            {
                rest = args[..at] + args[end..];
                return true;
            }
            at = end;
        }
        rest = args;
        return false;
    }
}
