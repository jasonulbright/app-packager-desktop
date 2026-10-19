using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

public enum MaintenanceEditorMode
{
    NewWindow,
    EditWindow,
    NewTemplate,
    EditTemplate
}

/// <summary>A device collection the editor offers, with its folder path.</summary>
public sealed record CollectionChoice(CollectionInfo Collection)
{
    public string Text => $"{Collection.Name} ({Collection.CollectionId})";
    public string FolderPath => string.IsNullOrEmpty(Collection.FolderPath) ? "(root)" : Collection.FolderPath;

    public override string ToString() => Text;
}

/// <summary>
/// The New, Edit and template dialog: the collection, the window values and the schedule
/// editor with a preview of the next occurrences. Save validates and hands the values to the
/// page, which shows the change plan; the dialog closes only after the page reports a write.
/// </summary>
public sealed partial class MaintenanceWindowEditorViewModel : ObservableObject
{
    public const int PreviewCount = 5;
    public const string NameRequired = "Enter a name.";
    public const string CollectionRequired = "Select a collection.";
    public const string StartRequired = "Select a start date.";
    public const string DurationRange = "The duration must be between 1 minute and 24 hours.";
    public const string NotProjected = "The schedule has no occurrence after now.";
    public const string NoEndDateNote = "ConfigMgr stores no end date. The window recurs until it is removed or disabled.";
    public const string ScheduleNotShown = "The schedule cannot be shown.";
    public const string DescriptionKeptNote = "The description of this window is kept when you save.";

    public static string NameTaken(string collection, string name) => $"The collection {collection} already has a window named {name}.";

    public static string MissingDayNote(int day) => $"A month without day {day} has no occurrence.";

    private readonly IScheduleProjector _projector;
    private readonly Func<DateTime> _now;
    private readonly IReadOnlyList<CollectionChoice> _allCollections;
    private readonly IReadOnlyList<MaintenanceWindowInfo> _existingWindows;
    private readonly Func<MaintenanceWindowEditorViewModel, Task<bool>> _save;
    private bool _loading;
    private string? _scheduleProblem;

