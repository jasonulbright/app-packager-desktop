using ApplicationPackagerEngine.Services.Logging;
using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Deploy tab: a type switch over one deployment form. The object and collection lists, the
/// programs of a package and the DP group status are read off the UI thread; a read that ends
/// after the type, the object or the visit changed is discarded. Validate shows the five checks;
/// a check result is discarded when the form changed while it ran. Deploy and Distribute content
/// read the form once, prepare the change and show the plan made from the prepared change.
/// </summary>
public sealed partial class DeploymentFormViewModel : DeploymentTabViewModel
{
    public const string ReadFailedText = "The objects and collections could not be read. The log has the details.";
    public const string DetailsFailed = "The distribution point groups or programs could not be read. The log has the details.";
    public const string PickObject = "Pick an object in the list.";
    public const string PickProgram = "Pick the program of the package.";
    public const string PickCollection = "Pick a device collection in the list.";
    public const string BuiltInRefused = "Built-in collections cannot be chosen. Pick a device collection that was made at the site.";
    public const string ValidateAgain = "The form changed after the checks ran. Select Validate again.";
    public const string ChangedDuringCheck = "The form changed while the checks ran. The result was discarded. Select Validate again.";
    public const string WrittenText = "A change was written to ConfigMgr. Select Validate again to check the current state.";
    public const string ValidateFailed ="The checks could not run. The log has the details.";
    public const string StalePlanMarker = "Skipped: the plan is stale";
    public const string StalePlanText =
        "The object or the collection changed after the checks ran, so nothing was created. The form is kept. Select Validate again, then Deploy.";
    public const string NoGroupsChecked = "Check at least one distribution point group.";
    public const string TemplateSaveFailed = "The template could not be saved. The log has the details.";
    public const string TemplateNameTaken = "A shipped template has this name. Choose another name.";
    public const string TimeFormatMessage = "Enter the time as hours and minutes, for example 08:30.";
    public const string RequiredOnlyText =
        "Rerun, maintenance window, metered connection and restart settings apply to Required deployments only. ConfigMgr ignores them for Available.";
    public const string LocalNote = "Local time: each client reads the available time and the deadline in its own time zone.";
    public const string UtcNote = "UTC: the available time and the deadline are UTC times, the same moment on every client.";
    public const string NoExpirationNote = " No expiration time is set.";

    private IReadOnlyList<DeployableObject> _objects = [];
    private IReadOnlyList<CollectionRow> _collections = [];
    private int _formVersion;
    private int _objectGeneration;
    private int _detailGeneration;
    private int _validationGeneration;
    private bool _applying;
    private string? _templateId;
    private CancellationTokenSource? _objectsCts;

    /// <summary>True when <see cref="DpMessage"/> answers the last action, not the state of the group list.</summary>
    private bool _dpActionMessage;

