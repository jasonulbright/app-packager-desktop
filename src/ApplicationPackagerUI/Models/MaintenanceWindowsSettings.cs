namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, filters and dialog size of the Maintenance Windows page.</summary>
public sealed class MaintenanceWindowsSettings
{
    /// <summary>0 Windows, 1 Coverage, 2 Templates.</summary>
    public int SelectedTab { get; set; }

    public string TextFilter { get; set; } = "";

    /// <summary>Empty for every type, else Any, SoftwareUpdates or TaskSequences.</summary>
    public string TypeFilter { get; set; } = "";

    /// <summary>All, Enabled or Disabled.</summary>
    public string EnabledFilter { get; set; } = "All";

    public bool OnlyCollectionsWithWindows { get; set; } = true;

    /// <summary>Empty for every folder.</summary>
    public string FolderFilter { get; set; } = "";

    public bool CoverageWithoutWindowOnly { get; set; }

    /// <summary>0 Properties, 1 Next 5 occurrences.</summary>
    public int DetailTab { get; set; }

    public double EditorWidth { get; set; }
    public double EditorHeight { get; set; }
}
