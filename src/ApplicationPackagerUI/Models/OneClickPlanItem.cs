using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.Models;

/// <summary>
/// One row of the One Click plan window. Include and the destination boxes apply
/// to this run only; the run writes the step, the outcome and the per-destination
/// result, package id and full message back into the row.
/// </summary>
public partial class OneClickPlanItem : ObservableObject
{
    /// <summary>The operator opened the change details of the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsOpen))]
    private bool _showDetails;

    /// <summary>What this run creates in each destination, one line for each; empty when the plan writes nothing for the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChangeDetails))]
    [NotifyPropertyChangedFor(nameof(DetailsOpen))]
    private string _changeDetails = "";

    public bool HasChangeDetails => ChangeDetails.Length > 0;

    public bool DetailsOpen => ShowDetails && HasChangeDetails;

    public required string Key { get; init; }
    public required string Application { get; init; }
    public string Vendor { get; init; } = "";

    /// <summary>The two characters before the name in the plan.</summary>
    public string Mark => Row?.Mark ?? Helpers.PackagesCells.MarkOf(Application);

    /// <summary>
    /// "Create application and deployment type" for an included row, "Skip \u00b7 reason" for one that is not. An included row has a reason when the run
    /// leaves one of its destinations out, so it shows the reason too.
    /// </summary>
    public string PlannedLine => (Include ? Planned : "Skip") + (Reason.Length > 0 ? " \u00b7 " + Reason : "");

    /// <summary>The action of a run that only checks: "Check", or "Skip" for a row that is not included.</summary>
    public string PlannedWord => Include ? Planned : "Skip";

    public string LastPublishedShown => LastPublished.Length > 0 ? LastPublished : "None";

    /// <summary>The grid row the run checks, stages and publishes; null in a plan built without one.</summary>
    public PackagerInfo? Row { get; init; }

    /// <summary>
    /// The version the history held before the chain checks of this One Click run recorded theirs; null reads the history at the start of the row.
    /// The run compares the new version with this one, so the checks that open One Click do not hide an update.
    /// </summary>
    public string? PriorKnownVersion { get; init; }

    [ObservableProperty] private string _version = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlannedLine))]
    [NotifyPropertyChangedFor(nameof(PlannedWord))]
    private bool _include;
    [ObservableProperty] private bool _configMgr;
    [ObservableProperty] private bool _wsus;
    [ObservableProperty] private bool _intune;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastPublishedShown))]
    private string _lastPublished = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlannedLine))]
    [NotifyPropertyChangedFor(nameof(PlannedWord))]
    private string _planned = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlannedLine))]
    private string _reason = "";
    [ObservableProperty] private string _step = "";
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private string _resultConfigMgr = "";
    [ObservableProperty] private string _resultWsus = "";
    [ObservableProperty] private string _resultIntune = "";

    /// <summary>Where the destination boxes of this row come from: the saved selection of the application, the default, or a change for this run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceToolTip))]
    private string _sourceText = "";

    public string SourceToolTip => "Destinations from: " + SourceText;

    /// <summary>One cell for each destination that the run reached or refused, for the results window.</summary>
    public List<PublishDestinationResult> Cells { get; } = [];

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
