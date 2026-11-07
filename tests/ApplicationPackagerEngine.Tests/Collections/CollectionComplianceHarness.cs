using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>A small ConfigMgr site: collections, rules, members and saved queries the fake changes as the real service would.</summary>
internal sealed class FakeCollectionService : ICollectionService
{
    private int _nextId = 0x100;
    private int _nextQuery = 0x10;

    public const string CancelledSkip = "Skipped: the run was cancelled before this change.";

    public List<CollectionRow> Collections { get; } = [];
    public Dictionary<string, List<CollectionRule>> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<CollectionMember>> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SavedQuery> Queries { get; } = [];
    public List<(string ResourceId, string Name)> Devices { get; } = [];

    public int ListCalls;
    public int GetCalls;
    public int DetailCalls;
    public int PreviewCalls;
    public int ValidateCalls;
    public int QueryListCalls;
    public int PrepareCalls;
    public List<int> DetailPages { get; } = [];
    public List<string> PreviewExpressions { get; } = [];

    public Exception? ListFailure;
    public Exception? GetFailure;
    public TaskCompletionSource? ListGate;
    public TaskCompletionSource? GetGate;
    public TaskCompletionSource? DetailGate;
    public TaskCompletionSource? PreviewGate;
    public int MemberPageSize = 2;

    public List<IReadOnlyList<CollectionChange>> Applied { get; } = [];

    /// <summary>An exception makes the state check of that request fail, as the service's checks do.</summary>
    public Func<CollectionChange, Exception?>? PrepareFails;

    /// <summary>A message makes the change fail; null lets it succeed.</summary>
    public Func<CollectionChange, string?>? Fail;

    /// <summary>Set while a confirmed change plan runs; a write outside it fails the test.</summary>
    public Func<bool>? InConfirmedRun;

    /// <summary>Called after each change was reported; a test can cancel here.</summary>
    public Action<int>? AfterChange;
    public TaskCompletionSource? ApplyGate;



    public static CollectionRule QueryRule(string name, string wql) => new(CollectionRuleKind.Query, name, wql, "", "", "");

    public CollectionRow Add(string id, string name, string folder = "", string limitingId = "SMS00001", int members = 3, string refresh = "Periodic")
    {
        var limiting = Collections.FirstOrDefault(c => c.CollectionId == limitingId);
        var row = new CollectionRow(id, name, folder, id.StartsWith("SMS", StringComparison.OrdinalIgnoreCase) ? "" : limitingId,
            id.StartsWith("SMS", StringComparison.OrdinalIgnoreCase) ? "" : limiting?.Name ?? "All Systems", members,
            id.StartsWith("SMS", StringComparison.OrdinalIgnoreCase), refresh, "", 0, 0, 0, 0);
        Collections.Add(row);
        Rules[id] = [];
        Members[id] = Enumerable.Range(1, members).Select(i => new CollectionMember($"{16777000 + i}", $"{name}-PC{i:00}", "Active", i == 1)).ToList();
        return row;
    }

    public void AddRule(string collectionId, CollectionRule rule)
    {
        lock (Collections)
        {
            Rules[collectionId].Add(rule);
            Recount(collectionId);
        }
    }

    private void Recount(string id)
    {
        var index = Collections.FindIndex(c => c.CollectionId == id);
        if (index < 0) return;
        var rules = Rules[id];
        Collections[index] = Collections[index] with
        {
            DirectRuleCount = rules.Count(r => r.Kind == CollectionRuleKind.Direct),
            QueryRuleCount = rules.Count(r => r.Kind == CollectionRuleKind.Query),
            IncludeRuleCount = rules.Count(r => r.Kind == CollectionRuleKind.Include),
            ExcludeRuleCount = rules.Count(r => r.Kind == CollectionRuleKind.Exclude)
        };
    }

