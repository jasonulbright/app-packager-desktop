using System.ComponentModel;
using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

public enum CiSettingEditorMode { Add, Edit }

/// <summary>
/// Adds or edits one setting of a configuration item: a registry value or a script, with its
/// value rule. Save hands the setting to the page, which shows the change plan; the dialog closes
/// only after the page reports a confirmed write. The rules ConfigMgr applies to a stored setting
/// come from <see cref="SettingRules"/>.
/// </summary>
public sealed partial class CiSettingEditorViewModel : ObservableObject
{
    public const string RegistryKind = "Registry";
    public const string ScriptKind = "Script";

    public const string NameRequired = "Enter a name for the setting.";
    public const string KeyRequired = "Enter the registry key path.";
    public const string ExpectedRequired = "Enter the expected value.";
    public const string DiscoveryRequired = "Enter the discovery script.";
    public const string ScriptTooLong = "A script is longer than 256 KB.";
    public const string ControlCharacters = "The name, key, value name and rule name cannot contain line breaks.";
    public const string NoChange = "Nothing was changed in the setting.";
    public const string NotEditable = "This setting is not a registry or script setting with one value rule. This page shows it and does not change it.";
    public const string ReplaceNote =
        "ConfigMgr cannot change the data type, the rule or the expected value of a setting in place. The new setting is added first, then the old one is removed. The setting gets a new ID, and the configuration item gets a new revision for each step.";
    public const string ListValueNote = "Enter one value per line.";

    public static string NotInteger(string value) => $"{value} is not a whole number.";
    public static string NotBoolean(string value) => $"{value} is not True or False.";
    public static string NotVersion(string value) => $"{value} is not a version, for example 10.0.1.";
    public static string NotNumber(string value) => $"{value} is not a number.";
    public static string NotDate(string value) => $"{value} is not a date and time, for example 2026-10-19T08:00:00.";
    public static string NameTaken(string name) =>
        $"This configuration item already has a setting named {name}. ConfigMgr allows two settings with one name, but its cmdlets then change or remove both, so the plan refuses this name.";
    public static string DefaultRuleName(string name) => (name.Length > SettingRules.MaxNameLength - 5 ? name[..(SettingRules.MaxNameLength - 5)] : name) + " rule";

    public static IReadOnlyList<string> Hives => SettingRules.Hives;
    public static IReadOnlyList<string> Languages => SettingRules.Languages;
    public static IReadOnlyList<string> Severities => SettingRules.Severities;
    public static IReadOnlyList<string> Operators => SettingRules.Operators;
    public static IReadOnlyList<string> Kinds { get; } = [RegistryKind, ScriptKind];

    private static readonly HashSet<string> DerivedProperties =
        [nameof(ReplacesSetting), nameof(NameWarning), nameof(HasNameWarning), nameof(IsListValue), nameof(ValidationMessage), nameof(IsBusy), nameof(DataTypes)];

    private readonly Func<CiSettingEditorViewModel, Task<bool>> _save;
    private readonly IReadOnlyList<string> _existingNames;

    public CiSettingEditorViewModel(CiSettingEditorMode mode, string ciText, Func<CiSettingEditorViewModel, Task<bool>> save, CiSetting? original = null,
        IReadOnlyList<string>? existingNames = null)
    {
        if (mode == CiSettingEditorMode.Edit && original is null) throw new ArgumentNullException(nameof(original));
        Mode = mode;
        CiText = ciText;
        Original = original;
        _save = save;
        _existingNames = existingNames ?? [];
        if (original is not null)
        {
            _kind = string.Equals(original.Kind, ScriptKind, StringComparison.OrdinalIgnoreCase) ? ScriptKind : RegistryKind;
            _name = original.Name;
            _description = original.Description ?? "";
            _dataType = original.DataType;
            _expectedValue = original.ExpectedValue ?? "";
            _hive = Hives.Contains(original.Hive) ? original.Hive : Hives[0];
            _keyPath = original.KeyName ?? "";
            _valueName = original.ValueName ?? "";
            _is64Bit = original.Is64Bit;
            _language = Languages.Contains(original.DiscoveryLanguage) ? original.DiscoveryLanguage : Languages[0];
            _discoveryScript = original.DiscoveryScript ?? "";
            _remediationLanguage = Languages.Contains(original.RemediationLanguage) ? original.RemediationLanguage : _language;
            _remediationScript = original.RemediationScript ?? "";
            _ruleName = original.RuleName ?? "";
            _operator = Operators.Contains(original.Operator) ? original.Operator : Operators[0];
            _remediate = original.Remediate;
            _reportNoncompliance = original.ReportNoncompliance;
            _severity = Severities.Contains(original.Severity) ? original.Severity : "Warning";
            _remediateDword = original.RemediateDword;
        }
        PropertyChanged += OnFormChanged;
    }

