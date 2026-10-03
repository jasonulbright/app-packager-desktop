using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerUI.Models;

/// <summary>An editable Workbench grid row; any property change is reported to the owning page.</summary>
public abstract class WorkbenchRow : ObservableObject
{
    public Action? Changed { get; set; }

    protected void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name)) Changed?.Invoke();
    }
}

public sealed class DetectionClauseRow : WorkbenchRow
{
    private string _group = "1", _type = "Registry", _subject = "", _operator = "Exists", _expected = "";
    public string Group { get => _group; set => Set(ref _group, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Subject { get => _subject; set => Set(ref _subject, value); }
    public string Operator { get => _operator; set => Set(ref _operator, value); }
    public string Expected { get => _expected; set => Set(ref _expected, value); }

    public WorkbenchDetectionClause ToClause() => new(Group, Type, Subject, Operator, Expected);

    public static DetectionClauseRow From(WorkbenchDetectionClause clause) => new()
    {
        _group = clause.Group, _type = clause.Type, _subject = clause.Subject, _operator = clause.Operator, _expected = clause.Expected
    };
}

public sealed class RequirementOperationRow : WorkbenchRow
{
    private string _op = "Add", _value = "", _appliesTo = "*";
    public string Op { get => _op; set => Set(ref _op, value); }
    public string RuleId { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Value { get => _value; set => Set(ref _value, value); }
    public string AppliesTo { get => _appliesTo; set => Set(ref _appliesTo, value); }

    public WorkbenchRequirementRow ToRow() => new(Op, RuleId, Summary, Value, AppliesTo);

    public static RequirementOperationRow From(WorkbenchRequirementRow row) => new()
    {
        _op = row.Op, RuleId = row.RuleId, Summary = row.Summary, _value = row.Value, _appliesTo = row.AppliesTo
    };
}

public sealed class VariantOverrideRow : WorkbenchRow
{
    private string _installCommand = "", _detection = "", _estimated = "", _maximum = "";
    public string Suffix { get; init; } = "";
    public string InstallCommand { get => _installCommand; set => Set(ref _installCommand, value); }
    public string Detection { get => _detection; set => Set(ref _detection, value); }
    public string Estimated { get => _estimated; set => Set(ref _estimated, value); }
    public string Maximum { get => _maximum; set => Set(ref _maximum, value); }

    public static VariantOverrideRow From(string suffix, string install, string detection, string estimated, string maximum) => new()
    {
        Suffix = suffix, _installCommand = install, _detection = detection, _estimated = estimated, _maximum = maximum
    };
}

public sealed class SourceFileRow : WorkbenchRow
{
    private string _destination = "";
    private bool _linked;
    public WorkbenchSourceFile Entry { get; private set; } = new("", "", "", "", 0, false);
    public string Provenance => Entry.Provenance;
    public string SizeText => Entry.SizeText;
    public string ShortHash => Entry.ShortHash;
    public string Destination { get => _destination; set => Set(ref _destination, value); }
    public bool Linked { get => _linked; set => Set(ref _linked, value); }

    public WorkbenchSourceFile ToEntry() => Entry with { Destination = Destination, Linked = Linked };

    public void Replace(WorkbenchSourceFile entry)
    {
        Entry = entry;
        _destination = entry.Destination;
        _linked = entry.Linked;
        OnPropertyChanged(string.Empty);
        Changed?.Invoke();
    }

    public static SourceFileRow From(WorkbenchSourceFile entry) => new()
    {
        Entry = entry, _destination = entry.Destination, _linked = entry.Linked
    };
}

/// <summary>One row of the Prior builds grid.</summary>
public sealed record PriorBuildRow(string BuildId, string Version, string Revision, string Result);

/// <summary>One entry of the profile picker; the last entry opens Save as.</summary>
public sealed record WorkbenchProfileChoice(string ProfileId, string Name, bool IsSaveAs = false)
{
    public override string ToString() => Name;
}