    private CollectionRow? Find(string id)
    {
        lock (Collections) return Collections.FirstOrDefault(c => string.Equals(c.CollectionId, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<CollectionRow>> ListCollectionsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        if (ListGate is { } gate) await gate.Task.WaitAsync(ct);
        if (ListFailure is { } failure) throw failure;
        lock (Collections) return Collections.ToList();
    }

    public async Task<CollectionRow?> GetCollectionAsync(string collectionId, CancellationToken ct)
    {
        Interlocked.Increment(ref GetCalls);
        if (GetGate is { } gate) await gate.Task.WaitAsync(ct);
        if (GetFailure is { } failure) throw failure;
        return Find(collectionId);
    }

    /// <summary>The device name as a single-quoted WQL literal: a backslash escapes a quote or a backslash.</summary>
    private static string Quoted(string name) => "'" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    public async Task<CollectionDetail> GetDetailAsync(string collectionId, int memberPage, CancellationToken ct)
    {
        Interlocked.Increment(ref DetailCalls);
        lock (DetailPages) DetailPages.Add(memberPage);
        if (DetailGate is { } gate) await gate.Task.WaitAsync(ct);
        lock (Collections)
        {
            var row = Find(collectionId) ?? throw new CollectionServiceException($"The collection {collectionId} no longer exists.");
            var members = Members[row.CollectionId];
            return new CollectionDetail(row, Rules[row.CollectionId].ToList(), members.Skip(memberPage * MemberPageSize).Take(MemberPageSize).ToList(), members.Count);
        }
    }

    public Task<WqlResult> ValidateWqlAsync(string expression, CancellationToken ct)
    {
        Interlocked.Increment(ref ValidateCalls);
        return Task.FromResult((expression.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)
            ? new WqlResult(true, "The query is valid.", 0, [], [])
            : new WqlResult(false, "The query is not a valid WQL select statement.", 0, [], [])));
    }

    public async Task<WqlResult> PreviewWqlAsync(string expression, int maxRows, CancellationToken ct)
    {
        Interlocked.Increment(ref PreviewCalls);
        lock (PreviewExpressions) PreviewExpressions.Add(expression);
        if (PreviewGate is { } gate) await gate.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!expression.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
            return new WqlResult(false, "The query is not a valid WQL select statement.", 0, [], []);
        var matches = Devices.Where(d => expression.Contains(Quoted(d.Name), StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(expression, @"ResourceId = " + d.ResourceId + @"\b")).ToList();
        if (matches.Count == 0 && !expression.Contains(" where ", StringComparison.OrdinalIgnoreCase)) matches = Devices.ToList();
        return new WqlResult(true, $"{matches.Count} matches.", matches.Count, ["SMS_R_System.ResourceId", "SMS_R_System.Name"],
            matches.Take(maxRows).Select(d => (IReadOnlyList<string>)[d.ResourceId, d.Name]).ToList());
    }

    public Task<IReadOnlyList<SavedQuery>> ListSavedQueriesAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref QueryListCalls);
        lock (Collections) return Task.FromResult<IReadOnlyList<SavedQuery>>(Queries.ToList());
    }

    private static CollectionRule? RuleOf(CollectionChange change) => change.RuleAfter ?? change.RuleBefore;

    private CollectionRule? FindRule(string collectionId, CollectionRule? rule)
    {
        if (rule is null || !Rules.TryGetValue(collectionId, out var rules)) return null;
        return rules.FirstOrDefault(r => r.Kind == rule.Kind && string.Equals(r.RuleName, rule.RuleName, StringComparison.OrdinalIgnoreCase)
            && (r.Kind != CollectionRuleKind.Direct || r.ResourceId == rule.ResourceId));
    }

