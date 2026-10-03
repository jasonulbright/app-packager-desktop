using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Models;

/// <summary>
/// One row of the One Click plan window. Include and the destination boxes apply
/// to this run only; the run writes the step, the outcome and the per-destination
/// result, package id and full message back into the row.
/// </summary>
public partial class OneClickPlanItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Application { get; init; }
    public string Vendor { get; init; } = "";

    /// <summary>The grid row the run checks, stages and publishes; null in a plan built without one.</summary>
    public PackagerInfo? Row { get; init; }

    [ObservableProperty] private string _version = "";
    [ObservableProperty] private bool _include;
    [ObservableProperty] private bool _configMgr;
    [ObservableProperty] private bool _wsus;
    [ObservableProperty] private bool _intune;
    [ObservableProperty] private string _lastPublished = "";
    [ObservableProperty] private string _planned = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _step = "";
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private string _resultConfigMgr = "";
    [ObservableProperty] private string _resultWsus = "";
    [ObservableProperty] private string _resultIntune = "";

    /// <summary>True once the operator changed Include; a replan then keeps the operator's value.</summary>
    public bool IncludeTouched { get; set; }

    public IReadOnlyList<string> PublishTo { get; set; } = [];

    /// <summary>The checked destinations the plan left out as not ready, with the plan's reason.</summary>
    public IReadOnlyDictionary<string, string> NotReady { get; set; } = new Dictionary<string, string>();

    public Dictionary<string, string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DestinationSet Destinations => new(ConfigMgr, Wsus, Intune);

    public string Result(string destination) => destination switch
    {
        OneClickDestinations.ConfigMgr => ResultConfigMgr,
        OneClickDestinations.Wsus => ResultWsus,
        OneClickDestinations.Intune => ResultIntune,
        _ => ""
    };

    /// <summary>Writes one destination's result cell, package id and full message.</summary>
    public void SetResult(string destination, string cell, string id = "", string detail = "")
    {
        switch (destination)
        {
            case OneClickDestinations.ConfigMgr: ResultConfigMgr = cell; break;
            case OneClickDestinations.Wsus: ResultWsus = cell; break;
            case OneClickDestinations.Intune: ResultIntune = cell; break;
            default: return;
        }
        Ids[destination] = id;
        Details[destination] = detail;
    }

    public IReadOnlyDictionary<string, string> Results => OneClickDestinations.All.ToDictionary(d => d, Result);

    /// <summary>The application name, which a screen reader announces for the grid row.</summary>
    public override string ToString() => Application;
}
