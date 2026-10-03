using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Models;

/// <summary>
/// One application in the One Click tracking grid: whether a run includes it, the
/// destinations it publishes to, and how many days a fresh check stays valid. A
/// blank cadence means the default; destinations equal to the default store nothing.
/// </summary>
public partial class OneClickRow : ObservableObject
{
    public required string Uid { get; init; }
    public required string Application { get; init; }
    public required string Vendor { get; init; }

    [ObservableProperty] private bool _tracked;
    [ObservableProperty] private string _cadenceDisplay = "";
    [ObservableProperty] private bool _configMgr;
    [ObservableProperty] private bool _wsus;
    [ObservableProperty] private bool _intune;

    /// <summary>The override in days, or null for the default or an unreadable value.</summary>
    public int? CadenceOverride =>
        int.TryParse(CadenceDisplay.Trim(), out var days) && days > 0 ? days : null;

    public DestinationSet Destinations => new(ConfigMgr, Wsus, Intune);

    /// <summary>The application name, which a screen reader announces for the grid row.</summary>
    public override string ToString() => Application;
}
