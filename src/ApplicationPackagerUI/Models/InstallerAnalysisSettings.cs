namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, the filters and the recent files of the Installer Analysis page.</summary>
public sealed class InstallerAnalysisSettings
{
    /// <summary>0 Overview, 1 MSI Properties, 2 Payload, 3 Inner Installers, 4 File Browser, 5 Registry Settings, 6 Strings.</summary>
    public int SelectedTab { get; set; }

    public string MsiFilter { get; set; } = "";
    public string PayloadFilter { get; set; } = "";
    public string FileFilter { get; set; } = "";
    public string RegistryFilter { get; set; } = "";
    public string StringsFilter { get; set; } = "";

    /// <summary>Empty for every category.</summary>
    public string StringsCategory { get; set; } = "";

    /// <summary>The paths of the last opened installers, newest first. A path is not read until the user opens it again.</summary>
    public List<string>? RecentFiles { get; set; }
}
