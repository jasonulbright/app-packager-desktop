using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Collections;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>A small compliance site: items, baselines, deployments and settings the fake changes as the real service would.</summary>
internal sealed class FakeComplianceService : IComplianceService
{
    public const string CancelledSkip = "Skipped: the run was cancelled before this change.";
    public const string UnconfirmedSkip = "Skipped: Remediation changes client settings and needs its own confirmation, so nothing was changed.";

    private int _next = 0x100;

    public List<ConfigurationItemRow> Items { get; } = [];
    public List<BaselineRow> Baselines { get; } = [];
    public List<BaselineDeploymentRow> Deployments { get; } = [];
    public Dictionary<string, List<CiSetting>> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DriftRow> Drift { get; } = [];
    public List<ComplianceRow> Compliance { get; } = [];

    public int ItemCalls;
    public int BaselineCalls;
    public int DeploymentCalls;
    public int SettingCalls;
    public int DriftCalls;
    public int ComplianceCalls;
    public int PrepareCalls;
    public List<string?> DeploymentFilters { get; } = [];

    public TaskCompletionSource? ListGate;
    public TaskCompletionSource? SettingsGate;
    public Exception? ListFailure;

    /// <summary>Holds a catalog deploy after its first step until the test releases it.</summary>
    public TaskCompletionSource? DeployGate;

    /// <summary>Holds every write until the test releases it; the writes then run whether or not the run was cancelled.</summary>
    public TaskCompletionSource? ApplyGate;

    public List<IReadOnlyList<ComplianceChange>> Applied { get; } = [];
    public Func<ComplianceChange, Exception?>? PrepareFails;

    /// <summary>Changes a prepared change as the real service fills it from the live state.</summary>
    public Func<ComplianceChange, ComplianceChange>? Prepared;

    /// <summary>Changes the undo change of each outcome as the real service fills it.</summary>
    public Func<ComplianceChange, ComplianceChange>? UndoFill;
    public Func<ComplianceChange, string?>? Fail;
    public Func<bool>? InConfirmedRun;
    public List<string> Impact { get; } = [];

    public ConfigurationItemRow AddItem(string id, string name, int baselines = 1)
    {
        var row = new ConfigurationItemRow(id, name, "", 1, 0, baselines, new DateTime(2026, 10, 1, 8, 0, 0));
        Items.Add(row);
        Settings[id] = [];
        return row;
    }

    public void AddSetting(string ciId, CiSetting setting)
    {
        Settings[ciId].Add(setting);
        var index = Items.FindIndex(i => i.CiId == ciId);
        Items[index] = Items[index] with { SettingCount = Settings[ciId].Count };
    }

    public BaselineRow AddBaseline(string id, string name, string? catalogId = null, string? version = null)
    {
        var row = new BaselineRow(id, name, "", 1, catalogId, version, 1, 0, new DateTime(2026, 10, 2, 8, 0, 0));
        Baselines.Add(row);
        return row;
    }

    public BaselineDeploymentRow AddDeployment(BaselineRow baseline, string collectionId, string collectionName, bool remediate = false, int compliant = 3, int nonCompliant = 1)
    {
        var row = new BaselineDeploymentRow($"{{DEP-{_next++:X4}}}", baseline.BaselineId, baseline.Name, collectionId, collectionName, remediate, "Every 1 day",
            compliant, nonCompliant, 0, 1, new DateTime(2026, 10, 18, 7, 0, 0));
        Deployments.Add(row);
        Recount();
        return row;
    }

    private void Recount()
    {
        for (var i = 0; i < Baselines.Count; i++)
            Baselines[i] = Baselines[i] with { DeploymentCount = Deployments.Count(d => d.BaselineId == Baselines[i].BaselineId) };
    }

