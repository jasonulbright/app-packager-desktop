using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>The plan, selection, background-run and log rules of the Compliance section.</summary>
public sealed class CompliancePageReviewTests
{
    private static void Deploy(CatalogDeployViewModel form) => FakeComplianceDialogs.Run(form.DeployCommand.ExecuteAsync(null));

    private static async Task<(ComplianceHarness Harness, ComplianceViewModel ViewModel)> CatalogOn(string id, Action<ComplianceHarness>? arrange = null)
    {
        var harness = new ComplianceHarness();
        arrange?.Invoke(harness);
        var viewModel = await harness.OpenAsync();
        viewModel.Catalog.SelectedRow = viewModel.Catalog.Rows.Single(r => r.Entry.Id == id);
        return (harness, viewModel);
    }

    private static async Task<ComplianceViewModel> LiveAsync(ComplianceHarness harness)
    {
        var viewModel = await harness.OpenAsync(ComplianceViewModel.LiveTab);
        await viewModel.Live.LoadTask;
        return viewModel;
    }

    private static async Task<(ComplianceHarness Harness, ComplianceViewModel ViewModel)> EditorOnSmb()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.EditorTab);
        await viewModel.Editor.LoadTask;
        viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-SMB");
        await viewModel.Editor.SettingsLoad;
        return (harness, viewModel);
    }

    // ---- Catalog deploy: the plan is the prepared change -------------------------------------

    [Fact]
    public async Task ADetectOnlyBoxCheckedAgainDuringThePrepareCannotHideTheRemediationOfTheChange()
    {
        CatalogDeployViewModel? form = null;
        bool? editableDuringPrepare = null;
        var (harness, viewModel) = await CatalogOn("autorun-disable", h => h.Service.PrepareFails = change =>
        {
            if (change.Kind != ComplianceChangeKind.CatalogDeploy || form is null) return null;
            editableDuringPrepare = form.CanEditFields;
            form.Field("DetectOnly")!.IsChecked = true;
            return null;
        });
        harness.Dialogs.Deploy = f =>
        {
            form = f;
            f.Field("CollectionName")!.Value = "Workstations";
            f.Field("DetectOnly")!.IsChecked = false;
            Deploy(f);
            Assert.True(f.Accepted, f.ValidationMessage);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        Assert.False(editableDuringPrepare);
        Assert.True(form!.CanEditFields);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(ComplianceCatalogViewModel.RemediationCheck(1, 4, false), plan.ExtraConfirmation);
        Assert.Equal("Deploy with remediation", plan.ActionName);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.True(change.RemediateAfter);
        Assert.True(change.RemediationConfirmed);
        Assert.Contains("1 changed", viewModel.Shared.ResultSummary);
    }

    [Fact]
    public async Task TheDeployPlanNamesTheCollectionsOfThePreparedChangeWithTheirCurrentMembers()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable", h =>
            h.Service.Prepared = change => change.Kind == ComplianceChangeKind.CatalogDeploy ? change with { TargetCollectionIds = ["LAB00011"] } : change);
        harness.Dialogs.Deploy = f =>
        {
            var index = harness.Collections.Collections.FindIndex(c => c.CollectionId == "LAB00011");
            harness.Collections.Collections[index] = harness.Collections.Collections[index] with { MemberCount = 9 };
            f.Field("CollectionName")!.Value = "";
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        var detail = Assert.Single(Assert.Single(harness.Plans.Shown).Items).Detail!;
        Assert.DoesNotContain("Deploys to no collection.", detail);
        Assert.Contains("Deploys to Workstations (LAB00011): 9 members now.", detail);
    }

    [Fact]
    public async Task TwoCollectionParametersThatNameOneCollectionShowTwoDeployLines()
    {
        var (harness, viewModel) = await CatalogOn("specexec-mitigations", h =>
            h.Service.Prepared = change => change.Kind == ComplianceChangeKind.CatalogDeploy ? change with { TargetCollectionIds = ["LAB00012", "LAB00012"] } : change);
        harness.Dialogs.Deploy = f =>
        {
            f.Field("HtDisabledCollectionName")!.Value = "Devices: HT Enabled";
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        var detail = Assert.Single(Assert.Single(harness.Plans.Shown).Items).Detail!;
        Assert.Equal(2, Regex.Matches(detail, Regex.Escape("Deploys to Devices: HT Enabled (LAB00012): 2 members now.")).Count);
    }

    [Fact]
    public async Task TheServiceImpactLinesAreThePlanWhenTheServiceGivesThem()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Service.Impact.Add("Deploys to collection Servers (LAB00010) (5 members now), evaluated every day (CollectionName).");
        harness.Dialogs.Deploy = f =>
        {
            f.Field("CollectionName")!.Value = "Servers";
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        var detail = Assert.Single(Assert.Single(harness.Plans.Shown).Items).Detail!;
        Assert.Contains("(5 members now)", detail);
        Assert.DoesNotContain("3 members now", detail);
    }

    [Fact]
    public async Task TheDeployFormReadsTheCollectionsAgainWhenItOpens()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Dialogs.Deploy = _ => { };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        var first = harness.Collections.ListCalls;
        harness.Collections.Add("LAB00020", "New Pilot", members: 1);
        CatalogDeployViewModel? form = null;
        harness.Dialogs.Deploy = f => form = f;
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        Assert.True(harness.Collections.ListCalls > first);
        form!.Field("CollectionName")!.Value = "New Pilot";
        Assert.Null(form.Validate());
    }

    // ---- Live tab --------------------------------------------------------------------------------

    [Fact]
    public async Task ARefreshThatReplacesASelectedRowDropsItFromTheSelectionAndTheActionsFollow()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnServers.DeploymentId));
        Assert.True(live.DeleteDeploymentsCommand.CanExecute(null));

        var index = harness.Service.Deployments.FindIndex(d => d.DeploymentId == harness.SmbOnServers.DeploymentId);
        harness.Service.Deployments[index] = harness.Service.Deployments[index] with { Compliant = 7 };
        await live.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(live.SelectedDeployments);
        Assert.False(live.DeleteDeploymentsCommand.CanExecute(null));
        Assert.False(live.RedeployCommand.CanExecute(null));
        Assert.False(live.EnableRemediationCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARefreshKeepsASelectedRowThatDidNotChange()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        var row = live.Deployments.Single(d => d.Row.DeploymentId == harness.TlsOnServers.DeploymentId);
        live.SetSelectedDeployments([row]);
        await live.RefreshCommand.ExecuteAsync(null);
        Assert.Same(row, Assert.Single(live.SelectedDeployments));
        Assert.Contains(live.Deployments, d => ReferenceEquals(d, row));
    }

    [Fact]
    public async Task ReassignAsksTheSecondCheckFromThePreparedDeploymentNotTheGridRow()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnServers.DeploymentId));
        var index = harness.Service.Deployments.FindIndex(d => d.DeploymentId == harness.SmbOnServers.DeploymentId);
        harness.Service.Deployments[index] = harness.Service.Deployments[index] with { RemediateNoncompliant = true };
        harness.Dialogs.Picker = p =>
        {
            p.Items.Single(i => i.CollectionId == "LAB00011").IsChecked = true;
            FakeComplianceDialogs.Run(p.AcceptCommand.ExecuteAsync(null));
        };
        await live.ReassignCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        Assert.NotNull(plan.ExtraConfirmation);
        Assert.Contains("Mode stays Remediate", plan.Items[0].Detail);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.True(change.RemediationConfirmed);
        Assert.Contains("1 changed", viewModel.Shared.ResultSummary);
    }

    [Fact]
    public async Task APlanWithoutTheCheckThatAChangeNeedsSkipsItWithAPlainMessage()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var harness = new ComplianceHarness();
        harness.Service.InConfirmedRun = null;
        var change = ComplianceRequests.SetRemediation(harness.SmbOnServers, true);
        var plan = new CompliancePlan("Enable remediation", "Enable remediation", "Enable", [CompliancePlanEntry.Write(new ChangePlanItem("Enable", "Servers", null), change)]);
        var writer = harness.Writer();
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.NotEqual(ChangePlanViewModel.FailedText, dialog.ResultText);
        var row = Assert.Single(dialog.ResultRows);
        Assert.True(row.Skipped);
        Assert.Equal(ComplianceWriter.NotAcknowledgedText, row.Message);
        Assert.Empty(harness.Service.Applied);
    }

    // ---- Background run after a force close -----------------------------------------------------

    /// <summary>Confirms the plan, asks the run to stop and closes the window while the run still works.</summary>
    private sealed class DetachingPlanDialogs(Func<bool> started) : IChangePlanDialogs
    {
        public ChangePlanViewModel? Dialog;

        public ChangePlanOutcome Show(ChangePlan plan, ChangePlanRun run, ChangePlanRun? undo = null)
        {
            var dialog = new ChangePlanViewModel(plan, run, undo, TimeSpan.Zero) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
            Dialog = dialog;
            _ = dialog.ConfirmCommand.ExecuteAsync(null);
            Assert.True(SpinWait.SpinUntil(started, TimeSpan.FromSeconds(10)));
            dialog.RequestCancel();
            Assert.True(SpinWait.SpinUntil(() => dialog.ShowForceClose, TimeSpan.FromSeconds(10)));
            dialog.ForceCloseCommand.Execute(null);
            return dialog.Outcome;
        }
    }

    [Fact]
    public async Task AWindowClosedDuringARunKeepsWritesBlockedUntilTheRunEndsAndReadsNothing()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var harness = new ComplianceHarness();
        harness.Service.InConfirmedRun = null;
        var dialogs = new DetachingPlanDialogs(() => { lock (harness.Service.Applied) return harness.Service.Applied.Count > 0; });
        var viewModel = new ComplianceViewModel(harness.Services() with { Plans = dialogs });
        await viewModel.OnNavigatedToAsync(true);
        viewModel.SelectedTabIndex = ComplianceViewModel.LiveTab;
        await viewModel.CurrentTab.EnsureLoadedAsync();
        var live = viewModel.Live;
        var reads = harness.Service.DeploymentCalls;
        harness.Service.ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnServers.DeploymentId));

        await live.DeleteDeploymentsCommand.ExecuteAsync(null);

        Assert.True(dialogs.Dialog!.IsDetached);
        Assert.True(viewModel.Shared.IsWriting);
        Assert.Equal(ComplianceShared.BackgroundRunText, viewModel.Shared.StatusMessage);
        Assert.False(live.DeleteDeploymentsCommand.CanExecute(null));
        Assert.Equal(reads, harness.Service.DeploymentCalls);

        harness.Service.ApplyGate.SetResult();
        Assert.True(SpinWait.SpinUntil(() => !viewModel.Shared.IsWriting, TimeSpan.FromSeconds(10)));
        Assert.Equal(ComplianceShared.BackgroundRunEndedText, viewModel.Shared.StatusMessage);
        Assert.True(viewModel.Shared.HasUndo);
    }

    // ---- Logs carry the fixed action only -------------------------------------------------------

    public static TheoryData<string, string> PlanKinds() => new()
    {
        { "Compliance", "Add setting" },
        { "Collections", "Apply template" },
        { "Maintenance", "Remove" }
    };

    [Theory]
    [MemberData(nameof(PlanKinds))]
    public async Task AFailedRunLogsTheFixedActionAndNotTheTitleWithItsNames(string kind, string action)
    {
        const string secretName = "Payroll Servers Baseline";
        var plan = kind switch
        {
            "Compliance" => new CompliancePlan(action, $"{action} {secretName}", action,
                [CompliancePlanEntry.Write(new ChangePlanItem("Add", "x", null), ComplianceRequests.SetRemediation(new ComplianceHarness().SmbOnServers, false))]).Plan,
            "Collections" => ChangePlanFor.Collection(action, $"{action} {secretName}"),
            _ => ChangePlanFor.Maintenance(action, $"{action} {secretName}")
        };
        var log = new RecordingMaintenanceLog();
        var dialog = new ChangePlanViewModel(plan, (_, _, _) => throw new InvalidOperationException("session lost"), (_, _, _) => Task.FromResult(new ChangePlanResult([])), log: log);
        await dialog.ConfirmCommand.ExecuteAsync(null);
        var entry = Assert.Single(log.Entries, e => e.Level == AppLogLevel.Error);
        Assert.DoesNotContain(secretName, entry.Message);
        Assert.StartsWith(action + " failed:", entry.Message);
    }

    // ---- Editor ------------------------------------------------------------------------------

    [Fact]
    public async Task ALongScriptIsShortenedInThePlanAndTheRestIsCounted()
    {
        var (harness, viewModel) = await EditorOnSmb();
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Script");
        var script = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"Write-Output 'line {i:000}'"));
        harness.Dialogs.SettingEditor = e =>
        {
            e.DiscoveryScript = script;
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
        };
        viewModel.Editor.EditSettingCommand.Execute(null);

        var detail = harness.Plans.Shown.Single().Items.Single().Detail!;
        Assert.Contains("line 020", detail);
        Assert.DoesNotContain("line 021", detail);
        var shown = string.Join("\n", script.Split('\n').Take(20));
        Assert.Contains($"and {script.Length - shown.Length} more characters", detail);
        Assert.Contains(ComplianceEditorViewModel.FullScriptNote, detail);
        Assert.True(detail.Length < 6000, $"The plan text is {detail.Length} characters.");
        Assert.Equal(script, harness.Service.Applied.Single().Single().SettingAfter!.DiscoveryScript);
        Assert.DoesNotContain(harness.Log.Entries.Select(e => e.Message), l => l.Contains("line 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AScriptOfOneLongLineIsCutAtTheCharacterLimit()
    {
        var text = new string('x', 5000);
        var shortened = ComplianceText.Script(text);
        Assert.Equal(new string('x', ComplianceText.ScriptPlanCharacters) + $" ... and {5000 - ComplianceText.ScriptPlanCharacters} more characters", shortened);
        Assert.Equal("Get-Item", ComplianceText.Script("Get-Item"));
    }

    [Fact]
    public async Task ARemediatingSettingInADeployedItemNeedsTheSecondCheckAndCarriesIt()
    {
        var (harness, viewModel) = await EditorOnSmb();
        harness.Plans.AcknowledgeExtra = false;
        harness.Dialogs.SettingEditor = e =>
        {
            e.Remediate = true;
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
        };
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Registry");
        viewModel.Editor.EditSettingCommand.Execute(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(ComplianceEditorViewModel.RemediationCheck, plan.ExtraConfirmation);
        Assert.Empty(harness.Service.Applied);

        harness.Plans.AcknowledgeExtra = true;
        viewModel.Editor.EditSettingCommand.Execute(null);
        Assert.True(Assert.Single(Assert.Single(harness.Service.Applied)).RemediationConfirmed);
    }

    [Fact]
    public async Task ARemediatingSettingInAnItemOfNoBaselineNeedsNoSecondCheck()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.EditorTab);
        await viewModel.Editor.LoadTask;
        viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-TLS");
        await viewModel.Editor.SettingsLoad;
        harness.Dialogs.SettingEditor = e =>
        {
            e.Name = "Enabled";
            e.KeyPath = @"SYSTEM\Tls";
            e.ValueName = "Enabled";
            e.DataType = "Integer";
            e.ExpectedValue = "0";
            e.Remediate = true;
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
        };
        viewModel.Editor.AddSettingCommand.Execute(null);
        Assert.Null(Assert.Single(harness.Plans.Shown).ExtraConfirmation);
    }

    // ---- Writer-only scan ------------------------------------------------------------------------

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ApplicationPackagerUI"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src");
    }

    [Fact]
    public void OnlyTheThreeWritersCallAServiceWrite()
    {
        var root = SourceRoot();
        var pattern = new Regex(@"\.ApplyAsync\s*\(", RegexOptions.CultureInvariant);
        var found = Directory.EnumerateFiles(Path.Combine(root, "ApplicationPackagerUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(File.ReadAllText(f))).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["CollectionsPlan.cs", "CompliancePlan.cs", "MaintenanceWindowsPlan.cs"], found);
        var compliance = File.ReadAllText(Path.Combine(root, "ApplicationPackagerUI", "Services", "CompliancePlan.cs"));
        Assert.Equal(pattern.Matches(compliance).Count, Regex.Matches(compliance, @"\bservice\.ApplyAsync\s*\(").Count);
    }
}

/// <summary>The change plans of the Collections and Maintenance Windows pages, with a placeholder change.</summary>
internal static class ChangePlanFor
{
    private static T Placeholder<T>() => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

    public static ChangePlan Collection(string action, string title) =>
        new CollectionPlan(action, title, action, [CollectionPlanEntry.Write(new ChangePlanItem("Add", "x", null), Placeholder<CollectionChange>())]).Plan;

    public static ChangePlan Maintenance(string action, string title) =>
        new MaintenancePlan(action, title, action, [MaintenancePlanEntry.Write(new ChangePlanItem("Remove", "x", null), Placeholder<MaintenanceChange>())]).Plan;
}
