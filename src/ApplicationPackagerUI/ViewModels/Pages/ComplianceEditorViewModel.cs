using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Editor tab: the settings of one configuration item, and Add, Edit and Remove of registry
/// value and script settings through plans that show the old and the new values. Script text
/// shows in the dialog and the plan and never reaches the log.
/// </summary>
public sealed partial class ComplianceEditorViewModel : ComplianceTabViewModel
{
    public const string ReadFailedText = "The configuration items could not be read. The log has the details.";
    public const string SettingsFailed = "The settings of the configuration item could not be read. The log has the details.";
    public const string PickItem = "Select a configuration item first.";
    public const string PickSetting = "Select a setting first.";
    public const string RevisionNote = "ConfigMgr stores the change as a new revision of the configuration item.";
    public const string ReevaluateNote = "Clients that have a baseline with this configuration item evaluate it again with the new revision.";

    private IReadOnlyList<ConfigurationItemRow> _all = [];
    private CancellationTokenSource? _settingsLoad;
    private int _settingsGeneration;
    private bool _dialogOpen;

    public ComplianceEditorViewModel(ComplianceShared shared) : base(shared)
    {
        shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComplianceShared.IsWriting)) NotifyActions();
        };
    }

    protected override string ReadFailed => ReadFailedText;

    public ObservableCollection<ConfigurationItemGridRow> Items { get; } = [];
    public ObservableCollection<SettingGridRow> Settings { get; } = [];

    [ObservableProperty] private string _itemFilter = "";
    [ObservableProperty] private string _settingsMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem), nameof(SelectedItemText))]
    [NotifyCanExecuteChangedFor(nameof(AddSettingCommand))]
    private ConfigurationItemGridRow? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSettingCommand), nameof(RemoveSettingCommand))]
    private SettingGridRow? _selectedSetting;

    /// <summary>The configuration item to select once the list is read, for example from the Live CI/CB tab.</summary>
    private string? _wantedItemId;

    public bool HasSelectedItem => SelectedItem is not null;
    public string SelectedItemText => SelectedItem is { } i ? $"{i} revision {i.Revision}" : "No configuration item selected.";

    /// <summary>The settings read that runs, or a completed task.</summary>
    public Task SettingsLoad { get; private set; } = Task.CompletedTask;

    partial void OnItemFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(ConfigurationItemGridRow? value) => SettingsLoad = LoadSettingsAsync(value);

    private void NotifyActions()
    {
        AddSettingCommand.NotifyCanExecuteChanged();
        EditSettingCommand.NotifyCanExecuteChanged();
        RemoveSettingCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selects the configuration item after the list is read.</summary>
    public void Select(ConfigurationItemRow item)
    {
        _wantedItemId = item.CiId;
        ItemFilter = "";
        if (Items.FirstOrDefault(i => i.CiId == item.CiId) is { } row) SelectedItem = row;
    }

    protected override async Task<Action> ReadAsync(CancellationToken ct)
    {
        var items = await Services.Compliance.ListConfigurationItemsAsync(ct);
        return () =>
        {
            _all = items;
            ApplyFilter();
            Services.Log.Info(ComplianceShared.LogArea, $"Read {items.Count} configuration items for the editor.");
        };
    }

    protected override void ReleaseData()
    {
        _settingsGeneration++;
        try { _settingsLoad?.Cancel(); } catch (ObjectDisposedException) { }
        _settingsLoad = null;
        _all = [];
        _wantedItemId = null;
        SelectedSetting = null;
        SelectedItem = null;
        Items.Clear();
        Settings.Clear();
        SettingsMessage = "";
        SettingsLoad = Task.CompletedTask;
    }

    private void ApplyFilter()
    {
        var wanted = _wantedItemId ?? SelectedItem?.CiId;
        var text = ItemFilter.Trim();
        Replace(Items, _all.Where(i => text.Length == 0 || Contains(i.Name, text) || Contains(i.CiId, text))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i => new ConfigurationItemGridRow(i)).ToList());
        var selected = Items.FirstOrDefault(i => i.CiId == wanted);
        if (selected is not null) _wantedItemId = null;
        if (!Equals(selected, SelectedItem)) SelectedItem = selected;
        else if (selected is not null && !ReferenceEquals(selected, SelectedItem)) SelectedItem = selected;
    }

    private async Task LoadSettingsAsync(ConfigurationItemGridRow? item)
    {
        var mine = ++_settingsGeneration;
        var generation = Shared.Generation;
        try { _settingsLoad?.Cancel(); } catch (ObjectDisposedException) { }
        Settings.Clear();
        SelectedSetting = null;
        SettingsMessage = "";
        if (item is null) return;
        using var cts = new CancellationTokenSource();
        _settingsLoad = cts;
        SettingsMessage = "Reading the settings.";
        try
        {
            var settings = await Task.Run(() => Services.Compliance.ListSettingsAsync(item.CiId, cts.Token), cts.Token);
            if (mine != _settingsGeneration || generation != Shared.Generation) return;
            foreach (var setting in settings) Settings.Add(new SettingGridRow(setting));
            SettingsMessage = ComplianceText.Count(settings.Count, "setting");
        }
        catch (OperationCanceledException)
        {
            if (mine == _settingsGeneration) SettingsMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _settingsGeneration || generation != Shared.Generation) return;
            SettingsMessage = ComplianceShared.PlainMessage(ex, SettingsFailed);
            Services.Log.Warning(ComplianceShared.LogArea, $"The settings of a configuration item could not be read ({ComplianceShared.FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_settingsLoad, cts)) _settingsLoad = null;
        }
    }

    private bool CanAdd() => SelectedItem is not null && !Shared.IsWriting;

    private bool CanChange() => SelectedItem is not null && SelectedSetting is not null && !Shared.IsWriting;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSetting()
    {
        if (_dialogOpen || SelectedItem is not { } item) return;
        ShowEditor(new CiSettingEditorViewModel(CiSettingEditorMode.Add, item.ToString(), editor => SaveAsync(item.Row, null, editor)));
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private void EditSetting()
    {
        if (_dialogOpen || SelectedItem is not { } item || SelectedSetting is not { } setting) return;
        ShowEditor(new CiSettingEditorViewModel(CiSettingEditorMode.Edit, item.ToString(), editor => SaveAsync(item.Row, setting.Setting, editor), setting.Setting));
    }

    private void ShowEditor(CiSettingEditorViewModel editor)
    {
        _dialogOpen = true;
        try { Services.Dialogs.ShowSettingEditor(editor); }
        finally { _dialogOpen = false; }
    }

    /// <summary>The plain lines that state the setting's values, script text included.</summary>
    public static string Values(CiSetting setting)
    {
        var parts = new List<string> { $"{setting.Kind}, data type {setting.DataType}, expected value {setting.ExpectedValue ?? "(none)"}." };
        if (string.Equals(setting.Kind, CiSettingEditorViewModel.ScriptKind, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Language {setting.DiscoveryLanguage}. Discovery script: {setting.DiscoveryScript}");
            parts.Add(setting.RemediationScript is { Length: > 0 } remediation ? $"Remediation script: {remediation}" : "No remediation script.");
        }
        else parts.Add($"Registry value {setting.Detail}.");
        return string.Join(" ", parts);
    }

    private static string Notes(ConfigurationItemRow item, ComplianceChange change) =>
        string.Join(" ", new[] { RevisionNote, item.BaselineCount > 0 ? ReevaluateNote : "" }.Concat(change.Impact).Where(l => l.Length > 0));

    private static string Describe(ConfigurationItemRow item, ComplianceChange change) =>
        $"{(change.SettingAfter ?? change.SettingBefore)?.Name} in {ComplianceText.Target(item.Name, item.CiId)}";

    private async Task<bool> SaveAsync(ConfigurationItemRow item, CiSetting? before, CiSettingEditorViewModel editor)
    {
        var after = editor.Build();
        var request = before is null ? ComplianceRequests.AddSetting(item.CiId, after) : ComplianceRequests.EditSetting(item.CiId, before, after);
        var run = await Shared.RunPlanAsync([request], prepared =>
        {
            var change = prepared[0];
            string detail;
            if (before is null) detail = $"New: {Values(after)} {Notes(item, change)} Undo removes the setting.";
            else
            {
                var old = change.SettingBefore ?? before;
                if (!string.Equals(old.DataType, after.DataType, StringComparison.OrdinalIgnoreCase)) return (null, CiSettingEditorViewModel.DataTypeFixedNote);
                detail = $"Old: {Values(old)} New: {Values(after)} {Notes(item, change)} Undo sets the old values back.";
            }
            var action = before is null ? "Add setting" : "Edit setting";
            return (new CompliancePlan(action, $"{action} {after.Name}", action,
                [CompliancePlanEntry.Write(new ChangePlanItem(before is null ? "Add" : "Edit", Describe(item, change), detail), change)]), null);
        }, change => Describe(item, change), editor.ShowMessage);
        if (run is not { Outcome.Confirmed: true }) return false;
        await Shared.AfterWriteAsync();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task RemoveSetting()
    {
        if (_dialogOpen || SelectedItem is not { } item || SelectedSetting is not { } setting) return;
        var run = await Shared.RunPlanAsync([ComplianceRequests.RemoveSetting(item.CiId, setting.Setting)], prepared =>
        {
            var change = prepared[0];
            var captured = change.SettingBefore ?? setting.Setting;
            var detail = $"Removes: {Values(captured)} {Notes(item.Row, change)} Undo adds the setting again from these values; the recreated setting has a new ID.";
            return (new CompliancePlan("Remove setting", $"Remove setting {captured.Name}", "Remove setting",
                [CompliancePlanEntry.Write(new ChangePlanItem("Remove", Describe(item.Row, change), detail), change)], isDestructive: true), null);
        }, change => Describe(item.Row, change));
        if (run is { Outcome.Confirmed: true }) await Shared.AfterWriteAsync();
    }

    /// <summary>After a write the list and the settings of the selected item are read again.</summary>
    public async Task RefreshSettingsAsync()
    {
        await SettingsLoad;
        if (Shared.IsActive && SelectedItem is { } item) await (SettingsLoad = LoadSettingsAsync(item));
    }
}