    public CiSettingEditorMode Mode { get; }
    public string CiText { get; }
    public CiSetting? Original { get; }
    public bool IsEdit => Mode == CiSettingEditorMode.Edit;
    public bool CanChooseKind => !IsEdit;
    public bool CanChooseDataType => true;
    public bool IsReadOnly => Original is { IsEditable: false };
    public string Title => IsEdit ? $"Edit setting {Original!.Name}" : "Add setting";

    public bool Accepted { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegistry), nameof(IsScript))]
    private string _kind = RegistryKind;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _dataType = "String";
    [ObservableProperty] private string _expectedValue = "";
    [ObservableProperty] private string _hive = Hives[0];
    [ObservableProperty] private string _keyPath = "";
    [ObservableProperty] private string _valueName = "";
    [ObservableProperty] private bool _is64Bit = true;
    [ObservableProperty] private string _language = Languages[0];
    [ObservableProperty] private string _discoveryScript = "";
    [ObservableProperty] private string _remediationLanguage = Languages[0];
    [ObservableProperty] private string _remediationScript = "";
    [ObservableProperty] private string _ruleName = "";
    [ObservableProperty] private string _operator = "IsEquals";
    [ObservableProperty] private bool _remediate;
    [ObservableProperty] private bool _reportNoncompliance = true;
    [ObservableProperty] private string _severity = "Warning";
    [ObservableProperty] private bool _remediateDword;
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    public bool IsRegistry => Kind == RegistryKind;
    public bool IsScript => Kind == ScriptKind;

    /// <summary>The data types ConfigMgr offers for the kind: Boolean for scripts only, StringArray for registry values only.</summary>
    public IReadOnlyList<string> DataTypes => IsScript ? SettingRules.ScriptDataTypes : SettingRules.RegistryDataTypes;

    /// <summary>True when the expected value is a list: one value per line.</summary>
    public bool IsListValue => DataType == "StringArray" || Operator is "OneOf" or "NoneOf" or "AllOf";

    /// <summary>True when the edit cannot be made in place, so ConfigMgr gets a new setting and the old one is removed.</summary>
    public bool ReplacesSetting => IsEdit && !IsReadOnly && !SettingRules.CanChangeInPlace(Original!, Build());

    public string NameWarning
    {
        get
        {
            var name = Name.Trim();
            if (name.Length == 0 || (IsEdit && name == Original!.Name)) return "";
            return _existingNames.Contains(name, StringComparer.Ordinal) ? NameTaken(name) : "";
        }
    }

    public bool HasNameWarning => NameWarning.Length > 0;

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(DataTypes));
        if (!DataTypes.Contains(DataType)) DataType = "String";
    }

    partial void OnDataTypeChanged(string? oldValue, string newValue)
    {
        if (newValue == "StringArray") Operator = "AllOf";
        else if (oldValue == "StringArray" && Operator == "AllOf") Operator = "IsEquals";
    }

    private void OnFormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || DerivedProperties.Contains(e.PropertyName)) return;
        OnPropertyChanged(nameof(ReplacesSetting));
        OnPropertyChanged(nameof(NameWarning));
        OnPropertyChanged(nameof(HasNameWarning));
        OnPropertyChanged(nameof(IsListValue));
    }

    private IReadOnlyList<string> ExpectedValues() =>
        IsListValue
            ? ExpectedValue.Split('\n').Select(v => v.Trim('\r', ' ', '\t')).Where(v => v.Length > 0).ToList()
            : [ExpectedValue.Trim()];

    /// <summary>Why the values cannot be saved, or null.</summary>
    public string? Validate()
    {
        if (IsReadOnly) return NotEditable;
        if (Name.Trim().Length == 0) return NameRequired;
        if (new[] { Name, KeyPath, ValueName, RuleName }.Any(v => v.Any(char.IsControl))) return ControlCharacters;
        if (IsRegistry && KeyPath.Trim().Trim('\\').Length == 0) return KeyRequired;
        if (IsScript)
        {
            if (DiscoveryScript.Trim().Length == 0) return DiscoveryRequired;
            if (DiscoveryScript.Length > SettingRules.MaxScriptLength || RemediationScript.Length > SettingRules.MaxScriptLength) return ScriptTooLong;
        }
        var values = ExpectedValues();
        if (values.Count == 0 || values.All(v => v.Length == 0)) return ExpectedRequired;
        var invariant = CultureInfo.InvariantCulture;
        foreach (var expected in values)
        {
            string? typeProblem = DataType switch
            {
                "Integer" => long.TryParse(expected, NumberStyles.AllowLeadingSign, invariant, out _) ? null : NotInteger(expected),
                "Boolean" => expected is "True" or "False" ? null : NotBoolean(expected),
                "Version" => System.Version.TryParse(expected, out _) ? null : NotVersion(expected),
                "FloatingPoint" => double.TryParse(expected, NumberStyles.Float, invariant, out _) ? null : NotNumber(expected),
                "DateTime" => DateTime.TryParse(expected, invariant, DateTimeStyles.RoundtripKind, out _) ? null : NotDate(expected),
                _ => null
            };
            if (typeProblem is not null) return typeProblem;
        }
        var setting = Build();
        if (SettingRules.Check(setting) is { } rule) return rule;
        if (IsEdit && SettingRules.Same(setting, Original!)) return NoChange;
        return null;
    }

    /// <summary>
    /// The setting with the values of the form. An edit keeps the ID of the original and every
    /// value the form does not show, so an unchanged form describes the stored setting.
    /// </summary>
    public CiSetting Build()
    {
        var name = Name.Trim();
        var expected = string.Join("\n", ExpectedValues());
        var rule = RuleName.Trim().Length > 0 ? RuleName.Trim() : IsEdit && Original!.RuleName.Length > 0 ? Original.RuleName : DefaultRuleName(name);
        var start = Original ?? new CiSetting("", "", Kind, DataType, "", null);
        var common = start with
        {
            Name = name, Kind = Kind, DataType = DataType, ExpectedValue = expected, Description = Description, Is64Bit = Is64Bit, RuleName = rule,
            Operator = Operator, Remediate = Remediate, ReportNoncompliance = ReportNoncompliance, Severity = Severity, IsEditable = true
        };
        if (IsRegistry)
        {
            var key = KeyPath.Trim().Trim('\\');
            var value = ValueName.Trim();
            return common with
            {
                Detail = $@"{Hive}\{key}{(value.Length == 0 ? "" : @"\" + value)}", Hive = Hive, KeyName = key, ValueName = value, RemediateDword = RemediateDword,
                DiscoveryLanguage = "", DiscoveryScript = "", RemediationLanguage = "", RemediationScript = ""
            };
        }
        var hasRemediation = RemediationScript.Trim().Length > 0;
        var lines = DiscoveryScript.TrimEnd('\r', '\n').Split('\n').Length;
        return common with
        {
            Detail = $"{Language} script, {lines} {(lines == 1 ? "line" : "lines")}" + (hasRemediation ? ", with a remediation script" : ", no remediation script"),
            Hive = "", KeyName = "", ValueName = "", RemediateDword = false,
            DiscoveryLanguage = Language, DiscoveryScript = DiscoveryScript,
            RemediationLanguage = hasRemediation ? RemediationLanguage : Original?.RemediationLanguage ?? "",
            RemediationScript = hasRemediation ? RemediationScript : ""
        };
    }

    private bool CanSave() => !IsBusy && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (IsBusy) return;
        if (Validate() is { } problem)
        {
            ValidationMessage = problem;
            return;
        }
        ValidationMessage = "";
        IsBusy = true;
        try
        {
            if (!await _save(this)) return;
        }
        finally { IsBusy = false; }
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMessage(string message) => ValidationMessage = message;
}
