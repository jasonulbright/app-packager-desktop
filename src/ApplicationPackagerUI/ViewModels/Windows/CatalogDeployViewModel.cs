using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>The checks a value of a catalog builder parameter passes before it reaches a plan.</summary>
public static class CatalogParameterRules
{
    public const int MaxLength = 256;
    public const string DetectOnlyName = "DetectOnly";
    public const string SkipDeployName = "SkipDeploy";
    public const string SiteCodeName = "SiteCode";
    public const string SiteServerName = "SiteServer";

    private static readonly Regex SiteCodePattern = new("^[A-Za-z0-9]{3}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,253})$", RegexOptions.CultureInvariant);

    public static bool IsSwitch(CatalogParameter parameter) => string.Equals(parameter.Type, "Switch", StringComparison.OrdinalIgnoreCase);

    /// <summary>A String parameter whose value names a device collection.</summary>
    public static bool IsCollection(CatalogParameter parameter) =>
        !IsSwitch(parameter) && parameter.Name.EndsWith("CollectionName", StringComparison.OrdinalIgnoreCase);

    public static string Required(string name) => $"Enter a value for {name}.";
    public static string TooLong(string name) => $"{name} is longer than {MaxLength} characters.";
    public static string ControlCharacters(string name) => $"{name} contains a line break or another control character.";
    public static string LeadingDash(string name) => $"{name} cannot start with a dash.";
    /// <summary>The builders pass a collection name to a cmdlet parameter that reads these characters as wildcards.</summary>
    private static readonly char[] WildcardCharacters = ['*', '?', '[', ']'];

    public static string Wildcard(string name) =>
        $"{name} cannot contain *, ?, [ or ]. The builder passes the name to a ConfigMgr cmdlet that reads these characters as wildcards, so it could deploy to other collections. Rename the collection or pick another one.";
    public static string BadSiteCode => "The site code has three letters or digits, for example LAB.";
    public static string BadSiteServer => "The site server is a computer name, for example cm01.lab.local.";
    public static string NoCollection(string value) => $"No device collection named {value} exists.";
    public static string BuiltInCollection(string value) => $"{value} is a built-in collection. Pick another collection.";
    public static string SameNameCollections(string value, int count) => $"{count} device collections are named {value}. A builder finds a collection by its name; rename one in ConfigMgr first.";

    /// <summary>Why the text value cannot be used, or null.</summary>
    public static string? Check(CatalogParameter parameter, string? value)
    {
        var text = value ?? "";
        if (text.Trim().Length == 0) return parameter.Required ? Required(parameter.Name) : null;
        if (text.Length > MaxLength) return TooLong(parameter.Name);
        if (text.Any(char.IsControl)) return ControlCharacters(parameter.Name);
        if (text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('–') || text.TrimStart().StartsWith('—')) return LeadingDash(parameter.Name);
        if (IsCollection(parameter) && text.IndexOfAny(WildcardCharacters) >= 0) return Wildcard(parameter.Name);
        if (string.Equals(parameter.Name, SiteCodeName, StringComparison.OrdinalIgnoreCase) && !SiteCodePattern.IsMatch(text.Trim())) return BadSiteCode;
        if (string.Equals(parameter.Name, SiteServerName, StringComparison.OrdinalIgnoreCase) && !HostPattern.IsMatch(text.Trim())) return BadSiteServer;
        return null;
    }
}

/// <summary>One input of the deploy form, made from a builder parameter.</summary>
public sealed partial class CatalogParameterField : ObservableObject
{
    public CatalogParameterField(CatalogParameter parameter, string value, bool isChecked, bool isReadOnly)
    {
        Parameter = parameter;
        _value = value;
        _isChecked = isChecked;
        IsReadOnly = isReadOnly;
    }

    public CatalogParameter Parameter { get; }
    public string Name => Parameter.Name;
    public string Label => Parameter.Required ? Parameter.Name + " *" : Parameter.Name;
    public string Description => Parameter.Description;
    public bool IsSwitch => CatalogParameterRules.IsSwitch(Parameter);
    public bool IsText => !IsSwitch;
    public bool IsCollection => CatalogParameterRules.IsCollection(Parameter);
    public bool IsReadOnly { get; }
    public bool IsEditable => !IsReadOnly;

    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isChecked;

