using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>A ring plan as the Plans list shows it.</summary>
public sealed class RingPlanRow(RingPlan plan)
{
    public RingPlan Plan { get; } = plan;
    public string Name => Plan.Name;
    public string Rings => Plan.Rings.Count.ToString(CultureInfo.CurrentCulture);
    public string Hold => Plan.HoldLaterRings ? "Hold later rings" : "All at once";
    public string Basis => RingText.IsUtc(Plan.TimeBasedOn) ? "UTC" : "Local time";
    public string Source => Plan.IsSeed ? "Shipped" : "User";
    public string Description => Plan.Description;

    public override string ToString() => Name;
}

/// <summary>A ring of the expanded preview; a collection set for this run only is marked.</summary>
public sealed partial class RingPreviewRow(RingExpansionRow row) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Index), nameof(Name), nameof(Collection), nameof(MemberCount), nameof(Purpose), nameof(Available), nameof(Deadline),
        nameof(DpGroup), nameof(Threshold), nameof(CheckText))]
    private RingExpansionRow _row = row;

    /// <summary>True when the collection was set for this run; the plan file keeps its own collection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Change))]
    private bool _edited;

    /// <summary>True once Validate filled the checks of the ring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckText))]
    private bool _checked;

    public int Index => Row.Index;
    public string Name => Row.Name;
    public string Collection => Row.CollectionId.Length == 0 ? "(no collection)" : $"{Row.CollectionName} ({Row.CollectionId})";
    public string MemberCount => Row.MemberCount.ToString(CultureInfo.CurrentCulture);
    public string Purpose => Row.Purpose;
    public string Available => RingText.Time(Row.AvailableTime);
    public string Deadline => RingText.Time(Row.Deadline);
    public string DpGroup => Row.DpGroup;
    public string Threshold => RingText.Threshold(Row.SuccessThresholdPercent);
    public string Change => Edited ? "Collection set for this run" : "";

    public string CheckText
    {
        get
        {
            if (!Checked) return "";
            var blocking = Row.Checks.Count(DeploymentText.Blocks);
            var warnings = Row.Checks.Count(c => c.Result == DeploymentText.Warn);
            return blocking > 0 ? $"{DeploymentText.Count(blocking, "check")} did not pass"
                : warnings > 0 ? $"Passed with {DeploymentText.Count(warnings, "warning")}"
                : "Passed";
        }
    }

    public override string ToString() => $"{Name} {Collection}";
}

/// <summary>A check of the run or of one ring, as the Rings checks grid lists it.</summary>
public sealed record RingCheckRow(string Ring, string Check, string Result, string Message)
{
    public override string ToString() => $"{Ring} {Check}: {Result}";
}

/// <summary>A run as the Open runs grid shows it.</summary>
public sealed class RingRunRow(RingRun run)
{
    public RingRun Run { get; } = run;
    public string RunId => Run.RunId;
    public string Plan => Run.PlanName;
    public string Object => $"{Run.ObjectName}{(Run.Program.Length > 0 ? $" ({Run.Program})" : "")}";
    public string Type => DeploymentText.TypeName(Run.ObjectType);
    public string Started => DeploymentText.Time(Run.CreatedUtc.ToLocalTime());
    public string Rings => RingText.StatusLine(Run);
    public string CreatedBy => Run.CreatedBy;

    public override string ToString() => $"{RunId} {Plan} {Object}";
}

/// <summary>A ring of the selected run with its live counts; a count that is not known reads 'unknown'.</summary>
public sealed partial class RingRunRingRow(RingRunRing ring, string timeBasedOn) : ObservableObject
{
    public RingRunRing Ring { get; } = ring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Targeted), nameof(Success), nameof(Error), nameof(InProgress), nameof(Unknown), nameof(SuccessRate), nameof(CountsNote))]
    private RingLiveCounts? _counts;

    public int Index => Ring.Index;
    public string Name => Ring.Name;
    public string Status => Ring.Status;
    public string Collection => $"{Ring.CollectionName} ({Ring.CollectionId})";
    public string Purpose => Ring.Purpose;
    public string Available => RingText.Time(Ring.AvailableTime) + (RingText.IsUtc(timeBasedOn) ? " UTC" : "");
    public string Deadline => Ring.Deadline is null ? "" : RingText.Time(Ring.Deadline) + (RingText.IsUtc(timeBasedOn) ? " UTC" : "");
    public string DeploymentId => Ring.DeploymentId ?? "";
    public string Threshold => RingText.Threshold(Ring.SuccessThresholdPercent);

    private bool Known => Counts is { CountsKnown: true };
    private string Count(Func<RingLiveCounts, int> value) => Known ? value(Counts!).ToString(CultureInfo.CurrentCulture) : Ring.DeploymentId is null ? "" : RingText.UnknownCount;

    public string Targeted => Count(c => c.Targeted);
    public string Success => Count(c => c.Success);
    public string Error => Count(c => c.Error);
    public string InProgress => Count(c => c.InProgress);
    public string Unknown => Count(c => c.Unknown);
    public string SuccessRate => RingText.SuccessRate(Counts) is { } rate ? $"{rate.ToString("0", CultureInfo.CurrentCulture)} %" : "";

    /// <summary>The message of the live counts: why they are not known, or that the deployment was not found by its ID.</summary>
    public string CountsNote => Counts?.Message ?? "";

    public override string ToString() => $"{Name} {Status}";
}