    public async Task<IReadOnlyList<ConfigurationItemRow>> ListConfigurationItemsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ItemCalls);
        if (ListGate is { } gate) await gate.Task.WaitAsync(ct);
        if (ListFailure is { } failure) throw failure;
        lock (Items) return Items.ToList();
    }

    public Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref BaselineCalls);
        lock (Items) return Task.FromResult<IReadOnlyList<BaselineRow>>(Baselines.ToList());
    }

    public Task<IReadOnlyList<BaselineDeploymentRow>> ListDeploymentsAsync(string? baselineId, CancellationToken ct)
    {
        Interlocked.Increment(ref DeploymentCalls);
        lock (DeploymentFilters) DeploymentFilters.Add(baselineId);
        lock (Items) return Task.FromResult<IReadOnlyList<BaselineDeploymentRow>>(Deployments.Where(d => baselineId is null || d.BaselineId == baselineId).ToList());
    }

    public async Task<IReadOnlyList<CiSetting>> ListSettingsAsync(string ciId, CancellationToken ct)
    {
        var held = SettingsGate;
        Interlocked.Increment(ref SettingCalls);
        if (held is { } gate) await gate.Task.WaitAsync(ct);
        lock (Items) return Settings.TryGetValue(ciId, out var list) ? list.ToList() : [];
    }

    public Task<IReadOnlyList<DriftRow>> GetDriftAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref DriftCalls);
        lock (Items) return Task.FromResult<IReadOnlyList<DriftRow>>(Drift.ToList());
    }

    public Task<IReadOnlyList<ComplianceRow>> GetComplianceAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ComplianceCalls);
        lock (Items) return Task.FromResult<IReadOnlyList<ComplianceRow>>(Compliance.ToList());
    }

    public async Task<ComplianceChange> PrepareAsync(ComplianceChange request, CancellationToken ct)
    {
        var prepared = await PrepareCoreAsync(request);
        return Prepared is { } fill ? fill(prepared) : prepared;
    }

    private Task<ComplianceChange> PrepareCoreAsync(ComplianceChange request)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareFails?.Invoke(request) is { } failure) throw failure;
        lock (Items)
        {
            if (request.DeploymentBefore is { } d)
            {
                var current = Deployments.FirstOrDefault(x => x.DeploymentId == d.DeploymentId)
                    ?? throw new ComplianceServiceException($"The deployment of {d.BaselineName} to {d.CollectionName} no longer exists.");
                return Task.FromResult(request with { DeploymentBefore = current, Impact = Impact.ToList() });
            }
            if (request.SettingBefore is { } s)
            {
                var current = Settings[request.CiId!].FirstOrDefault(x => x.SettingId == s.SettingId)
                    ?? throw new ComplianceServiceException($"The setting {s.Name} no longer exists.");
                return Task.FromResult(request with { SettingBefore = current, Impact = Impact.ToList() });
            }
            return Task.FromResult(request with { Impact = Impact.ToList() });
        }
    }

    public async Task<IReadOnlyList<ComplianceOutcome>> ApplyAsync(IReadOnlyList<ComplianceChange> changes, IProgress<ComplianceOutcome> progress, CancellationToken ct)
    {
        if (InConfirmedRun is { } check) Assert.True(check(), "A write ran outside a confirmed change plan.");
        lock (Applied) Applied.Add(changes.ToList());
        if (ApplyGate is { } held) await held.Task;
        var outcomes = new List<ComplianceOutcome>();
        foreach (var change in changes)
        {
            ComplianceOutcome outcome;
            if (ct.IsCancellationRequested && ApplyGate is null) outcome = new ComplianceOutcome(change, false, CancelledSkip, null, []);
            else if (change.Kind == ComplianceChangeKind.CatalogDeploy && !change.Values.ContainsKey(ComplianceRequests.CreateDependencyKey))
                outcome = await DeployAsync(change, progress, ct);
            else outcome = Apply(change);
            if (UndoFill is { } fill && outcome.UndoChange is { } undo) outcome = outcome with { UndoChange = fill(undo) };
            outcomes.Add(outcome);
            progress.Report(outcome);
            await Task.Yield();
        }
        return outcomes;
    }

    private async Task<ComplianceOutcome> DeployAsync(ComplianceChange change, IProgress<ComplianceOutcome> progress, CancellationToken ct)
    {
        if (ComplianceWriter.NeedsRemediationConfirmation(change) && !change.RemediationConfirmed)
            return new ComplianceOutcome(change, false, UnconfirmedSkip, null, []);
        progress.Report(new ComplianceOutcome(change, true, "Build: running the builder.", null, ["Build: running the builder."], InProgress: true));
        if (DeployGate is { } gate)
        {
            try { await gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                return new ComplianceOutcome(change, false, "Cancelled after the build started. 1 configuration item was created; Undo removes it.",
                    change with { Kind = ComplianceChangeKind.RemoveCatalogObjects, CreatedIds = ["ConfigurationItem:16777300"] }, ["Build: running the builder.", "Build: cancelled."]);
            }
        }
        if (Fail?.Invoke(change) is { } message) return new ComplianceOutcome(change, false, message, null, ["Build: failed."]);
        lock (Items)
        {
            var item = AddItem($"CI-{_next++:X4}", "Created item", 1);
            var baseline = AddBaseline($"CB-{_next++:X4}", "Created baseline", change.CatalogId, "1.0.0");
            foreach (var target in change.TargetCollectionIds) AddDeployment(baseline, target, target, change.RemediateAfter == true);
            return new ComplianceOutcome(change, true, "Created 1 configuration item and 1 baseline.",
                change with { Kind = ComplianceChangeKind.RemoveCatalogObjects, CreatedIds = [$"Baseline:{baseline.BaselineId}", $"ConfigurationItem:{item.CiId}"] }, ["Build: done.", "Deploy: done.", "Tag: done."]);
        }
    }

    private ComplianceOutcome Apply(ComplianceChange change)
    {
        if (Fail?.Invoke(change) is { } message) return new ComplianceOutcome(change, false, message, null, []);
        if (ComplianceWriter.NeedsRemediationConfirmation(change) && !change.RemediationConfirmed)
            return new ComplianceOutcome(change, false, UnconfirmedSkip, null, []);
        lock (Items)
        {
            switch (change.Kind)
            {
                case ComplianceChangeKind.SetRemediation:
                {
                    var index = Deployments.FindIndex(d => d.DeploymentId == change.DeploymentBefore!.DeploymentId);
                    var before = Deployments[index];
                    Deployments[index] = before with { RemediateNoncompliant = change.RemediateAfter!.Value };
                    return new ComplianceOutcome(change, true, "Updated.", ComplianceRequests.SetRemediation(Deployments[index], before.RemediateNoncompliant), []);
                }
                case ComplianceChangeKind.DeleteDeployment:
                {
                    var before = Deployments.Single(d => d.DeploymentId == change.DeploymentBefore!.DeploymentId);
                    Deployments.Remove(before);
                    Recount();
                    return new ComplianceOutcome(change, true, "Deleted.", ComplianceRequests.Delete(before) with { Kind = ComplianceChangeKind.CreateDeployment }, []);
                }
                case ComplianceChangeKind.Redeploy:
                {
                    var before = change.DeploymentBefore!;
                    var again = before with { DeploymentId = $"{{DEP-{_next++:X4}}}" };
                    Deployments.Add(again);
                    Deployments.RemoveAll(d => d.DeploymentId == before.DeploymentId);
                    Recount();
                    return new ComplianceOutcome(change, true, "Redeployed.", null, []);
                }
                case ComplianceChangeKind.Reassign:
                {
                    var before = change.DeploymentBefore!;
                    var index = Deployments.FindIndex(d => d.DeploymentId == before.DeploymentId);
                    Deployments[index] = before with { DeploymentId = $"{{DEP-{_next++:X4}}}", CollectionId = change.ToCollectionId!, CollectionName = change.ToCollectionId! };
                    return new ComplianceOutcome(change, true, "Reassigned.", ComplianceRequests.Reassign(Deployments[index], before.CollectionId), []);
                }
                case ComplianceChangeKind.AddSetting:
                {
                    var added = change.SettingAfter! with { SettingId = $"S-{_next++:X4}" };
                    AddSetting(change.CiId!, added);
                    Bump(change.CiId!);
                    return new ComplianceOutcome(change, true, "Added.", ComplianceRequests.RemoveSetting(change.CiId!, added), []);
                }
                case ComplianceChangeKind.EditSetting:
                {
                    var list = Settings[change.CiId!];
                    var index = list.FindIndex(s => s.SettingId == change.SettingBefore!.SettingId);
                    var before = list[index];
                    list[index] = change.SettingAfter! with { SettingId = before.SettingId };
                    Bump(change.CiId!);
                    return new ComplianceOutcome(change, true, "Updated.", ComplianceRequests.EditSetting(change.CiId!, list[index], before), []);
                }
                case ComplianceChangeKind.RemoveSetting:
                {
                    var list = Settings[change.CiId!];
                    list.RemoveAll(s => s.SettingId == change.SettingBefore!.SettingId);
                    Bump(change.CiId!);
                    return new ComplianceOutcome(change, true, "Removed.", ComplianceRequests.AddSetting(change.CiId!, change.SettingBefore!), []);
                }
                case ComplianceChangeKind.CreateDeployment:
                {
                    var made = change.DeploymentBefore! with { DeploymentId = $"{{DEP-{_next++:X4}}}" };
                    Deployments.Add(made);
                    Recount();
                    return new ComplianceOutcome(change, true, "Created.", ComplianceRequests.Delete(made), []);
                }
                default:
                    return new ComplianceOutcome(change, true, "Created.", change with { CreatedIds = ["MCM-DEP"] }, []);
            }
        }
    }

    private void Bump(string ciId)
    {
        var index = Items.FindIndex(i => i.CiId == ciId);
        Items[index] = Items[index] with { Revision = Items[index].Revision + 1, SettingCount = Settings[ciId].Count };
    }
}

