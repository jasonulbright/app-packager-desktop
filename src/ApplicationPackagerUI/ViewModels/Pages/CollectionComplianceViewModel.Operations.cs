using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class CollectionComplianceViewModel
{
    private CancellationTokenSource? _detail;
    private int _detailGeneration;
    private IReadOnlyList<CollectionRule> _detailRules = [];
    private int _memberTotal;
    private int _memberPageSize;
    private IReadOnlyList<MemberRow> _selectedMembers = [];

    public ObservableCollection<PropertyRow> Properties { get; } = [];
    public ObservableCollection<MemberRow> Members { get; } = [];
    public ObservableCollection<RuleRow> QueryRules { get; } = [];
    public ObservableCollection<RuleRow> IncludeRules { get; } = [];
    public ObservableCollection<RuleRow> ExcludeRules { get; } = [];

    /// <summary>The detail read of the selected collection that runs or ran last.</summary>
    public Task DetailLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextMemberPageCommand), nameof(PreviousMemberPageCommand))]
    private int _memberPage;

    [ObservableProperty] private string _memberPageText = "";
    [ObservableProperty] private string _detailMessage = "";
    [ObservableProperty] private bool _isDetailLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditRuleInEditorCommand), nameof(RemoveSelectedQueryRuleCommand))]
    private RuleRow? _selectedQueryRule;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveIncludeRuleCommand))]
    private RuleRow? _selectedIncludeRule;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveExcludeRuleCommand))]
    private RuleRow? _selectedExcludeRule;

    public void SetSelectedMembers(IEnumerable<MemberRow> rows)
    {
        _selectedMembers = rows.ToList();
        RemoveDirectMembersCommand.NotifyCanExecuteChanged();
    }

    private void ReleaseDetail()
    {
        _detailGeneration++;
        try { _detail?.Cancel(); } catch (ObjectDisposedException) { }
        _detail = null;
        IsDetailLoading = false;
        _detailRules = [];
        _memberTotal = 0;
        _memberPageSize = 0;
        Properties.Clear();
        Members.Clear();
        QueryRules.Clear();
        IncludeRules.Clear();
        ExcludeRules.Clear();
        SetSelectedMembers([]);
        MemberPage = 0;
        MemberPageText = "";
        DetailMessage = "";
    }

    private void StartDetail(CollectionGridRow? row, int page)
    {
        var keepPageSize = row is not null && page > 0 ? _memberPageSize : 0;
        ReleaseDetail();
        _memberPageSize = keepPageSize;
        if (row is null)
        {
            DetailLoad = Task.CompletedTask;
            return;
        }
        ShowProperties(row.Row);
        var cts = new CancellationTokenSource();
        _detail = cts;
        DetailLoad = LoadDetailAsync(row.Row, page, _detailGeneration, cts);
    }

    private void ShowProperties(CollectionRow row)
    {
        Properties.Clear();
        Properties.Add(new PropertyRow("Name", row.Name));
        Properties.Add(new PropertyRow("Collection ID", row.CollectionId));
        Properties.Add(new PropertyRow("Type", row.IsBuiltIn ? "Built-in" : "Custom"));
        Properties.Add(new PropertyRow("Folder", string.IsNullOrEmpty(row.FolderPath) ? "(root)" : row.FolderPath));
        Properties.Add(new PropertyRow("Limiting collection", CollectionsText.OrNone(CollectionsText.Limiting(row))));
        Properties.Add(new PropertyRow("Members", row.MemberCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Properties.Add(new PropertyRow("Refresh", row.RefreshType));
        Properties.Add(new PropertyRow("Rules", CollectionsText.Rules(row)));
        Properties.Add(new PropertyRow("Comment", CollectionsText.OrNone(row.Comment)));
    }

    private async Task LoadDetailAsync(CollectionRow row, int page, int generation, CancellationTokenSource cts)
    {
        IsDetailLoading = true;
        CollectionDetail? detail = null;
        string? problem = null;
        try
        {
            detail = await Task.Run(() => _services.Collections.GetDetailAsync(row.CollectionId, page, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            problem = PlainMessage(ex, DetailFailed);
            if (generation == _detailGeneration) _services.Log.Warning(LogArea, $"The details of a collection could not be read ({FailureName(ex)}).");
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_detail, cts)) _detail = null;
        }
        if (generation != _detailGeneration) return;
        IsDetailLoading = false;
        if (detail is null)
        {
            DetailMessage = problem ?? "";
            return;
        }
        _detailRules = detail.Rules;
        ShowProperties(detail.Row);
        foreach (var member in detail.Members) Members.Add(new MemberRow(member));
        foreach (var rule in detail.Rules)
        {
            switch (rule.Kind)
            {
                case CollectionRuleKind.Query: QueryRules.Add(new RuleRow(rule, rule.RuleName)); break;
                case CollectionRuleKind.Include: IncludeRules.Add(new RuleRow(rule, CollectionText(rule.IncludedCollectionId, rule.RuleName))); break;
                case CollectionRuleKind.Exclude: ExcludeRules.Add(new RuleRow(rule, CollectionText(rule.IncludedCollectionId, rule.RuleName))); break;
            }
        }
        _memberTotal = detail.MemberTotal;
        _memberPageSize = Math.Max(_memberPageSize, detail.Members.Count);
        MemberPage = page;
        var first = page * _memberPageSize + 1;
        MemberPageText = detail.MemberTotal == 0 ? "No members"
            : detail.Members.Count == 0 ? $"No members on this page of {detail.MemberTotal}"
            : $"Members {first} to {first + detail.Members.Count - 1} of {detail.MemberTotal}";
        NextMemberPageCommand.NotifyCanExecuteChanged();
        PreviousMemberPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanNextMemberPage() => _memberPageSize > 0 && MemberPage * _memberPageSize + Members.Count < _memberTotal;

    [RelayCommand(CanExecute = nameof(CanNextMemberPage))]
    private Task NextMemberPage()
    {
        if (SelectedRow is not { } row || !CanNextMemberPage()) return Task.CompletedTask;
        StartDetail(row, MemberPage + 1);
        return DetailLoad;
    }

    private bool CanPreviousMemberPage() => MemberPage > 0;

    [RelayCommand(CanExecute = nameof(CanPreviousMemberPage))]
    private Task PreviousMemberPage()
    {
        if (SelectedRow is not { } row || MemberPage == 0) return Task.CompletedTask;
        StartDetail(row, MemberPage - 1);
        return DetailLoad;
    }

    // ---- Collection writes ----

    private bool CanChangeSelected() => SelectedRow is { IsBuiltIn: false };

    private bool CanCopySelected() => SelectedRow is not null;

    private CollectionChange Request(CollectionChangeKind kind, CollectionRow row, CollectionRow? after = null, CollectionRule? ruleBefore = null,
        CollectionRule? ruleAfter = null, IReadOnlyList<string>? impact = null) =>
        new(kind, row.CollectionId, row.Name, row, after, ruleBefore, ruleAfter, null, null, impact ?? []);

    private static CollectionChange QueryRequest(CollectionChangeKind kind, SavedQuery? before, SavedQuery? after) =>
        new(kind, "", (after ?? before)!.Name, null, null, null, null, before, after, []);

    private CollectionEditorViewModel NewEditor(CollectionEditorMode mode, CollectionRow? original)
    {
        var editor = new CollectionEditorViewModel(mode, _collections ?? [], SaveCollectionAsync, original);
        if (double.IsFinite(_editorWidth) && _editorWidth >= 400) editor.Width = _editorWidth;
        if (double.IsFinite(_editorHeight) && _editorHeight >= 400) editor.Height = _editorHeight;
        return editor;
    }

    private async Task ShowEditorAsync(CollectionEditorViewModel editor)
    {
        _services.Prompts.ShowCollectionEditor(editor);
        _editorWidth = editor.Width;
        _editorHeight = editor.Height;
        _ = SavePreferencesAsync();
        if (editor.Saved || _lastRunPlan is not null) await AfterWriteAsync();
    }

    private bool RequireList()
    {
        if (IsDataLoaded) return true;
        StatusMessage = ConnectionMessage.Length > 0 ? ConnectionMessage : RefreshFirst;
        return false;
    }

    [RelayCommand]
    private Task NewCollection() => RequireList() ? ShowEditorAsync(NewEditor(CollectionEditorMode.New, null)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private Task EditCollection() => SelectedRow is { IsBuiltIn: false } row ? ShowEditorAsync(NewEditor(CollectionEditorMode.Edit, row.Row)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanCopySelected))]
    private Task CopyCollection() => SelectedRow is { } row ? ShowEditorAsync(NewEditor(CollectionEditorMode.Copy, row.Row)) : Task.CompletedTask;

    /// <summary>The editor's Save: one change, its plan, and true when the write changed ConfigMgr.</summary>
    private async Task<bool> SaveCollectionAsync(CollectionEditorViewModel editor)
    {
        if (editor.BuildRow() is not { } after) return false;
        CollectionChange request = editor.Mode switch
        {
            CollectionEditorMode.New => new CollectionChange(CollectionChangeKind.Create, "", after.Name, null, after, null, null, null, null, []),
            CollectionEditorMode.Copy => Request(CollectionChangeKind.Copy, editor.Original!, after),
            _ => Request(CollectionChangeKind.SetProperties, editor.Original!, after)
        };
        var run = await RunPlanAsync([request], prepared => BuildEditorPlan(prepared[0]), editor.ShowMessage);
        if (run is not { } done) return false;
        if (done.Outcome.Result is not { } result)
        {
            editor.ShowMessage(NotConfirmed);
            return false;
        }
        if (result.SucceededCount > 0) return true;
        editor.ShowMessage(result.Items.FirstOrDefault(i => !i.Succeeded)?.Message ?? NotConfirmed);
        return false;
    }

    private static (CollectionPlan?, string?) BuildEditorPlan(CollectionChange change)
    {
        var after = change.AfterRow!;
        switch (change.Kind)
        {
            case CollectionChangeKind.Create:
                return (new CollectionPlan("Create", "New collection", "Create collection",
                [
                    CollectionPlanEntry.Write(new ChangePlanItem("Create", after.Name,
                        $"Limiting collection: {CollectionsText.Limiting(after)}. Refresh: {after.RefreshType}. Comment: {CollectionsText.OrNone(after.Comment)}. Undo removes it."), change)
                ]), null);
            case CollectionChangeKind.Copy:
            {
                var source = change.BeforeRow ?? after;
                return (new CollectionPlan("Copy", "Copy collection", "Copy collection",
                [
                    CollectionPlanEntry.Write(new ChangePlanItem("Copy", CollectionsText.Target(source),
                        $"New name: {after.Name}. The copy gets the properties and rules of {source.Name}. Undo removes the copy."), change)
                ]), null);
            }
            default:
            {
                var before = change.BeforeRow!;
                if (before.IsBuiltIn) return (null, BuiltInRefused);
                var lines = new List<string>();
                if (before.Name != after.Name) lines.Add($"Name: {before.Name} to {after.Name}");
                if (before.Comment != after.Comment) lines.Add($"Comment: {CollectionsText.OrNone(before.Comment)} to {CollectionsText.OrNone(after.Comment)}");
                if (!string.Equals(before.LimitingCollectionId, after.LimitingCollectionId, StringComparison.OrdinalIgnoreCase))
                    lines.Add($"Limiting collection: {CollectionsText.OrNone(CollectionsText.Limiting(before))} to {CollectionsText.Limiting(after)}");
                if (before.RefreshType != after.RefreshType) lines.Add($"Refresh: {before.RefreshType} to {after.RefreshType}");
                if (lines.Count == 0) return (null, NothingToChange);
                var set = before with
                {
                    Name = after.Name,
                    Comment = after.Comment,
                    RefreshType = after.RefreshType,
                    LimitingCollectionId = after.LimitingCollectionId,
                    LimitingCollectionName = after.LimitingCollectionName
                };
                return (new CollectionPlan("Set properties", "Edit collection", "Save changes",
                [
                    CollectionPlanEntry.Write(new ChangePlanItem("Set properties", CollectionsText.Target(before), string.Join(". ", lines) + ". Undo sets the old values back."),
                        change with { AfterRow = set })
                ]), null);
            }
        }
    }

    private bool CanEvaluate() => !_writing && _selectedRows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private async Task Evaluate()
    {
        var rows = _selectedRows.Select(r => r.Row).ToList();
        if (rows.Count == 0) return;
        var run = await RunPlanAsync(rows.Select(r => Request(CollectionChangeKind.Evaluate, r)).ToList(), prepared =>
        {
            var entries = prepared.Select(c => CollectionPlanEntry.Write(new ChangePlanItem("Evaluate", CollectionText(c.CollectionId, c.CollectionName),
                $"{EvaluateNote} {NoUndoNote}"), c)).ToList();
            return (new CollectionPlan("Update membership", "Update membership", $"Update membership of {CollectionsText.Count(entries.Count, "collection")}", entries, hasUndo: false), null);
        });
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    private bool CanRemoveSelected() => !_writing && SelectedRow is { IsBuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveCollection()
    {
        if (SelectedRow is not { } row) return;
        if (row.IsBuiltIn)
        {
            StatusMessage = BuiltInRefused;
            return;
        }
        var run = await RunPlanAsync([Request(CollectionChangeKind.Remove, row.Row)], prepared =>
        {
            var change = prepared[0];
            var before = change.BeforeRow ?? row.Row;
            if (before.IsBuiltIn) return (null, BuiltInRefused);
            var impact = change.Impact.Count == 0 ? "" : string.Join(" ", change.Impact.Select(Sentence)) + " ";
            return (new CollectionPlan("Remove", "Remove collection", "Remove collection",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Remove", CollectionsText.Target(before), $"{impact}{RemoveUndoNote} {CollectionWriter.RecreatedNote}"), change)
            ], isDestructive: true), null);
        });
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    private static string Sentence(string line)
    {
        var text = line.Trim();
        return text.Length == 0 || text.EndsWith('.') ? text : text + ".";
    }

    // ---- Direct members ----

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private async Task AddDirectMembers()
    {
        if (SelectedRow is not { IsBuiltIn: false } row) return;
        var prompt = new TextPromptViewModel("Add direct members", "Device names",
            $"One name per line, at most {MaxDeviceNames}. The page looks each name up in ConfigMgr and shows a plan.", "Look up and show plan", true,
            DeviceNamesProblem, p => AddDirectMembersAsync(row.Row, p));
        _services.Prompts.ShowTextPrompt(prompt);
        if (prompt.Accepted || _lastRunPlan is not null) await AfterWriteAsync();
    }

    private static List<string> DeviceNames(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? DeviceNamesProblem(string text)
    {
        var names = DeviceNames(text);
        if (names.Count == 0) return TextPromptViewModel.Required;
        if (names.Count > MaxDeviceNames) return $"Enter at most {MaxDeviceNames} names.";
        if (names.Any(n => n.Any(char.IsControl))) return WqlParameters.ControlCharacter;
        if (names.Any(n => n.Length > WqlParameters.MaxLength)) return WqlParameters.TooLong;
        return null;
    }

    private async Task<bool> AddDirectMembersAsync(CollectionRow collection, TextPromptViewModel prompt)
    {
        var names = DeviceNames(prompt.Text);
        var query = "select SMS_R_System.ResourceId, SMS_R_System.Name from SMS_R_System where "
            + string.Join(" or ", names.Select(n => "SMS_R_System.Name = " + WqlParameters.Literal(n)));
        WqlResult result;
        try
        {
            result = await Task.Run(() => _services.Collections.PreviewWqlAsync(query, names.Count * 4 + 10, CancellationToken.None));
        }
        catch (Exception ex)
        {
            prompt.ShowMessage(PlainMessage(ex, LookupFailed));
            _services.Log.Warning(LogArea, $"The device names could not be looked up ({FailureName(ex)}).");
            return false;
        }
        if (!result.IsValid)
        {
            prompt.ShowMessage(LookupFailed);
            _services.Log.Warning(LogArea, "The device name lookup was not valid.");
            return false;
        }
        var idColumn = IndexOf(result.Columns, "ResourceId");
        var nameColumn = IndexOf(result.Columns, "Name");
        var found = new Dictionary<string, (string Id, string Name)>(StringComparer.OrdinalIgnoreCase);
        if (idColumn >= 0 && nameColumn >= 0)
            foreach (var r in result.FirstRows)
                if (r.Count > Math.Max(idColumn, nameColumn)) found.TryAdd(r[nameColumn], (r[idColumn], r[nameColumn]));
        _services.Log.Info(LogArea, $"Looked up {names.Count} device names; {names.Count(found.ContainsKey)} found.");
        var skips = names.Where(n => !found.ContainsKey(n))
            .Select(n => CollectionPlanEntry.Skip(new ChangePlanItem("Skip", n, DeviceNotFound), DeviceNotFound)).ToList();
        var requests = names.Where(found.ContainsKey).Select(n => found[n])
            .Select(d => Request(CollectionChangeKind.AddRule, collection, ruleAfter: new CollectionRule(CollectionRuleKind.Direct, d.Name, "", d.Id, "", ""))).ToList();
        if (requests.Count == 0)
        {
            prompt.ShowMessage(NoDeviceFound);
            return false;
        }
        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = prepared.Select(c => CollectionPlanEntry.Write(new ChangePlanItem("Add", CollectionText(c.CollectionId, c.CollectionName),
                $"Direct member {c.RuleAfter!.RuleName} ({c.RuleAfter.ResourceId})."), c)).ToList();
            return (new CollectionPlan("Add direct members", "Add direct members", $"Add {CollectionsText.Count(entries.Count, "member")}", entries), null);
        }, prompt.ShowMessage, extraSkips: skips.Count == 0 ? null : skips);
        return Done(run, prompt.ShowMessage);
    }

    /// <summary>True when the confirmed run changed something; otherwise the dialog shows why not.</summary>
    private static bool Done((CollectionPlan Plan, ChangePlanOutcome Outcome)? run, Action<string> show)
    {
        if (run is not { } done) return false;
        if (done.Outcome.Result is not { } result)
        {
            show(NotConfirmed);
            return false;
        }
        if (result.SucceededCount > 0) return true;
        show(result.Items.FirstOrDefault(i => !i.Succeeded && !i.Skipped)?.Message ?? result.Items.FirstOrDefault()?.Message ?? NotConfirmed);
        return false;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var dot = column.LastIndexOf('.');
            if (string.Equals(dot >= 0 ? column[(dot + 1)..] : column, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private bool CanRemoveMembers() => !_writing && SelectedRow is { IsBuiltIn: false } && _selectedMembers.Any(m => m.IsDirect);

    [RelayCommand(CanExecute = nameof(CanRemoveMembers))]
    private async Task RemoveDirectMembers()
    {
        if (SelectedRow is not { IsBuiltIn: false } row) return;
        var members = _selectedMembers.Where(m => m.IsDirect).ToList();
        if (members.Count == 0)
        {
            StatusMessage = NoDirectMembers;
            return;
        }
        var requests = members.Select(m => Request(CollectionChangeKind.RemoveRule, row.Row,
            ruleBefore: _detailRules.FirstOrDefault(r => r.Kind == CollectionRuleKind.Direct && r.ResourceId == m.ResourceId)
                ?? new CollectionRule(CollectionRuleKind.Direct, m.Name, "", m.ResourceId, "", ""))).ToList();
        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = prepared.Select(c => CollectionPlanEntry.Write(new ChangePlanItem("Remove", CollectionText(c.CollectionId, c.CollectionName),
                $"Direct member {c.RuleBefore!.RuleName} ({c.RuleBefore.ResourceId}). Undo adds it again."), c)).ToList();
            return (new CollectionPlan("Remove direct members", "Remove direct members", $"Remove {CollectionsText.Count(entries.Count, "member")}", entries, isDestructive: true), null);
        });
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    // ---- Include and exclude rules ----

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private Task AddIncludeRule() => AddMembershipRuleAsync(CollectionRuleKind.Include);

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private Task AddExcludeRule() => AddMembershipRuleAsync(CollectionRuleKind.Exclude);

    private static string KindText(CollectionRuleKind kind) => kind == CollectionRuleKind.Include ? "Include" : "Exclude";

    private async Task AddMembershipRuleAsync(CollectionRuleKind kind)
    {
        if (SelectedRow is not { IsBuiltIn: false } row || !RequireList()) return;
        var word = KindText(kind).ToLowerInvariant();
        var picker = new CollectionPickerViewModel($"Add {word} rules", $"Check the collections whose members {row.Name} {word}s.", "Show plan",
            _collections!.Where(c => !string.Equals(c.CollectionId, row.CollectionId, StringComparison.OrdinalIgnoreCase)),
            p => AddMembershipRulesAsync(row.Row, kind, p));
        _services.Prompts.ShowPicker(picker);
        if (picker.Accepted || _lastRunPlan is not null) await AfterWriteAsync();
    }

    private async Task<bool> AddMembershipRulesAsync(CollectionRow collection, CollectionRuleKind kind, CollectionPickerViewModel picker)
    {
        var requests = picker.Checked.Select(target => Request(CollectionChangeKind.AddRule, collection,
            ruleAfter: new CollectionRule(kind, target.Name, "", "", target.CollectionId, ""))).ToList();
        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = prepared.Select(c => CollectionPlanEntry.Write(new ChangePlanItem("Add", CollectionText(c.CollectionId, c.CollectionName),
                $"{KindText(kind)} rule: {CollectionText(c.RuleAfter!.IncludedCollectionId, c.RuleAfter.RuleName)}. {MembershipNote} Undo removes it."), c)).ToList();
            return (new CollectionPlan($"Add {KindText(kind).ToLowerInvariant()} rules", $"Add {KindText(kind).ToLowerInvariant()} rules",
                $"Add {CollectionsText.Count(entries.Count, "rule")}", entries), null);
        }, picker.ShowMessage);
        return Done(run, picker.ShowMessage);
    }

    private bool CanRemoveInclude() => !_writing && SelectedRow is { IsBuiltIn: false } && SelectedIncludeRule is not null;

    private bool CanRemoveExclude() => !_writing && SelectedRow is { IsBuiltIn: false } && SelectedExcludeRule is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveInclude))]
    private Task RemoveIncludeRule() => RemoveRuleRowAsync(SelectedIncludeRule);

    [RelayCommand(CanExecute = nameof(CanRemoveExclude))]
    private Task RemoveExcludeRule() => RemoveRuleRowAsync(SelectedExcludeRule);

    private bool CanRemoveDetailQueryRule() => SelectedRow is { IsBuiltIn: false } && SelectedQueryRule is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveDetailQueryRule))]
    private Task RemoveSelectedQueryRule() => RemoveRuleRowAsync(SelectedQueryRule);

    private async Task RemoveRuleRowAsync(RuleRow? rule)
    {
        if (SelectedRow is not { IsBuiltIn: false } row || rule is null) return;
        await RemoveRuleAsync(row.Row, rule.Rule, null);
    }

    private string RuleDetail(CollectionRule rule) => rule.Kind switch
    {
        CollectionRuleKind.Query => $"Query rule {rule.RuleName}: {rule.Expression}.",
        CollectionRuleKind.Direct => $"Direct member {rule.RuleName} ({rule.ResourceId}).",
        _ => $"{KindText(rule.Kind)} rule: {CollectionText(rule.IncludedCollectionId, rule.RuleName)}. {MembershipNote}"
    };

    private async Task RemoveRuleAsync(CollectionRow collection, CollectionRule rule, Action<string>? report)
    {
        var run = await RunPlanAsync([Request(CollectionChangeKind.RemoveRule, collection, ruleBefore: rule)], prepared =>
        {
            var change = prepared[0];
            return (new CollectionPlan("Remove rule", "Remove rule", "Remove rule",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Remove", CollectionText(change.CollectionId, change.CollectionName),
                    $"{RuleDetail(change.RuleBefore ?? rule)} Undo adds it again."), change)
            ], isDestructive: true), null);
        }, report);
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEditRuleInEditor))]
    private async Task EditRuleInEditor()
    {
        if (SelectedRow is not { } row || SelectedQueryRule is not { } rule) return;
        SelectedTabIndex = WqlTab;
        WqlCollection = row;
        await WqlRulesLoad;
        SelectedWqlRule = WqlRules.FirstOrDefault(r => string.Equals(r.RuleName, rule.RuleName, StringComparison.OrdinalIgnoreCase));
        if (SelectedWqlRule is null)
        {
            WqlRuleName = rule.RuleName;
            WqlText = rule.Expression;
        }
    }

    private bool CanEditRuleInEditor() => SelectedQueryRule is not null;

    // ---- WQL Editor ----

    private CancellationTokenSource? _wqlRules;
    private int _wqlGeneration;
    private CancellationTokenSource? _preview;
    private (string Expression, int Count)? _lastPreview;

    public ObservableCollection<CollectionTreeNode> WqlTree { get; } = [];
    public ObservableCollection<RuleRow> WqlRules { get; } = [];
    public ObservableCollection<WqlPreviewRow> PreviewRows { get; } = [];

    /// <summary>The rule read of the WQL Editor's collection that runs or ran last.</summary>
    public Task WqlRulesLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private string _wqlFilter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WqlCollectionText), nameof(WqlCollectionNote))]
    private CollectionGridRow? _wqlCollection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateRuleCommand), nameof(RemoveRuleCommand))]
    private RuleRow? _selectedWqlRule;

    [ObservableProperty] private string _wqlRuleName = "";
    [ObservableProperty] private string _wqlText = "";
    [ObservableProperty] private string _wqlMessage = "";
    [ObservableProperty] private IReadOnlyList<string> _previewColumns = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewWqlCommand), nameof(CancelPreviewCommand))]
    private bool _isPreviewing;

    public string WqlCollectionText => WqlCollection is { } c ? c.ToString() : "No collection selected.";
    public string WqlCollectionNote => WqlCollection?.IsBuiltIn == true ? BuiltInRefused : "";

    partial void OnWqlFilterChanged(string value) => RebuildTree();

    partial void OnWqlCollectionChanged(CollectionGridRow? value) => StartWqlRules(value);

    partial void OnSelectedWqlRuleChanged(RuleRow? value)
    {
        if (value is null) return;
        WqlRuleName = value.RuleName;
        WqlText = value.Expression;
    }

    private void ReleaseWql()
    {
        _wqlGeneration++;
        try { _wqlRules?.Cancel(); } catch (ObjectDisposedException) { }
        try { _preview?.Cancel(); } catch (ObjectDisposedException) { }
        _wqlRules = null;
        WqlTree.Clear();
        WqlRules.Clear();
        PreviewRows.Clear();
        PreviewColumns = [];
        SelectedWqlRule = null;
        WqlCollection = null;
        WqlRulesLoad = Task.CompletedTask;
    }

    private void StartWqlRules(CollectionGridRow? row)
    {
        _wqlGeneration++;
        try { _wqlRules?.Cancel(); } catch (ObjectDisposedException) { }
        var selectedName = SelectedWqlRule?.RuleName;
        WqlRules.Clear();
        SelectedWqlRule = null;
        if (row is null)
        {
            WqlRulesLoad = Task.CompletedTask;
            return;
        }
        var cts = new CancellationTokenSource();
        _wqlRules = cts;
        WqlRulesLoad = LoadWqlRulesAsync(row.CollectionId, _wqlGeneration, cts, selectedName);
    }

    private async Task LoadWqlRulesAsync(string id, int generation, CancellationTokenSource cts, string? selectedName)
    {
        CollectionDetail? detail = null;
        try
        {
            detail = await Task.Run(() => _services.Collections.GetDetailAsync(id, 0, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _wqlGeneration)
            {
                WqlMessage = PlainMessage(ex, DetailFailed);
                _services.Log.Warning(LogArea, $"The rules of a collection could not be read ({FailureName(ex)}).");
            }
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_wqlRules, cts)) _wqlRules = null;
        }
        if (generation != _wqlGeneration || detail is null) return;
        foreach (var rule in detail.Rules.Where(r => r.Kind == CollectionRuleKind.Query)) WqlRules.Add(new RuleRow(rule, rule.RuleName));
        if (selectedName is not null)
        {
            var keep = WqlRules.FirstOrDefault(r => string.Equals(r.RuleName, selectedName, StringComparison.OrdinalIgnoreCase));
            if (keep is not null && keep.Expression == WqlText.Trim()) SelectedWqlRule = keep;
        }
    }

    private void RebuildTree()
    {
        WqlTree.Clear();
        if (_collections is null) return;
        var filter = WqlFilter.Trim();
        var rows = _collections.Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.CollectionId.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var folders = new Dictionary<string, CollectionTreeNode>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<CollectionTreeNode>();
        var rootItems = new List<CollectionTreeNode>();

        CollectionTreeNode Folder(string path)
        {
            if (folders.TryGetValue(path, out var existing)) return existing;
            var cut = path.LastIndexOf('\\');
            var node = new CollectionTreeNode(cut < 0 ? path : path[(cut + 1)..], null) { IsExpanded = filter.Length > 0 };
            folders[path] = node;
            if (cut < 0) roots.Add(node);
            else Folder(path[..cut]).Children.Add(node);
            return node;
        }

        foreach (var path in rows.Select(r => r.FolderPath.Trim('\\')).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Folder(path);
        foreach (var row in rows)
        {
            var node = new CollectionTreeNode(CollectionsText.Target(row), Rows.FirstOrDefault(r => r.CollectionId == row.CollectionId) ?? new CollectionGridRow(row));
            var path = row.FolderPath.Trim('\\');
            if (path.Length == 0) rootItems.Add(node);
            else folders[path].Children.Add(node);
        }
        foreach (var node in roots.Concat(rootItems)) WqlTree.Add(node);
    }

    [RelayCommand]
    private async Task ValidateWql()
    {
        var text = WqlText.Trim();
        if (text.Length == 0)
        {
            WqlMessage = QueryRequired;
            return;
        }
        try
        {
            var result = await Task.Run(() => _services.Collections.ValidateWqlAsync(text, CancellationToken.None));
            WqlMessage = SecretMask.Apply(result.Message);
            _services.Log.Info(LogArea, $"Checked a query: valid={result.IsValid}.");
        }
        catch (Exception ex)
        {
            WqlMessage = PlainMessage(ex, WqlFailed);
            _services.Log.Warning(LogArea, $"A query could not be checked ({FailureName(ex)}).");
        }
    }

    private bool CanPreview() => !IsPreviewing;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewWql()
    {
        var text = WqlText.Trim();
        if (text.Length == 0)
        {
            WqlMessage = QueryRequired;
            return;
        }
        using var cts = new CancellationTokenSource();
        _preview = cts;
        IsPreviewing = true;
        WqlMessage = "Running the preview.";
        PreviewRows.Clear();
        PreviewColumns = [];
        var generation = _generation;
        try
        {
            var result = await Task.Run(() => _services.Collections.PreviewWqlAsync(text, MaxPreviewRows, cts.Token), cts.Token);
            if (generation != _generation) return;
            if (!result.IsValid)
            {
                WqlMessage = SecretMask.Apply(result.Message);
                _lastPreview = null;
                return;
            }
            PreviewColumns = result.Columns;
            foreach (var row in result.FirstRows) PreviewRows.Add(new WqlPreviewRow(row));
            _lastPreview = (text, result.MatchCount);
            WqlMessage = PreviewText(result.MatchCount, result.FirstRows.Count);
            _services.Log.Info(LogArea, $"Previewed a query: {result.MatchCount} matches.");
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) WqlMessage = PreviewCancelled;
        }
        catch (Exception ex)
        {
            if (generation == _generation) WqlMessage = PlainMessage(ex, WqlFailed);
            _services.Log.Warning(LogArea, $"A query preview failed ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_preview, cts)) _preview = null;
            IsPreviewing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsPreviewing))]
    private void CancelPreview()
    {
        try { _preview?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private string PreviewLine(string expression) =>
        _lastPreview is { } last && last.Expression == expression ? PreviewNote(last.Count) : NoPreviewNote;

    /// <summary>The WQL Editor's collection, rule name and query, or the reason they cannot be written.</summary>
    private (CollectionRow? Collection, string Name, string Text, string? Problem) WqlInputs(bool needsName = true)
    {
        if (WqlCollection is not { } row) return (null, "", "", PickCollection);
        if (row.IsBuiltIn) return (null, "", "", BuiltInRefused);
        var name = WqlRuleName.Trim();
        if (needsName && name.Length == 0) return (null, "", "", RuleNameRequired);
        if (name.Any(char.IsControl)) return (null, "", "", CollectionEditorViewModel.NameInvalid);
        var text = WqlText.Trim();
        return (row.Row, name, text, null);
    }

    [RelayCommand]
    private async Task AddRule()
    {
        var (collection, name, text, problem) = WqlInputs();
        if (problem is null && WqlRules.Any(r => string.Equals(r.RuleName, name, StringComparison.OrdinalIgnoreCase))) problem = RuleNameTaken(name);
        if (problem is null && text.Length == 0) problem = QueryRequired;
        if (problem is not null)
        {
            WqlMessage = problem;
            return;
        }
        var preview = PreviewLine(text);
        var run = await RunPlanAsync([Request(CollectionChangeKind.AddRule, collection!, ruleAfter: QueryRule(name, text), impact: [preview])], prepared =>
        {
            var change = prepared[0];
            return (new CollectionPlan("Add rule", "Add query rule", "Add rule",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Add", CollectionText(change.CollectionId, change.CollectionName),
                    $"Query rule {name}: {text}. {preview} Undo removes it."), change)
            ]), null);
        }, m => WqlMessage = m);
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    private static CollectionRule QueryRule(string name, string text) => new(CollectionRuleKind.Query, name, text, "", "", "");

    private bool CanChangeWqlRule() => SelectedWqlRule is not null;

    [RelayCommand(CanExecute = nameof(CanChangeWqlRule))]
    private async Task UpdateRule()
    {
        var (collection, name, text, problem) = WqlInputs();
        if (problem is null && SelectedWqlRule is null) problem = PickRule;
        var old = SelectedWqlRule?.Rule;
        if (problem is null && !string.Equals(name, old!.RuleName, StringComparison.OrdinalIgnoreCase)
            && WqlRules.Any(r => string.Equals(r.RuleName, name, StringComparison.OrdinalIgnoreCase))) problem = RuleNameTaken(name);
        if (problem is null && text.Length == 0) problem = QueryRequired;
        if (problem is null && name == old!.RuleName && text == old.Expression) problem = NothingToChange;
        if (problem is not null)
        {
            WqlMessage = problem;
            return;
        }
        var preview = PreviewLine(text);
        var run = await RunPlanAsync([Request(CollectionChangeKind.UpdateRule, collection!, ruleBefore: old, ruleAfter: QueryRule(name, text), impact: [preview])], prepared =>
        {
            var change = prepared[0];
            var before = change.RuleBefore ?? old!;
            var rename = before.RuleName == name ? "" : $" New name: {name}.";
            return (new CollectionPlan("Update rule", "Update query rule", "Update rule",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Update", CollectionText(change.CollectionId, change.CollectionName),
                    $"Rule {before.RuleName}.{rename} Old: {before.Expression}. New: {text}. {preview} {UpdateOrderNote} Undo sets the old query back."), change)
            ]), null);
        }, m => WqlMessage = m);
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    [RelayCommand(CanExecute = nameof(CanChangeWqlRule))]
    private async Task RemoveRule()
    {
        var (collection, _, _, problem) = WqlInputs(needsName: false);
        if (problem is null && SelectedWqlRule is null) problem = PickRule;
        if (problem is not null)
        {
            WqlMessage = problem;
            return;
        }
        await RemoveRuleAsync(collection!, SelectedWqlRule!.Rule, m => WqlMessage = m);
    }

    // ---- Templates ----

    private IReadOnlyList<QueryTemplate> _templates = [];

    public ObservableCollection<TemplateItemRow> TemplateRows { get; } = [];
    public ObservableCollection<string> Categories { get; } = [AllCategories];
    public ObservableCollection<TemplateParameterRow> TemplateParameters { get; } = [];

    [ObservableProperty] private string _categoryFilter = AllCategories;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTemplate), nameof(HasTemplateParameters))]
    private TemplateItemRow? _selectedTemplate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyTemplateToEditorCommand), nameof(ApplyTemplateCommand))]
    private string _templateWql = "";

    [ObservableProperty] private string _templateMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTemplatesMessage))]
    private string _templatesMessage = "";

    [ObservableProperty] private string _templateCount = "";

    public bool HasTemplatesMessage => TemplatesMessage.Length > 0;
    public bool HasSelectedTemplate => SelectedTemplate is not null;
    public bool HasTemplateParameters => TemplateParameters.Count > 0;

    partial void OnCategoryFilterChanged(string value)
    {
        ApplyTemplateFilter();
        SaveChoicesLater();
    }

    private async Task LoadTemplatesAsync()
    {
        try
        {
            var (templates, message) = await Task.Run(() => (_services.Templates.All(), _services.Templates.Message));
            _templates = templates;
            TemplatesMessage = message is null ? "" : SecretMask.Apply(message);
            var category = CategoryFilter;
            while (Categories.Count > 1) Categories.RemoveAt(1);
            foreach (var c in templates.Select(t => t.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                Categories.Add(c);
            if (category != AllCategories && !Categories.Contains(category)) Categories.Add(category);
            ApplyTemplateFilter();
        }
        catch (Exception ex)
        {
            TemplatesMessage = "The templates could not be read. The log has the details.";
            _services.Log.Warning(LogArea, $"The templates could not be read ({ex.GetType().Name}).");
        }
    }

    private void ApplyTemplateFilter()
    {
        var selected = SelectedTemplate?.Template;
        TemplateRows.Clear();
        foreach (var t in _templates)
            if (CategoryFilter == AllCategories || string.Equals(t.Category, CategoryFilter, StringComparison.OrdinalIgnoreCase))
                TemplateRows.Add(new TemplateItemRow(t));
        SelectedTemplate = selected is null ? null : TemplateRows.FirstOrDefault(r => ReferenceEquals(r.Template, selected));
        TemplateCount = TemplateRows.Count == _templates.Count ? CollectionsText.Count(_templates.Count, "template") : $"{TemplateRows.Count} of {CollectionsText.Count(_templates.Count, "template")}";
    }

    partial void OnSelectedTemplateChanged(TemplateItemRow? value)
    {
        foreach (var p in TemplateParameters) p.PropertyChanged -= OnParameterChanged;
        TemplateParameters.Clear();
        if (value is not null)
        {
            var details = value.Template.ParameterDetails ?? [];
            foreach (var parameter in value.Template.Parameters)
            {
                var placeholder = WqlParameters.Placeholder(parameter);
                var detail = details.FirstOrDefault(d => string.Equals(d.Placeholder, placeholder, StringComparison.Ordinal) || string.Equals(d.Placeholder, parameter, StringComparison.Ordinal));
                var row = new TemplateParameterRow(placeholder, string.IsNullOrWhiteSpace(detail?.Label) ? WqlParameters.Label(parameter) : detail.Label,
                    detail?.HelpText ?? "", detail?.DefaultValue ?? "");
                row.PropertyChanged += OnParameterChanged;
                TemplateParameters.Add(row);
            }
        }
        OnPropertyChanged(nameof(HasTemplateParameters));
        UpdateTemplateWql();
    }

    private void OnParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateTemplateWql();

    private void UpdateTemplateWql()
    {
        if (SelectedTemplate is not { } row)
        {
            TemplateWql = "";
            TemplateMessage = "";
            return;
        }
        if (TemplateParameters.Select(p => WqlParameters.Problem(p.Value)).FirstOrDefault(p => p is not null) is { } problem)
        {
            TemplateWql = "";
            TemplateMessage = problem;
            return;
        }
        TemplateWql = WqlParameters.Insert(row.Template.Expression, TemplateParameters.Select(p => (p.Placeholder, p.Value)));
        TemplateMessage = "";
    }

    private bool HasTemplateWql() => TemplateWql.Length > 0;

    [RelayCommand(CanExecute = nameof(HasTemplateWql))]
    private Task CopyTemplateToEditor()
    {
        if (SelectedTemplate is not { } row || TemplateWql.Length == 0) return Task.CompletedTask;
        SelectedWqlRule = null;
        WqlText = TemplateWql;
        WqlRuleName = row.Name;
        SelectedTabIndex = WqlTab;
        WqlMessage = "";
        return Task.CompletedTask;
    }

    private bool CanApplyTemplate() => !_writing && TemplateWql.Length > 0;

    [RelayCommand(CanExecute = nameof(CanApplyTemplate))]
    private async Task ApplyTemplate()
    {
        if (SelectedTemplate is not { } row || TemplateWql.Length == 0 || !RequireList()) return;
        var wql = TemplateWql;
        var picker = new CollectionPickerViewModel($"Apply template {row.Name}", "Check the collections that get the template as a query rule. Built-in collections are not offered.",
            "Show plan", _collections!.Where(c => !c.IsBuiltIn), p => ApplyTemplateAsync(row.Template, wql, p));
        _services.Prompts.ShowPicker(picker);
        if (picker.Accepted || _lastRunPlan is not null) await AfterWriteAsync();
    }

    private async Task<bool> ApplyTemplateAsync(QueryTemplate template, string wql, CollectionPickerViewModel picker)
    {
        var requests = picker.Checked.Select(c => Request(CollectionChangeKind.ApplyTemplate, c, ruleAfter: QueryRule(template.Name, wql))).ToList();
        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = new List<CollectionPlanEntry>();
            foreach (var change in prepared)
            {
                var target = CollectionText(change.CollectionId, change.CollectionName);
                if (change.RuleBefore is not null)
                {
                    var reason = RuleNameExists(template.Name);
                    entries.Add(CollectionPlanEntry.Skip(new ChangePlanItem("Skip", target, reason), reason));
                    continue;
                }
                entries.Add(CollectionPlanEntry.Write(new ChangePlanItem("Add", target, $"Query rule {template.Name}: {wql}. Undo removes it."), change));
            }
            var writes = entries.Count(e => e.Change is not null);
            if (writes == 0) return (null, $"Every checked collection already has a rule named {template.Name}. Nothing to change.");
            return (new CollectionPlan("Apply template", $"Apply template {template.Name}", $"Add {CollectionsText.Count(writes, "rule")}", entries), null);
        }, picker.ShowMessage);
        return Done(run, picker.ShowMessage);
    }

    // ---- Site queries ----

    private bool _queriesLoaded;

    public ObservableCollection<SavedQueryRow> SavedQueries { get; } = [];

    /// <summary>The saved query read that runs or ran last.</summary>
    public Task QueriesLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadQueryToEditorCommand), nameof(UpdateSavedQueryCommand), nameof(DeleteSavedQueryCommand))]
    private SavedQueryRow? _selectedSavedQuery;

    [ObservableProperty] private string _queriesMessage = "";

    private void ReleaseQueries()
    {
        _queriesLoaded = false;
        SelectedSavedQuery = null;
        SavedQueries.Clear();
        QueriesLoad = Task.CompletedTask;
    }

    private Task StartQueriesLoad()
    {
        QueriesLoad = LoadQueriesAsync(_generation);
        return QueriesLoad;
    }

    [RelayCommand]
    private Task RefreshQueries() => StartQueriesLoad();

    private async Task LoadQueriesAsync(int generation)
    {
        IReadOnlyList<SavedQuery>? queries = null;
        string problem = "";
        try
        {
            queries = await Task.Run(async () =>
            {
                if (ConnectionProblem(_services.LoadSettings()) is { } connection) throw new CollectionServiceException(connection);
                return await _services.Collections.ListSavedQueriesAsync(CancellationToken.None);
            });
        }
        catch (Exception ex)
        {
            problem = PlainMessage(ex, QueriesFailed);
            _services.Log.Warning(LogArea, $"The saved queries could not be read ({FailureName(ex)}).");
        }
        if (generation != _generation) return;
        QueriesMessage = problem;
        if (queries is null) return;
        _queriesLoaded = true;
        var selected = SelectedSavedQuery?.QueryId;
        SavedQueries.Clear();
        foreach (var q in queries.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)) SavedQueries.Add(new SavedQueryRow(q));
        SelectedSavedQuery = SavedQueries.FirstOrDefault(q => q.QueryId == selected);
        _services.Log.Info(LogArea, $"Read {queries.Count} saved queries.");
    }

    private bool HasSelectedQuery() => SelectedSavedQuery is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedQuery))]
    private Task LoadQueryToEditor()
    {
        if (SelectedSavedQuery is not { } query) return Task.CompletedTask;
        SelectedWqlRule = null;
        WqlText = query.Expression;
        SelectedTabIndex = WqlTab;
        WqlMessage = "";
        return Task.CompletedTask;
    }

    private string? QueryNameProblem(string text)
    {
        var name = text.Trim();
        if (name.Any(char.IsControl)) return CollectionEditorViewModel.NameInvalid;
        if (name.Length > 255) return CollectionEditorViewModel.NameTooLong;
        return SavedQueries.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)) ? QueryNameTaken(name) : null;
    }

    [RelayCommand]
    private async Task SaveQueryAsNew()
    {
        var wql = WqlText.Trim();
        if (wql.Length == 0)
        {
            StatusMessage = QueryRequired;
            QueriesMessage = QueryRequired;
            return;
        }
        var prompt = new TextPromptViewModel("Save query", "Name", "The WQL Editor's query is saved as a new ConfigMgr query on the site.", "Show plan", false,
            QueryNameProblem, p => SaveQueryAsync(p, wql));
        _services.Prompts.ShowTextPrompt(prompt);
        if (prompt.Accepted || _lastRunPlan is not null) await AfterWriteAsync();
    }

    private async Task<bool> SaveQueryAsync(TextPromptViewModel prompt, string wql)
    {
        var name = prompt.Text.Trim();
        var run = await RunPlanAsync([QueryRequest(CollectionChangeKind.SaveQuery, null, new SavedQuery("", name, wql, "SMS_R_System", ""))], prepared =>
            (new CollectionPlan("Save query", "Save query", "Save query",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Save", name, $"New saved query: {wql}. Undo deletes it."), prepared[0])
            ]), null), prompt.ShowMessage);
        return Done(run, prompt.ShowMessage);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedQuery))]
    private async Task UpdateSavedQuery()
    {
        if (SelectedSavedQuery is not { } row) return;
        var wql = WqlText.Trim();
        if (wql.Length == 0 || wql == row.Expression)
        {
            QueriesMessage = wql.Length == 0 ? QueryRequired : NothingToChange;
            StatusMessage = QueriesMessage;
            return;
        }
        var run = await RunPlanAsync([QueryRequest(CollectionChangeKind.UpdateQuery, row.Query, row.Query with { Expression = wql })], prepared =>
        {
            var change = prepared[0];
            var before = change.QueryBefore ?? row.Query;
            return (new CollectionPlan("Update query", "Update saved query", "Update query",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Update", before.Name, $"Old: {before.Expression}. New: {wql}. Undo sets the old query back."),
                    change with { QueryAfter = before with { Expression = wql } })
            ]), null);
        });
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedQuery))]
    private async Task DeleteSavedQuery()
    {
        if (SelectedSavedQuery is not { } row) return;
        var run = await RunPlanAsync([QueryRequest(CollectionChangeKind.DeleteQuery, row.Query, null)], prepared =>
        {
            var change = prepared[0];
            var before = change.QueryBefore ?? row.Query;
            return (new CollectionPlan("Delete query", "Delete saved query", "Delete query",
            [
                CollectionPlanEntry.Write(new ChangePlanItem("Delete", before.Name, $"{before.Expression}. Undo saves it again. {CollectionWriter.NewQueryIdNote}"), change)
            ], isDestructive: true), null);
        });
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }
}
