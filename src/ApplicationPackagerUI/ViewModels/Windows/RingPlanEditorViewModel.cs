using System.Globalization;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One ring of the plan editor; the texts are parsed when the plan is saved.</summary>
public sealed partial class RingEditorRow : ObservableObject
{
    public RingEditorRow(RingDefinition? ring = null)
    {
        _name = ring?.Name ?? "";
        _collectionId = ring?.CollectionId ?? "";
        _collectionName = ring?.CollectionName ?? "";
        _isRequired = ring is not null && RingText.IsRequired(ring.Purpose);
        _availableOffsetText = (ring?.AvailableOffsetDays ?? 0).ToString(CultureInfo.InvariantCulture);
        _deadlineOffsetText = ring?.DeadlineOffsetDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        _notification = DeploymentText.Notifications.FirstOrDefault(n => string.Equals(n.Value, ring?.UserNotification, StringComparison.OrdinalIgnoreCase))
            ?? DeploymentText.Notifications[0];
        _overrideServiceWindow = ring?.OverrideServiceWindow ?? false;
        _rebootOutsideServiceWindow = ring?.RebootOutsideServiceWindow ?? false;
        _allowMetered = ring?.AllowMetered ?? false;
        _dpGroup = ring?.DpGroup ?? "";
        _thresholdText = ring?.SuccessThresholdPercent?.ToString(CultureInfo.InvariantCulture) ?? "";
        _fastNetwork = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.FastNetworkChoices, ring?.FastNetworkOption);
        _slowNetwork = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.SlowNetworkChoices, ring?.SlowNetworkOption);
        _rerun = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.RerunChoices, ring?.RerunBehavior);
    }

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Collection))]
    private string _collectionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Collection))]
    private string _collectionName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Purpose), nameof(IsAvailable))]
    private bool _isRequired;

    [ObservableProperty] private string _availableOffsetText;
    [ObservableProperty] private string _deadlineOffsetText;
    [ObservableProperty] private DeploymentChoice _notification;
    [ObservableProperty] private bool _overrideServiceWindow;
    [ObservableProperty] private bool _rebootOutsideServiceWindow;
    [ObservableProperty] private bool _allowMetered;
    [ObservableProperty] private string _dpGroup;
    [ObservableProperty] private string _thresholdText;
    [ObservableProperty] private DeploymentChoice _fastNetwork;
    [ObservableProperty] private DeploymentChoice _slowNetwork;
    [ObservableProperty] private DeploymentChoice _rerun;

    public string Purpose => IsRequired ? DeploymentText.Required : DeploymentText.Available;

    public bool IsAvailable
    {
        get => !IsRequired;
        set => IsRequired = !value;
    }

    public string Collection => CollectionId.Length == 0 ? "(no collection)" : $"{CollectionName} ({CollectionId})";

    private static int? Number(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The ring as the inputs hold it, without the plan rules: a text that is not a number reads as 0 or no value.</summary>
    public RingDefinition ToDefinition() => new(Name: Name.Trim(), CollectionId: CollectionId.Trim(), CollectionName: CollectionName.Trim(), Purpose: Purpose,
        AvailableOffsetDays: Number(AvailableOffsetText) ?? 0, DeadlineOffsetDays: IsRequired ? Number(DeadlineOffsetText) : null, UserNotification: Notification.Value,
        OverrideServiceWindow: OverrideServiceWindow, RebootOutsideServiceWindow: RebootOutsideServiceWindow, AllowMetered: AllowMetered, DpGroup: DpGroup.Trim(),
        SuccessThresholdPercent: Number(ThresholdText), FastNetworkOption: FastNetwork.Value.Length == 0 ? null : FastNetwork.Value,
        SlowNetworkOption: SlowNetwork.Value.Length == 0 ? null : SlowNetwork.Value, RerunBehavior: Rerun.Value.Length == 0 ? null : Rerun.Value);

    public override string ToString() => $"{Name} {Collection}";
}