internal sealed class FakeCatalogService : ICatalogService
{
    public List<CatalogEntry> Entries { get; } = [];
    public string? Message { get; set; }
    public int Reads;

    public IReadOnlyList<CatalogEntry> All()
    {
        Interlocked.Increment(ref Reads);
        return Entries.ToList();
    }

    public CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);
}

internal sealed class FakeComplianceReportWriter : IComplianceReportWriter
{
    public List<(IReadOnlyList<DriftRow> Drift, IReadOnlyList<ComplianceRow> Compliance)> Written { get; } = [];
    public Exception? Failure;

    public string WriteCsv(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance)
    {
        if (Failure is { } failure) throw failure;
        Written.Add((drift, compliance));
        return @"C:\data\Collections\Reports\compliance.csv";
    }

    public string WriteHtml(IReadOnlyList<DriftRow> drift, IReadOnlyList<ComplianceRow> compliance)
    {
        if (Failure is { } failure) throw failure;
        Written.Add((drift, compliance));
        return @"C:\data\Collections\Reports\compliance.html";
    }
}

internal sealed class FakeComplianceDialogs : IComplianceDialogs
{
    public Action<CatalogDeployViewModel>? Deploy;
    public Action<CiSettingEditorViewModel>? SettingEditor;
    public Action<CollectionPickerViewModel>? Picker;
    public List<string> OpenedFolders { get; } = [];
    public int DeployShows;
    public int EditorShows;
    public int PickerShows;