    public DeploymentFormViewModel(DeploymentShared shared) : base(shared)
    {
        shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeploymentShared.IsWriting))
            {
                DeployCommand.NotifyCanExecuteChanged();
                DistributeCommand.NotifyCanExecuteChanged();
            }
        };
        ResetForm();
    }

    protected override string ReadFailed => ReadFailedText;

    public IReadOnlyList<string> TypeNames { get; } =
        [.. Enum.GetValues<DeploymentObjectType>().Select(DeploymentText.TypePlural)];

    public IReadOnlyList<DeploymentChoice> Notifications => DeploymentText.Notifications;
    public IReadOnlyList<DeploymentChoice> NetworkBehaviors => DeploymentText.NetworkBehaviors;
    public IReadOnlyList<DeploymentChoice> SlowNetworkBehaviors => DeploymentText.SlowNetworkBehaviors;
    public IReadOnlyList<DeploymentChoice> TaskSequenceContents => DeploymentText.TaskSequenceContents;
    public IReadOnlyList<DeploymentChoice> TaskSequenceAvailabilities => DeploymentText.TaskSequenceAvailabilities;
    public IReadOnlyList<DeploymentChoice> RerunBehaviors => DeploymentText.RerunBehaviors;
    public string SuppressRestartLabel => DeploymentText.SuppressRestartLabel;

    public ObservableCollection<DeploymentObjectRow> Objects { get; } = [];
    public ObservableCollection<DeploymentCollectionRow> Collections { get; } = [];
    public ObservableCollection<string> Programs { get; } = [];
    public ObservableCollection<DpGroupRow> DpGroups { get; } = [];
    public ObservableCollection<DeploymentCheckRow> Checks { get; } = [];
    public ObservableCollection<DeploymentTemplateRow> Templates { get; } = [];

    /// <summary>Raised when the type or the template changes, so the page saves its choices.</summary>
    public event EventHandler? ChoicesChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Type), nameof(IsApplication), nameof(IsPackage), nameof(IsTaskSequence), nameof(IsUpdateGroup),
        nameof(ShowMaintenanceOptions), nameof(ShowMetered), nameof(ShowRerun), nameof(ShowNotification), nameof(ShowAllowFallback), nameof(ShowContent),
        nameof(ObjectHeader), nameof(TimeBasisNote))]
    private int _typeIndex;

    public DeploymentObjectType Type => (DeploymentObjectType)Math.Clamp(TypeIndex, 0, 3);
    public bool IsApplication => Type == DeploymentObjectType.Application;
    public bool IsPackage => Type == DeploymentObjectType.Package;
    public bool IsTaskSequence => Type == DeploymentObjectType.TaskSequence;
    public bool IsUpdateGroup => Type == DeploymentObjectType.SoftwareUpdateGroup;
    public bool ShowMaintenanceOptions => true;
    public bool ShowMetered => true;
    public bool ShowRerun => Type is DeploymentObjectType.Package or DeploymentObjectType.TaskSequence;
    public bool ShowNotification => DeploymentText.HasNotification(Type);
    public bool ShowAllowFallback => Type is DeploymentObjectType.Package or DeploymentObjectType.TaskSequence;

    /// <summary>False for a software update group: its content is in deployment packages, which the console distributes.</summary>
    public bool ShowContent => Type != DeploymentObjectType.SoftwareUpdateGroup;
    public string ObjectHeader => DeploymentText.TypePlural(Type);

    /// <summary>True when the settings that ConfigMgr stores for Required only can be changed.</summary>
    public bool RequiredOnlyEnabled => IsRequired;

    /// <summary>Why the Required-only settings are off; empty for a Required deployment.</summary>
    public string RequiredOnlyNote => IsRequired ? "" : RequiredOnlyText;

    public string TimeBasisNote => (UtcBasis ? UtcNote : LocalNote) + (Type is DeploymentObjectType.Package or DeploymentObjectType.TaskSequence ? NoExpirationNote : "");

    /// <summary>The note of the service when it cut the object list; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasObjectListNote))]
    private string _objectListNote = "";

    public bool HasObjectListNote => ObjectListNote.Length > 0;

    [ObservableProperty] private string _objectFilter = "";
    [ObservableProperty] private string _collectionFilter = "";
    [ObservableProperty] private string _objectCount = "";
    [ObservableProperty] private string _collectionCount = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DistributeCommand), nameof(RefreshDpGroupsCommand))]
    private DeploymentObjectRow? _selectedObject;

    [ObservableProperty] private DeploymentCollectionRow? _selectedCollection;
    [ObservableProperty] private string? _selectedProgram;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Purpose), nameof(IsAvailable), nameof(RequiredOnlyEnabled), nameof(RequiredOnlyNote))]
    private bool _isRequired;

    public string Purpose => IsRequired ? DeploymentText.Required : DeploymentText.Available;

    /// <summary>The Available choice of the purpose radio buttons.</summary>
    public bool IsAvailable
    {
        get => !IsRequired;
        set => IsRequired = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableDateTip))]
    private DateTime? _availableDate;

    [ObservableProperty] private string _availableTimeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeadlineDateTip))]
    private DateTime? _deadlineDate;

    /// <summary>The date with its weekday; the date box shows the short date.</summary>
    public string? AvailableDateTip => LongDate(AvailableDate);

    public string? DeadlineDateTip => LongDate(DeadlineDate);

    // The long date pattern of some cultures has no weekday.
    private static string? LongDate(DateTime? date)
    {
        if (date is not { } day) return null;
        var culture = CultureInfo.CurrentCulture;
        var text = day.ToString("D", culture);
        var weekday = day.ToString("dddd", culture);
        return text.Contains(weekday, StringComparison.CurrentCultureIgnoreCase) ? text : $"{weekday}, {text}";
    }
    [ObservableProperty] private string _deadlineTimeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalTime), nameof(TimeBasisNote))]
    private bool _utcBasis;

    /// <summary>The local time choice of the time basis radio buttons.</summary>
    public bool IsLocalTime
    {
        get => !UtcBasis;
        set => UtcBasis = !value;
    }
    [ObservableProperty] private DeploymentChoice _notification = DeploymentText.Notifications[0];
    [ObservableProperty] private bool _overrideServiceWindow;
    [ObservableProperty] private bool _rebootOutsideServiceWindow;
    [ObservableProperty] private bool _allowMetered;
    [ObservableProperty] private DeploymentChoice _networkBehavior = DeploymentText.NetworkBehaviors[0];
    [ObservableProperty] private DeploymentChoice _slowNetworkBehavior = DeploymentText.SlowNetworkBehaviors[0];
    [ObservableProperty] private DeploymentChoice _taskSequenceContent = DeploymentText.TaskSequenceContents[0];
    [ObservableProperty] private DeploymentChoice _taskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities[0];
    [ObservableProperty] private DeploymentChoice _rerunBehavior = DeploymentText.RerunBehaviors[0];
    [ObservableProperty] private bool _showProgress = true;
    [ObservableProperty] private bool _allowFallback;
    [ObservableProperty] private bool _fallbackToDp = true;
    [ObservableProperty] private bool _postRebootScan = true;
    [ObservableProperty] private bool _suppressRestart;
    [ObservableProperty] private bool _allowMicrosoftUpdate;

    /// <summary>Deploy distributes the content to the checked groups first; the plan then offers 'create now'.</summary>
    [ObservableProperty] private bool _distributeFirst;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormMessage))]
    private string _formMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollectionMessage))]
    private string _collectionMessage = "";

    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private string _dpMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand))]
    private bool _isValidating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDpGroupsCommand))]
    private bool _isLoadingDetails;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyTemplateCommand))]
    private DeploymentTemplateRow? _selectedTemplate;

    [ObservableProperty] private string _templateMessage = "";

    public bool HasFormMessage => FormMessage.Length > 0;
    public bool HasCollectionMessage => CollectionMessage.Length > 0;

    /// <summary>The id of the template last chosen; restored on the next visit.</summary>
    public string? TemplateId => _templateId;

    partial void OnTypeIndexChanged(int value)
    {
        FormChanged();
        if (_applying) return;
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
        SelectedObject = null;
        Objects.Clear();
        _objects = [];
        ObjectCount = "";
        ObjectListNote = "";
        if (Shared.IsActive && IsLoaded) _ = ReloadObjectsAsync();
    }

    partial void OnObjectFilterChanged(string value) => ApplyObjectFilter();
    partial void OnCollectionFilterChanged(string value) => ApplyCollectionFilter();

    partial void OnSelectedObjectChanged(DeploymentObjectRow? value)
    {
        FormChanged();
        Programs.Clear();
        SelectedProgram = null;
        ClearDpGroups();
        SetDpMessage("", action: false);
        if (value is not null && Shared.IsActive) _ = LoadDetailsAsync(value);
    }

    partial void OnSelectedCollectionChanged(DeploymentCollectionRow? value)
    {
        FormChanged();
        CollectionMessage = value is { IsBuiltIn: true } ? BuiltInRefused : "";
    }

    partial void OnSelectedTemplateChanged(DeploymentTemplateRow? value)
    {
        if (_applying || value is null) return;
        _templateId = value.Template.Id;
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedProgramChanged(string? value) => FormChanged();
    partial void OnIsRequiredChanged(bool value) => FormChanged();
    partial void OnAvailableDateChanged(DateTime? value) => FormChanged();
    partial void OnAvailableTimeTextChanged(string value) => FormChanged();
    partial void OnDeadlineDateChanged(DateTime? value) => FormChanged();
    partial void OnDeadlineTimeTextChanged(string value) => FormChanged();
    partial void OnUtcBasisChanged(bool value) => FormChanged();
    partial void OnNotificationChanged(DeploymentChoice value) => FormChanged();
    partial void OnOverrideServiceWindowChanged(bool value) => FormChanged();
    partial void OnRebootOutsideServiceWindowChanged(bool value) => FormChanged();
    partial void OnAllowMeteredChanged(bool value) => FormChanged();
    partial void OnNetworkBehaviorChanged(DeploymentChoice value) => FormChanged();
    partial void OnSlowNetworkBehaviorChanged(DeploymentChoice value) => FormChanged();
    partial void OnTaskSequenceContentChanged(DeploymentChoice value) => FormChanged();
    partial void OnTaskSequenceAvailabilityChanged(DeploymentChoice value) => FormChanged();
    partial void OnRerunBehaviorChanged(DeploymentChoice value) => FormChanged();
    partial void OnShowProgressChanged(bool value) => FormChanged();
    partial void OnAllowFallbackChanged(bool value) => FormChanged();
    partial void OnFallbackToDpChanged(bool value) => FormChanged();
    partial void OnPostRebootScanChanged(bool value) => FormChanged();
    partial void OnSuppressRestartChanged(bool value) => FormChanged();
    partial void OnAllowMicrosoftUpdateChanged(bool value) => FormChanged();
    partial void OnDistributeFirstChanged(bool value) => FormChanged();

    /// <summary>Every change of the form makes the checks of the last Validate and the message of the last action stale.</summary>
    private void FormChanged()
    {
        _formVersion++;
        FormMessage = "";
        if (_dpActionMessage) SetDpMessage("", action: false);
        InvalidateChecks(ValidateAgain);
    }

    private void InvalidateChecks(string message)
    {
        if (Checks.Count == 0) return;
        Checks.Clear();
        ValidationMessage = message;
    }

    private void SetDpMessage(string message, bool action)
    {
        DpMessage = message;
        _dpActionMessage = action && message.Length > 0;
    }

    private void OnDpGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DpGroupRow.IsChecked)) FormChanged();
    }

    /// <summary>The form as a new visit shows it: Available now, the defaults of the source tool.</summary>
    private void ResetForm()
    {
        var now = Services.Clock.GetLocalNow().DateTime;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(5 - now.Minute % 5);
        IsRequired = false;
        AvailableDate = start.Date;
        AvailableTimeText = start.ToString(DeploymentText.TimeFormat, CultureInfo.InvariantCulture);
        DeadlineDate = start.Date.AddDays(1);
        DeadlineTimeText = AvailableTimeText;
        UtcBasis = false;
        Notification = DeploymentText.Notifications[0];
        OverrideServiceWindow = false;
        RebootOutsideServiceWindow = false;
        AllowMetered = false;
        NetworkBehavior = DeploymentText.NetworkBehaviors[0];
        SlowNetworkBehavior = DeploymentText.SlowNetworkBehaviors[0];
        TaskSequenceContent = DeploymentText.TaskSequenceContents[0];
        TaskSequenceAvailability = DeploymentText.TaskSequenceAvailabilities[0];
        RerunBehavior = DeploymentText.RerunBehaviors[0];
        ShowProgress = true;
        AllowFallback = false;
        FallbackToDp = true;
        PostRebootScan = true;
        SuppressRestart = false;
        AllowMicrosoftUpdate = false;
        DistributeFirst = false;
        FormMessage = "";
        ValidationMessage = "";
        Checks.Clear();
    }

    public void ApplyChoices(int type, string? templateId)
    {
        _applying = true;
        try { TypeIndex = Math.Clamp(type, 0, 3); }
        finally { _applying = false; }
        _templateId = string.IsNullOrEmpty(templateId) ? null : templateId;
    }

    protected override async Task<Action> ReadAsync(CancellationToken ct)
    {
        var type = Type;
        var objects = await Services.Deployments.ListObjectsAsync(type, ct);
        var collections = await Services.Collections.ListCollectionsAsync(ct);
        var templates = Services.Templates.All();
        var templateMessage = Services.Templates.Message;
        return () =>
        {
            ShowTemplates(templates, templateMessage);
            _collections = collections;
            ApplyCollectionFilter();
            if (type != Type)
            {
                _ = ReloadObjectsAsync();
                return;
            }
            _objectGeneration++;
            _objects = objects;
            ObjectListNote = ListNote(objects);
            ApplyObjectFilter();
            Services.Log.Info(DeploymentShared.LogArea, $"Read {objects.Count} objects and {collections.Count} collections.");
        };
    }

    /// <summary>Reads the objects of the current type again; a read that ends after another type was chosen is discarded.</summary>
    public async Task ReloadObjectsAsync()
    {
        var mine = ++_objectGeneration;
        var generation = Shared.Generation;
        var type = Type;
        CancelObjectRead();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Shared.VisitToken);
        _objectsCts = cts;
        IsLoading = true;
        try
        {
            var objects = await Task.Run(() => Services.Deployments.ListObjectsAsync(type, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (mine != _objectGeneration || generation != Shared.Generation || type != Type) return;
            _objects = objects;
            ObjectListNote = ListNote(objects);
            ApplyObjectFilter();
            Services.Log.Info(DeploymentShared.LogArea, $"Read {objects.Count} objects.");
        }
        catch (OperationCanceledException)
        {
            if (mine == _objectGeneration && generation == Shared.Generation) Shared.StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _objectGeneration || generation != Shared.Generation) return;
            Shared.StatusMessage = DeploymentShared.PlainMessage(ex, ReadFailedText);
            Services.Log.Warning(DeploymentShared.LogArea, $"The objects could not be read ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_objectsCts, cts)) _objectsCts = null;
            if (mine == _objectGeneration) IsLoading = false;
        }
    }

    private static string ListNote<T>(IReadOnlyList<T> rows) =>
        rows is DeploymentList<T> { Truncated: true } cut ? SecretMask.Apply(cut.Note ?? "") : "";

    private void CancelObjectRead()
    {
        try { _objectsCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    protected override void CancelOtherReads() => CancelObjectRead();

    private void ShowTemplates(IReadOnlyList<DeploymentTemplate> templates, string? message)
    {
        _applying = true;
        try
        {
            Templates.Clear();
            foreach (var t in templates.OrderBy(t => t.IsSeeded ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) Templates.Add(new DeploymentTemplateRow(t));
            SelectedTemplate = Templates.FirstOrDefault(t => string.Equals(t.Template.Id, _templateId, StringComparison.OrdinalIgnoreCase));
        }
        finally { _applying = false; }
        TemplateMessage = message ?? "";
    }

    /// <summary>Reads the templates again after the Options tab changed them.</summary>
    public async Task ReloadTemplatesAsync()
    {
        var generation = Shared.Generation;
        var (templates, message) = await Task.Run(() => (Services.Templates.All(), Services.Templates.Message));
        if (generation == Shared.Generation) ShowTemplates(templates, message);
    }

    private void ApplyObjectFilter()
    {
        var selectedId = SelectedObject?.Object.Id;
        var text = ObjectFilter.Trim();
        var wanted = _objects.Where(o => text.Length == 0 || Contains(o.Name, text) || Contains(o.Id, text) || Contains(o.Version, text))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Version, StringComparer.OrdinalIgnoreCase).ToList();
        var keep = Objects.Where(r => wanted.Contains(r.Object)).ToDictionary(r => r.Object);
        Replace(Objects, wanted.Select(o => keep.TryGetValue(o, out var row) ? row : new DeploymentObjectRow(o)).ToList());
        if (SelectedObject is not null && !Objects.Contains(SelectedObject))
            SelectedObject = Objects.FirstOrDefault(r => r.Object.Id == selectedId);
        ObjectCount = Objects.Count == _objects.Count ? DeploymentText.Count(_objects.Count, "object") : $"{Objects.Count} of {DeploymentText.Count(_objects.Count, "object")}";
    }

    private void ApplyCollectionFilter()
    {
        var selectedId = SelectedCollection?.CollectionId;
        var text = CollectionFilter.Trim();
        var wanted = _collections.Where(c => text.Length == 0 || Contains(c.Name, text) || Contains(c.CollectionId, text) || Contains(c.FolderPath, text))
            .OrderBy(c => c.IsBuiltIn ? 1 : 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var keep = Collections.Where(r => wanted.Contains(r.Row)).ToDictionary(r => r.Row);
        Replace(Collections, wanted.Select(c => keep.TryGetValue(c, out var row) ? row : new DeploymentCollectionRow(c)).ToList());
        if (SelectedCollection is not null && !Collections.Contains(SelectedCollection))
            SelectedCollection = Collections.FirstOrDefault(r => r.CollectionId == selectedId);
        CollectionCount = Collections.Count == _collections.Count
            ? DeploymentText.Count(_collections.Count, "collection")
            : $"{Collections.Count} of {DeploymentText.Count(_collections.Count, "collection")}";
    }

    /// <summary>Reads the programs of a package and the DP group status of the object; a read for an object no longer selected is discarded.</summary>
    private async Task LoadDetailsAsync(DeploymentObjectRow row)
    {
        var mine = ++_detailGeneration;
        var generation = Shared.Generation;
        IsLoadingDetails = true;
        try
        {
            var visit = Shared.VisitToken;
            var (programs, groups) = await Task.Run(async () =>
            {
                IReadOnlyList<string> p = row.Object.Type == DeploymentObjectType.Package
                    ? await Services.Deployments.ListProgramsAsync(row.Object.Id, visit) : [];
                var g = await Services.Deployments.ListDpGroupsAsync(row.Object, visit);
                return (p, g);
            }, visit);
            if (mine != _detailGeneration || generation != Shared.Generation || !ReferenceEquals(SelectedObject, row)) return;
            ShowPrograms(programs);
            ShowDpGroups(groups);
        }
        catch (Exception ex)
        {
            if (mine != _detailGeneration || generation != Shared.Generation) return;
            SetDpMessage(DeploymentShared.PlainMessage(ex, DetailsFailed), action: false);
            Services.Log.Warning(DeploymentShared.LogArea, $"The DP groups or programs could not be read ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (mine == _detailGeneration) IsLoadingDetails = false;
        }
    }

    /// <summary>
    /// Shows the programs of the package and keeps the chosen one while the package still has it.
    /// Clearing the list makes the bound program box set the choice to null, so an unchanged list stays as it is.
    /// </summary>
    private void ShowPrograms(IReadOnlyList<string> programs)
    {
        var chosen = SelectedProgram;
        if (Programs.SequenceEqual(programs, StringComparer.Ordinal))
        {
            if (chosen is null && Programs.Count == 1) SelectedProgram = Programs[0];
            return;
        }
        Programs.Clear();
        foreach (var program in programs) Programs.Add(program);
        SelectedProgram = chosen is not null && Programs.Contains(chosen) ? chosen : Programs.Count == 1 ? Programs[0] : null;
    }

    private void ShowDpGroups(IReadOnlyList<DpGroupInfo> groups)
    {
        var checkedNames = DpGroups.Where(g => g.IsChecked).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ClearDpGroups();
        foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new DpGroupRow(group) { IsChecked = checkedNames.Contains(group.Name) };
            row.PropertyChanged += OnDpGroupChanged;
            DpGroups.Add(row);
        }
        SetDpMessage(groups.Count == 0 ? "The site has no distribution point groups." : "", action: false);
    }

    private void ClearDpGroups()
    {
        foreach (var row in DpGroups) row.PropertyChanged -= OnDpGroupChanged;
        DpGroups.Clear();
    }

    private bool CanRefreshDpGroups() => SelectedObject is not null && !IsLoadingDetails;

    [RelayCommand(CanExecute = nameof(CanRefreshDpGroups))]
    private Task RefreshDpGroups() => SelectedObject is { } row ? LoadDetailsAsync(row) : Task.CompletedTask;

    protected override void ReleaseData()
    {
        _objectGeneration++;
        _detailGeneration++;
        _validationGeneration++;
        _objects = [];
        _collections = [];
        SelectedObject = null;
        SelectedCollection = null;
        Objects.Clear();
        Collections.Clear();
        Programs.Clear();
        ClearDpGroups();
        Templates.Clear();
        ObjectFilter = "";
        CollectionFilter = "";
        ObjectCount = "";
        ObjectListNote = "";
        CollectionCount = "";
        SetDpMessage("", action: false);
        CollectionMessage = "";
        TemplateMessage = "";
        IsValidating = false;
        IsLoadingDetails = false;
        ResetForm();
    }

    private static bool TryTime(DateTime? date, string text, out DateTime value)
    {
        value = default;
        if (date is not { } day) return false;
        if (!TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1)) return false;
        value = day.Date + time;
        return true;
    }

    /// <summary>The options of the form, or null with the reason in <paramref name="problem"/>.</summary>
    public DeploymentOptions? ReadOptions(out string? problem)
    {
        problem = null;
        if (!TryTime(AvailableDate, AvailableTimeText, out var available))
        {
            problem = AvailableDate is null ? "Pick the available date." : "Available time: " + TimeFormatMessage;
            return null;
        }
        DateTime? deadline = null;
        if (IsRequired)
        {
            if (!TryTime(DeadlineDate, DeadlineTimeText, out var due))
            {
                problem = DeadlineDate is null ? "Pick the deadline date." : "Deadline: " + TimeFormatMessage;
                return null;
            }
            deadline = due;
        }
        var required = IsRequired;
        var type = Type;
        var options = new DeploymentOptions(
            Purpose: Purpose,
            AvailableTime: available,
            Deadline: deadline,
            UtcBasis: UtcBasis,
            UserNotification: Notification.Value,
            OverrideServiceWindow: required && OverrideServiceWindow,
            RebootOutsideServiceWindow: required && RebootOutsideServiceWindow,
            AllowMetered: required && AllowMetered,
            NetworkBehavior: type switch
            {
                DeploymentObjectType.Package => NetworkBehavior.Value,
                DeploymentObjectType.TaskSequence => TaskSequenceContent.Value,
                _ => ""
            },
            RerunBehavior: ShowRerun ? RerunBehavior.Value : "",
            ShowProgress: ShowProgress,
            AllowFallback: ShowAllowFallback && AllowFallback,
            FallbackToDp: FallbackToDp,
            PostRebootScan: PostRebootScan,
            SuppressRestart: required && SuppressRestart,
            SlowNetworkBehavior: SlowNetworkBehavior.Value,
            TaskSequenceAvailability: TaskSequenceAvailability.Value,
            AllowMicrosoftUpdate: AllowMicrosoftUpdate);
        problem = DeploymentText.ScheduleProblem(type, options, DeploymentText.Now(Services.Clock, UtcBasis));
        return problem is null ? options : null;
    }

    /// <summary>
    /// The create request of the form, read once; null with the reason in <see cref="FormMessage"/>.
    /// The checked DP groups travel with the create only when it distributes first.
    /// </summary>
    public DeploymentChange? BuildCreateRequest()
    {
        string? problem = null;
        DeploymentChange? request = null;
        if (SelectedObject is not { } row) problem = PickObject;
        else if (row.Object.Type == DeploymentObjectType.Package && string.IsNullOrEmpty(SelectedProgram)) problem = PickProgram;
        else if (SelectedCollection is not { } collection) problem = PickCollection;
        else if (collection.IsBuiltIn) problem = BuiltInRefused;
        else if (ReadOptions(out problem) is { } options)
        {
            var obj = row.Object.Type == DeploymentObjectType.Package ? row.Object with { Program = SelectedProgram! } : row.Object;
            IReadOnlyList<string> groups = DistributeFirst && ShowContent ? DpGroups.Where(g => g.IsChecked).Select(g => g.Name).ToList() : [];
            request = new DeploymentChange(Kind: DeploymentChangeKind.Create, Object: obj, Collection: collection.ToCollection(), Options: options,
                DpGroups: groups, CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: null,
                Checks: [], Impact: [], CreatedId: null);
        }
        FormMessage = problem ?? "";
        return request;
    }

    /// <summary>The distribution request for the checked DP groups; null with the reason in <see cref="DpMessage"/>.</summary>
    private DeploymentChange? BuildDistributeRequest()
    {
        if (SelectedObject is not { } row)
        {
            SetDpMessage(PickObject, action: true);
            return null;
        }
        var groups = DpGroups.Where(g => g.IsChecked).Select(g => g.Name).ToList();
        if (groups.Count == 0)
        {
            SetDpMessage(NoGroupsChecked, action: true);
            return null;
        }
        SetDpMessage("", action: false);
        var obj = row.Object.Type == DeploymentObjectType.Package && !string.IsNullOrEmpty(SelectedProgram) ? row.Object with { Program = SelectedProgram } : row.Object;
        return new DeploymentChange(Kind: DeploymentChangeKind.Distribute, Object: obj, Collection: null, Options: null, DpGroups: groups,
            CreateNowContentDistributing: false, RequiredConfirmed: false, BackdatedAcknowledged: false, Existing: null, Checks: [], Impact: [], CreatedId: null);
    }

    /// <summary>Lists every check the service reported, the five first; a failed or unknown check blocks, a warning does not.</summary>
    private void ShowChecks(DeploymentChange prepared)
    {
        Checks.Clear();
        var type = prepared.Object?.Type;
        foreach (var check in DeploymentText.Ordered(prepared.Checks))
            Checks.Add(new DeploymentCheckRow(Name: check.Name, Check: DeploymentText.CheckLabel(check.Name), Result: check.Result, Message: DeploymentText.CheckMessage(check, type)));
        var blocking = prepared.Checks.Count(DeploymentText.Blocks);
        var warnings = prepared.Checks.Count(c => c.Result == DeploymentText.Warn);
        ValidationMessage = blocking > 0
            ? $"{DeploymentText.Count(blocking, "check")} did not pass. The deployment is blocked."
            : warnings == 0 ? "Every check passed."
            : $"No check blocks the deployment. {(warnings == 1 ? "1 check has a warning: read its message." : $"{warnings} checks have a warning: read their messages.")}";
    }

    private bool CanValidate() => !IsValidating;

    /// <summary>Runs the five checks and the schedule rules on the form; a second click while they run does nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task Validate()
    {
        if (IsValidating) return;
        if (Shared.ConnectionProblem() is { } connection)
        {
            FormMessage = connection;
            return;
        }
        if (BuildCreateRequest() is not { } request) return;
        var version = _formVersion;
        var mine = ++_validationGeneration;
        var generation = Shared.Generation;
        IsValidating = true;
        ValidationMessage = "Running the checks.";
        try
        {
            var visit = Shared.VisitToken;
            var prepared = await Task.Run(() => Services.Deployments.PrepareAsync(request, visit), visit);
            if (mine != _validationGeneration || generation != Shared.Generation) return;
            if (version != _formVersion)
            {
                Checks.Clear();
                ValidationMessage = ChangedDuringCheck;
                return;
            }
            ShowChecks(prepared);
            var backdated = prepared.NeedsBackdatedAcknowledgement
                || DeploymentText.IsBackdated(prepared.Options ?? request.Options!, DeploymentText.Now(Services.Clock, request.Options!.UtcBasis));
            if (backdated) ValidationMessage += " Warning: the available time is in the past; the plan asks for an extra confirmation.";
            Services.Log.Info(DeploymentShared.LogArea, $"Checks ran: {Checks.Count(c => c.Result == DeploymentText.Pass)} of {Checks.Count} passed.");
        }
        catch (Exception ex)
        {
            if (mine != _validationGeneration || generation != Shared.Generation) return;
            ValidationMessage = DeploymentShared.PlainMessage(ex, ValidateFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"The checks could not run ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (mine == _validationGeneration) IsValidating = false;
        }
    }

    private bool CanWrite() => !Shared.IsWriting;

    /// <summary>
    /// Reads the form once, prepares the change and shows the plan made from the prepared change; a
    /// change of the form while the state is read does not change the plan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task Deploy()
    {
        if (Shared.IsWriting) return;
        if (BuildCreateRequest() is not { } create) return;
        if (DistributeFirst && ShowContent && create.DpGroups.Count == 0)
        {
            FormMessage = NoGroupsChecked;
            return;
        }
        var version = _formVersion;
        var run = await Shared.RunPlanAsync([create], prepared => DeploymentText.BuildDeployPlan(prepared, Services.Clock),
            prepared =>
            {
                if (version == _formVersion && prepared.FirstOrDefault(c => c.Kind == DeploymentChangeKind.Create) is { } done) ShowChecks(done);
            },
            message => FormMessage = message);
        if (run is not { Outcome.Confirmed: true } done) return;
        await Shared.AfterWriteAsync();
        if (done.Plan.Outcomes.Any(o => o.Message.StartsWith(StalePlanMarker, StringComparison.Ordinal))) ShowStalePlan();
    }

    /// <summary>Drops the checks of the stale plan and keeps the form, so Validate reads the current state.</summary>
    private void ShowStalePlan()
    {
        _validationGeneration++;
        IsValidating = false;
        Checks.Clear();
        ValidationMessage = StalePlanText;
        FormMessage = StalePlanText;
    }

    /// <summary>Distributes the content of the selected object to the checked DP groups through a plan.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task Distribute()
    {
        if (Shared.IsWriting) return;
        if (BuildDistributeRequest() is not { } request) return;
        var run = await Shared.RunPlanAsync([request], prepared => DeploymentText.BuildDeployPlan(prepared, Services.Clock), report: message => SetDpMessage(message, action: true));
        if (run is not { Outcome.Confirmed: true }) return;
        await Shared.AfterWriteAsync();
    }

    /// <summary>Drops the checks, which describe the state before the write, and reads the DP group status again; the object lists stay.</summary>
    public async Task RefreshAfterDeployAsync()
    {
        _validationGeneration++;
        IsValidating = false;
        InvalidateChecks(WrittenText);
        if (SelectedObject is { } row && Shared.IsActive) await LoadDetailsAsync(row);
    }

    private bool CanApplyTemplate() => SelectedTemplate is not null;

    /// <summary>Fills the form from the template; nothing about objects or collections changes.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyTemplate))]
    private void ApplyTemplate()
    {
        if (SelectedTemplate?.Template is not { } template) return;
        IsRequired = string.Equals(template.Purpose, DeploymentText.Required, StringComparison.OrdinalIgnoreCase);
        Notification = DeploymentText.Notifications.FirstOrDefault(n => string.Equals(n.Value, template.UserNotification, StringComparison.OrdinalIgnoreCase))
            ?? DeploymentText.Notifications[0];
        UtcBasis = template.UtcBasis;
        OverrideServiceWindow = template.OverrideServiceWindow;
        RebootOutsideServiceWindow = template.RebootOutsideServiceWindow;
        AllowMetered = template.AllowMetered;
        FallbackToDp = template.AllowBoundaryFallback;
        AllowFallback = template.AllowBoundaryFallback;
        AllowMicrosoftUpdate = template.AllowMicrosoftUpdate;
        PostRebootScan = template.PostRebootScan;
        if (IsRequired && template.DeadlineOffsetHours > 0 && TryTime(AvailableDate, AvailableTimeText, out var available))
        {
            var deadline = available.AddHours(template.DeadlineOffsetHours);
            DeadlineDate = deadline.Date;
            DeadlineTimeText = deadline.ToString(DeploymentText.TimeFormat, CultureInfo.InvariantCulture);
        }
        _templateId = template.Id;
        TemplateMessage = $"Applied the template {template.Name}.";
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The template id for a name: lower-case letters, digits and hyphens.</summary>
    public static string TemplateIdFor(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var id = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return id.Length > 64 ? id[..64].TrimEnd('-') : id;
    }

    /// <summary>The template the form describes, under <paramref name="name"/>.</summary>
    public DeploymentTemplate TemplateFromForm(string name)
    {
        var offset = 0;
        if (IsRequired && TryTime(AvailableDate, AvailableTimeText, out var available) && TryTime(DeadlineDate, DeadlineTimeText, out var deadline) && deadline > available)
            offset = (int)Math.Min(int.MaxValue, Math.Round((deadline - available).TotalHours));
        return new DeploymentTemplate(Id: TemplateIdFor(name), Name: name.Trim(), Purpose: Purpose, UserNotification: Notification.Value, UtcBasis: UtcBasis,
            OverrideServiceWindow: OverrideServiceWindow, RebootOutsideServiceWindow: RebootOutsideServiceWindow, AllowMetered: AllowMetered,
            AllowBoundaryFallback: IsUpdateGroup ? FallbackToDp : AllowFallback, AllowMicrosoftUpdate: AllowMicrosoftUpdate,
            PostRebootScan: PostRebootScan, DeadlineOffsetHours: offset, IsSeeded: false);
    }

    /// <summary>Asks for a name and saves the form as a user template.</summary>
    [RelayCommand]
    private void SaveAsTemplate()
    {
        var prompt = new TextPromptViewModel("Save as template", "Template name",
            "The template keeps the purpose, notification, time basis, options and the deadline offset. It keeps no object or collection.",
            "Save template", false, Validate, SaveTemplateAsync, SelectedTemplate is { Template.IsSeeded: false } user ? user.Name : "");
        Services.Dialogs.ShowTextPrompt(prompt);

        string? Validate(string text)
        {
            var id = TemplateIdFor(text);
            if (id.Length == 0) return "Use letters or digits in the name.";
            return Templates.Any(t => t.Template.IsSeeded && string.Equals(t.Template.Id, id, StringComparison.OrdinalIgnoreCase)) ? TemplateNameTaken : null;
        }
    }

    private async Task<bool> SaveTemplateAsync(TextPromptViewModel prompt)
    {
        var template = TemplateFromForm(prompt.Text);
        try { await Task.Run(() => Services.Templates.Save(template)); }
        catch (Exception ex)
        {
            prompt.ShowMessage(ex is DeploymentServiceException d ? d.Message : TemplateSaveFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"A template could not be saved ({ex.GetType().Name}).");
            return false;
        }
        _templateId = template.Id;
        Services.Log.Info(DeploymentShared.LogArea, "Saved a user template.");
        await ReloadTemplatesAsync();
        TemplateMessage = $"Saved the template {template.Name}.";
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
