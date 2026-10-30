using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Models;

/// <summary>
/// One reviewable field of a dropped-installer analysis: the editable value plus the
/// confidence badge and provenance the operator needs to judge it.
/// </summary>
public partial class ProposedFieldRow : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(BadgeText), nameof(BadgeKind))]
    private string _value = "";

    public string Label { get; init; } = "";
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Predicted;
    public string Provenance { get; init; } = "";

    public bool IsPredicted => Confidence == AnalysisConfidence.Predicted;
    /// <summary>An empty value carries no evidence, so it never shows the Confirmed badge.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public string BadgeText => IsEmpty ? "Not set" : IsPredicted ? "Predicted" : "Confirmed";

    /// <summary>The style key of the badge: Confirmed, Predicted or NotSet.</summary>
    public string BadgeKind => IsEmpty ? "NotSet" : IsPredicted ? "Predicted" : "Confirmed";
    public string ProvenanceText => string.IsNullOrWhiteSpace(Provenance) ? "no source" : Provenance;

    /// <summary>Read-only rows carry a summary the operator confirms but does not edit.</summary>
    public bool IsEditable { get; init; } = true;

    public bool IsReadOnlyValue => !IsEditable;

    public static ProposedFieldRow From(string label, ProposedField field) => new()
    {
        Label = label,
        Value = field.Value,
        Confidence = field.Confidence,
        Provenance = field.Source
    };
}
