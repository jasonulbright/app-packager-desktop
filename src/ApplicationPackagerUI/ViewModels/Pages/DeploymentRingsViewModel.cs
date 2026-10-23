using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Rings tab: a new run (object, plan, plan start, Expand, Validate, Create run), the ring plans
/// (New, Edit, Copy, Delete) and the open runs (live counts, Refresh and reconcile, Promote, Undo
/// run, Close run). Nothing is read before the first visit of the tab; every read runs off the UI
/// thread, and a read that ends after the operator changed the form, the run or the visit is discarded.
/// </summary>
public sealed partial class DeploymentRingsViewModel : DeploymentTabViewModel
{
    public const string ReadFailedText = "The ring plans and runs could not be read. The log has the details.";
    public const string ObjectsFailedText = "The objects could not be read. The log has the details.";
    public const string PlanSaveFailed = "The plan could not be saved. The log has the details.";
    public const string PlanDeleteFailed = "The plan could not be deleted. The log has the details.";
    public const string SeedReadOnly = "Shipped plans cannot be changed or deleted. Copy the plan and change the copy.";
    public const string PickPlan = "Pick a ring plan in the list.";
    public const string NoCollections = "The collections are not read yet. Check the connection in Settings, then select Refresh.";

    private IReadOnlyList<DeployableObject> _objects = [];
    private IReadOnlyList<CollectionRow> _collections = [];
    private IReadOnlyList<RingPlan> _plans = [];
    private string? _planName;
    private bool _applying;
    private RingPlan? _refreshedPlan;
    private int _objectGeneration;
    private int _programGeneration;
    private CancellationTokenSource? _objectsCts;

