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

    public ProposalRow(string label, string? value, Evidence evidence, string? source, IReadOnlyList<string>? options = null, string? automationId = null)
    {
        Label = label;
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

    /// <summary>The choices of a value picked from a list; null for a free text value.</summary>
    public IReadOnlyList<string>? Options { get; }
    public bool IsChoice => Options is not null;
    public bool IsText => Options is null;

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

public sealed record DetectionKindOption(DetectionKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The detection rule in the review: the kind and the fields of that kind, with the evidence of the proposal.</summary>
public sealed partial class DetectionEditorViewModel : ObservableObject
{
    private bool _loading;
    private string? _proposedSource;

    public static IReadOnlyList<DetectionKindOption> EditableKinds { get; } =
    [
        new(Kind: DetectionKind.RegistryVersion, Label: "Registry value (version)"),
        new(Kind: DetectionKind.MsiProductCode, Label: "MSI product code"),
        new(Kind: DetectionKind.FileVersion, Label: "File version"),
        new(Kind: DetectionKind.FileExists, Label: "File exists"),
        new(Kind: DetectionKind.RegistryExists, Label: "Registry key exists"),
        new(Kind: DetectionKind.None, Label: "None")
    ];

    public IReadOnlyList<DetectionKindOption> KindOptions => IsCompound
        ? [.. EditableKinds, new DetectionKindOption(Kind: DetectionKind.Compound, Label: "Compound (edit in the Workbench)")]
        : EditableKinds;

    public IReadOnlyList<string> HiveOptions { get; } = ["HKLM", "HKCU"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRegistry), nameof(ShowValueName), nameof(ShowProductCode), nameof(ShowFile), nameof(ShowVersion),
        nameof(IsCompound), nameof(KindOptions), nameof(SelectedKind))]
    private DetectionKind _kind = DetectionKind.None;

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _hive = "HKLM";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _valueName = "DisplayVersion";
    [ObservableProperty] private string _expectedValue = "";
    [ObservableProperty] private bool _is64Bit = true;
    [ObservableProperty] private string _productCode = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _version = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkerText), nameof(MarkerToolTip))]
    private Evidence _evidence = Evidence.None;

    public DetectionKindOption? SelectedKind
    {
        get => KindOptions.FirstOrDefault(o => o.Kind == Kind);
        set { if (value is not null) Kind = value.Kind; }
    }

    public bool ShowRegistry => Kind is DetectionKind.RegistryVersion or DetectionKind.RegistryExists;
    public bool ShowValueName => Kind == DetectionKind.RegistryVersion;
    public bool ShowProductCode => Kind == DetectionKind.MsiProductCode;
    public bool ShowFile => Kind is DetectionKind.FileVersion or DetectionKind.FileExists;
    public bool ShowVersion => Kind is DetectionKind.FileVersion or DetectionKind.MsiProductCode;
    public bool IsCompound => Kind == DetectionKind.Compound;

    public string MarkerText => EvidenceMarkers.Text(Evidence);
    public string MarkerToolTip => EvidenceMarkers.ToolTip(Evidence, Evidence == Evidence.Manual ? null : _proposedSource);

    public bool IsDefault => DetectionProposalMapping.IsDefault(ToProposal());

    public string WarningText => IsCompound
        ? "The analysis proposes a compound rule. Open the definition in the Workbench to review the rule."
        : IsDefault
            ? "Detection is still the default: the rule has no registry key or file. Set a rule here or in the Workbench."
            : "";

    public void Load(DetectionProposal proposal)
    {
        _loading = true;
        try
        {
            string Read(string key, string fallback = "") => proposal.Values.TryGetValue(key, out var v) && v is not null ? v : fallback;
            Kind = proposal.Kind;
            Description = proposal.Description;
            Hive = Read(DetectionValueKeys.Hive, "HKLM");
            Key = Read(DetectionValueKeys.Key);
            ValueName = Read(DetectionValueKeys.ValueName, "DisplayVersion");
            ExpectedValue = Read(DetectionValueKeys.ExpectedValue);
            Is64Bit = !string.Equals(Read(DetectionValueKeys.Is64Bit), "false", StringComparison.OrdinalIgnoreCase);
            ProductCode = Read(DetectionValueKeys.ProductCode);
            FilePath = Read(DetectionValueKeys.Path);
            FileName = Read(DetectionValueKeys.FileName);
            Version = Read(DetectionValueKeys.Version);
            _proposedSource = proposal.Source;
            Evidence = proposal.Evidence;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(MarkerToolTip));
    }

    public DetectionProposal ToProposal()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values[key] = value.Trim();
        }
        if (ShowRegistry)
        {
            Put(DetectionValueKeys.Hive, Hive);
            Put(DetectionValueKeys.Key, Key);
            if (ShowValueName)
            {
                Put(DetectionValueKeys.ValueName, ValueName);
                Put(DetectionValueKeys.ExpectedValue, ExpectedValue);
            }
        }
        if (ShowProductCode) Put(DetectionValueKeys.ProductCode, ProductCode);
        if (ShowFile)
        {
            Put(DetectionValueKeys.Path, FilePath);
            Put(DetectionValueKeys.FileName, FileName);
        }
        if (ShowVersion) Put(DetectionValueKeys.Version, Version);
        if (Kind != DetectionKind.None) values[DetectionValueKeys.Is64Bit] = Is64Bit ? "true" : "false";
        return new DetectionProposal(Kind: Kind, Description: Description, Values: values, Evidence: Evidence,
            Source: Evidence == Evidence.Manual ? null : _proposedSource);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Evidence) or nameof(MarkerText) or nameof(MarkerToolTip) or nameof(IsDefault) or nameof(WarningText)
            or nameof(ShowRegistry) or nameof(ShowValueName) or nameof(ShowProductCode) or nameof(ShowFile) or nameof(ShowVersion)
            or nameof(IsCompound) or nameof(KindOptions) or nameof(SelectedKind) or nameof(Description))
            return;
        Evidence = Evidence.Manual;
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(WarningText));
    }
}
