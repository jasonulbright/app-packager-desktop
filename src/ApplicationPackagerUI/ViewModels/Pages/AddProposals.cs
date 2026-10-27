using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>The marker text and tooltip of each evidence level.</summary>
public static class EvidenceMarkers
{
    public static string Text(Evidence evidence) => evidence switch
    {
        Evidence.Confirmed => "Confirmed",
        Evidence.Predicted => "Predicted, not verified",
        Evidence.Manual => "Manual",
        _ => "No value"
    };

    public static string ToolTip(Evidence evidence, string? source)
    {
        var text = evidence switch
        {
            Evidence.Confirmed => "Read from the installer.",
            Evidence.Predicted => "Taken from a table or from documentation, not read from the installer. The value is not verified.",
            Evidence.Manual => "Typed by you.",
            _ => "No value could be derived from the installer. Type a value."
        };
        return string.IsNullOrWhiteSpace(source) ? text : text + " Source: " + source;
    }
}

/// <summary>One proposed value of the analysis in the review: editable, with its evidence marker.</summary>
public sealed partial class ProposalRow : ObservableObject
{
    private readonly string? _proposedValue;
    private readonly Evidence _proposedEvidence;

    public ProposalRow(string label, string? value, Evidence evidence, string? source, IReadOnlyList<string>? options = null, string? automationId = null, bool isReadOnly = false)
    {
        Label = label;
        IsReadOnly = isReadOnly;
        Source = source;
        Options = options;
        AutomationId = automationId ?? "";
        _proposedValue = value ?? "";
        _proposedEvidence = evidence;
        _value = value ?? "";
        _evidence = evidence;
        _isAccepted = evidence != Evidence.None && !string.IsNullOrWhiteSpace(value);
    }

    public string Label { get; }
    public string? Source { get; }
    public string AutomationId { get; }

    /// <summary>A value the review shows but does not change; it is read from the file or follows from other values.</summary>
    public bool IsReadOnly { get; }

    /// <summary>The choices of a value picked from a list; null for a free text value.</summary>
    public IReadOnlyList<string>? Options { get; }
    public bool IsChoice => Options is not null && !IsReadOnly;
    public bool IsText => Options is null || IsReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerText), nameof(MarkerToolTip), nameof(MarkerAutomationName), nameof(IsPredicted))]
    private Evidence _evidence;

    [ObservableProperty] private string _value;

    /// <summary>Add from web copies only the accepted rows into its fields.</summary>
    [ObservableProperty] private bool _isAccepted;

    public string MarkerText => EvidenceMarkers.Text(Evidence);
    public string MarkerToolTip => EvidenceMarkers.ToolTip(Evidence, Source);
    public string MarkerAutomationName => Label + ": " + MarkerText;
    public bool IsPredicted => Evidence == Evidence.Predicted;

    partial void OnValueChanged(string value) =>
        Evidence = string.Equals(value, _proposedValue, StringComparison.Ordinal) ? _proposedEvidence : Evidence.Manual;

    public ProposedValue<string> ToProposed() => new(Value: Value, Evidence: Evidence, Source: Evidence == Evidence.Manual ? null : Source);

    public ProposedValue<string?> ToOptionalProposed() =>
        new(Value: string.IsNullOrWhiteSpace(Value) ? null : Value, Evidence: Evidence, Source: Evidence == Evidence.Manual ? null : Source);
}