    public DeploymentRingsViewModel(DeploymentShared shared) : base(shared)
    {
        shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DeploymentShared.IsWriting)) return;
            CreateRunCommand.NotifyCanExecuteChanged();
            ReconcileCommand.NotifyCanExecuteChanged();
            NotifyRunCommands();
        };
        ResetForm();
    }

    protected override bool NeedsSite => false;
    protected override string ReadFailed => ReadFailedText;

    public IReadOnlyList<string> TypeNames { get; } = [.. Enum.GetValues<DeploymentObjectType>().Select(DeploymentText.TypePlural)];

    public ObservableCollection<DeploymentObjectRow> Objects { get; } = [];
    public ObservableCollection<string> Programs { get; } = [];
    public ObservableCollection<RingPlanRow> Plans { get; } = [];

    /// <summary>The device collections the pickers offer; empty until the site was read.</summary>
    public IReadOnlyList<CollectionRow> CollectionList => _collections;

    /// <summary>Raised when the chosen plan changes, so the page saves its choices.</summary>
    public event EventHandler? ChoicesChanged;

    /// <summary>The name of the plan last chosen; restored on the next visit.</summary>
    public string? PlanName => _planName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Type), nameof(IsPackage), nameof(ObjectHeader))]
    private int _typeIndex;

    public DeploymentObjectType Type => (DeploymentObjectType)Math.Clamp(TypeIndex, 0, 3);
    public bool IsPackage => Type == DeploymentObjectType.Package;
    public string ObjectHeader => DeploymentText.TypePlural(Type);

    [ObservableProperty] private string _objectFilter = "";
    [ObservableProperty] private string _objectCount = "";
    [ObservableProperty] private DeploymentObjectRow? _selectedObject;
    [ObservableProperty] private string? _selectedProgram;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditPlanCommand), nameof(CopyPlanCommand), nameof(DeletePlanCommand))]
    [NotifyPropertyChangedFor(nameof(PlanBasisNote))]
    private RingPlanRow? _selectedPlan;

    /// <summary>A note about plan files that could not be read; empty when every file was read.</summary>
    [ObservableProperty] private string _planListNote = "";

    /// <summary>The answer to the last action of the Plans area.</summary>
    [ObservableProperty] private string _plansMessage = "";

    partial void OnTypeIndexChanged(int value)
    {
        FormChanged();
        if (_applying) return;
        SelectedObject = null;
        Objects.Clear();
        _objects = [];
        ObjectCount = "";
        if (Shared.IsActive && IsLoaded) _ = ReloadObjectsAsync();
    }

    partial void OnObjectFilterChanged(string value) => ApplyObjectFilter();

    partial void OnSelectedObjectChanged(DeploymentObjectRow? value)
    {
        FormChanged();
        Programs.Clear();
        SelectedProgram = null;
        if (value is { Object.Type: DeploymentObjectType.Package } row && Shared.IsActive) _ = LoadProgramsAsync(row);
    }

    partial void OnSelectedProgramChanged(string? value) => FormChanged();

    partial void OnSelectedPlanChanged(RingPlanRow? oldValue, RingPlanRow? newValue)
    {
        FormChanged();
        var value = newValue;
        var before = _applying ? _refreshedPlan : oldValue?.Plan;
        // A refresh that brings a changed file of the same plan keeps the operator's choices for this run.
        var samePlan = _applying && before is not null && value is not null && string.Equals(before.Name, value.Name, StringComparison.OrdinalIgnoreCase);
        if (value is not null && !samePlan) HoldLaterRings = value.Plan.HoldLaterRings;
        if (value is not null && (!samePlan || RingText.IsUtc(before!.TimeBasedOn) != RingText.IsUtc(value.Plan.TimeBasedOn))) DefaultStart(value.Plan.TimeBasedOn);
        if (_applying || value is null) return;
        _planName = value.Name;
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyChoices(string? planName) => _planName = string.IsNullOrEmpty(planName) ? null : planName;

    protected override async Task<Action> ReadAsync(CancellationToken ct)
    {
        var plans = Services.Rings.ListPlans();
        var planNote = Services.Rings.PlanMessage;
        var showClosed = ShowClosedRuns;
        var runsRead = BeginTabRunsRead();
        var runs = await ReadRunsAsync(showClosed, ct);
        var type = Type;
        var site = CollectionComplianceViewModel.ConnectionProblem(Services.LoadSettings()) is null;
        IReadOnlyList<DeployableObject> objects = site ? await Services.Deployments.ListObjectsAsync(type, ct) : [];
        IReadOnlyList<CollectionRow> collections = site ? await Services.Collections.ListCollectionsAsync(ct) : [];
        return () =>
        {
            ShowPlans(plans, planNote);
            _collections = collections;
            OnPropertyChanged(nameof(CollectionList));
            if (runsRead == Volatile.Read(ref _runsGeneration) && showClosed == ShowClosedRuns) ShowRuns(runs, showClosed);
            if (type != Type)
            {
                if (site) _ = ReloadObjectsAsync();
            }
            else
            {
                _objectGeneration++;
                _objects = objects;
                ApplyObjectFilter();
            }
            Services.Log.Info(DeploymentShared.LogArea, $"Rings: read {plans.Count} plans, {runs.Count} runs, {objects.Count} objects and {collections.Count} collections.");
        };
    }

    /// <summary>Reads the objects of the current type again; a read that ends after another type was chosen is discarded.</summary>
    public async Task ReloadObjectsAsync()
    {
        var mine = ++_objectGeneration;
        var generation = Shared.Generation;
        var type = Type;
        try { _objectsCts?.Cancel(); } catch (ObjectDisposedException) { }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Shared.VisitToken);
        _objectsCts = cts;
        IsLoading = true;
        try
        {
            var objects = await Task.Run(() => Services.Deployments.ListObjectsAsync(type, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (mine != _objectGeneration || generation != Shared.Generation || type != Type) return;
            _objects = objects;
            ApplyObjectFilter();
            Services.Log.Info(DeploymentShared.LogArea, $"Rings: read {objects.Count} objects.");
        }
        catch (OperationCanceledException)
        {
            if (mine == _objectGeneration && generation == Shared.Generation) Shared.StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _objectGeneration || generation != Shared.Generation) return;
            Shared.StatusMessage = DeploymentShared.PlainMessage(ex, ObjectsFailedText);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the objects could not be read ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_objectsCts, cts)) _objectsCts = null;
            if (mine == _objectGeneration) IsLoading = false;
        }
    }

    protected override void CancelOtherReads()
    {
        try { _objectsCts?.Cancel(); } catch (ObjectDisposedException) { }
        CancelRunReads();
    }

    private async Task LoadProgramsAsync(DeploymentObjectRow row)
    {
        var mine = ++_programGeneration;
        var generation = Shared.Generation;
        try
        {
            var visit = Shared.VisitToken;
            var programs = await Task.Run(() => Services.Deployments.ListProgramsAsync(row.Object.Id, visit), visit);
            if (mine != _programGeneration || generation != Shared.Generation || !ReferenceEquals(SelectedObject, row)) return;
            Programs.Clear();
            foreach (var program in programs) Programs.Add(program);
            if (Programs.Count == 1) SelectedProgram = Programs[0];
        }
        catch (Exception ex)
        {
            if (mine != _programGeneration || generation != Shared.Generation) return;
            FormMessage = DeploymentShared.PlainMessage(ex, DeploymentFormViewModel.DetailsFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the programs could not be read ({DeploymentShared.FailureName(ex)}).");
        }
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

    private void ShowPlans(IReadOnlyList<RingPlan> plans, string? note)
    {
        _plans = plans;
        var selected = SelectedPlan?.Name ?? _planName;
        _refreshedPlan = SelectedPlan?.Plan;
        _applying = true;
        try
        {
            // A plan whose content did not change keeps its row, so the selection, the expansion and the run choices stay.
            var old = Plans.ToList();
            var rows = plans.OrderBy(p => p.IsSeed ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => old.FirstOrDefault(r => RingPlanRow.SameContent(r.Plan, p)) ?? new RingPlanRow(p)).ToList();
            Replace(Plans, rows);
            SelectedPlan = Plans.FirstOrDefault(p => string.Equals(p.Name, selected, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _applying = false;
            _refreshedPlan = null;
        }
        PlanListNote = note is null ? "" : SecretMask.Apply(note);
    }

    private async Task ReloadPlansAsync()
    {
        var generation = Shared.Generation;
        var (plans, note) = await Task.Run(() => (Services.Rings.ListPlans(), Services.Rings.PlanMessage));
        if (generation == Shared.Generation) ShowPlans(plans, note);
    }

    protected override void ReleaseData()
    {
        _objectGeneration++;
        _programGeneration++;
        _objects = [];
        _collections = [];
        _plans = [];
        _applying = true;
        try
        {
            SelectedObject = null;
            SelectedPlan = null;
        }
        finally { _applying = false; }
        Objects.Clear();
        Programs.Clear();
        Plans.Clear();
        ObjectFilter = "";
        ObjectCount = "";
        PlanListNote = "";
        PlansMessage = "";
        OnPropertyChanged(nameof(CollectionList));
        ReleaseRun();
        ReleaseRuns();
        ResetForm();
    }

    // ---- Plans ----------------------------------------------------------------------------------

    [RelayCommand]
    private void NewPlan() => OpenEditor(null, copy: false);

    private bool HasPlan() => SelectedPlan is not null;

    [RelayCommand(CanExecute = nameof(HasPlan))]
    private void EditPlan()
    {
        if (SelectedPlan?.Plan is not { } plan) return;
        if (plan.IsSeed)
        {
            PlansMessage = SeedReadOnly;
            return;
        }
        OpenEditor(plan, copy: false);
    }

    [RelayCommand(CanExecute = nameof(HasPlan))]
    private void CopyPlan()
    {
        if (SelectedPlan?.Plan is { } plan) OpenEditor(plan, copy: true);
    }

    private void OpenEditor(RingPlan? plan, bool copy)
    {
        PlansMessage = "";
        var editor = new RingPlanEditorViewModel(plan, copy, _plans, _collections, SavePlanAsync,
            current => Services.Dialogs.PickCollection("Pick the collection of the ring", _collections, current),
            () => Services.Dialogs.PickCollections("Check the collections: one ring for each", _collections),
            Services.Dialogs.ConfirmDiscardRingPlanChanges);
        Services.Dialogs.ShowRingPlanEditor(editor);
    }

    private async Task<bool> SavePlanAsync(RingPlanEditorViewModel editor, RingPlan plan)
    {
        var original = editor.Original;
        var replace = original is not null && !editor.IsCopy && string.Equals(original.Name, plan.Name, StringComparison.OrdinalIgnoreCase);
        RingPlan? saved;
        try { saved = await Task.Run(() => Services.Rings.SavePlan(plan, replace)); }
        catch (Exception ex)
        {
            editor.ShowMessage(ex is DeploymentServiceException d ? SecretMask.Apply(d.Message) : PlanSaveFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: a plan could not be saved ({ex.GetType().Name}).");
            return false;
        }
        if (saved is null)
        {
            editor.ShowMessage(PlanSaveFailed);
            Services.Log.Warning(DeploymentShared.LogArea, "Rings: a plan was not saved.");
            return false;
        }
        Services.Log.Info(DeploymentShared.LogArea, $"Rings: saved a plan with {saved.Rings.Count} rings.");
        // The new file is written, so a failed removal of the old name leaves two plans; the save itself is done.
        string? oldKept = null;
        if (original is not null && !editor.IsCopy && !replace)
        {
            try { await Task.Run(() => Services.Rings.DeletePlan(original.Name)); }
            catch (Exception ex)
            {
                oldKept = original.Name;
                Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the plan under its old name could not be deleted ({ex.GetType().Name}).");
            }
        }
        _planName = saved.Name;
        await ReloadPlansAsync();
        SelectedPlan = Plans.FirstOrDefault(p => string.Equals(p.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
        PlansMessage = oldKept is null
            ? $"Saved the plan {saved.Name}."
            : $"The plan was saved as {saved.Name}; the old plan {oldKept} could not be removed. Delete it in the list when its file can be changed.";
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasPlan))]
    private async Task DeletePlan()
    {
        if (SelectedPlan?.Plan is not { } plan) return;
        if (plan.IsSeed)
        {
            PlansMessage = SeedReadOnly;
            return;
        }
        if (!await Services.Dialogs.ConfirmDeleteRingPlanAsync(plan.Name)) return;
        try { await Task.Run(() => Services.Rings.DeletePlan(plan.Name)); }
        catch (Exception ex)
        {
            PlansMessage = ex is DeploymentServiceException d ? SecretMask.Apply(d.Message) : PlanDeleteFailed;
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: a plan could not be deleted ({ex.GetType().Name}).");
            return;
        }
        Services.Log.Info(DeploymentShared.LogArea, "Rings: deleted a user plan.");
        await ReloadPlansAsync();
        PlansMessage = $"Deleted the plan {plan.Name}. Runs made with it keep their run files.";
    }
}