    public void ShowDeploy(CatalogDeployViewModel deploy)
    {
        DeployShows++;
        Deploy?.Invoke(deploy);
    }

    public void ShowSettingEditor(CiSettingEditorViewModel editor)
    {
        EditorShows++;
        SettingEditor?.Invoke(editor);
    }

    public void ShowPicker(CollectionPickerViewModel picker)
    {
        PickerShows++;
        Picker?.Invoke(picker);
    }

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);

    public static void Run(Task task) => ScriptedPlanDialogs.Wait(task);
}

internal sealed class ComplianceHarness
{
    public FakeComplianceService Service { get; } = new();
    public FakeCatalogService Catalog { get; } = new();
    public FakeCollectionService Collections { get; } = new();
    public FakeComplianceReportWriter Reports { get; } = new();
    public FakeComplianceDialogs Dialogs { get; } = new();
    public ScriptedPlanDialogs Plans { get; } = new();
    public RecordingSiteHistory History { get; } = new();
    public RecordingMaintenanceLog Log { get; } = new();
    public AppSettings Settings { get; } = new() { SiteCode = "LAB", ProviderMachine = "cm01.lab.local" };
    public ComplianceSettings? Saved { get; set; }
    public Action<ComplianceSettings>? BeforeSave;
    public int PreferenceSaves;

