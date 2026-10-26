namespace ApplicationPackagerUI.Models;

/// <summary>The last tabs, the library folder, the result wait time and the target kind of the Pivots and Scripts page. No value is a secret or a script text.</summary>
public sealed class PivotsScriptsSettings
{
    /// <summary>0 Pivot, 1 Script, 2 Site scripts, 3 History, 4 Options.</summary>
    public int SelectedTab { get; set; }

    /// <summary>0 Results, 1 Devices, 2 Raw, 3 Pipeline.</summary>
    public int ResultTab { get; set; }

    /// <summary>The user library folder; null or empty for the built-in samples only.</summary>
    public string? LibraryFolder { get; set; }

    /// <summary>The minutes the page waits for the results of a run; 0 gives the default.</summary>
    public int WaitMinutes { get; set; }

    /// <summary>Collection, Device, Pattern or List.</summary>
    public string? TargetKind { get; set; }
}