    public override string ToString() => Label;
}

/// <summary>A dependency of the entry with what the site has now.</summary>
public sealed record CatalogDependencyRow(CatalogDependency Dependency, bool Exists, bool Satisfied)
{
    public string Name => Dependency.Name;
    public bool CanCreate => !Satisfied && !string.IsNullOrWhiteSpace(Dependency.CreatedBy);

    public string Text => Satisfied
        ? Exists ? $"{Dependency.Type} {Dependency.Name}: exists." : $"{Dependency.Type} {Dependency.Name}: not needed with the values set."
        : CanCreate ? $"{Dependency.Type} {Dependency.Name}: missing. Create it or pick another collection." : $"{Dependency.Type} {Dependency.Name}: missing.";

    public override string ToString() => Text;
}

/// <summary>
/// The deploy form of one catalog entry: an input for each builder parameter, the dependency
/// check and Deploy, which hands the values to the page for the plan. The form stays open until
/// a deploy is confirmed.
/// </summary>
public sealed partial class CatalogDeployViewModel : ObservableObject
{
    public const string DependencyMissing = "A required collection is missing. Create it or pick another collection, then deploy.";

    private readonly Func<CatalogDeployViewModel, Task<bool>> _deploy;
    private readonly Func<CatalogDeployViewModel, CatalogDependency, Task> _createDependency;
    private readonly Func<CatalogDeployViewModel, CatalogParameterField, Task> _pick;
    private IReadOnlyList<CollectionRow> _collections;

    public CatalogDeployViewModel(CatalogEntry entry, AppSettings settings, IReadOnlyList<CollectionRow> collections,
        Func<CatalogDeployViewModel, Task<bool>> deploy, Func<CatalogDeployViewModel, CatalogDependency, Task> createDependency,
        Func<CatalogDeployViewModel, CatalogParameterField, Task> pick)
    {
        Entry = entry;
        _collections = collections;
        _deploy = deploy;
        _createDependency = createDependency;
        _pick = pick;
        foreach (var parameter in entry.Parameters)
        {
            var field = parameter.Name switch
            {
                _ when string.Equals(parameter.Name, CatalogParameterRules.SiteCodeName, StringComparison.OrdinalIgnoreCase)
                    => new CatalogParameterField(parameter, settings.SiteCode.Trim(), false, settings.SiteCode.Trim().Length > 0),
                _ when string.Equals(parameter.Name, CatalogParameterRules.SiteServerName, StringComparison.OrdinalIgnoreCase)
                    => new CatalogParameterField(parameter, settings.ProviderMachine.Trim(), false, settings.ProviderMachine.Trim().Length > 0),
                _ when string.Equals(parameter.Name, CatalogParameterRules.DetectOnlyName, StringComparison.OrdinalIgnoreCase)
                    => new CatalogParameterField(parameter, "", true, false),
                _ when CatalogParameterRules.IsSwitch(parameter)
                    => new CatalogParameterField(parameter, "", string.Equals(parameter.Default, "true", StringComparison.OrdinalIgnoreCase), false),
                _ => new CatalogParameterField(parameter, parameter.Default ?? "", false, false)
            };
            field.PropertyChanged += (_, _) => FieldChanged();
            Fields.Add(field);
        }
        CheckDependencies();
    }

    public CatalogEntry Entry { get; }
    public string Title => $"Deploy {Entry.DisplayName}";
    public string Summary => $"{Entry.DisplayName} {Entry.Version}. Creates {ComplianceText.Count(Entry.ProducesConfigurationItems.Count, "configuration item")} and {ComplianceText.Count(Entry.ProducesBaselines.Count, "baseline")}.";

    public ObservableCollection<CatalogParameterField> Fields { get; } = [];
    public ObservableCollection<CatalogDependencyRow> Dependencies { get; } = [];
    public bool HasDependencies => Entry.Dependencies.Count > 0;

