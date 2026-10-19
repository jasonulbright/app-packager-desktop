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
    public const string RedeployNote = "The new deployment is created first and the old one is removed after it exists, so the collection always has the deployment. The new deployment has a new ID. Undo is not offered after a full redeploy: the deployment holds the same values.";
    public const string DeleteNote = "Undo creates the deployment again from the captured values; the recreated deployment has a new ID.";
    public const string ReassignNote = "The deployment on the new collection is created first; the old one is removed only after it exists. If the create fails, the old one stays. Undo moves the deployment back; the deployments have new IDs.";

    public static string RemediationCheck(int collections, int devices) =>
        $"I understand that remediation changes registry values on every device of {ComplianceText.Count(collections, "collection")} ({ComplianceText.Count(devices, "device")} with a compliance state now).";

    public static string RemediationCheckWithoutCounts(int collections) =>
        $"I understand that remediation changes registry values on every device of {ComplianceText.Count(collections, "collection")} (the device count is not known: ConfigMgr has not summarized every deployment).";

    /// <summary>The second check box of a plan whose changes turn remediation on or keep it on; null when none does.</summary>
    public static string? RemediationConfirmation(IReadOnlyList<ComplianceChange> changes)
    {
        var remediating = changes.Where(c => ComplianceWriter.NeedsRemediationConfirmation(c) && c.DeploymentBefore is not null).Select(c => c.DeploymentBefore!).ToList();
        if (remediating.Count == 0) return null;
        var collections = remediating.Select(d => d.CollectionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (remediating.Any(d => !d.CountsKnown)) return RemediationCheckWithoutCounts(collections);
        var devices = remediating.DistinctBy(d => d.CollectionId, StringComparer.OrdinalIgnoreCase).Sum(d => d.Compliant + d.NonCompliant + d.Error + d.Unknown);
        return RemediationCheck(collections, devices);
    }

    /// <summary>The page lines of a plan entry, then the Impact lines of the service, or the page notes when the service gives none.</summary>
    private static string Detail(string summary, ComplianceChange change, params string[] notes) =>
        string.Join(" ", new[] { summary }.Concat(change.Impact.Count > 0 ? change.Impact : notes).Where(l => l.Length > 0));

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
        Replace(Deployments, _deployments.Where(d => id is null || string.Equals(d.BaselineId, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.BaselineName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.CollectionName, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeploymentGridRow(d)).ToList());
        // The grid deselects a row that Replace swaps for a new one; only the rows it still holds stay selected.
        SetSelectedDeployments(_selectedDeployments.Where(s => Deployments.Any(d => ReferenceEquals(d, s))));
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
                var detail = Detail($"Mode now: {ComplianceText.Mode(d.RemediateNoncompliant)}. New mode: {ComplianceText.Mode(remediate)}.", change,
                    remediate ? "Clients change the registry values of the baseline's configuration items on every device of the collection." : "Clients report compliance and change nothing.",
                    $"Undo sets {ComplianceText.Mode(d.RemediateNoncompliant)} back.");
                return CompliancePlanEntry.Write(new ChangePlanItem(remediate ? "Enable remediation" : "Disable remediation", Describe(change), detail), change);
            }).ToList();
            var title = remediate ? "Enable remediation" : "Disable remediation";
            return (new CompliancePlan(title, title, $"{title} on {ComplianceText.Count(entries.Count, "deployment")}", entries,
                extraConfirmation: RemediationConfirmation(prepared)), null);
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
                var detail = Detail($"Creates the deployment again ({ComplianceText.Mode(d.RemediateNoncompliant)}, schedule {d.Schedule}), then removes the old one. {ComplianceText.DeploymentSettings(d)}",
                    change, RedeployNote);
                return CompliancePlanEntry.Write(new ChangePlanItem("Redeploy", Describe(change), detail), change);
            }).ToList();
            return (new CompliancePlan("Redeploy", "Redeploy baselines", $"Redeploy {ComplianceText.Count(entries.Count, "deployment")}", entries, isDestructive: true,
                hasUndo: false, extraConfirmation: RemediationConfirmation(prepared)), null);
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
            Shared.ForgetCollections();
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
                        var now = change.DeploymentBefore ?? deployment;
                        var detail = Detail(
                            $"Moves the deployment from {ComplianceText.Target(now.CollectionName, now.CollectionId)} to {ComplianceText.Target(target.Name, target.CollectionId)} ({ComplianceText.Count(target.MemberCount, "member")} now). Mode stays {ComplianceText.Mode(now.RemediateNoncompliant)}. {ComplianceText.DeploymentSettings(now)}",
                            change, ReassignNote);
                        return (new CompliancePlan("Reassign", $"Reassign {now.BaselineName}", "Reassign deployment",
                            [CompliancePlanEntry.Write(new ChangePlanItem("Reassign", Describe(change), detail), change)],
                            extraConfirmation: ComplianceWriter.NeedsRemediationConfirmation(change)
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
                var detail = Detail(
                    $"Removes the deployment ({ComplianceText.Mode(d.RemediateNoncompliant)}). " + (d.CountsKnown
                        ? $"Compliance now: {d.Compliant} compliant, {d.NonCompliant} non-compliant, {d.Error} error, {d.Unknown} unknown."
                        : "Compliance now: not known; ConfigMgr has not summarized this deployment."),
                    change, DeleteNote);
                return CompliancePlanEntry.Write(new ChangePlanItem("Delete", Describe(change), detail), change);
            }).ToList();
            return (new CompliancePlan("Delete deployments", "Delete baseline deployments", $"Delete {ComplianceText.Count(entries.Count, "deployment")}", entries, isDestructive: true), null);
        }, Describe);
        if (run is { Outcome.Confirmed: true }) await Shared.AfterWriteAsync();
    }
}