    public Task<CollectionChange> PrepareAsync(CollectionChange request, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareFails?.Invoke(request) is { } failure) throw failure;
        lock (Collections)
        {
            var row = Find(request.CollectionId);
            switch (request.Kind)
            {
                case CollectionChangeKind.Create:
                    if (Collections.Any(c => string.Equals(c.Name, request.AfterRow!.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new CollectionServiceException($"A collection named {request.AfterRow!.Name} already exists.");
                    return Task.FromResult(request);
                case CollectionChangeKind.Copy:
                    if (row is null) throw new CollectionServiceException($"The collection {request.CollectionName} no longer exists.");
                    return Task.FromResult(request with { BeforeRow = row });
                case CollectionChangeKind.SaveQuery:
                    if (Queries.Any(q => string.Equals(q.Name, request.QueryAfter!.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new CollectionServiceException($"A saved query named {request.QueryAfter!.Name} already exists.");
                    return Task.FromResult(request);
                case CollectionChangeKind.UpdateQuery:
                case CollectionChangeKind.DeleteQuery:
                {
                    var id = (request.QueryBefore ?? request.QueryAfter)!.QueryId;
                    var query = Queries.FirstOrDefault(q => q.QueryId == id) ?? throw new CollectionServiceException("The saved query no longer exists.");
                    return Task.FromResult(request with { QueryBefore = query });
                }
            }
            if (row is null) throw new CollectionServiceException($"The collection {request.CollectionName} no longer exists.");
            switch (request.Kind)
            {
                case CollectionChangeKind.AddRule:
                case CollectionChangeKind.ApplyTemplate:
                {
                    var existing = FindRule(row.CollectionId, request.RuleAfter);
                    if (existing is not null && request.Kind == CollectionChangeKind.AddRule)
                        throw new CollectionServiceException($"{row.Name} already has a rule named {request.RuleAfter!.RuleName}.");
                    return Task.FromResult(request with { BeforeRow = row, RuleBefore = existing });
                }
                case CollectionChangeKind.UpdateRule:
                case CollectionChangeKind.RemoveRule:
                {
                    var current = FindRule(row.CollectionId, request.RuleBefore)
                        ?? throw new CollectionServiceException($"{row.Name} no longer has the rule {request.RuleBefore!.RuleName}.");
                    return Task.FromResult(request with { BeforeRow = row, RuleBefore = current });
                }
                case CollectionChangeKind.Remove:
                    if (row.IsBuiltIn) throw new CollectionServiceException($"{row.Name} is a built-in collection and cannot be removed.");
                    if (Collections.Any(c => c.LimitingCollectionId == row.CollectionId))
                        throw new CollectionServiceException($"{row.Name} is the limiting collection of another collection and cannot be removed.");
                    return Task.FromResult(request with
                    {
                        BeforeRow = row,
                        Rules = Rules[row.CollectionId].ToList(),
                        Impact =[$"{row.MemberCount} members.", "1 deployment is removed with the collection.", "0 maintenance windows are removed with the collection."]
                    });
                default:
                    return Task.FromResult(request with { BeforeRow = row });
            }
        }
    }

    public async Task<IReadOnlyList<CollectionOutcome>> ApplyAsync(IReadOnlyList<CollectionChange> changes, IProgress<CollectionOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } check) Assert.True(check(), "A write ran outside a confirmed change plan.");
        lock (Applied) Applied.Add(changes.ToList());
        if (ApplyGate is { } gate) await gate.Task;
        var outcomes = new List<CollectionOutcome>();
        for (var i = 0; i < changes.Count; i++)
        {
            var outcome = ct.IsCancellationRequested ? new CollectionOutcome(changes[i], false, CancelledSkip, null) : Apply(changes[i]);
            outcomes.Add(outcome);
            progress.Report(outcome);
            AfterChange?.Invoke(i);
            await Task.Yield();
        }
        return outcomes;
    }

    private static CollectionChange Change(CollectionChangeKind kind, string id, string name, CollectionRow? before = null, CollectionRow? after = null,
        CollectionRule? ruleBefore = null, CollectionRule? ruleAfter = null, SavedQuery? queryBefore = null, SavedQuery? queryAfter = null) =>
        new(kind, id, name, before, after, ruleBefore, ruleAfter, queryBefore, queryAfter, []);

    private CollectionOutcome Apply(CollectionChange change)
    {
        if (Fail?.Invoke(change) is { } message) return new CollectionOutcome(change, false, message, null);
        lock (Collections)
        {
            var row = Find(change.CollectionId);
            switch (change.Kind)
            {
                case CollectionChangeKind.Create:
                case CollectionChangeKind.Copy:
                {
                    var id = $"LAB{_nextId++:X5}";
                    var source = change.Kind == CollectionChangeKind.Copy ? row! : change.AfterRow!;
                    var created = source with { CollectionId = id, Name = change.AfterRow!.Name, IsBuiltIn = false };
                    Collections.Add(created);
                    Rules[id] = change.Kind == CollectionChangeKind.Copy ? Rules[row!.CollectionId].ToList() : (change.Rules ?? []).ToList();
                    Members[id] = [];
                    return new CollectionOutcome(change, true, "Created.", Change(CollectionChangeKind.Remove, id, created.Name, before: created));
                }
                case CollectionChangeKind.SetProperties:
                {
                    var index = Collections.IndexOf(row!);
                    Collections[index] = change.AfterRow! with { CollectionId = row!.CollectionId };
                    return new CollectionOutcome(change, true, "Updated.",
                        Change(CollectionChangeKind.SetProperties, row.CollectionId, change.AfterRow!.Name, before: Collections[index], after: row));
                }
                case CollectionChangeKind.AddRule:
                case CollectionChangeKind.ApplyTemplate:
                    Rules[row!.CollectionId].Add(change.RuleAfter!);
                    Recount(row.CollectionId);
                    return new CollectionOutcome(change, true, "Added.", Change(CollectionChangeKind.RemoveRule, row.CollectionId, row.Name, ruleBefore: change.RuleAfter));
                case CollectionChangeKind.UpdateRule:
                {
                    var rules = Rules[row!.CollectionId];
                    rules.Add(change.RuleAfter!);
                    rules.Remove(FindRule(row.CollectionId, change.RuleBefore)!);
                    return new CollectionOutcome(change, true, "Updated.",
                        Change(CollectionChangeKind.UpdateRule, row.CollectionId, row.Name, ruleBefore: change.RuleAfter, ruleAfter: change.RuleBefore));
                }
                case CollectionChangeKind.RemoveRule:
                    Rules[row!.CollectionId].Remove(FindRule(row.CollectionId, change.RuleBefore)!);
                    Recount(row.CollectionId);
                    return new CollectionOutcome(change, true, "Removed.", Change(CollectionChangeKind.AddRule, row.CollectionId, row.Name, ruleAfter: change.RuleBefore));
                case CollectionChangeKind.Evaluate:
                    return new CollectionOutcome(change, true, "The membership update started.", null);
                case CollectionChangeKind.Remove:
                    Collections.Remove(row!);
                    return new CollectionOutcome(change, true, "Removed.", Change(CollectionChangeKind.Create, "", row!.Name, after: row) with { Rules = change.Rules });
                case CollectionChangeKind.SaveQuery:
                {
                    var saved = change.QueryAfter! with { QueryId = $"LAB{_nextQuery++:X5}" };
                    Queries.Add(saved);
                    return new CollectionOutcome(change, true, "Saved.", Change(CollectionChangeKind.DeleteQuery, "", saved.Name, queryBefore: saved));
                }
                case CollectionChangeKind.UpdateQuery:
                {
                    var index = Queries.FindIndex(q => q.QueryId == change.QueryBefore!.QueryId);
                    var before = Queries[index];
                    Queries[index] = change.QueryAfter!;
                    return new CollectionOutcome(change, true, "Updated.", Change(CollectionChangeKind.UpdateQuery, "", before.Name, queryBefore: change.QueryAfter, queryAfter: before));
                }
                default:
                    Queries.RemoveAll(q => q.QueryId == change.QueryBefore!.QueryId);
                    return new CollectionOutcome(change, true, "Deleted.", Change(CollectionChangeKind.SaveQuery, "", change.QueryBefore!.Name, queryAfter: change.QueryBefore));
            }
        }
    }
}

internal sealed class FakeQueryTemplateStore : IQueryTemplateStore
{
    public List<QueryTemplate> Templates { get; } = [];
    public string? Message { get; set; }
    public int Reads;

    public IReadOnlyList<QueryTemplate> All()
    {
        Interlocked.Increment(ref Reads);
        lock (Templates) return Templates.ToList();
    }
}

internal sealed class FakeCollectionReportWriter : ICollectionReportWriter
{
    public List<IReadOnlyList<CollectionRow>> Written { get; } = [];
    public Exception? Failure;

    public string WriteCsv(IReadOnlyList<CollectionRow> rows)
    {
        if (Failure is { } failure) throw failure;
        Written.Add(rows);
        return @"C:\data\Collections\Reports\collections.csv";
    }

    public string WriteHtml(IReadOnlyList<CollectionRow> rows)
    {
        if (Failure is { } failure) throw failure;
        Written.Add(rows);
        return @"C:\data\Collections\Reports\collections.html";
    }
}

internal sealed class FakeCollectionPrompts : ICollectionCompliancePrompts
{
    public Action<CollectionEditorViewModel>? Editor;
    public Action<CollectionPickerViewModel>? Picker;
    public Action<TextPromptViewModel>? Prompt;
    public List<string> OpenedFolders { get; } = [];
    public int EditorShows;
    public int PickerShows;
    public int PromptShows;

    public void ShowCollectionEditor(CollectionEditorViewModel editor)
    {
        EditorShows++;
        Editor?.Invoke(editor);
    }

    public void ShowPicker(CollectionPickerViewModel picker)
    {
        PickerShows++;
        Picker?.Invoke(picker);
    }

    public void ShowTextPrompt(TextPromptViewModel prompt)
    {
        PromptShows++;
        Prompt?.Invoke(prompt);
    }

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);

    public static void Save(CollectionEditorViewModel editor) => ScriptedPlanDialogs.Wait(editor.SaveCommand.ExecuteAsync(null));

    public static void Accept(CollectionPickerViewModel picker) => ScriptedPlanDialogs.Wait(picker.AcceptCommand.ExecuteAsync(null));

    public static void Accept(TextPromptViewModel prompt) => ScriptedPlanDialogs.Wait(prompt.AcceptCommand.ExecuteAsync(null));
}

internal sealed class CollectionComplianceHarness
{
    public FakeCollectionService Service { get; } = new();
    public FakeQueryTemplateStore Store { get; } = new();
    public FakeCollectionReportWriter Reports { get; } = new();
    public FakeCollectionPrompts Prompts { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public AppSettings Settings { get; } = new() { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
    public CollectionComplianceSettings? Saved { get; set; }
    public Action<CollectionComplianceSettings>? BeforeSave;
    public int PreferenceSaves;
    public int SettingsLoads;

    public CollectionRow AllSystems { get; }
    public CollectionRow Servers { get; }
    public CollectionRow Workstations { get; }
    public CollectionRow Pilot { get; }

    public const string ServersWql = "select SMS_R_System.ResourceId from SMS_R_System where SMS_R_System.OperatingSystemNameandVersion like '%Server%'";

    public CollectionComplianceHarness()
    {
        AllSystems = Service.Add("SMS00001", "All Systems", members: 5);
        Servers = Service.Add("LAB00010", "Servers", @"Patching\Servers");
        Workstations = Service.Add("LAB00011", "Workstations", @"Patching\Clients", members: 0);
        Pilot = Service.Add("LAB00012", "Pilot", "", limitingId: "LAB00011", members: 1);
        Service.AddRule("LAB00010", FakeCollectionService.QueryRule("All servers", ServersWql));
        Service.AddRule("LAB00010", new CollectionRule(CollectionRuleKind.Direct, "SRV01", "", "16777001", "", ""));
        Service.AddRule("LAB00012", new CollectionRule(CollectionRuleKind.Include, "Servers", "", "", "LAB00010", ""));
        Service.AddRule("LAB00012", new CollectionRule(CollectionRuleKind.Exclude, "Workstations", "", "", "LAB00011", ""));
        Servers = Service.Collections.Single(c => c.CollectionId == "LAB00010");
        Pilot = Service.Collections.Single(c => c.CollectionId == "LAB00012");
        Service.Devices.Add(("16777201", "PC01"));
        Service.Devices.Add(("16777202", "PC02"));
        Service.Queries.Add(new SavedQuery("LAB00001", "All clients", "select * from SMS_R_System where Client = 1", "SMS_R_System", ""));
        Service.Queries.Add(new SavedQuery("LAB00002", "Old OS", "select * from SMS_R_System where Build < '10.0'", "SMS_R_System", ""));
        Store.Templates.Add(new QueryTemplate("all-workstations", "All workstations", "Operating system",
            "select SMS_R_System.ResourceId from SMS_R_System where SMS_R_System.OperatingSystemNameandVersion like '%Workstation%'", [], "Every workstation.", true));
        Store.Templates.Add(new QueryTemplate("app-by-name", "Software installed by name", "Software",
            "select SMS_R_System.ResourceId from SMS_R_System inner join SMS_G_System_ADD_REMOVE_PROGRAMS on SMS_G_System_ADD_REMOVE_PROGRAMS.ResourceID = SMS_R_System.ResourceId where SMS_G_System_ADD_REMOVE_PROGRAMS.DisplayName like '%{SoftwareName}%'",
            ["{SoftwareName}"], "Devices with the software.", false,
            [new QueryTemplateParameter("{SoftwareName}", "Software name", "Part of the display name, for example 7-Zip.", "")]));
        Store.Templates.Add(new QueryTemplate("os-build", "OS build", "Operating system",
            "select SMS_R_System.ResourceId from SMS_R_System where SMS_R_System.Build = {Build}", ["Build"], "Devices of one build.", true));
        Service.InConfirmedRun = () => Plans.InRun;
    }

    public CollectionWriter Writer() => new(Service, History, Log, () => "LAB");

    public CollectionComplianceViewModel Create() => new(new CollectionCompliancePageServices(
        Service, Store, Reports, Prompts, Plans, Writer(),
        () => { Interlocked.Increment(ref SettingsLoads); return Settings; },
        () => Saved,
        s =>
        {
            BeforeSave?.Invoke(s);
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
        },
        Log));

    public async Task<CollectionComplianceViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        return viewModel;
    }
}