/// <summary>
/// The New, Edit and Copy dialog of a ring plan: the plan's name, description, hold choice and time
/// basis, and its rings with collection, purpose, offsets and options. Save checks the plan rules and
/// hands the plan to the page; the dialog closes only after the page reports that it was saved.
/// </summary>
public sealed partial class RingPlanEditorViewModel : ObservableObject
{
    public const int MaxRings = RingPlanLimits.MaxRings;
    public const int MaxNameLength = 64;
    public static readonly string TooManyRings = $"A plan holds at most {MaxRings.ToString(CultureInfo.CurrentCulture)} rings.";
    public const int MaxOffsetDays = 3650;
    public const string NameRequired = "Enter a plan name.";
    public const string NameInvalid = "Use letters, digits, spaces, dots, hyphens and underscores in the plan name. It starts with a letter or a digit and has no '..'.";
    public const string NameReserved = "This name is reserved by Windows for a device. Choose another name.";
    public const string NoRings = "Add at least one ring.";

    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static readonly DeploymentChoice DefaultChoice = new("", "(default)");
    public static IReadOnlyList<DeploymentChoice> FastNetworkChoices { get; } = [DefaultChoice, .. DeploymentText.NetworkBehaviors];
    public static IReadOnlyList<DeploymentChoice> SlowNetworkChoices { get; } = [DefaultChoice, .. DeploymentText.SlowNetworkBehaviors];
    public static IReadOnlyList<DeploymentChoice> RerunChoices { get; } = [DefaultChoice, .. DeploymentText.RerunBehaviors];

    private readonly IReadOnlyList<RingPlan> _existing;
    private readonly Func<RingPlanEditorViewModel, RingPlan, Task<bool>> _save;
    private readonly Func<string?, CollectionRow?> _pick;
    private readonly Func<IReadOnlyList<CollectionRow>> _pickMany;
    private readonly IRingPlanBuilder _builder;

