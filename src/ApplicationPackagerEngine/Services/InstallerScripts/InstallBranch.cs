namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// One install branch of a compiled installer resolved end to end: the
/// folder, the uninstaller, the Add/Remove Programs key and hive, the
/// context, and the switches that select the branch.
/// </summary>
public sealed class InstallBranch
{
    /// <summary>Folder in the installer's own notation ($PROGRAMFILES64\App, {autopf}\App).</summary>
    public string InstallDir { get; set; } = "";
    /// <summary>Folder in Windows environment-variable form, or the notation left in place when unresolvable.</summary>
    public string InstallDirWindows { get; set; } = "";
    public string UninstallerPath { get; set; } = "";
    public string UninstallerPathWindows { get; set; } = "";
    /// <summary>Silent install switches, including the mode switch for a non-default branch.</summary>
    public string InstallArgs { get; set; } = "";
    /// <summary>Quoted uninstaller path plus silent switches; empty when the folder is not resolvable.</summary>
    public string SilentUninstallCommand { get; set; } = "";
    /// <summary>HKLM:\ or HKCU:\ prefixed key path; empty when the key is computed at run time or absent.</summary>
    public string UninstallRegistryKey { get; set; } = "";
    public string UninstallRegistryKeyNote { get; set; } = "";
    /// <summary>HKLM or HKCU.</summary>
    public string RegistryHive { get; set; } = "";
    /// <summary>"64" or "32" for an HKLM key; empty otherwise.</summary>
    public string RegistryView { get; set; } = "";
    /// <summary>PerMachine or PerUser.</summary>
    public string InstallContext { get; set; } = "";
    /// <summary>True when the install folder resolves before install.</summary>
    public bool Resolved { get; set; }
    /// <summary>
    /// True when run-time script code chooses the folder (an assignment in .onInit or a run-time
    /// candidate in place of a placeholder): the folder and the uninstall command are predictions.
    /// </summary>
    public bool InstallDirPredicted { get; set; }

    /// <summary>The key relative to its hive, without the hive prefix.</summary>
    public string UninstallRegistryKeyRelative
    {
        get
        {
            var key = UninstallRegistryKey;
            var index = key.IndexOf(":\\", StringComparison.Ordinal);
            return index < 0 ? key : key[(index + 2)..];
        }
    }

    /// <summary>True when the key sits in the 64-bit registry view or is not an HKLM key.</summary>
    public bool Is64BitView => RegistryView != "32";
}
