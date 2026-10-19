using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

public enum CiSettingEditorMode { Add, Edit }

/// <summary>
/// Adds or edits one setting of a configuration item: a registry value or a script. Save hands
/// the setting to the page, which shows the change plan; the dialog closes only after the page
/// reports a confirmed write.
/// </summary>
public sealed partial class CiSettingEditorViewModel : ObservableObject
{
    public const string RegistryKind = "Registry";
    public const string ScriptKind = "Script";
    public const int MaxNameLength = 256;
    public const int MaxScriptLength = 65536;

    public const string NameRequired = "Enter a name for the setting.";
    public const string KeyRequired = "Enter the registry key path.";
    public const string ValueNameRequired = "Enter the registry value name.";
    public const string ExpectedRequired = "Enter the expected value.";
    public const string DiscoveryRequired = "Enter the discovery script.";
    public const string ScriptTooLong = "A script is longer than 64 KB.";
    public const string ControlCharacters = "The name, key, value name and expected value cannot contain line breaks.";
    public const string NoChange = "Nothing was changed in the setting.";
    public const string DataTypeFixedNote = "ConfigMgr keeps the data type of an existing setting. To use another data type, remove the setting and add it again.";

    public static string NotInteger(string value) => $"{value} is not a whole number.";
    public static string NotBoolean(string value) => $"{value} is not True or False.";
    public static string NotVersion(string value) => $"{value} is not a version, for example 10.0.1.";
    public static string NotNumber(string value) => $"{value} is not a number.";
    public static string NotDate(string value) => $"{value} is not a date and time, for example 2026-10-19 08:00.";

    public static IReadOnlyList<string> Hives { get; } = ["HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_CLASSES_ROOT", "HKEY_USERS"];
    public static IReadOnlyList<string> DataTypes { get; } = ["String", "Integer", "Boolean", "DateTime", "Version", "FloatingPoint"];
    public static IReadOnlyList<string> Languages { get; } = ["PowerShell", "VBScript", "JScript"];
    public static IReadOnlyList<string> Kinds { get; } = [RegistryKind, ScriptKind];

    private readonly Func<CiSettingEditorViewModel, Task<bool>> _save;

    public CiSettingEditorViewModel(CiSettingEditorMode mode, string ciText, Func<CiSettingEditorViewModel, Task<bool>> save, CiSetting? original = null)
    {
        if (mode == CiSettingEditorMode.Edit && original is null) throw new ArgumentNullException(nameof(original));
        Mode = mode;
        CiText = ciText;
        Original = original;
        _save = save;
        if (original is null) return;
        _kind = string.Equals(original.Kind, ScriptKind, StringComparison.OrdinalIgnoreCase) ? ScriptKind : RegistryKind;
        _name = original.Name;
        _dataType = original.DataType;
        _expectedValue = original.ExpectedValue ?? "";
        _hive = original.Hive ?? Hives[0];
        _keyPath = original.KeyName ?? "";
        _valueName = original.ValueName ?? "";
        _language = (string.IsNullOrEmpty(original.DiscoveryLanguage) ? Languages[0] : original.DiscoveryLanguage);
        _discoveryScript = original.DiscoveryScript ?? "";
        _remediationScript = original.RemediationScript ?? "";
    }

    public CiSettingEditorMode Mode { get; }
    public string CiText { get; }
    public CiSetting? Original { get; }
    public bool IsEdit => Mode == CiSettingEditorMode.Edit;
    public bool CanChooseKind => !IsEdit;
    public bool CanChooseDataType => !IsEdit;
    public string Title => IsEdit ? $"Edit setting {Original!.Name}" : "Add setting";

    public bool Accepted { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegistry), nameof(IsScript))]
    private string _kind = RegistryKind;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _dataType = "String";
    [ObservableProperty] private string _expectedValue = "";
    [ObservableProperty] private string _hive = Hives[0];
    [ObservableProperty] private string _keyPath = "";
    [ObservableProperty] private string _valueName = "";
    [ObservableProperty] private string _language = Languages[0];
    [ObservableProperty] private string _discoveryScript = "";
    [ObservableProperty] private string _remediationScript = "";
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    public bool IsRegistry => Kind == RegistryKind;
    public bool IsScript => Kind == ScriptKind;

    /// <summary>Why the values cannot be saved, or null.</summary>
    public string? Validate()
    {
        if (Name.Trim().Length == 0) return NameRequired;
        if (Name.Length > MaxNameLength || ExpectedValue.Length > MaxNameLength || KeyPath.Length > MaxNameLength || ValueName.Length > MaxNameLength)
            return $"A value is longer than {MaxNameLength} characters.";
        if (new[] { Name, ExpectedValue, KeyPath, ValueName }.Any(v => v.Any(char.IsControl))) return ControlCharacters;
        if (IsRegistry)
        {
            if (KeyPath.Trim().Trim('\\').Length == 0) return KeyRequired;
            if (ValueName.Trim().Length == 0) return ValueNameRequired;
        }
        else
        {
            if (DiscoveryScript.Trim().Length == 0) return DiscoveryRequired;
            if (DiscoveryScript.Length > MaxScriptLength || RemediationScript.Length > MaxScriptLength) return ScriptTooLong;
        }
        if (ExpectedValue.Trim().Length == 0) return ExpectedRequired;
        var expected = ExpectedValue.Trim();
        var invariant = CultureInfo.InvariantCulture;
        string? typeProblem = DataType switch
        {
            "Integer" => long.TryParse(expected, NumberStyles.Integer, invariant, out _) ? null : NotInteger(expected),
            "Boolean" => bool.TryParse(expected, out _) ? null : NotBoolean(expected),
            "Version" => System.Version.TryParse(expected, out _) ? null : NotVersion(expected),
            "FloatingPoint" => double.TryParse(expected, NumberStyles.Float, invariant, out _) ? null : NotNumber(expected),
            "DateTime" => DateTime.TryParse(expected, invariant, DateTimeStyles.None, out _) ? null : NotDate(expected),
            _ => null
        };
        if (typeProblem is not null) return typeProblem;
        if (IsEdit && Equals(Build() with { SettingId = Original!.SettingId }, Original)) return NoChange;
        return null;
    }

    /// <summary>The setting with the values of the form; an edit keeps the ID and the data type of the original.</summary>
    public CiSetting Build()
    {
        var id = Original?.SettingId ?? "";
        var dataType = IsEdit ? Original!.DataType : DataType;
        if (IsRegistry)
        {
            var key = KeyPath.Trim().Trim('\\');
            return new CiSetting(id, Name.Trim(), RegistryKind, dataType, $@"{Hive}\{key}\{ValueName.Trim()}", ExpectedValue.Trim(), Hive: Hive, KeyName: key, ValueName: ValueName.Trim());
        }
        var lines = DiscoveryScript.Split('\n').Length;
        return new CiSetting(id, Name.Trim(), ScriptKind, dataType, $"{Language} script, {lines} {(lines == 1 ? "line" : "lines")}", ExpectedValue.Trim(),
            DiscoveryLanguage: Language, DiscoveryScript: DiscoveryScript, RemediationLanguage: Language, RemediationScript: RemediationScript.Trim().Length == 0 ? "" : RemediationScript);
    }

    private bool CanSave() => !IsBusy;

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