    public RingPlanEditorViewModel(RingPlan? original, bool copy, IReadOnlyList<RingPlan> existing, IReadOnlyList<CollectionRow> collections,
        Func<RingPlanEditorViewModel, RingPlan, Task<bool>> save, Func<string?, CollectionRow?> pickCollection,
        Func<IReadOnlyList<CollectionRow>>? pickCollections = null, IRingPlanBuilder? builder = null)
    {
        Original = original;
        IsCopy = copy && original is not null;
        _existing = existing;
        _save = save;
        _pick = pickCollection;
        _pickMany = pickCollections ?? (() => []);
        _builder = builder ?? LocalRingPlanBuilder.Instance;
        Rings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(RingPosition));
        Collections = collections;
        Title = original is null ? "New ring plan" : IsCopy ? $"Copy ring plan {original.Name}" : $"Edit ring plan {original.Name}";
        _name = original is null ? "" : IsCopy ? CopyName(original.Name, existing) : original.Name;
        _description = original?.Description ?? "";
        _holdLaterRings = original?.HoldLaterRings ?? false;
        _isUtc = original is not null && RingText.IsUtc(original.TimeBasedOn);
        foreach (var ring in original?.Rings ?? []) Rings.Add(new RingEditorRow(ring));
        if (Rings.Count == 0) Rings.Add(new RingEditorRow { Name = "Ring 1" });
        _selectedRing = Rings[0];
    }

    public RingPlan? Original { get; }

    /// <summary>True when Save makes a new plan from <see cref="Original"/> and keeps the original.</summary>
    public bool IsCopy { get; }

    public string Title { get; }
    public IReadOnlyList<CollectionRow> Collections { get; }
    public IReadOnlyList<DeploymentChoice> Notifications => DeploymentText.Notifications;
    public IReadOnlyList<DeploymentChoice> FastNetworks => FastNetworkChoices;
    public IReadOnlyList<DeploymentChoice> SlowNetworks => SlowNetworkChoices;
    public IReadOnlyList<DeploymentChoice> Reruns => RerunChoices;
    public ObservableCollection<RingEditorRow> Rings { get; } = [];

    public bool Saved { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _holdLaterRings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalTime))]
    private bool _isUtc;

    public bool IsLocalTime
    {
        get => !IsUtc;
        set => IsUtc = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRing), nameof(RingPosition))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRingCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(PickCollectionCommand),
        nameof(DuplicateRingCommand), nameof(ApplyToAllCommand))]
    private RingEditorRow? _selectedRing;

    /// <summary>Where the selected ring stands, for example "Ring 57 of 100".</summary>
    public string RingPosition => SelectedRing is { } ring && Rings.IndexOf(ring) is var i and >= 0
        ? $"Ring {(i + 1).ToString(CultureInfo.CurrentCulture)} of {Rings.Count.ToString(CultureInfo.CurrentCulture)}"
        : DeploymentText.Count(Rings.Count, "ring");

    [ObservableProperty] private string _firstOffsetText = "0";
    [ObservableProperty] private string _daysBetweenText = "7";
    [ObservableProperty] private string _deadlineDaysText = "2";

    /// <summary>Add rings from collections sorts the chosen collections by name; off keeps the order shown in the picker.</summary>
    [ObservableProperty] private bool _sortNewRingsByName;

    [ObservableProperty] private bool _applyPurpose = true;
    [ObservableProperty] private bool _applyNotification = true;
    [ObservableProperty] private bool _applyOptions = true;

    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    public bool HasSelectedRing => SelectedRing is not null;

    internal static DeploymentChoice Choice(IReadOnlyList<DeploymentChoice> choices, string? value) =>
        choices.FirstOrDefault(c => c.Value.Length > 0 && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)) ?? choices[0];

    private static string CopyName(string name, IReadOnlyList<RingPlan> existing)
    {
        var baseName = name.Length > MaxNameLength - 8 ? name[..(MaxNameLength - 8)].TrimEnd() : name;
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? $"{baseName} copy" : $"{baseName} copy {i.ToString(CultureInfo.InvariantCulture)}";
            if (!existing.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    [RelayCommand]
    private void AddRing()
    {
        if (Rings.Count >= MaxRings)
        {
            ValidationMessage = TooManyRings;
            return;
        }
        var previous = Rings.LastOrDefault();
        var offset = previous is not null && int.TryParse(previous.AvailableOffsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days + 7 : 0;
        var row = new RingEditorRow
        {
            Name = $"Ring {(Rings.Count + 1).ToString(CultureInfo.InvariantCulture)}",
            AvailableOffsetText = offset.ToString(CultureInfo.InvariantCulture),
            IsRequired = previous?.IsRequired ?? false,
            Notification = previous?.Notification ?? DeploymentText.Notifications[0]
        };
        if (row.IsRequired) row.DeadlineOffsetText = (offset + 7).ToString(CultureInfo.InvariantCulture);
        Rings.Add(row);
        SelectedRing = row;
    }

    private bool HasRing() => SelectedRing is not null;

    [RelayCommand(CanExecute = nameof(HasRing))]
    private void RemoveRing()
    {
        if (SelectedRing is not { } row) return;
        var index = Rings.IndexOf(row);
        Rings.Remove(row);
        SelectedRing = Rings.Count == 0 ? null : Rings[Math.Min(index, Rings.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(HasRing))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(HasRing))]
    private void MoveDown() => Move(1);

    private void Move(int by)
    {
        if (SelectedRing is not { } row) return;
        var index = Rings.IndexOf(row);
        var target = index + by;
        if (target < 0 || target >= Rings.Count) return;
        Rings.Move(index, target);
        SelectedRing = row;
    }

    [RelayCommand(CanExecute = nameof(HasRing))]
    private void PickCollection()
    {
        if (SelectedRing is not { } row) return;
        if (Collections.Count == 0)
        {
            ValidationMessage = DeploymentRingsViewModel.NoCollections;
            return;
        }
        if (_pick(row.CollectionId) is not { } picked) return;
        if (picked.IsBuiltIn)
        {
            ValidationMessage = DeploymentFormViewModel.BuiltInRefused;
            return;
        }
        row.CollectionId = picked.CollectionId;
        row.CollectionName = picked.Name;
        ValidationMessage = "";
    }

    /// <summary>Adds a copy of the selected ring after it; the copy has no collection, because each ring needs its own.</summary>
    [RelayCommand(CanExecute = nameof(HasRing))]
    private void DuplicateRing()
    {
        if (SelectedRing is not { } row) return;
        if (Rings.Count >= MaxRings)
        {
            ValidationMessage = TooManyRings;
            return;
        }
        var copy = new RingEditorRow(row.ToDefinition() with { Name = $"{row.Name.Trim()} copy", CollectionId = "", CollectionName = "" })
        {
            AvailableOffsetText = row.AvailableOffsetText, DeadlineOffsetText = row.DeadlineOffsetText, ThresholdText = row.ThresholdText
        };
        Rings.Insert(Rings.IndexOf(row) + 1, copy);
        SelectedRing = copy;
        ValidationMessage = "The copy has no collection. Pick its collection.";
    }

    private bool TryHelperNumbers(out int first, out int between, out int deadline)
    {
        between = deadline = 0;
        if (!TryDays(FirstOffsetText, out first) || !TryDays(DaysBetweenText, out between) || !TryDays(DeadlineDaysText, out deadline) || deadline < 1)
        {
            ValidationMessage = $"Enter the first offset and the days between rings as whole days from 0 to {MaxOffsetDays.ToString(CultureInfo.CurrentCulture)}, and the days to the deadline from 1.";
            return false;
        }
        return true;
    }

    /// <summary>Adds one ring for each collection the operator checks, named after the collection, with the offsets of the helper fields.</summary>
    [RelayCommand]
    private void AddRingsFromCollections()
    {
        if (Collections.Count == 0)
        {
            ValidationMessage = DeploymentRingsViewModel.NoCollections;
            return;
        }
        if (!TryHelperNumbers(out var first, out var between, out var deadline)) return;
        var picked = _pickMany();
        if (picked.Count == 0) return;
        var used = Rings.Select(r => r.CollectionId.Trim()).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = picked.Where(c => !c.IsBuiltIn && used.Add(c.CollectionId)).ToList();
        if (SortNewRingsByName) chosen = chosen.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var skipped = picked.Count - chosen.Count;
        var room = MaxRings - Rings.Count;
        if (chosen.Count > room)
        {
            ValidationMessage = $"{TooManyRings} {DeploymentText.Count(chosen.Count, "collection")} were chosen and the plan has room for {DeploymentText.Count(room, "more ring")}.";
            return;
        }
        // The new rings continue the offsets of the rings already in the plan.
        var start = Rings.Count == 0 ? first : first + Rings.Count * between;
        foreach (var ring in _builder.FromCollections(chosen, start, between, deadline)) Rings.Add(new RingEditorRow(ring));
        if (Rings.Count > 0) SelectedRing = Rings[^1];
        ValidationMessage = $"Added {DeploymentText.Count(chosen.Count, "ring")}." + (skipped > 0 ? $" {DeploymentText.Count(skipped, "collection")} already in the plan or built in {(skipped == 1 ? "was" : "were")} left out." : "");
    }

    /// <summary>Sets the available offset of every ring and the deadline of every Required ring from the helper fields; each stays editable after.</summary>
    [RelayCommand]
    private void FillOffsets()
    {
        if (!TryHelperNumbers(out var first, out var between, out var deadline)) return;
        var filled = _builder.FillOffsets(Rings.Select(r => r.ToDefinition()).ToList(), first, between, deadline);
        for (var i = 0; i < Rings.Count && i < filled.Count; i++)
        {
            Rings[i].AvailableOffsetText = filled[i].AvailableOffsetDays.ToString(CultureInfo.InvariantCulture);
            Rings[i].DeadlineOffsetText = filled[i].DeadlineOffsetDays?.ToString(CultureInfo.InvariantCulture) ?? Rings[i].DeadlineOffsetText;
        }
        ValidationMessage = $"Filled the offsets of {DeploymentText.Count(Rings.Count, "ring")}.";
    }

    /// <summary>Copies the chosen settings of the selected ring (purpose, notification, options) to every ring of the plan.</summary>
    [RelayCommand(CanExecute = nameof(HasRing))]
    private void ApplyToAll()
    {
        if (SelectedRing is not { } source) return;
        var settings = (ApplyPurpose ? RingSettings.Purpose : RingSettings.None) | (ApplyNotification ? RingSettings.Notification : RingSettings.None)
            | (ApplyOptions ? RingSettings.Options : RingSettings.None);
        if (settings == RingSettings.None)
        {
            ValidationMessage = "Check at least one setting to apply.";
            return;
        }
        var deadline = TryDays(DeadlineDaysText, out var days) && days >= 1 ? days : 2;
        var applied = _builder.ApplyToAll(Rings.Select(r => r.ToDefinition()).ToList(), source.ToDefinition(), settings, deadline);
        for (var i = 0; i < Rings.Count && i < applied.Count; i++)
        {
            var row = Rings[i];
            var ring = applied[i];
            if (settings.HasFlag(RingSettings.Purpose))
            {
                row.IsRequired = RingText.IsRequired(ring.Purpose);
                if (ring.DeadlineOffsetDays is { } d) row.DeadlineOffsetText = d.ToString(CultureInfo.InvariantCulture);
            }
            if (settings.HasFlag(RingSettings.Notification)) row.Notification = source.Notification;
            if (settings.HasFlag(RingSettings.Options))
            {
                row.OverrideServiceWindow = ring.OverrideServiceWindow;
                row.RebootOutsideServiceWindow = ring.RebootOutsideServiceWindow;
                row.AllowMetered = ring.AllowMetered;
                row.FastNetwork = source.FastNetwork;
                row.SlowNetwork = source.SlowNetwork;
                row.Rerun = source.Rerun;
            }
        }
        ValidationMessage = $"Applied the settings of ring {source.Name.Trim()} to {DeploymentText.Count(Rings.Count, "ring")}.";
    }

    /// <summary>Why the plan name cannot be a file name, or null.</summary>
    public static string? NameProblem(string name)
    {
        if (name.Length == 0) return NameRequired;
        if (name.Length > MaxNameLength) return $"Use at most {MaxNameLength.ToString(CultureInfo.CurrentCulture)} characters in the plan name.";
        if (!NamePattern.IsMatch(name) || name.Contains("..", StringComparison.Ordinal) || name.EndsWith('.') || name.EndsWith(' ')) return NameInvalid;
        var stem = name.Split('.')[0].TrimEnd();
        return DeviceNames.Contains(stem) ? NameReserved : null;
    }

    private static bool TryDays(string text, out int days) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out days) && days <= MaxOffsetDays;

    /// <summary>The plan of the inputs, or null with the first rule it breaks in <paramref name="problem"/>.</summary>
    public RingPlan? Build(out string? problem)
    {
        problem = null;
        var name = Name.Trim();
        if (NameProblem(name) is { } bad) problem = bad;
        else if (_existing.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                     && (Original is null || IsCopy || !string.Equals(p.Name, Original.Name, StringComparison.OrdinalIgnoreCase))))
            problem = _existing.Any(p => p.IsSeed && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ? "A shipped plan has this name. Choose another name."
                : "Another plan has this name. Choose another name.";
        else if (Rings.Count == 0) problem = NoRings;
        else if (Rings.Count > MaxRings) problem = TooManyRings;
        if (problem is not null) return null;

        var rings = new List<RingDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        (string Name, int Deadline)? lastRequired = null;
        foreach (var row in Rings)
        {
            var ring = row.Name.Trim();
            var label = ring.Length == 0 ? $"Ring {(Rings.IndexOf(row) + 1).ToString(CultureInfo.CurrentCulture)}" : $"Ring {ring}";
            int? deadline = null;
            int? threshold = null;
            if (ring.Length == 0) problem = $"{label} has no name. Enter a ring name.";
            else if (ring.Length > MaxNameLength) problem = $"{label}: use at most {MaxNameLength.ToString(CultureInfo.CurrentCulture)} characters in the ring name.";
            else if (!names.Add(ring)) problem = $"Two rings are named {ring}. Each ring needs its own name.";
            else if (row.CollectionId.Trim().Length == 0) problem = $"{label} has no collection, so the plan cannot run. Pick a device collection.";
            else if (row.CollectionId.Trim().StartsWith("SMS", StringComparison.OrdinalIgnoreCase))
                problem = $"{label} names the built-in collection {row.CollectionId.Trim()}. Built-in collections cannot be ring targets.";
            else if (collections.TryGetValue(row.CollectionId.Trim(), out var other))
                problem = $"{label} and ring {other} name the same collection {row.CollectionId.Trim()}. Each ring needs its own collection.";
            else if (!TryDays(row.AvailableOffsetText, out var available))
                problem = $"{label}: enter the available offset as whole days from 0 to {MaxOffsetDays.ToString(CultureInfo.CurrentCulture)}.";
            else if (row.IsRequired && !TryDays(row.DeadlineOffsetText, out _))
                problem = $"{label}: enter the deadline offset as whole days from 0 to {MaxOffsetDays.ToString(CultureInfo.CurrentCulture)}.";
            else if (row.IsRequired && (deadline = int.Parse(row.DeadlineOffsetText.Trim(), CultureInfo.InvariantCulture)) <= available)
                problem = $"{label}: the deadline must be later than the available time. Use a deadline offset larger than {available.ToString(CultureInfo.CurrentCulture)} days.";
            else if (row.IsRequired && lastRequired is { } last && deadline <= last.Deadline)
                problem = $"{label}: the deadline must be later than the deadline of ring {last.Name} ({last.Deadline.ToString(CultureInfo.CurrentCulture)} days).";
            else if (row.ThresholdText.Trim().Length > 0 && (!int.TryParse(row.ThresholdText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent > 100))
                problem = $"{label}: enter the success threshold as a whole percentage from 0 to 100, or leave it empty for no gate.";
            else if (!row.IsRequired && string.Equals(row.Notification.Value, DeploymentText.HideAll, StringComparison.OrdinalIgnoreCase))
                problem = $"{label}: an Available ring cannot hide all notifications: users could not find it in Software Center.";
            if (problem is not null) return null;

            if (row.ThresholdText.Trim().Length > 0) threshold = int.Parse(row.ThresholdText.Trim(), CultureInfo.InvariantCulture);
            collections[row.CollectionId.Trim()] = ring;
            if (row.IsRequired) lastRequired = (ring, deadline!.Value);
            rings.Add(new RingDefinition(Name: ring, CollectionId: row.CollectionId.Trim(), CollectionName: row.CollectionName.Trim(), Purpose: row.Purpose,
                AvailableOffsetDays: int.Parse(row.AvailableOffsetText.Trim(), CultureInfo.InvariantCulture), DeadlineOffsetDays: row.IsRequired ? deadline : null,
                UserNotification: row.Notification.Value, OverrideServiceWindow: row.IsRequired && row.OverrideServiceWindow,
                RebootOutsideServiceWindow: row.IsRequired && row.RebootOutsideServiceWindow, AllowMetered: row.IsRequired && row.AllowMetered,
                DpGroup: row.DpGroup.Trim(), SuccessThresholdPercent: threshold,
                FastNetworkOption: row.FastNetwork.Value.Length == 0 ? null : row.FastNetwork.Value,
                SlowNetworkOption: row.SlowNetwork.Value.Length == 0 ? null : row.SlowNetwork.Value,
                RerunBehavior: row.Rerun.Value.Length == 0 ? null : row.Rerun.Value));
        }
        return new RingPlan(Name: name, Description: Description.Trim(), HoldLaterRings: HoldLaterRings, TimeBasedOn: IsUtc ? RingText.Utc : RingText.LocalTime,
            Rings: rings, IsSeed: false);
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (IsBusy) return;
        if (Build(out var problem) is not { } plan)
        {
            ValidationMessage = problem ?? "";
            return;
        }
        ValidationMessage = "";
        IsBusy = true;
        try
        {
            if (!await _save(this, plan)) return;
        }
        finally { IsBusy = false; }
        Saved = true;
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