    public MaintenanceWindowEditorViewModel(
        MaintenanceEditorMode mode,
        IScheduleProjector projector,
        Func<MaintenanceWindowEditorViewModel, Task<bool>> save,
        Func<DateTime> now,
        IEnumerable<CollectionInfo>? collections = null,
        IEnumerable<MaintenanceWindowInfo>? existingWindows = null)
    {
        Mode = mode;
        _projector = projector;
        _save = save;
        _now = now;
        _allCollections = (collections ?? []).Where(c => !c.IsBuiltIn)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new CollectionChoice(c)).ToList();
        _existingWindows = (existingWindows ?? []).ToList();
        foreach (var c in _allCollections) Collections.Add(c);
        var start = now().Date.AddDays(1).AddHours(2);
        _startDate = start.Date;
        _startHour = start.Hour;
        UpdatePreview();
    }

    public MaintenanceEditorMode Mode { get; }
    public bool IsTemplate => Mode is MaintenanceEditorMode.NewTemplate or MaintenanceEditorMode.EditTemplate;
    public bool IsWindow => !IsTemplate;
    public bool CanPickCollection => Mode == MaintenanceEditorMode.NewWindow;

    public string Title => Mode switch
    {
        MaintenanceEditorMode.NewWindow => "New maintenance window",
        MaintenanceEditorMode.EditWindow => "Edit maintenance window",
        MaintenanceEditorMode.NewTemplate => "New template",
        _ => "Edit template"
    };

    public string SaveText => IsTemplate ? "Save template" : "Save";

    /// <summary>The window being edited, or null for a new window or a template.</summary>
    public MaintenanceWindowInfo? Original { get; private set; }

    /// <summary>The own description of the window being edited, read only; empty when the window has none.</summary>
    public string WindowDescription { get; private set; } = "";

    public bool ShowsWindowDescription => WindowDescription.Length > 0;

    /// <summary>The template being edited, or null.</summary>
    public MaintenanceTemplate? OriginalTemplate { get; private set; }

    /// <summary>True once the page reported the save; the dialog then closes.</summary>
    public bool Saved { get; private set; }

    public event EventHandler? CloseRequested;

    public ObservableCollection<CollectionChoice> Collections { get; } = [];
    public ObservableCollection<string> Preview { get; } = [];

    public IReadOnlyList<Choice<string>> WindowTypes => MaintenanceWindowsText.WindowTypes;
    public IReadOnlyList<Choice<MaintenanceScheduleKind>> Kinds => MaintenanceWindowsText.Kinds;
    public IReadOnlyList<Choice<DayOfWeek>> Days { get; } =
        Enum.GetValues<DayOfWeek>().Select(d => new Choice<DayOfWeek>(d, d.ToString())).ToList();
    public IReadOnlyList<Choice<int>> WeekOrders { get; } =
        [new(1, "First"), new(2, "Second"), new(3, "Third"), new(4, "Fourth"), new(5, "Last")];
    public IReadOnlyList<Choice<int>> DaysOfMonth { get; } =
        [new(0, "Last day"), .. Enumerable.Range(1, 31).Select(d => new Choice<int>(d, d.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> DurationHourChoices { get; } = Enumerable.Range(0, (int)ScheduleProjector.MaxDuration.TotalHours + 1).ToList();
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToList();
    public IReadOnlyList<int> PatchTuesdayOffsets { get; } = Enumerable.Range(-7, 29).ToList();
    public IReadOnlyList<int> WeekdayOffsets { get; } = Enumerable.Range(0, 8).ToList();

    public DateTime FirstStartDate { get; } = new(ScheduleProjector.FirstYear, 1, 1);
    public DateTime LastStartDate { get; } = new(ScheduleProjector.LastYear, 12, 31);

    /// <summary>The intervals ConfigMgr accepts for the kind: days 1 to 31, weeks 1 to 4, months 1 to 12.</summary>
    public IReadOnlyList<int> Intervals => Kind switch
    {
        MaintenanceScheduleKind.Daily => Enumerable.Range(1, 31).ToList(),
        MaintenanceScheduleKind.Weekly => Enumerable.Range(1, 4).ToList(),
        MaintenanceScheduleKind.OneTime => [1],
        _ => Enumerable.Range(1, 12).ToList()
    };

    public string IntervalLabel => Kind switch
    {
        MaintenanceScheduleKind.Daily => "Every (days)",
        MaintenanceScheduleKind.Weekly => "Every (weeks)",
        _ => "Every (months)"
    };

    [ObservableProperty] private string _collectionFilter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFolderPath))]
    private CollectionChoice? _selectedCollection;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _windowType = MaintenanceWindowsText.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecurring), nameof(ShowInterval), nameof(ShowDayOfWeek), nameof(ShowDayOfMonth), nameof(ShowWeekOrder),
        nameof(ShowPatchOffset), nameof(ShowWeekdayOffset), nameof(Intervals), nameof(IntervalLabel), nameof(DayOfMonthNote), nameof(HasDayOfMonthNote))]
    private MaintenanceScheduleKind _kind = MaintenanceScheduleKind.Weekly;

    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private int _startHour;
    [ObservableProperty] private int _startMinute;
    [ObservableProperty] private int _durationHours = 4;
    [ObservableProperty] private int _durationMinutes;
    [ObservableProperty] private bool _isUtc;
    [ObservableProperty] private int _interval = 1;
    [ObservableProperty] private DayOfWeek _dayOfWeek = DayOfWeek.Sunday;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayOfMonthNote), nameof(HasDayOfMonthNote))]
    private int _dayOfMonth = 1;

    [ObservableProperty] private int _weekOrder = 1;
    [ObservableProperty] private int _patchTuesdayOffsetDays;
    [ObservableProperty] private int _weekdayOffsetDays;

    [ObservableProperty] private string _scheduleText = "";
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    public string SelectedFolderPath => SelectedCollection?.FolderPath ?? "";
    public bool IsRecurring => Kind != MaintenanceScheduleKind.OneTime;
    public bool ShowInterval => IsRecurring;
    public bool ShowDayOfWeek => Kind is MaintenanceScheduleKind.Weekly or MaintenanceScheduleKind.MonthlyByWeekday;
    public bool ShowDayOfMonth => Kind == MaintenanceScheduleKind.MonthlyByDate;
    public bool ShowWeekOrder => Kind == MaintenanceScheduleKind.MonthlyByWeekday;
    public bool ShowPatchOffset => Kind == MaintenanceScheduleKind.PatchTuesday;
    public bool ShowWeekdayOffset => Kind == MaintenanceScheduleKind.MonthlyByWeekday;

    /// <summary>The reason the schedule inputs cannot be saved, from the editor's checks and the projector's Validate; null when none.</summary>
    public string? ScheduleProblem => _scheduleProblem;

    public string DayOfMonthNote => Kind == MaintenanceScheduleKind.MonthlyByDate && DayOfMonth >= 29 ? MissingDayNote(DayOfMonth) : "";
    public bool HasDayOfMonthNote => DayOfMonthNote.Length > 0;

    /// <summary>The dialog size the page stores between visits.</summary>
    public double Width { get; set; } = 760;
    public double Height { get; set; } = 720;

    /// <summary>Shrinks a stored size that is larger than the work area, for example after a change to a smaller screen.</summary>
    public void FitTo(double workAreaWidth, double workAreaHeight)
    {
        if (double.IsFinite(workAreaWidth) && workAreaWidth > 0 && !(Width <= workAreaWidth)) Width = workAreaWidth;
        if (double.IsFinite(workAreaHeight) && workAreaHeight > 0 && !(Height <= workAreaHeight)) Height = workAreaHeight;
    }

    partial void OnCollectionFilterChanged(string value)
    {
        if (!CanPickCollection) return;
        var filter = value.Trim();
        var selected = SelectedCollection;
        Collections.Clear();
        foreach (var c in _allCollections)
        {
            if (filter.Length == 0
                || c.Collection.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.Collection.CollectionId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.Collection.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Collections.Add(c);
        }
        if (selected is not null && Collections.Contains(selected)) SelectedCollection = selected;
    }

    partial void OnKindChanged(MaintenanceScheduleKind value)
    {
        if (!Intervals.Contains(Interval)) Interval = 1;
        UpdatePreview();
    }

    partial void OnStartDateChanged(DateTime? value) => UpdatePreview();
    partial void OnStartHourChanged(int value) => UpdatePreview();
    partial void OnStartMinuteChanged(int value) => UpdatePreview();
    partial void OnDurationHoursChanged(int value) => UpdatePreview();
    partial void OnDurationMinutesChanged(int value) => UpdatePreview();
    partial void OnIsUtcChanged(bool value) => UpdatePreview();
    partial void OnIntervalChanged(int value) => UpdatePreview();
    partial void OnDayOfWeekChanged(DayOfWeek value) => UpdatePreview();
    partial void OnDayOfMonthChanged(int value) => UpdatePreview();
    partial void OnWeekOrderChanged(int value) => UpdatePreview();
    partial void OnPatchTuesdayOffsetDaysChanged(int value) => UpdatePreview();
    partial void OnWeekdayOffsetDaysChanged(int value) => UpdatePreview();

    /// <summary>Fills the dialog from a window; in Edit the list holds only the window's own collection, which cannot change.</summary>
    public void Load(MaintenanceWindowInfo window)
    {
        Original = window;
        var choice = _allCollections.FirstOrDefault(c => string.Equals(c.Collection.CollectionId, window.CollectionId, StringComparison.OrdinalIgnoreCase))
            ?? new CollectionChoice(new CollectionInfo(window.CollectionId, window.CollectionName, "", 0, false));
        if (!CanPickCollection) Collections.Clear();
        if (!Collections.Contains(choice)) Collections.Add(choice);
        SelectedCollection = choice;
        Name = window.WindowName;
        WindowType = window.WindowType;
        try { WindowDescription = MaintenanceWindowService.OwnDescription(window, _projector) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { WindowDescription = ""; }
        OnPropertyChanged(nameof(WindowDescription));
        OnPropertyChanged(nameof(ShowsWindowDescription));
        LoadSchedule(window.Schedule);
    }

    public void Load(MaintenanceTemplate template)
    {
        OriginalTemplate = template;
        Name = template.Name;
        Description = template.Description;
        WindowType = template.WindowType;
        LoadSchedule(template.Schedule);
    }

    /// <summary>Fills the schedule inputs. An end date is dropped: ConfigMgr stores none.</summary>
    public void LoadSchedule(MaintenanceSchedule schedule)
    {
        _loading = true;
        try
        {
            Kind = schedule.Kind;
            StartDate = schedule.Start.Date;
            StartHour = schedule.Start.Hour;
            StartMinute = schedule.Start.Minute;
            var duration = schedule.Duration > ScheduleProjector.MaxDuration ? ScheduleProjector.MaxDuration : schedule.Duration;
            DurationHours = Math.Max(0, (int)duration.TotalHours);
            DurationMinutes = Math.Max(0, duration.Minutes);
            IsUtc = schedule.IsUtc;
            Interval = Intervals.Contains(schedule.Interval) ? schedule.Interval : 1;
            DayOfWeek = schedule.DayOfWeek ?? DayOfWeek.Sunday;
            DayOfMonth = schedule.DayOfMonth is >= 0 and <= 31 ? schedule.DayOfMonth.Value : 1;
            WeekOrder = schedule.WeekOrder is >= 1 and <= 5 ? schedule.WeekOrder.Value : 1;
            PatchTuesdayOffsetDays = schedule.Kind == MaintenanceScheduleKind.PatchTuesday && PatchTuesdayOffsets.Contains(schedule.PatchTuesdayOffsetDays)
                ? schedule.PatchTuesdayOffsetDays : 0;
            WeekdayOffsetDays = schedule.Kind == MaintenanceScheduleKind.MonthlyByWeekday && WeekdayOffsets.Contains(schedule.PatchTuesdayOffsetDays)
                ? schedule.PatchTuesdayOffsetDays : 0;
        }
        finally { _loading = false; }
        UpdatePreview();
    }

    /// <summary>The schedule the inputs describe, or null with the reason.</summary>
    public (MaintenanceSchedule? Schedule, string? Error) BuildSchedule()
    {
        if (StartDate is not { } date) return (null, StartRequired);
        var duration = TimeSpan.FromHours(DurationHours) + TimeSpan.FromMinutes(DurationMinutes);
        if (duration < TimeSpan.FromMinutes(1) || duration > ScheduleProjector.MaxDuration) return (null, DurationRange);
        var start = date.Date.AddHours(StartHour).AddMinutes(StartMinute);
        var kind = Kind;
        return (new MaintenanceSchedule(
            kind,
            start,
            duration,
            IsUtc,
            kind == MaintenanceScheduleKind.OneTime ? 1 : Interval,
            ShowDayOfWeek ? DayOfWeek : null,
            kind == MaintenanceScheduleKind.MonthlyByDate ? DayOfMonth : null,
            kind == MaintenanceScheduleKind.MonthlyByWeekday ? WeekOrder : null,
            kind switch
            {
                MaintenanceScheduleKind.PatchTuesday => PatchTuesdayOffsetDays,
                MaintenanceScheduleKind.MonthlyByWeekday => WeekdayOffsetDays,
                _ => 0
            },
            null), null);
    }

    /// <summary>The reason the inputs cannot be saved, or null.</summary>
    public string? Validate()
    {
        var name = Name.Trim();
        if (name.Length == 0) return NameRequired;
        if (IsWindow && SelectedCollection is null) return CollectionRequired;
        var error = CheckSchedule();
        if (error is not null) return error;
        if (IsWindow && SelectedCollection is { } collection)
        {
            var renamed = Original is null || !string.Equals(Original.WindowName, name, StringComparison.OrdinalIgnoreCase);
            if (renamed && _existingWindows.Any(w =>
                    string.Equals(w.CollectionId, collection.Collection.CollectionId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(w.WindowName, name, StringComparison.OrdinalIgnoreCase)))
                return NameTaken(collection.Collection.Name, name);
        }
        return null;
    }

    /// <summary>The editor's own reason, then the projector's reason, that the schedule cannot be stored; null when none.</summary>
    private string? CheckSchedule()
    {
        var (schedule, error) = BuildSchedule();
        if (error is not null) return error;
        try { return _projector.Validate(schedule!); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "The schedule cannot be stored."; }
    }

    /// <summary>Shows a schedule problem as the validation message and keeps Save disabled while one exists.</summary>
    private void UpdateScheduleProblem()
    {
        var previous = _scheduleProblem;
        _scheduleProblem = CheckSchedule();
        if (_scheduleProblem is not null) ValidationMessage = _scheduleProblem;
        else if (previous is not null && ValidationMessage == previous) ValidationMessage = "";
        if (previous != _scheduleProblem)
        {
            OnPropertyChanged(nameof(ScheduleProblem));
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdatePreview()
    {
        if (_loading) return;
        UpdateScheduleProblem();
        Preview.Clear();
        var (schedule, error) = BuildSchedule();
        if (schedule is null)
        {
            ScheduleText = error ?? "";
            return;
        }
        try
        {
            ScheduleText = _projector.Describe(schedule);
            var next = _projector.NextOccurrences(schedule, _now(), PreviewCount);
            foreach (var start in next) Preview.Add(MaintenanceWindowsText.Occurrence(start, schedule.Duration));
            if (next.Count == 0) Preview.Add(NotProjected);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            ScheduleText = ScheduleNotShown;
        }
    }

    /// <summary>The window the inputs describe, with the original's id and state when editing.</summary>
    public MaintenanceWindowInfo? BuildWindow()
    {
        var (schedule, _) = BuildSchedule();
        if (schedule is null || SelectedCollection is not { } collection) return null;
        var next = SafeNext(schedule);
        return new MaintenanceWindowInfo(collection.Collection.CollectionId, collection.Collection.Name, Name.Trim(),
            Original?.ServiceWindowId ?? "", WindowType, Original?.Enabled ?? true, schedule, SafeDescribe(schedule), next, Original?.Comment ?? "",
            Original?.Description ?? "");
    }

    public MaintenanceTemplate? BuildTemplate()
    {
        var (schedule, _) = BuildSchedule();
        if (schedule is null) return null;
        return new MaintenanceTemplate(OriginalTemplate?.Id ?? Guid.NewGuid().ToString("N"), Name.Trim(), Description.Trim(), schedule, WindowType, false);
    }

    private DateTime? SafeNext(MaintenanceSchedule schedule)
    {
        try { return _projector.NextOccurrences(schedule, _now(), 1).Cast<DateTime?>().FirstOrDefault(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private string SafeDescribe(MaintenanceSchedule schedule)
    {
        try { return _projector.Describe(schedule); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }

    private bool CanSave() => !IsSaving && _scheduleProblem is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (IsSaving) return;
        if (Validate() is { } error)
        {
            ValidationMessage = error;
            return;
        }
        ValidationMessage = "";
        IsSaving = true;
        try
        {
            if (!await _save(this)) return;
        }
        finally { IsSaving = false; }
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsSaving) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a message from the page, for example why a write did not run.</summary>
    public void ShowMessage(string message) => ValidationMessage = message;
}
