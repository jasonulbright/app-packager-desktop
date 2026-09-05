namespace ApplicationPackagerUI.Models;

/// <summary>
/// One application in the One Click tracking grid: whether a run includes it and
/// how many days a fresh check stays valid. A blank cadence means the default.
/// </summary>
public partial class OneClickRow : ObservableObject
{
    public required string Uid { get; init; }
    public required string Application { get; init; }
    public required string Vendor { get; init; }

    [ObservableProperty] private bool _tracked;
    [ObservableProperty] private string _cadenceDisplay = "";

    /// <summary>The override in days, or null for the default or an unreadable value.</summary>
    public int? CadenceOverride =>
        int.TryParse(CadenceDisplay.Trim(), out var days) && days > 0 ? days : null;
}