    public bool Accepted { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand), nameof(CreateDependencyCommand), nameof(PickCollectionCommand))]
    private bool _isBusy;

    /// <summary>True while DetectOnly is checked; an entry without the switch never remediates.</summary>
    public bool DetectOnly => Field(CatalogParameterRules.DetectOnlyName) is not { } f || f.IsChecked;

    public bool Remediate => !DetectOnly;

    public bool SkipDeploy => Field(CatalogParameterRules.SkipDeployName)?.IsChecked == true;

    public string ModeText => Remediate
        ? "Remediation is on: clients change the registry values of this entry on every device of the target collections."
        : "Detect only: clients report compliance and change nothing.";

    public CatalogParameterField? Field(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private void FieldChanged()
    {
        OnPropertyChanged(nameof(DetectOnly));
        OnPropertyChanged(nameof(Remediate));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(SkipDeploy));
        CheckDependencies();
    }

    private IReadOnlyList<CollectionRow> Named(string name) =>
        _collections.Where(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Gives the form the collections the site has now, for example after a dependency was created.</summary>
    public void UpdateCollections(IReadOnlyList<CollectionRow> collections)
    {
        _collections = collections;
        CheckDependencies();
    }

    private void CheckDependencies()
    {
        var rows = Entry.Dependencies.Select(d =>
        {
            var exists = !string.Equals(d.Type, "Collection", StringComparison.OrdinalIgnoreCase) || Named(d.Name).Count > 0;
            var replaced = Fields.Where(f => f.IsCollection && string.Equals(f.Parameter.Default, d.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var notUsed = replaced.Count > 0 && replaced.All(f => !string.Equals(f.Value.Trim(), d.Name, StringComparison.OrdinalIgnoreCase));
            return new CatalogDependencyRow(d, exists, exists || SkipDeploy || notUsed);
        }).ToList();
        Dependencies.Clear();
        foreach (var row in rows) Dependencies.Add(row);
        OnPropertyChanged(nameof(HasMissingDependency));
    }

    public bool HasMissingDependency => Dependencies.Any(d => !d.Satisfied);

    /// <summary>False while a plan or a dependency runs: the values sent stay the values shown.</summary>
    public bool CanEditFields => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditFields));

    /// <summary>The first reason the form cannot deploy, or null.</summary>
    public string? Validate()
    {
        foreach (var field in Fields.Where(f => f.IsText))
            if (CatalogParameterRules.Check(field.Parameter, field.Value) is { } problem) return problem;
        if (HasMissingDependency) return DependencyMissing;
        foreach (var field in Fields.Where(f => f.IsText))
        {
            if (!field.IsCollection || field.Value.Trim().Length == 0 || SkipDeploy) continue;
            var named = Named(field.Value);
            if (named.Count == 0) return CatalogParameterRules.NoCollection(field.Value.Trim());
            if (named.Count > 1) return CatalogParameterRules.SameNameCollections(field.Value.Trim(), named.Count);
            if (named[0].IsBuiltIn) return CatalogParameterRules.BuiltInCollection(field.Value.Trim());
        }
        return null;
    }

    /// <summary>The values the builder gets: each text value that is set, and every switch as True or False.</summary>
    public IReadOnlyDictionary<string, string> Values()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            if (field.IsSwitch) values[field.Name] = field.IsChecked ? "True" : "False";
            else if (field.Value.Trim().Length > 0) values[field.Name] = field.Value.Trim();
        }
        return values;
    }

    /// <summary>The collections the run deploys to; none when SkipDeploy is checked.</summary>
    public IReadOnlyList<CollectionRow> Targets() => SkipDeploy ? []
        : Fields.Where(f => f.IsCollection && f.Value.Trim().Length > 0).Select(f => Named(f.Value)).Where(n => n.Count == 1).Select(n => n[0])
            .DistinctBy(c => c.CollectionId, StringComparer.OrdinalIgnoreCase).ToList();

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Deploy()
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
            if (!await _deploy(this)) return;
        }
        finally { IsBusy = false; }
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CreateDependency(CatalogDependencyRow? row)
    {
        if (IsBusy || row is not { CanCreate: true }) return;
        IsBusy = true;
        try { await _createDependency(this, row.Dependency); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task PickCollection(CatalogParameterField? field)
    {
        if (IsBusy || field is not { IsCollection: true }) return;
        IsBusy = true;
        try { await _pick(this, field); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMessage(string message) => ValidationMessage = message;
}