    public CatalogEntry Autorun { get; }
    public CatalogEntry SpecExec { get; }
    public CatalogEntry Wdigest { get; }
    public ConfigurationItemRow Smb { get; }
    public ConfigurationItemRow Tls { get; }
    public BaselineRow SmbBaseline { get; }
    public BaselineRow TlsBaseline { get; }
    public BaselineDeploymentRow SmbOnServers { get; }
    public BaselineDeploymentRow SmbOnWorkstations { get; }
    public BaselineDeploymentRow TlsOnServers { get; }
    public CiSetting SmbValue { get; }
    public CiSetting SmbScript { get; }

    public static CatalogParameter Text(string name, bool required = false, string? value = null, string description = "A value.") => new(name, "String", required, value, description);

    public static CatalogParameter Switch(string name) => new(name, "Switch", false, "false", "A switch.");

    public static CatalogEntry Entry(string id, string name, string subcategory, string severity, IReadOnlyList<CatalogParameter> parameters,
        IReadOnlyList<CatalogDependency>? dependencies = null, IReadOnlyList<string>? cves = null, bool reboot = false, int baselines = 1) =>
        new(id, "1.0.0", name, subcategory, "Draft", $"{name} description.", severity, cves ?? [], [123456], ["KB0000001"], ["Windows 10", "Windows 11"],
            ["Server 2022"], reboot, [new RegistrySurfaceItem("HKLM", @"SOFTWARE\Policies\Example", ["Value1"])], parameters, dependencies ?? [],
            Enumerable.Range(1, baselines).Select(i => $"{name} item {i}").ToList(), Enumerable.Range(1, baselines).Select(i => $"{name} baseline {i}").ToList(),
            $"Catalog.{id}.ps1", new string('a', 64));

