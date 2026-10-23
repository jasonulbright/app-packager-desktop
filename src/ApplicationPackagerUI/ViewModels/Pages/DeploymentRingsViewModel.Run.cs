using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class DeploymentRingsViewModel
{
    public const string ExpandFirst = "Select Expand first: the run uses the expanded rings.";
    public const string ExpandAgain = "The object, the plan or the plan start changed. Select Expand again.";
    public const string ChangedDuringExpand = "The form changed while the rings were expanded. The result was discarded. Select Expand again.";
    public const string ValidateAgain = "The rings changed after the checks ran. Select Validate again.";
    public const string ChangedDuringCheck = "The form changed while the checks ran. The result was discarded. Select Validate again.";
    public const string ExpandFailed = "The rings could not be expanded. The log has the details.";
    public const string ValidateFailed = "The checks could not run. The log has the details.";
    public const string WriteRunning = "Another change is running. Wait until it ends, then try again.";
    public const string StalePlanMarker = "Skipped: the plan is stale";
    public const string StalePlanText = "A ring changed after the checks ran, so it was not created. The form is kept. Select Validate again.";
    public const string PickRing = "Select a ring in the expanded list.";
    public const string WrittenText = "A change was written. Select Expand and Validate again for a new run.";

    private int _formVersion;
    private int _expandGeneration;
    private int _validationGeneration;
    private bool _background;

    /// <summary>The plan, object and start of the shown expansion; the run is made from them and the collections set for this run.</summary>
    private (RingPlan Plan, DeployableObject Object, DateTime Start)? _expanded;

    public ObservableCollection<RingPreviewRow> PreviewRows { get; } = [];
    public ObservableCollection<RingCheckRow> Checks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanStartDateTip))]
    private DateTime? _planStartDate;

    [ObservableProperty] private string _planStartTimeText = "";

    /// <summary>Create only the first ring now; Promote creates the others. It applies to this run only.</summary>
    [ObservableProperty] private bool _holdLaterRings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetCollectionCommand))]
    private RingPreviewRow? _selectedPreviewRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormMessage))]
    private string _formMessage = "";

    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExpandCommand))]
    private bool _isExpanding;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand))]
    private bool _isValidating;

    public bool HasFormMessage => FormMessage.Length > 0;

    public string? PlanStartDateTip => PlanStartDate?.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>The clock of the plan start: the plan's time basis.</summary>
    public string PlanBasisNote => SelectedPlan is { } plan
        ? RingText.IsUtc(plan.Plan.TimeBasedOn)
            ? "The plan uses UTC: the plan start and every ring time are UTC times, the same moment on every client."
            : "The plan uses local time: each client reads the ring times in its own time zone."
        : "";

    /// <summary>True once the operator set the plan start; a plan change then keeps it.</summary>
    private bool _startEdited;
    private bool _settingStart;

    partial void OnPlanStartDateChanged(DateTime? value) => StartChanged();
    partial void OnPlanStartTimeTextChanged(string value) => StartChanged();

    private void StartChanged()
    {
        if (!_settingStart) _startEdited = true;
        FormChanged();
    }

    /// <summary>The next five-minute mark on the clock of the plan's time basis, unless the operator set the start.</summary>
    private void DefaultStart(string? timeBasedOn, bool force = false)
    {
        if (_startEdited && !force) return;
        var now = DeploymentText.Now(Services.Clock, timeBasedOn is not null && RingText.IsUtc(timeBasedOn));
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(5 - now.Minute % 5);
        _settingStart = true;
        try
        {
            PlanStartDate = start.Date;
            PlanStartTimeText = start.ToString(DeploymentText.TimeFormat, CultureInfo.InvariantCulture);
        }
        finally { _settingStart = false; }
        _startEdited = false;
    }
    partial void OnHoldLaterRingsChanged(bool value) => InvalidateChecks(ValidateAgain);

    /// <summary>The page status line text of the last refused Rings action; empty when the line holds no refusal of this tab.</summary>
    private string _refusal = "";

    private int _refusalVersion;

    private void ShowRefusal(string message)
    {
        _refusalVersion++;
        _refusal = message;
        Shared.StatusMessage = message;
    }

    /// <summary>Clears the page status line only while it still shows the refusal of this tab, so a newer message of another action stays.</summary>
    private void ClearRefusal()
    {
        if (_refusal.Length > 0 && Shared.StatusMessage == _refusal) Shared.StatusMessage = "";
        _refusal = "";
    }

    /// <summary>A change of the object, the plan or the start makes the expansion and the checks stale.</summary>
    private void FormChanged()
    {
        _formVersion++;
        FormMessage = "";
        ClearRefusal();
        if (PreviewRows.Count > 0)
        {
            PreviewRows.Clear();
            _expanded = null;
            ValidationMessage = ExpandAgain;
        }
        Checks.Clear();
    }

    private void InvalidateChecks(string message)
    {
        _formVersion++;
        foreach (var row in PreviewRows) row.Checked = false;
        if (Checks.Count == 0) return;
        Checks.Clear();
        ValidationMessage = message;
    }

    private void ResetForm()
    {
        DefaultStart(SelectedPlan?.Plan.TimeBasedOn, force: true);
        HoldLaterRings = false;
        FormMessage = "";
        ValidationMessage = "";
        PreviewRows.Clear();
        Checks.Clear();
        _expanded = null;
    }

    private void ReleaseRun()
    {
        _expandGeneration++;
        _validationGeneration++;
        IsExpanding = false;
        IsValidating = false;
        SelectedPreviewRow = null;
    }

    private bool TryStart(out DateTime start)
    {
        start = default;
        if (PlanStartDate is not { } day) return false;
        if (!TimeSpan.TryParseExact(PlanStartTimeText.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1)) return false;
        start = day.Date + time;
        return true;
    }

    /// <summary>The object, plan and start of the form, read once; null with the reason in <see cref="FormMessage"/>.</summary>
    private (RingPlan Plan, DeployableObject Object, DateTime Start)? ReadForm()
    {
        string? problem = null;
        (RingPlan, DeployableObject, DateTime)? form = null;
        if (SelectedObject is not { } row) problem = DeploymentFormViewModel.PickObject;
        else if (row.Object.Type == DeploymentObjectType.Package && string.IsNullOrEmpty(SelectedProgram)) problem = DeploymentFormViewModel.PickProgram;
        else if (SelectedPlan is not { } plan) problem = PickPlan;
        else if (plan.Plan.Rings.Count == 0) problem = $"The plan {plan.Name} has no ring. Edit the plan first.";
        else if (!TryStart(out var start)) problem = PlanStartDate is null ? "Pick the plan start date." : "Plan start: " + DeploymentFormViewModel.TimeFormatMessage;
        else
        {
            var obj = row.Object.Type == DeploymentObjectType.Package ? row.Object with { Program = SelectedProgram! } : row.Object;
            form = (plan.Plan, obj, start);
        }
        FormMessage = problem ?? "";
        return form;
    }

    private bool CanExpand() => !IsExpanding;

    /// <summary>Expands the plan for the object and the start: the dates and the member count of each ring. Nothing is changed.</summary>
    [RelayCommand(CanExecute = nameof(CanExpand))]
    private async Task Expand()
    {
        if (IsExpanding) return;
        ClearRefusal();
        if (Shared.ConnectionProblem() is { } connection)
        {
            FormMessage = connection;
            return;
        }
        if (ReadForm() is not { } form) return;
        var version = _formVersion;
        var mine = ++_expandGeneration;
        var generation = Shared.Generation;
        IsExpanding = true;
        ValidationMessage = "Expanding the rings.";
        try
        {
            var visit = Shared.VisitToken;
            var rows = await Task.Run(() => Services.Rings.ExpandAsync(form.Plan, form.Object, form.Start, visit), visit);
            if (mine != _expandGeneration || generation != Shared.Generation) return;
            if (version != _formVersion)
            {
                ValidationMessage = ChangedDuringExpand;
                return;
            }
            PreviewRows.Clear();
            foreach (var row in rows.OrderBy(r => r.Index)) PreviewRows.Add(new RingPreviewRow(row));
            Checks.Clear();
            _expanded = form;
            ValidationMessage = $"Expanded {DeploymentText.Count(rows.Count, "ring")}. Select Validate to run the checks.";
            Services.Log.Info(DeploymentShared.LogArea, $"Rings: expanded {rows.Count} rings.");
        }
        catch (OperationCanceledException)
        {
            if (mine == _expandGeneration && generation == Shared.Generation) ValidationMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _expandGeneration || generation != Shared.Generation) return;
            ValidationMessage = DeploymentShared.PlainMessage(ex, ExpandFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the rings could not be expanded ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (mine == _expandGeneration) IsExpanding = false;
        }
    }

    private bool CanSetCollection() => SelectedPreviewRow is not null;

    /// <summary>Sets the collection of the selected ring for this run only; the plan file keeps its own collection.</summary>
    [RelayCommand(CanExecute = nameof(CanSetCollection))]
    private void SetCollection()
    {
        if (SelectedPreviewRow is not { } row)
        {
            FormMessage = PickRing;
            return;
        }
        if (_collections.Count == 0)
        {
            FormMessage = NoCollections;
            return;
        }
        if (Services.Dialogs.PickCollection($"Pick the collection of ring {row.Name} for this run", _collections, row.Row.CollectionId) is not { } picked) return;
        if (picked.IsBuiltIn)
        {
            FormMessage = DeploymentFormViewModel.BuiltInRefused;
            return;
        }
        var other = PreviewRows.FirstOrDefault(r => !ReferenceEquals(r, row) && string.Equals(r.Row.CollectionId, picked.CollectionId, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            FormMessage = $"Ring {other.Name} already uses {picked.Name} ({picked.CollectionId}). Each ring needs its own collection.";
            return;
        }
        row.Row = row.Row with { CollectionId = picked.CollectionId, CollectionName = picked.Name, MemberCount = picked.MemberCount, Checks = [] };
        row.Edited = true;
        FormMessage = "";
        InvalidateChecks(ValidateAgain);
    }

    /// <summary>
    /// The CreateRun request of the shown expansion, read once: the plan with the hold choice and the
    /// collections set for this run. Null with the reason in <see cref="FormMessage"/>.
    /// </summary>
    public RingChange? BuildRunRequest()
    {
        if (_expanded is not { } expanded || PreviewRows.Count == 0)
        {
            FormMessage = ExpandFirst;
            return null;
        }
        var rows = PreviewRows.Select(r => r.Row).ToList();
        var rings = expanded.Plan.Rings.Select((ring, i) =>
            i < rows.Count && !string.Equals(ring.CollectionId, rows[i].CollectionId, StringComparison.OrdinalIgnoreCase)
                ? ring with { CollectionId = rows[i].CollectionId, CollectionName = rows[i].CollectionName }
                : ring).ToList();
        var plan = expanded.Plan with { HoldLaterRings = HoldLaterRings, Rings = rings };
        FormMessage = "";
        return new RingChange(Kind: RingChangeKind.CreateRun, Plan: plan, Object: expanded.Object, Rows: rows, PlanStart: expanded.Start, RunId: null, RingIndex: null,
            RequiredConfirmed: false, BackdatedAcknowledged: false, CreateNowContentDistributing: false, RingChanges: [], Impact: [], Checks: [], Fingerprint: null);
    }

    /// <summary>Lists the checks of the run and of each ring; a failed or unknown check blocks, a warning does not.</summary>
    private void ShowChecks(RingChange prepared)
    {
        Checks.Clear();
        foreach (var check in DeploymentText.Ordered(RingText.RunOnly(prepared.Checks, prepared.Rows)))
            Checks.Add(new RingCheckRow(Ring: "Run", Check: DeploymentText.CheckLabel(check.Name), Result: check.Result, Message: DeploymentText.CheckMessage(check, prepared.Object?.Type)));
        foreach (var ring in prepared.Rows.OrderBy(r => r.Index))
        {
            if (PreviewRows.FirstOrDefault(p => p.Index == ring.Index) is { } preview)
            {
                preview.Row = preview.Row with { Checks = ring.Checks, MemberCount = ring.MemberCount };
                preview.Checked = true;
            }
            foreach (var check in DeploymentText.Ordered(ring.Checks))
                Checks.Add(new RingCheckRow(Ring: ring.Name, Check: DeploymentText.CheckLabel(check.Name), Result: check.Result, Message: DeploymentText.CheckMessage(check, prepared.Object?.Type)));
        }
        var hold = prepared.Plan?.HoldLaterRings ?? HoldLaterRings;
        var blocking = RingText.Blocking(prepared, RingText.CreatedNow(prepared.Rows, hold)).Count;
        var heldBlocking = prepared.Rows.Except(RingText.CreatedNow(prepared.Rows, hold)).Sum(r => r.Checks.Count(DeploymentText.Blocks));
        var warnings = Checks.Count(c => c.Result == DeploymentText.Warn);
        ValidationMessage = blocking > 0 ? $"{DeploymentText.Count(blocking, "check")} did not pass. The run is blocked."
            : heldBlocking > 0 ? $"No check blocks the rings this run creates now. {DeploymentText.Count(heldBlocking, "check")} of the held rings did not pass; they run again at Promote."
            : warnings == 0 ? "Every check passed."
            : $"No check blocks the run. {DeploymentText.Count(warnings, "check")} with a warning: read the messages.";
    }

    private bool CanValidate() => !IsValidating;

    /// <summary>Runs the checks of each ring and the plan rules on the shown expansion; a result is discarded when the form changed while it ran.</summary>
    [RelayCommand(CanExecute = nameof(CanValidate))]
    private async Task Validate()
    {
        if (IsValidating) return;
        ClearRefusal();
        if (Shared.ConnectionProblem() is { } connection)
        {
            FormMessage = connection;
            return;
        }
        if (BuildRunRequest() is not { } request) return;
        var version = _formVersion;
        var mine = ++_validationGeneration;
        var generation = Shared.Generation;
        IsValidating = true;
        ValidationMessage = "Running the checks.";
        try
        {
            var visit = Shared.VisitToken;
            var prepared = await Task.Run(() => Services.Rings.PrepareAsync(request, visit), visit);
            if (mine != _validationGeneration || generation != Shared.Generation) return;
            if (version != _formVersion)
            {
                Checks.Clear();
                ValidationMessage = ChangedDuringCheck;
                return;
            }
            ShowChecks(prepared);
            Services.Log.Info(DeploymentShared.LogArea, $"Rings: checks ran: {Checks.Count(c => c.Result == DeploymentText.Pass)} of {Checks.Count} passed.");
        }
        catch (OperationCanceledException)
        {
            if (mine == _validationGeneration && generation == Shared.Generation) ValidationMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _validationGeneration || generation != Shared.Generation) return;
            ValidationMessage = DeploymentShared.PlainMessage(ex, ValidateFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the checks could not run ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (mine == _validationGeneration) IsValidating = false;
        }
    }

    private bool CanWrite() => !Shared.IsWriting;

    /// <summary>
    /// Reads the shown expansion once, prepares the run and shows the plan made from the prepared
    /// change; a change of the form while the state is read does not change the plan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task CreateRun()
    {
        if (Shared.IsWriting)
        {
            FormMessage = _background ? DeploymentShared.BackgroundRunText : WriteRunning;
            return;
        }
        if (BuildRunRequest() is not { } request) return;
        var version = _formVersion;
        var run = await RunRingPlanAsync(request, prepared => RingText.BuildCreateRunPlan(prepared, Services.Clock),
            prepared =>
            {
                if (version == _formVersion) ShowChecks(prepared);
            },
            message => FormMessage = message);
        if (run is not { Outcome.Confirmed: true } done) return;
        var stale = done.Plan.Outcomes.SelectMany(o => o.RingOutcomes).Any(o => o.Message.StartsWith(StalePlanMarker, StringComparison.Ordinal));
        _validationGeneration++;
        IsValidating = false;
        InvalidateChecks(stale ? StalePlanText : WrittenText);
        if (stale) FormMessage = StalePlanText;
        await AfterRingWriteAsync();
    }

    /// <summary>
    /// Reads the current state of the request, builds the plan from the prepared change and shows it.
    /// One write runs at a time on the page; a second one is refused with a plain message. The writer
    /// applies only the change of the plan the operator confirms. Null when no plan ran.
    /// </summary>
    private async Task<(RingRunPlan Plan, ChangePlanOutcome Outcome)?> RunRingPlanAsync(RingChange request, Func<RingChange, (RingRunPlan? Plan, string? Problem)> build,
        Action<RingChange>? prepared, Action<string>? report)
    {
        void Refuse(string message)
        {
            ShowRefusal(message);
            report?.Invoke(message);
        }

        ClearRefusal();
        RunsMessage = "";
        if (Shared.IsWriting)
        {
            Refuse(_background ? DeploymentShared.BackgroundRunText : WriteRunning);
            return null;
        }
        Shared.IsWriting = true;
        var detached = false;
        try
        {
            var generation = Shared.Generation;
            if (Shared.ConnectionProblem() is { } problem)
            {
                Refuse(problem);
                return null;
            }
            Shared.StatusMessage = "Reading the current state.";
            var visit = Shared.VisitToken;
            RingChange ready;
            try { ready = await Task.Run(() => Services.Rings.PrepareAsync(request, visit), visit); }
            catch (OperationCanceledException) when (visit.IsCancellationRequested) { return null; }
            catch (Exception ex)
            {
                Services.Log.Warning(DeploymentShared.LogArea, $"Rings: the state before a write could not be read ({DeploymentShared.FailureName(ex)}).");
                if (generation == Shared.Generation) Refuse(DeploymentShared.PlainMessage(ex, DeploymentShared.PrepareFailed));
                return null;
            }
            if (generation != Shared.Generation) return null;
            Shared.StatusMessage = "";
            prepared?.Invoke(ready);
            var (plan, refusal) = build(ready);
            if (plan is null)
            {
                if (refusal is not null) Refuse(SecretMask.Apply(refusal));
                return null;
            }
            var outcome = Services.Plans.Show(plan.Plan, Services.RingWriter.RunFor(plan));
            if (outcome.Result?.Stop == ChangePlanStop.Detached)
            {
                detached = true;
                _ = WaitForBackgroundRunAsync();
                if (generation == Shared.Generation) Refuse(DeploymentShared.BackgroundRunText);
                return null;
            }
            if (generation != Shared.Generation) return null;
            if (!outcome.Confirmed || outcome.Result is not { } result) Shared.StatusMessage = DeploymentShared.NotConfirmed;
            else Shared.ShowExternalResult(plan.Plan.Title, result);
            return (plan, outcome);
        }
        finally
        {
            if (!detached) Shared.IsWriting = false;
        }
    }

    /// <summary>Keeps every write blocked until the run that a closed plan window left working ends, then reads the runs again.</summary>
    private async Task WaitForBackgroundRunAsync()
    {
        _background = true;
        try { await Services.RingWriter.Running; }
        catch (Exception ex)
        {
            Services.Log.Warning(DeploymentShared.LogArea, $"Rings: a run that continued in the background failed ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            _background = false;
            Shared.IsWriting = false;
            if (Shared.IsActive) Shared.StatusMessage = DeploymentShared.BackgroundRunEndedText;
        }
        if (Shared.IsActive && IsLoaded) await AfterRingWriteAsync();
    }

    /// <summary>Reads the runs, the history and the DP status again after a ring write.</summary>
    private async Task AfterRingWriteAsync()
    {
        if (!Shared.IsActive) return;
        await LoadRunsAsync();
        await Shared.AfterWriteAsync();
    }
}
