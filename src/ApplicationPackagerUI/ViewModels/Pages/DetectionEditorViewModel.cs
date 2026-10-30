using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed record DetectionKindOption(DetectionKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The key names of <see cref="DetectionProposal.Values"/>, as the analysis service writes and reads them.</summary>
public static class DetectionValueKeys
{
    public const string Hive = "Hive";
    public const string Key = "Key";
    public const string ValueName = "ValueName";
    public const string Operator = "Operator";
    public const string Expected = "Expected";
    public const string Is64Bit = "Is64Bit";
    public const string FilePath = "FilePath";
    public const string FileName = "FileName";
    public const string ProductCode = "ProductCode";
    public const string PatchCode = "PatchCode";
    public const string PackageName = "PackageName";
    public const string Connector = "Connector";
}

/// <summary>
/// The detection rule in the review: the kind and the fields of that kind, with the evidence of the proposal. A
/// compound, patch or package rule is shown as the analysis proposed it and is kept unchanged until the kind changes.
/// </summary>
public sealed partial class DetectionEditorViewModel : ObservableObject
{
    public const string UninstallKeyRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private bool _loading;
    private string? _proposedSource;
    private string _operator = "";
    private DetectionProposal? _proposed;

    public static IReadOnlyList<DetectionKindOption> EditableKinds { get; } =
    [
        new(Kind: DetectionKind.RegistryVersion, Label: "Registry value (version)"),
        new(Kind: DetectionKind.MsiProductCode, Label: "MSI product code"),
        new(Kind: DetectionKind.FileVersion, Label: "File version"),
        new(Kind: DetectionKind.FileExists, Label: "File exists"),
        new(Kind: DetectionKind.RegistryExists, Label: "Registry key exists"),
        new(Kind: DetectionKind.None, Label: "None")
    ];

    private static readonly Dictionary<DetectionKind, string> FixedKinds = new()
    {
        [DetectionKind.Compound] = "Compound rule (edit in the Workbench)",
        [DetectionKind.MsiPatchCode] = "MSI patch code",
        [DetectionKind.MsixPackage] = "MSIX package identity"
    };

    public IReadOnlyList<DetectionKindOption> KindOptions => _proposed is { } p && FixedKinds.TryGetValue(p.Kind, out var label)
        ? [.. EditableKinds, new DetectionKindOption(Kind: p.Kind, Label: label)]
        : EditableKinds;

    public IReadOnlyList<string> HiveOptions { get; } = ["HKLM", "HKCU"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRegistry), nameof(ShowValueName), nameof(ShowProductCode), nameof(ShowFile), nameof(ShowVersion),
        nameof(IsFixedRule), nameof(SelectedKind), nameof(Show64Bit))]
    private DetectionKind _kind = DetectionKind.None;

    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _hive = "HKLM";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _valueName = "DisplayVersion";
    [ObservableProperty] private string _expected = "";
    [ObservableProperty] private bool _is64Bit = true;
    [ObservableProperty] private string _productCode = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _fixedRuleText = "";

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
    public bool Show64Bit => !IsFixedRule && Kind != DetectionKind.None;

    /// <summary>A compound, patch or package rule: shown as proposed, edited in the Workbench.</summary>
    public bool IsFixedRule => FixedKinds.ContainsKey(Kind);

    public string MarkerText => EvidenceMarkers.Text(Evidence);
    public string MarkerToolTip => EvidenceMarkers.ToolTip(Evidence, Evidence == Evidence.Manual ? null : _proposedSource);

    /// <summary>True when the rule gives no detection: kind None, a registry rule with an empty key, or a file rule without a folder or a name.</summary>
    public bool IsDefault => ToConfig(Build()) is null;

    /// <summary>A version rule whose key or file is set but whose expected version is empty.</summary>
    public bool IsMissingVersion => Kind is DetectionKind.RegistryVersion or DetectionKind.FileVersion or DetectionKind.MsiProductCode
        && string.IsNullOrWhiteSpace(Expected) && ToConfig(WithExpected(Build(), "0")) is not null;

    private static DetectionProposal WithExpected(DetectionProposal proposal, string expected) =>
        proposal with { Values = new Dictionary<string, string>(proposal.Values) { [DetectionValueKeys.Expected] = expected } };

    public string WarningText => Kind == DetectionKind.Compound
        ? "The analysis proposes a compound rule. Open the definition in the Workbench to review the rule."
        : !IsDefault ? ""
        : IsMissingVersion ? "Detection is still the default: the version rule has no expected version. Enter the version or choose another rule."
        : "Detection is still the default: the rule has no registry key or file. Set a rule here or in the Workbench.";

    public bool HasWarning => WarningText.Length > 0;

    /// <summary>The definition rule of a proposal, or null when the proposal gives no rule. A null or empty registry key never gives a rule.</summary>
    public static DetectionConfig? ToConfig(DetectionProposal proposal)
    {
        if (proposal.Kind == DetectionKind.None || proposal.IsDefault || proposal is { Kind: DetectionKind.Compound, Clauses.Count: 0 }) return null;
        try { return AddFromInstallerService.ToDetectionConfig(proposal, msiProductCode: null, expectedToken: null, version: ""); }
        catch (InstallerAnalysisException) { return null; }
    }

    public void Load(DetectionProposal proposal)
    {
        _loading = true;
        try
        {
            string Read(string key, string fallback = "") => proposal.Values.TryGetValue(key, out var v) && v is not null ? v : fallback;
            _proposed = proposal;
            _proposedSource = proposal.Source;
            Kind = proposal.Kind;
            Description = proposal.Description;
            Hive = Read(DetectionValueKeys.Hive, "HKLM");
            ProductCode = Read(DetectionValueKeys.ProductCode);
            var key = Read(DetectionValueKeys.Key);
            Key = proposal.Kind == DetectionKind.MsiProductCode && ProductCode.Length > 0 ? "" : key;
            if (proposal.Kind == DetectionKind.MsiProductCode && ProductCode.Length == 0 && key.StartsWith(UninstallKeyRoot, StringComparison.OrdinalIgnoreCase))
                ProductCode = key[UninstallKeyRoot.Length..];
            ValueName = Read(DetectionValueKeys.ValueName, "DisplayVersion");
            Expected = Read(DetectionValueKeys.Expected);
            _operator = Read(DetectionValueKeys.Operator);
            Is64Bit = !string.Equals(Read(DetectionValueKeys.Is64Bit), "false", StringComparison.OrdinalIgnoreCase);
            FilePath = Read(DetectionValueKeys.FilePath);
            FileName = Read(DetectionValueKeys.FileName);
            FixedRuleText = FixedText(proposal);
            Evidence = proposal.Evidence;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(KindOptions));
        OnPropertyChanged(nameof(SelectedKind));
        RaiseState();
    }

    private static string FixedText(DetectionProposal proposal) => proposal.Kind switch
    {
        DetectionKind.MsiPatchCode => $"Patch code {Value(proposal, DetectionValueKeys.PatchCode)} on product {Value(proposal, DetectionValueKeys.ProductCode)}",
        DetectionKind.MsixPackage => $"Package {Value(proposal, DetectionValueKeys.PackageName)}",
        DetectionKind.Compound => string.Join(proposal.Values.TryGetValue(DetectionValueKeys.Connector, out var c) && c == "Or" ? " or " : " and ",
            proposal.Clauses.Select(x => x.Description)),
        _ => ""
    };

    private static string Value(DetectionProposal proposal, string key) => proposal.Values.TryGetValue(key, out var v) ? v : "";

    /// <summary>The reviewed rule; IsDefault is set when the rule gives no detection.</summary>
    public DetectionProposal ToProposal()
    {
        var built = Build();
        return built with { IsDefault = ToConfig(built) is null };
    }

    private DetectionProposal Build()
    {
        var source = Evidence == Evidence.Manual ? null : _proposedSource;
        if (IsFixedRule && _proposed is { } fixedRule && fixedRule.Kind == Kind)
            return fixedRule with { Evidence = Evidence, Source = source };

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
                Put(DetectionValueKeys.Expected, Expected);
            }
        }
        if (ShowProductCode && !string.IsNullOrWhiteSpace(ProductCode))
        {
            Put(DetectionValueKeys.ProductCode, ProductCode);
            Put(DetectionValueKeys.Hive, Hive);
            values[DetectionValueKeys.Key] = UninstallKeyRoot + ProductCode.Trim();
        }
        if (ShowFile)
        {
            Put(DetectionValueKeys.FilePath, FilePath);
            Put(DetectionValueKeys.FileName, FileName);
        }
        if (ShowVersion) Put(DetectionValueKeys.Expected, Expected);
        if (_operator.Length > 0 && Kind is DetectionKind.RegistryVersion or DetectionKind.FileVersion or DetectionKind.MsiProductCode)
            values[DetectionValueKeys.Operator] = _operator;
        if (Kind != DetectionKind.None) values[DetectionValueKeys.Is64Bit] = Is64Bit ? "true" : "false";
        return new DetectionProposal(Kind: Kind, Description: Description, Values: values, Evidence: Evidence, Source: source);
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(IsMissingVersion));
    }

    private static readonly HashSet<string> StateProperties =
    [
        nameof(Evidence), nameof(MarkerText), nameof(MarkerToolTip), nameof(IsDefault), nameof(WarningText), nameof(HasWarning),
        nameof(ShowRegistry), nameof(ShowValueName), nameof(ShowProductCode), nameof(ShowFile), nameof(ShowVersion), nameof(Show64Bit),
        nameof(IsFixedRule), nameof(KindOptions), nameof(SelectedKind), nameof(Description), nameof(FixedRuleText), nameof(IsMissingVersion)
    ];

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null || StateProperties.Contains(e.PropertyName)) return;
        Evidence = Evidence.Manual;
        RaiseState();
    }
}
