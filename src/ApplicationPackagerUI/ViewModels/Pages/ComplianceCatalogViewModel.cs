using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Catalog tab: the shipped catalog entries with facet filters and a detail panel, and
/// Deploy, which runs the entry's builder through a plan. Detect only is the default; a deploy
/// with remediation needs the plan's second check box.
/// </summary>
public sealed partial class ComplianceCatalogViewModel(ComplianceShared shared) : ComplianceTabViewModel(shared)
{
    public const string ReadFailedText = "The catalog could not be read. The log has the details.";
    public const string PickOne = "Check one collection.";
    public const string AllText = "All";

    public static string RemediationCheck(int collections, int devices, bool reboot) =>
        $"I understand that remediation changes registry values on every device of {ComplianceText.Count(collections, "collection")} "
        + $"({ComplianceText.Count(devices, "device")} now){(reboot ? " and that the devices need a restart" : "")}.";

    public const string NoTargetRemediationCheck =
        "I understand that the configuration items are made with remediation on: a later deployment of the baselines changes registry values on the devices.";

    private IReadOnlyList<CatalogEntry> _entries = [];
    private bool _applying;
    private bool _deploying;

    protected override bool NeedsSite => false;
    protected override string ReadFailed => ReadFailedText;

    public ObservableCollection<CatalogGridRow> Rows { get; } = [];
    public ObservableCollection<string> Subcategories { get; } = [AllText];
    public ObservableCollection<string> Severities { get; } = [AllText];
    public ObservableCollection<string> Statuses { get; } = [AllText];
    public ObservableCollection<string> OsChoices { get; } = [AllText];
    public ObservableCollection<PropertyRow> Details { get; } = [];

    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _referenceFilter = "";
    [ObservableProperty] private string _subcategoryFilter = AllText;
    [ObservableProperty] private string _severityFilter = AllText;
    [ObservableProperty] private string _statusFilter = AllText;
    [ObservableProperty] private string _osFilter = AllText;
    [ObservableProperty] private string _rowCount = "";
    [ObservableProperty] private string _catalogMessage = "";
    [ObservableProperty] private bool _showDetails = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyCanExecuteChangedFor(nameof(DeployCommand))]
    private CatalogGridRow? _selectedRow;

    public bool HasSelectedRow => SelectedRow is not null;
    public bool HasCatalogMessage => CatalogMessage.Length > 0;

    /// <summary>Raised when a filter or the details choice changes, so the section saves its choices.</summary>
    public event EventHandler? ChoicesChanged;

    partial void OnCatalogMessageChanged(string value) => OnPropertyChanged(nameof(HasCatalogMessage));
    partial void OnTextFilterChanged(string value) => FilterChanged();
    partial void OnReferenceFilterChanged(string value) => FilterChanged();
    partial void OnSubcategoryFilterChanged(string value) => FilterChanged();
    partial void OnSeverityFilterChanged(string value) => FilterChanged();
    partial void OnStatusFilterChanged(string value) => FilterChanged();
    partial void OnOsFilterChanged(string value) => FilterChanged();
    partial void OnShowDetailsChanged(bool value) => ChoicesChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSelectedRowChanged(CatalogGridRow? value) => FillDetails(value?.Entry);

    private void FilterChanged()
    {
        if (_applying) return;
        ApplyFilter();
        ChoicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyChoices(ComplianceSettings saved)
    {
        _applying = true;
        try
        {
            TextFilter = saved.CatalogText ?? "";
            ReferenceFilter = saved.CatalogReference ?? "";
            SubcategoryFilter = Choice(saved.CatalogSubcategory);
            SeverityFilter = Choice(saved.CatalogSeverity);
            StatusFilter = Choice(saved.CatalogStatus);
            OsFilter = Choice(saved.CatalogOs);
            ShowDetails = !saved.CatalogDetailsHidden;
        }
        finally { _applying = false; }
        ApplyFilter();
    }

    private static string Choice(string? value) => string.IsNullOrEmpty(value) ? AllText : value;

    private static string Stored(string value) => value == AllText ? "" : value;

    public void StoreChoices(ComplianceSettings choices)
    {
        choices.CatalogText = TextFilter;
        choices.CatalogReference = ReferenceFilter;
        choices.CatalogSubcategory = Stored(SubcategoryFilter);
        choices.CatalogSeverity = Stored(SeverityFilter);
        choices.CatalogStatus = Stored(StatusFilter);
        choices.CatalogOs = Stored(OsFilter);
        choices.CatalogDetailsHidden = !ShowDetails;
    }

    protected override Task<Action> ReadAsync(CancellationToken ct)
    {
        var entries = Services.Catalog.All();
        var message = Services.Catalog.Message;
        return Task.FromResult<Action>(() =>
        {
            _entries = entries;
            CatalogMessage = message ?? "";
            Fill(Subcategories, entries.Select(e => e.Subcategory), SubcategoryFilter);
            Fill(Severities, entries.Select(e => e.Severity), SeverityFilter);
            Fill(Statuses, entries.Select(e => e.Status), StatusFilter);
            Fill(OsChoices, entries.SelectMany(e => e.ClientOs.Concat(e.ServerOs)), OsFilter);
            ApplyFilter();
            Services.Log.Info(ComplianceShared.LogArea, $"Read {entries.Count} catalog entries.");
        });
    }

    private static void Fill(ObservableCollection<string> choices, IEnumerable<string> values, string keep)
    {
        var wanted = new List<string> { AllText };
        wanted.AddRange(values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        if (keep != AllText && !wanted.Contains(keep, StringComparer.OrdinalIgnoreCase)) wanted.Add(keep);
        Replace(choices, wanted);
    }

    protected override void ReleaseData()
    {
        _entries = [];
        SelectedRow = null;
        Rows.Clear();
        Details.Clear();
        RowCount = "";
        CatalogMessage = "";
    }

    private bool Matches(CatalogEntry entry)
    {
        if (SubcategoryFilter != AllText && !string.Equals(entry.Subcategory, SubcategoryFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (SeverityFilter != AllText && !string.Equals(entry.Severity, SeverityFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (StatusFilter != AllText && !string.Equals(entry.Status, StatusFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (OsFilter != AllText && !entry.ClientOs.Concat(entry.ServerOs).Contains(OsFilter, StringComparer.OrdinalIgnoreCase)) return false;
        var reference = ReferenceFilter.Trim();
        if (reference.Length > 0 && !entry.Cves.Any(c => Contains(c, reference))
            && !entry.TenablePlugins.Any(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture) == reference)) return false;
        var text = TextFilter.Trim();
        return text.Length == 0 || Contains(entry.DisplayName, text) || Contains(entry.Id, text) || Contains(entry.Description, text)
            || entry.RegistrySurface.Any(r => Contains(r.KeyPath, text) || r.Values.Any(v => Contains(v, text)));
    }

    private void ApplyFilter()
    {
        var selectedId = SelectedRow?.Entry.Id;
        var wanted = _entries.Where(Matches).OrderBy(e => e.Subcategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).Select(e => new CatalogGridRow(e)).ToList();
        Replace(Rows, wanted);
        SelectedRow = Rows.FirstOrDefault(r => r.Entry.Id == selectedId);
        RowCount = Rows.Count == _entries.Count ? ComplianceText.Count(_entries.Count, "entry", "entries") : $"{Rows.Count} of {ComplianceText.Count(_entries.Count, "entry", "entries")}";
    }

    private void FillDetails(CatalogEntry? entry)
    {
        Details.Clear();
        if (entry is null) return;
        Details.Add(new PropertyRow("Description", entry.Description));
        Details.Add(new PropertyRow("ID and version", $"{entry.Id} {entry.Version}"));
        Details.Add(new PropertyRow("Severity and status", $"{entry.Severity}, {entry.Status}"));
        Details.Add(new PropertyRow("Operating systems", ComplianceText.Os(entry)));
        Details.Add(new PropertyRow("Restart needed after remediation", entry.RebootRequired ? "Yes" : "No"));
        Details.Add(new PropertyRow("Registry values", ComplianceText.Lines(entry.RegistrySurface.Select(ComplianceText.Registry))));
        Details.Add(new PropertyRow("Creates configuration items", ComplianceText.Lines(entry.ProducesConfigurationItems)));
        Details.Add(new PropertyRow("Creates baselines", ComplianceText.Lines(entry.ProducesBaselines)));
        Details.Add(new PropertyRow("Needs", entry.Dependencies.Count == 0 ? "(none)" : ComplianceText.Lines(entry.Dependencies.Select(d => $"{d.Type} {d.Name}"))));
        Details.Add(new PropertyRow("CVEs", ComplianceText.OrNone(entry.Cves)));
        Details.Add(new PropertyRow("Plugin IDs", ComplianceText.OrNone(entry.TenablePlugins.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList())));
        Details.Add(new PropertyRow("References", entry.References.Count == 0 ? "(none)" : ComplianceText.Lines(entry.References)));
        Details.Add(new PropertyRow("Parameters", ComplianceText.Lines(entry.Parameters.Select(p => $"{p.Name} ({p.Type}{(p.Required ? ", required" : "")}): {p.Description}"))));
    }

    private bool CanDeploy() => SelectedRow is not null;

    /// <summary>Opens the deploy form of the selected entry; a second click while a form is open does nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private async Task Deploy()
    {
        if (_deploying || SelectedRow is not { } row) return;
        _deploying = true;
        try
        {
            if (Shared.ConnectionProblem() is { } problem)
            {
                Shared.StatusMessage = problem;
                return;
            }
            var generation = Shared.Generation;
            Shared.StatusMessage = "Reading the device collections.";
            Shared.ForgetCollections();
            var collections = await Shared.DeviceCollectionsAsync();
            if (collections is null || generation != Shared.Generation) return;
            Shared.StatusMessage = "";
            var form = new CatalogDeployViewModel(row.Entry, Services.LoadSettings(), collections, DeployAsync, CreateDependencyAsync, PickAsync);
            Services.Dialogs.ShowDeploy(form);
        }
        finally { _deploying = false; }
    }

    private async Task PickAsync(CatalogDeployViewModel form, CatalogParameterField field)
    {
        var collections = await Shared.DeviceCollectionsAsync();
        if (collections is null)
        {
            form.ShowMessage(Shared.StatusMessage);
            return;
        }
        var picker = new CollectionPickerViewModel($"Pick the collection for {field.Name}", field.Description, "Use collection",
            collections.Where(c => !c.IsBuiltIn), p =>
            {
                if (p.CheckedCount != 1)
                {
                    p.ShowMessage(PickOne);
                    return Task.FromResult(false);
                }
                field.Value = p.Checked[0].Name;
                return Task.FromResult(true);
            });
        foreach (var item in picker.Items.Where(i => string.Equals(i.Name, field.Value.Trim(), StringComparison.OrdinalIgnoreCase))) item.IsChecked = true;
        Services.Dialogs.ShowPicker(picker);
    }

    private async Task<bool> DeployAsync(CatalogDeployViewModel form)
    {
        var entry = form.Entry;
        var request = ComplianceRequests.Deploy(entry, form.Values(), form.Targets().Select(t => t.CollectionId).ToList(), form.Remediate);
        Shared.ForgetCollections();
        var collections = await Shared.DeviceCollectionsAsync();
        if (collections is null)
        {
            form.ShowMessage(Shared.StatusMessage);
            return false;
        }
        var run = await Shared.RunPlanAsync([request], prepared => BuildDeployPlan(entry, prepared[0], collections),
            _ => $"{entry.DisplayName} {entry.Version}", form.ShowMessage);
        if (run is not { Outcome.Confirmed: true }) return false;
        await Shared.AfterWriteAsync();
        return true;
    }

    /// <summary>
    /// The plan of a catalog deploy, made from the prepared change only: its target collections,
    /// its mode and the Impact lines of the service. <paramref name="collections"/> gives the
    /// names and member counts of the targets when the service gives no Impact lines.
    /// </summary>
    public static (CompliancePlan? Plan, string? Problem) BuildDeployPlan(CatalogEntry entry, ComplianceChange change, IReadOnlyList<CollectionRow> collections)
    {
        var remediate = ComplianceWriter.NeedsRemediationConfirmation(change);
        var targets = change.TargetCollectionIds
            .Select(id => (Id: id, Row: collections.FirstOrDefault(c => string.Equals(c.CollectionId, id, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var distinct = targets.DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var devices = distinct.Sum(t => t.Row?.MemberCount ?? 0);
        List<string> lines;
        if (change.Impact.Count > 0) lines = [.. change.Impact];
        else
        {
            lines =
            [
                $"Creates configuration items: {string.Join(", ", entry.ProducesConfigurationItems)}.",
                $"Creates baselines: {string.Join(", ", entry.ProducesBaselines)}."
            ];
            if (targets.Count == 0) lines.Add("Deploys to no collection.");
            else lines.AddRange(targets.Select(t => t.Row is { } row
                ? $"Deploys to {ComplianceText.Target(row.Name, row.CollectionId)}: {ComplianceText.Count(row.MemberCount, "member")} now."
                : $"Deploys to collection {t.Id}."));
            lines.Add(remediate
                ? $"Mode: {ComplianceText.Remediate}. Clients change these registry values on every device of the target collections: "
                  + string.Join("; ", entry.RegistrySurface.Select(ComplianceText.Registry)) + "."
                : $"Mode: {ComplianceText.DetectOnly}. Clients report compliance and change nothing.");
            if (remediate) lines.Add(entry.RebootRequired ? "The devices need a restart after remediation." : "The devices need no restart after remediation.");
            lines.Add($"Each baseline gets the tag {ComplianceText.DriftTag(entry)} once in its description. The existing description stays.");
            lines.Add("Undo removes the deployments, baselines and configuration items this run creates. An object that existed before the run stays.");
        }
        var item = new ChangePlanItem("Deploy", $"{entry.DisplayName} {entry.Version}", string.Join(" ", lines));
        var extra = !remediate ? null
            : distinct.Count > 0 ? RemediationCheck(distinct.Count, devices, entry.RebootRequired)
            : NoTargetRemediationCheck;
        return (new CompliancePlan("Catalog deploy", $"Deploy {entry.DisplayName}", remediate ? "Deploy with remediation" : "Deploy detect only",
            [CompliancePlanEntry.Write(item, change)], extraConfirmation: extra), null);
    }

    private async Task CreateDependencyAsync(CatalogDeployViewModel form, CatalogDependency dependency)
    {
        var request = ComplianceRequests.CreateDependency(form.Entry, dependency);
        var run = await Shared.RunPlanAsync([request], prepared =>
        {
            var detail = string.Join(" ", new[] { $"Creates the {dependency.Type.ToLowerInvariant()} {dependency.Name} that {form.Entry.DisplayName} needs." }
                .Concat(prepared[0].Impact).Append("Undo removes it."));
            return (new CompliancePlan("Create dependency", $"Create {dependency.Name}", $"Create {dependency.Type.ToLowerInvariant()}",
                [CompliancePlanEntry.Write(new ChangePlanItem("Create", dependency.Name, detail), prepared[0])]), null);
        }, _ => dependency.Name, form.ShowMessage);
        if (run is not { Outcome.Confirmed: true }) return;
        await Shared.AfterWriteAsync();
        if (await Shared.DeviceCollectionsAsync() is { } collections) form.UpdateCollections(collections);
        form.ShowMessage(form.HasMissingDependency ? CatalogDeployViewModel.DependencyMissing : "");
    }
}