    public ComplianceHarness()
    {
        Autorun = Entry("autorun-disable", "Autorun Disable", "Compliance", "High",
            [Text("SiteCode", true), Text("SiteServer", true), Text("CollectionName"), Switch("DetectOnly")]);
        SpecExec = Entry("specexec-mitigations", "CPU Speculative Execution Mitigations", "CVE-Remediation", "Critical",
            [Text("SiteCode", true), Text("SiteServer", true), Text("HtEnabledCollectionName", value: "Devices: HT Enabled"),
                Text("HtDisabledCollectionName", value: "Devices: HT Disabled"), Switch("SkipDeploy"), Switch("DetectOnly")],
            [new CatalogDependency("Collection", "Devices: HT Enabled", "../Collections/New-HT.ps1"), new CatalogDependency("Collection", "Devices: HT Disabled", "../Collections/New-HT.ps1")],
            ["CVE-2017-5715"], reboot: true, baselines: 2);
        Wdigest = Entry("wdigest-disable", "WDigest Disable", "Hardening", "High",
            [Text("SiteCode", true), Text("SiteServer", true), Text("CollectionName"), Switch("DetectOnly")]);
        Catalog.Entries.AddRange([Autorun, SpecExec, Wdigest]);

        Collections.Add("SMS00001", "All Systems", members: 5);
        Collections.Add("LAB00010", "Servers", @"Patching\Servers", members: 3);
        Collections.Add("LAB00011", "Workstations", @"Patching\Clients", members: 4);
        Collections.Add("LAB00012", "Devices: HT Enabled", "", members: 2);

        Smb = Service.AddItem("CI-SMB", "SMB Signing");
        Tls = Service.AddItem("CI-TLS", "TLS Legacy", baselines: 0);
        SmbValue = new CiSetting(SettingId: "S-1", Name: "RequireSecuritySignature", Kind: "Registry", DataType: "Integer",
            Detail: @"LocalMachine\SYSTEM\Lanman\RequireSecuritySignature", ExpectedValue: "1",
            Hive: "LocalMachine", KeyName: @"SYSTEM\Lanman", ValueName: "RequireSecuritySignature", RuleName: "RequireSecuritySignature rule");
        SmbScript = new CiSetting(SettingId: "S-2", Name: "Signing check", Kind: "Script", DataType: "Boolean", Detail: "PowerShell script, 1 line, no remediation script",
            ExpectedValue: "True", DiscoveryLanguage: "PowerShell", DiscoveryScript: "(Get-SmbServerConfiguration).RequireSecuritySignature",
            RemediationLanguage: "PowerShell", RemediationScript: "", RuleName: "Signing check rule");
        Service.AddSetting("CI-SMB", SmbValue);
        Service.AddSetting("CI-SMB", SmbScript);
        SmbBaseline = Service.AddBaseline("CB-SMB", "SMB Signing Required", "smb-signing-require", "1.0.0");
        TlsBaseline = Service.AddBaseline("CB-TLS", "TLS Legacy Disable");
        SmbOnServers = Service.AddDeployment(SmbBaseline, "LAB00010", "Servers");
        SmbOnWorkstations = Service.AddDeployment(SmbBaseline, "LAB00011", "Workstations", remediate: true);
        TlsOnServers = Service.AddDeployment(TlsBaseline, "LAB00010", "Servers");
        Service.Drift.Add(new DriftRow("SMB Signing Required", "CB-SMB", "smb-signing-require", "1.0.0", "1.0.0", "Current"));
        Service.Drift.Add(new DriftRow("LSA Protection", "CB-LSA", "lsa-protection", "0.9.0", "1.0.0", "Behind"));
        Service.Drift.Add(new DriftRow("TLS Legacy Disable", "CB-TLS", null, null, null, "Untagged"));
        Service.Compliance.Add(new ComplianceRow("SMB Signing Required", "Servers", 3, 1, 0, 1, new DateTime(2026, 10, 18), false));
        Service.Compliance.Add(new ComplianceRow("SMB Signing Required", "Workstations", 2, 2, 0, 0, new DateTime(2026, 9, 1), true));
        Service.InConfirmedRun = () => Plans.InRun;
    }

    /// <summary>A service that replaces the fake, for example the real service on the stub site.</summary>
    public IComplianceService? RealService { get; set; }

    private IComplianceService Compliance => RealService ?? Service;

    public ComplianceWriter Writer() => new(Compliance, History, Log, () => "LAB");

    public CompliancePageServices Services() => new(Compliance, Catalog, Collections, Reports, Dialogs, Plans, Writer(), () => Settings, () => Saved,
        s =>
        {
            BeforeSave?.Invoke(s);
            Interlocked.Increment(ref PreferenceSaves);
            Saved = s;
        }, Log);

    public ComplianceViewModel Create() => new(Services());

    public async Task<ComplianceViewModel> OpenAsync(int tab = ComplianceViewModel.CatalogTab)
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync(true);
        if (viewModel.SelectedTabIndex != tab)
        {
            viewModel.SelectedTabIndex = tab;
            await viewModel.CurrentTab.EnsureLoadedAsync();
        }
        return viewModel;
    }
}
