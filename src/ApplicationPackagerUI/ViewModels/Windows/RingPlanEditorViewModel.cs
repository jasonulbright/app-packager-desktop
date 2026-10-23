using System.Globalization;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// One ring of the plan editor; the texts are parsed when the plan is saved. The row keeps the ring it
/// was made from: a field the operator did not change keeps the ring's own value, also a value the
/// editor has no choice for, so a save never resets a setting.
/// </summary>
public sealed partial class RingEditorRow : ObservableObject
{
    private static readonly RingDefinition Blank = new(Name: "", CollectionId: "", CollectionName: "", Purpose: DeploymentText.Available, AvailableOffsetDays: 0,
        DeadlineOffsetDays: null, UserNotification: DeploymentText.Notifications[0].Value, OverrideServiceWindow: false, RebootOutsideServiceWindow: false, AllowMetered: false,
        DpGroup: "", SuccessThresholdPercent: null, FastNetworkOption: null, SlowNetworkOption: null, RerunBehavior: null);

    private readonly RingDefinition _original;
    private readonly RowState _initial;

    public RingEditorRow(RingDefinition? ring = null)
    {
        _original = ring ?? Blank;
        var r = _original;
        _name = r.Name;
        _collectionId = r.CollectionId;
        _collectionName = r.CollectionName;
        _purpose = RingText.IsRequired(r.Purpose) ? DeploymentText.Required : DeploymentText.Available;
        _availableOffsetText = r.AvailableOffsetDays.ToString(CultureInfo.InvariantCulture);
        _deadlineOffsetText = r.DeadlineOffsetDays?.ToString(CultureInfo.InvariantCulture) ?? "";
        _notification = DeploymentText.Notifications.FirstOrDefault(n => string.Equals(n.Value, r.UserNotification, StringComparison.OrdinalIgnoreCase))
            ?? DeploymentText.Notifications[0];
        _overrideServiceWindow = r.OverrideServiceWindow;
        _rebootOutsideServiceWindow = r.RebootOutsideServiceWindow;
        _allowMetered = r.AllowMetered;
        _dpGroup = r.DpGroup;
        _thresholdText = r.SuccessThresholdPercent?.ToString(CultureInfo.InvariantCulture) ?? "";
        _fastNetwork = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.FastNetworkChoices, r.FastNetworkOption);
        _slowNetwork = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.SlowNetworkChoices, r.SlowNetworkOption);
        _rerun = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.RerunChoices, r.RerunBehavior);
        _allowBoundaryFallback = r.AllowBoundaryFallback;
        _allowMicrosoftUpdate = r.AllowMicrosoftUpdate;
        _requirePostRebootFullScan = r.RequirePostRebootFullScan;
        _showTaskSequenceProgress = r.ShowTaskSequenceProgress;
        _taskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities.FirstOrDefault(c => string.Equals(c.Value, r.TaskSequenceAvailability, StringComparison.OrdinalIgnoreCase))
            ?? DeploymentText.TaskSequenceAvailabilities[0];
        _taskSequenceContent = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.TaskSequenceContentChoices, r.TaskSequenceDeploymentOption);
        _initial = State();
    }

    private sealed record RowState(string Name, string CollectionId, string CollectionName, bool IsRequired, string AvailableOffsetText, string DeadlineOffsetText,
        DeploymentChoice Notification, string DpGroup, string ThresholdText, DeploymentChoice FastNetwork, DeploymentChoice SlowNetwork, DeploymentChoice Rerun,
        DeploymentChoice TaskSequenceAvailability, DeploymentChoice TaskSequenceContent);

    private RowState State() => new(Name, CollectionId, CollectionName, IsRequired, AvailableOffsetText, DeadlineOffsetText, Notification, DpGroup, ThresholdText,
        FastNetwork, SlowNetwork, Rerun, TaskSequenceAvailability, TaskSequenceContent);

    [ObservableProperty] private bool _allowBoundaryFallback;
    [ObservableProperty] private bool _allowMicrosoftUpdate;
    [ObservableProperty] private bool _requirePostRebootFullScan;
    [ObservableProperty] private bool _showTaskSequenceProgress;
    [ObservableProperty] private DeploymentChoice _taskSequenceAvailability;
    [ObservableProperty] private DeploymentChoice _taskSequenceContent;

    [ObservableProperty] private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Collection))]
    private string _collectionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Collection))]
    private string _collectionName;

    /// <summary>Available or Required; the only stored purpose of the row, so no binding writes a second member back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRequired))]
    private string _purpose;

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

    public bool IsRequired
    {
        get => RingText.IsRequired(Purpose);
        set => Purpose = value ? DeploymentText.Required : DeploymentText.Available;
    }

    public string Collection => CollectionId.Length == 0 ? "(no collection)" : $"{CollectionName} ({CollectionId})";

    private static int? Number(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Optional(DeploymentChoice choice) => choice.Value.Length == 0 ? null : choice.Value;

    /// <summary>
    /// The ring as the inputs hold it, without the plan rules: a text that is not a number reads as 0 or
    /// no value. A field the operator did not change keeps the value of the ring the row was made from.
    /// </summary>
    public RingDefinition ToDefinition()
    {
        var o = _original;
        var i = _initial;
        var purposeKept = IsRequired == i.IsRequired;
        return o with
        {
            Name = Name == i.Name ? o.Name : Name.Trim(),
            CollectionId = CollectionId == i.CollectionId ? o.CollectionId : CollectionId.Trim(),
            CollectionName = CollectionName == i.CollectionName ? o.CollectionName : CollectionName.Trim(),
            Purpose = purposeKept ? o.Purpose : Purpose,
            AvailableOffsetDays = AvailableOffsetText == i.AvailableOffsetText ? o.AvailableOffsetDays : Number(AvailableOffsetText) ?? 0,
            DeadlineOffsetDays = purposeKept && DeadlineOffsetText == i.DeadlineOffsetText ? o.DeadlineOffsetDays : IsRequired ? Number(DeadlineOffsetText) : null,
            UserNotification = ReferenceEquals(Notification, i.Notification) ? o.UserNotification : Notification.Value,
            OverrideServiceWindow = OverrideServiceWindow,
            RebootOutsideServiceWindow = RebootOutsideServiceWindow,
            AllowMetered = AllowMetered,
            DpGroup = DpGroup == i.DpGroup ? o.DpGroup : DpGroup.Trim(),
            SuccessThresholdPercent = ThresholdText == i.ThresholdText ? o.SuccessThresholdPercent : Number(ThresholdText),
            FastNetworkOption = ReferenceEquals(FastNetwork, i.FastNetwork) ? o.FastNetworkOption : Optional(FastNetwork),
            SlowNetworkOption = ReferenceEquals(SlowNetwork, i.SlowNetwork) ? o.SlowNetworkOption : Optional(SlowNetwork),
            RerunBehavior = ReferenceEquals(Rerun, i.Rerun) ? o.RerunBehavior : Optional(Rerun),
            AllowBoundaryFallback = AllowBoundaryFallback,
            AllowMicrosoftUpdate = AllowMicrosoftUpdate,
            RequirePostRebootFullScan = RequirePostRebootFullScan,
            ShowTaskSequenceProgress = ShowTaskSequenceProgress,
            TaskSequenceAvailability = ReferenceEquals(TaskSequenceAvailability, i.TaskSequenceAvailability) ? o.TaskSequenceAvailability : TaskSequenceAvailability.Value,
            TaskSequenceDeploymentOption = ReferenceEquals(TaskSequenceContent, i.TaskSequenceContent) ? o.TaskSequenceDeploymentOption : Optional(TaskSequenceContent)
        };
    }

    /// <summary>Sets the inputs of the settings <paramref name="settings"/> names from <paramref name="ring"/>.</summary>
    internal void Take(RingDefinition ring, RingSettings settings)
    {
        if (settings.HasFlag(RingSettings.Purpose))
        {
            IsRequired = RingText.IsRequired(ring.Purpose);
            if (ring.DeadlineOffsetDays is { } d) DeadlineOffsetText = d.ToString(CultureInfo.InvariantCulture);
        }
        if (settings.HasFlag(RingSettings.Notification))
            Notification = DeploymentText.Notifications.FirstOrDefault(n => string.Equals(n.Value, ring.UserNotification, StringComparison.OrdinalIgnoreCase)) ?? Notification;
        if (!settings.HasFlag(RingSettings.Options)) return;
        OverrideServiceWindow = ring.OverrideServiceWindow;
        RebootOutsideServiceWindow = ring.RebootOutsideServiceWindow;
        AllowMetered = ring.AllowMetered;
        FastNetwork = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.FastNetworkChoices, ring.FastNetworkOption);
        SlowNetwork = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.SlowNetworkChoices, ring.SlowNetworkOption);
        Rerun = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.RerunChoices, ring.RerunBehavior);
        AllowBoundaryFallback = ring.AllowBoundaryFallback;
        AllowMicrosoftUpdate = ring.AllowMicrosoftUpdate;
        RequirePostRebootFullScan = ring.RequirePostRebootFullScan;
        ShowTaskSequenceProgress = ring.ShowTaskSequenceProgress;
        TaskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities.FirstOrDefault(c => string.Equals(c.Value, ring.TaskSequenceAvailability, StringComparison.OrdinalIgnoreCase))
            ?? TaskSequenceAvailability;
        TaskSequenceContent = RingPlanEditorViewModel.Choice(RingPlanEditorViewModel.TaskSequenceContentChoices, ring.TaskSequenceDeploymentOption);
    }

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

    /// <summary>The content choices of a task sequence; the default follows the availability: all content first for clients only, else when needed.</summary>
    public static IReadOnlyList<DeploymentChoice> TaskSequenceContentChoices { get; } =
        [new("", "(default: all content first for clients only, else when needed)"), .. DeploymentText.TaskSequenceContents];

    private readonly IReadOnlyList<RingPlan> _existing;
    private readonly Func<RingPlanEditorViewModel, RingPlan, Task<bool>> _save;
    private readonly Func<string?, CollectionRow?> _pick;
    private readonly Func<IReadOnlyList<CollectionRow>> _pickMany;
    private readonly Func<bool> _confirmDiscard;
    private readonly (string Name, string Description, bool Hold, bool Utc, IReadOnlyList<RingDefinition> Rings) _start;

    /// <param name="confirmDiscard">Asks whether unsaved changes may be discarded; true closes the editor.</param>
    public RingPlanEditorViewModel(RingPlan? original, bool copy, IReadOnlyList<RingPlan> existing, IReadOnlyList<CollectionRow> collections,
        Func<RingPlanEditorViewModel, RingPlan, Task<bool>> save, Func<string?, CollectionRow?> pickCollection,
        Func<IReadOnlyList<CollectionRow>>? pickCollections = null, Func<bool>? confirmDiscard = null)
    {
        Original = original;
        IsCopy = copy && original is not null;
        _existing = existing;
        _save = save;
        _pick = pickCollection;
        _pickMany = pickCollections ?? (() => []);
        _confirmDiscard = confirmDiscard ?? (() => true);
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
        _start = Snapshot();
    }

    private (string Name, string Description, bool Hold, bool Utc, IReadOnlyList<RingDefinition> Rings) Snapshot() =>
        (Name, Description, HoldLaterRings, IsUtc, Rings.Select(r => r.ToDefinition()).ToList());

    /// <summary>True when the inputs differ from the plan the editor opened with.</summary>
    public bool HasChanges
    {
        get
        {
            var now = Snapshot();
            return now.Name != _start.Name || now.Description != _start.Description || now.Hold != _start.Hold || now.Utc != _start.Utc
                || !now.Rings.SequenceEqual(_start.Rings);
        }
    }

    /// <summary>True when the editor may close: after a save, without changes, or when the operator agreed to discard the changes.</summary>
    public bool ConfirmClose() => Saved || !HasChanges || _confirmDiscard();

    public RingPlan? Original { get; }

    /// <summary>True when Save makes a new plan from <see cref="Original"/> and keeps the original.</summary>
    public bool IsCopy { get; }

    public string Title { get; }
    public IReadOnlyList<CollectionRow> Collections { get; }
    public IReadOnlyList<DeploymentChoice> Notifications => DeploymentText.Notifications;
    public IReadOnlyList<DeploymentChoice> FastNetworks => FastNetworkChoices;
    public IReadOnlyList<DeploymentChoice> SlowNetworks => SlowNetworkChoices;
    public IReadOnlyList<DeploymentChoice> Reruns => RerunChoices;
    public IReadOnlyList<DeploymentChoice> TaskSequenceAvailabilities => DeploymentText.TaskSequenceAvailabilities;
    public IReadOnlyList<DeploymentChoice> TaskSequenceContents => TaskSequenceContentChoices;
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
        var skipped = picked.Count - chosen.Count;
        var room = MaxRings - Rings.Count;
        if (chosen.Count > room)
        {
            ValidationMessage = $"{TooManyRings} {DeploymentText.Count(chosen.Count, "collection")} were chosen and the plan has room for {DeploymentText.Count(room, "more ring")}.";
            return;
        }
        // The new rings continue the offsets of the rings already in the plan.
        var start = Rings.Count == 0 ? first : first + Rings.Count * between;
        var (made, problem) = RingPlanHelpers.FromCollections(chosen, SortNewRingsByName, start, between, deadline);
        if (problem is not null)
        {
            ValidationMessage = problem;
            return;
        }
        foreach (var ring in made) Rings.Add(new RingEditorRow(ring));
        if (Rings.Count > 0) SelectedRing = Rings[^1];
        ValidationMessage = $"Added {DeploymentText.Count(chosen.Count, "ring")}." + (skipped > 0 ? $" {DeploymentText.Count(skipped, "collection")} already in the plan or built in {(skipped == 1 ? "was" : "were")} left out." : "");
    }

    /// <summary>Sets the available offset of every ring and the deadline of every Required ring from the helper fields; each stays editable after.</summary>
    [RelayCommand]
    private void FillOffsets()
    {
        if (!TryHelperNumbers(out var first, out var between, out var deadline)) return;
        var (filled, problem) = RingPlanHelpers.FillOffsets(Rings.Select(r => r.ToDefinition()).ToList(), first, between, deadline);
        if (problem is not null)
        {
            ValidationMessage = problem;
            return;
        }
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
        var (applied, problem) = RingPlanHelpers.ApplyToAll(Rings.Select(r => r.ToDefinition()).ToList(), source.ToDefinition(), settings, deadline);
        if (problem is not null)
        {
            ValidationMessage = problem;
            return;
        }
        for (var i = 0; i < Rings.Count && i < applied.Count; i++) Rings[i].Take(applied[i], settings);
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
            else if (!row.IsRequired && string.Equals(row.ToDefinition().UserNotification, DeploymentText.HideAll, StringComparison.OrdinalIgnoreCase))
                problem = $"{label}: an Available ring cannot hide all notifications: users could not find it in Software Center.";
            if (problem is not null) return null;

            collections[row.CollectionId.Trim()] = ring;
            if (row.IsRequired) lastRequired = (ring, deadline!.Value);
            rings.Add(row.ToDefinition());
        }
        var description = Original is { } o && Description == _start.Description ? o.Description : Description.Trim();
        var basis = Original is { } p && IsUtc == RingText.IsUtc(p.TimeBasedOn) ? p.TimeBasedOn : IsUtc ? RingText.Utc : RingText.LocalTime;
        return new RingPlan(Name: name, Description: description, HoldLaterRings: HoldLaterRings, TimeBasedOn: basis, Rings: rings, IsSeed: false);
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

    /// <summary>Closes the editor; the window asks <see cref="ConfirmClose"/> first, so unsaved changes are discarded only when the operator agrees.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMessage(string message) => ValidationMessage = message;
}
