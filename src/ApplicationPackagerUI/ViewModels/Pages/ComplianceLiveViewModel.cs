using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Live CI/CB tab: the configuration items, the baselines and the deployments of the selected
/// baseline with their compliance counts. One read gets all three lists; selecting a row reads
/// nothing. Remediation, Redeploy, Reassign and Delete of deployments run through plans.
/// </summary>
public sealed partial class ComplianceLiveViewModel : ComplianceTabViewModel
{
    public ComplianceLiveViewModel(ComplianceShared shared) : base(shared)
    {
        shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComplianceShared.IsWriting)) NotifyActions();
        };
    }

    public const string ReadFailedText = "The configuration items and baselines could not be read. The log has the details.";
    public const string PickDeployments = "Select one or more deployments.";
    public const string PickOneDeployment = "Select one deployment to reassign.";
    public const string PickTarget = "Check one collection.";
    public const string SameCollection = "The deployment already targets this collection.";
    public const string AlreadyInMode = "The selected deployments already have this mode.";
    public const string RedeployNote = "ConfigMgr cannot hold two deployments of one baseline to one collection, so the deployment is removed first and created again. When the create fails, the page creates it again from the captured values; when that fails too, the result says so and keeps the captured values for Undo.";
    public const string DeleteNote = "Undo creates the deployment again from the captured values; the recreated deployment has a new ID.";
    public const string ReassignNote = "The deployment on the new collection is created first; the old one is removed only after it exists.";

    private IReadOnlyList<ConfigurationItemRow> _items = [];
    private IReadOnlyList<BaselineRow> _baselines = [];
    private IReadOnlyList<BaselineDeploymentRow> _deployments = [];
    private IReadOnlyList<DeploymentGridRow> _selectedDeployments = [];
    private bool _applying;
    private bool _picking;

    protected override string ReadFailed => ReadFailedText;

    public ObservableCollection<ConfigurationItemGridRow> Items { get; } = [];
    public ObservableCollection<BaselineGridRow> Baselines { get; } = [];
    public ObservableCollection<DeploymentGridRow> Deployments { get; } = [];

    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private bool _showDeployments = true;
    [ObservableProperty] private ConfigurationItemGridRow? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeploymentsTitle))]
    private BaselineGridRow? _selectedBaseline;

    public string DeploymentsTitle => SelectedBaseline is { } b ? $"Deployments of {b.Name}" : "Deployments of every baseline";

    public IReadOnlyList<DeploymentGridRow> SelectedDeployments => _selectedDeployments;

    /// <summary>Raised when a filter or the panel choice changes, so the section saves its choices.</summary>
    public event EventHandler? ChoicesChanged;

    /// <summary>Raised when the operator asks to edit the settings of the selected configuration item.</summary>
    public event EventHandler<ConfigurationItemRow>? EditSettingsRequested;

    partial void OnTextFilterChanged(string value)
    {
        if (_applying) return;
        ApplyFilter();
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowDeploymentsChanged(bool value) => ChoicesChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSelectedBaselineChanged(BaselineGridRow? value) => ShowDeploymentsOf();

    public void ApplyChoices(ComplianceSettings saved)
    {
        _applying = true;
        try
        {
            TextFilter = saved.LiveText ?? "";
            ShowDeployments = !saved.LiveDeploymentsHidden;
        }
        finally { _applying = false; }
    }

    public void StoreChoices(ComplianceSettings choices)
    {
        choices.LiveText = TextFilter;
        choices.LiveDeploymentsHidden = !ShowDeployments;
    }

    public void SetSelectedDeployments(IEnumerable<DeploymentGridRow> rows)
    {
        _selectedDeployments = rows.ToList();
        NotifyActions();
    }

    private void NotifyActions()
    {
        EnableRemediationCommand.NotifyCanExecuteChanged();
        DisableRemediationCommand.NotifyCanExecuteChanged();
        RedeployCommand.NotifyCanExecuteChanged();
        ReassignCommand.NotifyCanExecuteChanged();
        DeleteDeploymentsCommand.NotifyCanExecuteChanged();
    }

    protected override async Task<Action> ReadAsync(CancellationToken ct)
    {
        var items = await Services.Compliance.ListConfigurationItemsAsync(ct);
        var baselines = await Services.Compliance.ListBaselinesAsync(ct);
        var deployments = await Services.Compliance.ListDeploymentsAsync(null, ct);
        return () =>
        {
            _items = items;
            _baselines = baselines;
            _deployments = deployments;
            ApplyFilter();
            Services.Log.Info(ComplianceShared.LogArea, $"Read {items.Count} configuration items, {baselines.Count} baselines and {deployments.Count} deployments.");
        };
    }

    protected override void ReleaseData()
    {
        _items = [];
        _baselines = [];
        _deployments = [];
        SelectedItem = null;
        SelectedBaseline = null;
        SetSelectedDeployments([]);
        Items.Clear();
        Baselines.Clear();
        Deployments.Clear();
        CountText = "";
    }

    private void ApplyFilter()
    {
        var text = TextFilter.Trim();
        var itemId = SelectedItem?.CiId;
        var baselineId = SelectedBaseline?.BaselineId;
        Replace(Items, _items.Where(i => text.Length == 0 || Contains(i.Name, text) || Contains(i.CiId, text))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i => new ConfigurationItemGridRow(i)).ToList());
        Replace(Baselines, _baselines.Where(b => text.Length == 0 || Contains(b.Name, text) || Contains(b.BaselineId, text) || Contains(b.CatalogId, text))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(b => new BaselineGridRow(b)).ToList());
        SelectedItem = Items.FirstOrDefault(i => i.CiId == itemId);
        var baseline = Baselines.FirstOrDefault(b => b.BaselineId == baselineId);
        if (!ReferenceEquals(baseline, SelectedBaseline)) SelectedBaseline = baseline;
        else ShowDeploymentsOf();
        CountText = $"{ComplianceText.Count(_items.Count, "configuration item")}, {ComplianceText.Count(_baselines.Count, "baseline")}, {ComplianceText.Count(_deployments.Count, "deployment")}";
    }

    private void ShowDeploymentsOf()
    {
        var id = SelectedBaseline?.BaselineId;
        var keep = _selectedDeployments.Select(d => d.Row.DeploymentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Replace(Deployments, _deployments.Where(d => id is null || string.Equals(d.BaselineId, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.BaselineName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.CollectionName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeploymentGridRow(d)).ToList());
        SetSelectedDeployments(Deployments.Where(d => keep.Contains(d.Row.DeploymentId)));
    }

    [RelayCommand]
    private void EditSettings()
    {
        if (SelectedItem is { } item) EditSettingsRequested?.Invoke(this, item.Row);
    }

    private bool HasSelection() => _selectedDeployments.Count > 0 && !Shared.IsWriting;

    private bool HasOneSelection() => _selectedDeployments.Count == 1 && !Shared.IsWriting;

    private static string Describe(ComplianceChange change) =>
        change.DeploymentBefore is { } d ? $"{d.BaselineName} on {ComplianceText.Target(d.CollectionName, d.CollectionId)}" : change.Kind.ToString();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EnableRemediation() => SetRemediationAsync(true);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DisableRemediation() => SetRemediationAsync(false);

    private async Task SetRemediationAsync(bool remediate)
    {
        var rows = _selectedDeployments.Where(d => d.Row.RemediateNoncompliant != remediate).Select(d => d.Row).ToList();
        if (rows.Count == 0)
        {
            Shared.StatusMessage = _selectedDeployments.Count == 0 ? PickDeployments : AlreadyInMode;
            return;
        }
        var run = await Shared.RunPlanAsync(rows.Select(r => ComplianceRequests.SetRemediation(r, remediate)).ToList(), prepared =>
        {
            var entries = prepared.Select(change =>
            {
                var d = change.DeploymentBefore!;
                var detail = string.Join(" ", new[]
                {
                    $"Mode now: {ComplianceText.Mode(d.RemediateNoncompliant)}. New mode: {ComplianceText.Mode(remediate)}.",
                    remediate ? "Clients change the registry values of the baseline's configuration items on every device of the collection." : "Clients report compliance and change nothing.",
                    $"Undo sets {ComplianceText.Mode(d.RemediateNoncompliant)} back."
                }.Concat(change.Impact));
                return CompliancePlanEntry.Write(new ChangePlanItem(remediate ? "Enable remediation" : "Disable remediation", Describe(change), detail), change);
            }).ToList();
            var devices = prepared.Select(c => c.DeploymentBefore!).DistinctBy(d => d.CollectionId).Sum(d => d.Compliant + d.NonCompliant + d.Error + d.Unknown);
            var collections = prepared.Select(c => c.DeploymentBefore!.CollectionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var title = remediate ? "Enable remediation" : "Disable remediation";
            return (new CompliancePlan(title, title, $"{title} on {ComplianceText.Count(entries.Count, "deployment")}", entries,
                extraConfirmation: remediate ? $"I understand that remediation changes registry values on every device of {ComplianceText.Count(collections, "collection")} ({ComplianceText.Count(devices, "device")} with a compliance state now)." : null), null);
        }, Describe);
        if (run is { Outcome.Confirmed: true }) await Shared.AfterWriteAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Redeploy()
    {
        var rows = _selectedDeployments.Select(d => d.Row).ToList();
        var run = await Shared.RunPlanAsync(rows.Select(ComplianceRequests.Redeploy).ToList(), prepared =>
        {
            var entries = prepared.Select(change =>
            {
                var d = change.DeploymentBefore!;
                var detail = string.Join(" ", new[] { $"Removes the deployment ({ComplianceText.Mode(d.RemediateNoncompliant)}, schedule {d.Schedule}) and creates it again with the same values.", RedeployNote }
                    .Concat(change.Impact));
                return CompliancePlanEntry.Write(new ChangePlanItem("Redeploy", Describe(change), detail), change);
            }).ToList();
            return (new CompliancePlan("Redeploy", "Redeploy baselines", $"Redeploy {ComplianceText.Count(entries.Count, "deployment")}", entries, isDestructive: true), null);
        }, Describe);
        if (run is { Outcome.Confirmed: true }) await Shared.AfterWriteAsync();
    }

    [RelayCommand(CanExecute = nameof(HasOneSelection))]
    private async Task Reassign()
    {
        if (_picking || _selectedDeployments is not [var selected]) return;
        _picking = true;
        try
        {
            var generation = Shared.Generation;
            var collections = await Shared.DeviceCollectionsAsync();
            if (collections is null || generation != Shared.Generation) return;
            var deployment = selected.Row;
            var picker = new CollectionPickerViewModel($"Reassign {deployment.BaselineName}", $"Check the collection that gets the deployment instead of {ComplianceText.Target(deployment.CollectionName, deployment.CollectionId)}.",
                "Show plan", collections.Where(c => !c.IsBuiltIn), async p =>
                {
                    if (p.CheckedCount != 1)
                    {
                        p.ShowMessage(PickTarget);
                        return false;
                    }
                    var target = p.Checked[0];
                    if (string.Equals(target.CollectionId, deployment.CollectionId, StringComparison.OrdinalIgnoreCase))
                    {
                        p.ShowMessage(SameCollection);
                        return false;
                    }
                    var run = await Shared.RunPlanAsync([ComplianceRequests.Reassign(deployment, target.CollectionId)], prepared =>
                    {
                        var change = prepared[0];
                        var detail = string.Join(" ", new[]
                        {
                            $"Moves the deployment from {ComplianceText.Target(deployment.CollectionName, deployment.CollectionId)} to {ComplianceText.Target(target.Name, target.CollectionId)} ({ComplianceText.Count(target.MemberCount, "member")} now).",
                            $"Mode stays {ComplianceText.Mode(deployment.RemediateNoncompliant)}.", ReassignNote, "Undo moves it back."
                        }.Concat(change.Impact));
                        return (new CompliancePlan("Reassign", $"Reassign {deployment.BaselineName}", "Reassign deployment",
                            [CompliancePlanEntry.Write(new ChangePlanItem("Reassign", Describe(change), detail), change)],
                            extraConfirmation: deployment.RemediateNoncompliant
                                ? $"I understand that remediation changes registry values on every device of {ComplianceText.Target(target.Name, target.CollectionId)} ({ComplianceText.Count(target.MemberCount, "device")} now)."
                                : null), null);
                    }, Describe, p.ShowMessage);
                    if (run is not { Outcome.Confirmed: true }) return false;
                    await Shared.AfterWriteAsync();
                    return true;
                });
            Services.Dialogs.ShowPicker(picker);
        }
        finally { _picking = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteDeployments()
    {
        var rows = _selectedDeployments.Select(d => d.Row).ToList();
        var run = await Shared.RunPlanAsync(rows.Select(ComplianceRequests.Delete).ToList(), prepared =>
        {
            var entries = prepared.Select(change =>
            {
                var d = change.DeploymentBefore!;
                var detail = string.Join(" ", new[]
                {
                    $"Removes the deployment ({ComplianceText.Mode(d.RemediateNoncompliant)}). Compliance now: {d.Compliant} compliant, {d.NonCompliant} non-compliant, {d.Error} error, {d.Unknown} unknown.",
                    DeleteNote
                }.Concat(change.Impact));
                return CompliancePlanEntry.Write(new ChangePlanItem("Delete", Describe(change), detail), change);
            }).ToList();
            return (new CompliancePlan("Delete deployments", "Delete baseline deployments", $"Delete {ComplianceText.Count(entries.Count, "deployment")}", entries, isDestructive: true), null);
        }, Describe);
        if (run is { Outcome.Confirmed: true }) await Shared.AfterWriteAsync();
    }
}
