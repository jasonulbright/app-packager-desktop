using System.Globalization;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One parameter of a site script with the value the operator types and the result of its check.</summary>
public sealed partial class PivotsParameterRow : ObservableObject
{
    public PivotsParameterRow(ScriptParameter parameter, string? value)
    {
        Parameter = parameter;
        _value = value ?? "";
        Check();
    }

    public ScriptParameter Parameter { get; }
    public string Name => Parameter.Name;
    public string Type => Parameter.Type;
    public string Required => Parameter.Required ? "Yes" : "No";
    public string Default => Parameter.Default ?? "";
    public string Description => Parameter.Description;
    public bool HasDescription => Parameter.Description.Length > 0;

    [ObservableProperty] private string _value;
    [ObservableProperty] private string _error = "";

    /// <summary>The value as the site gets it; null when the field is empty and the default applies.</summary>
    public string? Typed { get; private set; }

    public bool IsValid => Error.Length == 0;
    public bool HasError => !IsValid;

    partial void OnValueChanged(string value) => Check();

    private void Check()
    {
        var (typed, error) = PivotsParameterCheck.Check(Parameter, Value);
        Typed = typed;
        Error = error;
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(HasError));
    }
}

/// <summary>The type checks of a parameter value against the definition the site holds.</summary>
public static class PivotsParameterCheck
{
    private static string Short(string type)
    {
        var name = type.Trim();
        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        return name.ToUpperInvariant();
    }

    /// <summary>The typed text of a value, or an error; an empty optional value gives null and no error.</summary>
    public static (string? Typed, string Error) Check(ScriptParameter parameter, string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return parameter.Required && string.IsNullOrEmpty(parameter.Default) ? (null, "A value is required.") : (null, "");
        var inv = CultureInfo.InvariantCulture;
        switch (Short(parameter.Type))
        {
            case "INT16": return long.TryParse(text, NumberStyles.Integer, inv, out var a) && a is >= short.MinValue and <= short.MaxValue ? (a.ToString(inv), "") : (null, "Type a whole number from -32768 to 32767.");
            case "INT32" or "INT": return long.TryParse(text, NumberStyles.Integer, inv, out var b) && b is >= int.MinValue and <= int.MaxValue ? (b.ToString(inv), "") : (null, "Type a whole number from -2147483648 to 2147483647.");
            case "INT64" or "LONG": return long.TryParse(text, NumberStyles.Integer, inv, out var c) ? (c.ToString(inv), "") : (null, "Type a whole number.");
            case "UINT32": return ulong.TryParse(text, NumberStyles.Integer, inv, out var d) && d <= uint.MaxValue ? (d.ToString(inv), "") : (null, "Type a whole number from 0 to 4294967295.");
            case "BYTE": return byte.TryParse(text, NumberStyles.Integer, inv, out var e) ? (e.ToString(inv), "") : (null, "Type a whole number from 0 to 255.");
            case "BOOLEAN" or "BOOL" or "SWITCHPARAMETER" or "SWITCH":
                return text.ToUpperInvariant() switch
                {
                    "TRUE" or "$TRUE" => ("true", ""),
                    "FALSE" or "$FALSE" => ("false", ""),
                    _ => (null, "Type true or false.")
                };
            case "DOUBLE" or "SINGLE" or "DECIMAL": return double.TryParse(text, NumberStyles.Float, inv, out var f) && double.IsFinite(f) ? (f.ToString("R", inv), "") : (null, "Type a number with a period as the decimal mark.");
            case "DATETIME": return DateTime.TryParse(text, inv, DateTimeStyles.None, out var g) ? (g.ToString("s", inv), "") : (null, "Type a date as yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss.");
            default: return (text, "");
        }
    }
}

/// <summary>The Parameters dialog: one field per parameter definition of the site script.</summary>
public sealed partial class PivotsParametersViewModel : ObservableObject
{
    public PivotsParametersViewModel(SiteScript script, IReadOnlyDictionary<string, string>? current)
    {
        Script = script;
        Rows = script.Parameters.Select(p => new PivotsParameterRow(p, current is not null && current.TryGetValue(p.Name, out var v) ? v : null)).ToList();
        foreach (var row in Rows) row.PropertyChanged += (_, _) => { OkCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(Summary)); };
    }

    public SiteScript Script { get; }
    public string Title => $"Parameters of {Script.Name}";
    public IReadOnlyList<PivotsParameterRow> Rows { get; }
    public bool HasParameters => Rows.Count > 0;

    public string Summary => Rows.Count == 0 ? "The script has no parameters."
        : Rows.All(r => r.IsValid) ? "Every value matches the type the site holds. An empty field uses the default of the script."
        : $"{Rows.Count(r => !r.IsValid)} of {Rows.Count} values need a change.";

    /// <summary>The typed values the operator accepted; null until OK.</summary>
    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    public event EventHandler? CloseRequested;

    private bool CanOk() => Rows.All(r => r.IsValid);

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        if (!CanOk()) return;
        Result = Rows.Where(r => r.Typed is not null).ToDictionary(r => r.Name, r => r.Typed!, StringComparer.OrdinalIgnoreCase);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A one-field dialog: the comment of a Deny.</summary>
public sealed partial class PivotsCommentViewModel(string title, string prompt) : ObservableObject
{
    public const int MaxLength = 1000;

    public string Title { get; } = title;
    public string Prompt { get; } = prompt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _text = "";

    public string? Result { get; private set; }

    public event EventHandler? CloseRequested;

    private bool CanOk() => Text.Trim().Length is > 0 and <= MaxLength;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        if (!CanOk()) return;
        Result = Text.Trim();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
