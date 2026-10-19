using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

public sealed class ComplianceViewModelTests
{
    private static void Deploy(CatalogDeployViewModel form) => FakeComplianceDialogs.Run(form.DeployCommand.ExecuteAsync(null));

    private static async Task<ComplianceViewModel> LiveAsync(ComplianceHarness harness)
    {
        var viewModel = await harness.OpenAsync(ComplianceViewModel.LiveTab);
        await viewModel.Live.LoadTask;
        return viewModel;
    }

    [Fact]
    public async Task NothingReachesConfigMgrBeforeATabIsVisitedAndTheCatalogNeedsNoSite()
    {
        var harness = new ComplianceHarness();
        var viewModel = harness.Create();
        await viewModel.OnNavigatedToAsync(false);
        Assert.Equal(0, harness.Catalog.Reads);
        Assert.Equal(0, harness.Service.ItemCalls + harness.Service.BaselineCalls + harness.Service.DeploymentCalls + harness.Service.DriftCalls);

        await viewModel.ShowAsync();
        Assert.Equal(1, harness.Catalog.Reads);
        Assert.Equal(3, viewModel.Catalog.Rows.Count);
        Assert.Equal(0, harness.Service.ItemCalls + harness.Service.BaselineCalls + harness.Service.DeploymentCalls);
        Assert.Equal(0, harness.Collections.ListCalls);
    }

    [Fact]
    public async Task TheLiveTabReadsItsThreeListsOnceAndASelectionReadsNothing()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        Assert.Equal((1, 1, 1), (harness.Service.ItemCalls, harness.Service.BaselineCalls, harness.Service.DeploymentCalls));
        Assert.Equal([null], harness.Service.DeploymentFilters);
        Assert.Equal(2, viewModel.Live.Items.Count);
        Assert.Equal(2, viewModel.Live.Baselines.Count);
        Assert.Equal(3, viewModel.Live.Deployments.Count);

        viewModel.Live.SelectedBaseline = viewModel.Live.Baselines.Single(b => b.BaselineId == "CB-SMB");
        Assert.Equal(2, viewModel.Live.Deployments.Count);
        Assert.Equal("Deployments of SMB Signing Required", viewModel.Live.DeploymentsTitle);
        Assert.Equal((1, 1, 1), (harness.Service.ItemCalls, harness.Service.BaselineCalls, harness.Service.DeploymentCalls));
        Assert.Equal("2 configuration items, 2 baselines, 3 deployments", viewModel.Live.CountText);
    }

    [Fact]
    public async Task WithoutASiteCodeTheTabsShowOneMessageAndReadNothing()
    {
        var harness = new ComplianceHarness();
        harness.Settings.SiteCode = "";
        var viewModel = await LiveAsync(harness);
        Assert.Equal(CollectionComplianceViewModel.SiteCodeMessage, viewModel.Shared.ConnectionMessage);
        Assert.Equal(0, harness.Service.ItemCalls);
        Assert.Empty(viewModel.Live.Items);
    }

    [Fact]
    public async Task TheCatalogFiltersBySubcategorySeverityReferenceOsAndText()
    {
        var harness = new ComplianceHarness();
        var catalog = (await harness.OpenAsync()).Catalog;
        Assert.Equal(["All", "Compliance", "CVE-Remediation", "Hardening"], catalog.Subcategories);
        catalog.SubcategoryFilter = "Hardening";
        Assert.Equal(["WDigest Disable"], catalog.Rows.Select(r => r.DisplayName));
        Assert.Equal("1 of 3 entries", catalog.RowCount);
        catalog.SubcategoryFilter = ComplianceCatalogViewModel.AllText;
        catalog.SeverityFilter = "Critical";
        Assert.Equal(["CPU Speculative Execution Mitigations"], catalog.Rows.Select(r => r.DisplayName));
        catalog.SeverityFilter = ComplianceCatalogViewModel.AllText;
        catalog.ReferenceFilter = "CVE-2017";
        Assert.Single(catalog.Rows);
        catalog.ReferenceFilter = "123456";
        Assert.Equal(3, catalog.Rows.Count);
        catalog.ReferenceFilter = "";
        catalog.OsFilter = "Server 2022";
        Assert.Equal(3, catalog.Rows.Count);
        catalog.TextFilter = "autorun";
        Assert.Equal(["Autorun Disable"], catalog.Rows.Select(r => r.DisplayName));
        catalog.TextFilter = @"Policies\Example";
        Assert.Equal(3, catalog.Rows.Count);
    }

    [Fact]
    public async Task TheDetailPanelNamesWhatTheEntryChecksAndCreates()
    {
        var harness = new ComplianceHarness();
        var catalog = (await harness.OpenAsync()).Catalog;
        catalog.SelectedRow = catalog.Rows.Single(r => r.Entry.Id == "specexec-mitigations");
        var details = catalog.Details.ToDictionary(d => d.Name, d => d.Value);
        Assert.Equal("Yes", details["Restart needed after remediation"]);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\Example (Value1)", details["Registry values"]);
        Assert.Contains("CPU Speculative Execution Mitigations baseline 2", details["Creates baselines"]);
        Assert.Contains("Collection Devices: HT Disabled", details["Needs"]);
        Assert.Equal("CVE-2017-5715", details["CVEs"]);
        Assert.Equal("Windows 10, Windows 11; Server 2022", details["Operating systems"]);
    }

    private static async Task<(ComplianceHarness Harness, ComplianceViewModel ViewModel)> CatalogOn(string id, Action<ComplianceHarness>? arrange = null)
    {
        var harness = new ComplianceHarness();
        arrange?.Invoke(harness);
        var viewModel = await harness.OpenAsync();
        viewModel.Catalog.SelectedRow = viewModel.Catalog.Rows.Single(r => r.Entry.Id == id);
        return (harness, viewModel);
    }

    [Fact]
    public async Task ADeployIsDetectOnlyByDefaultWithTheSiteFromTheSettingsAndRunsOnlyThroughAConfirmedPlan()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        CatalogDeployViewModel? form = null;
        harness.Dialogs.Deploy = f =>
        {
            form = f;
            Assert.True(f.DetectOnly);
            Assert.Equal("LAB", f.Field("SiteCode")!.Value);
            Assert.True(f.Field("SiteCode")!.IsReadOnly);
            Assert.Equal("cm01.lab.local", f.Field("SiteServer")!.Value);
            Assert.Equal("SiteCode *", f.Field("SiteCode")!.Label);
            Assert.True(f.Field("CollectionName")!.IsCollection);
            f.Field("CollectionName")!.Value = "Servers";
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        Assert.True(form!.Accepted);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Equal("Deploy detect only", plan.ActionName);
        var detail = Assert.Single(plan.Items).Detail!;
        Assert.Contains("Mode: Detect only.", detail);
        Assert.Contains("Deploys to Servers (LAB00010): 3 members now.", detail);
        Assert.Contains("[ccm:autorun-disable@1.0.0] once", detail);
        Assert.Contains("Undo removes the deployments, baselines and configuration items this run creates.", detail);
        var change = Assert.Single(Assert.Single(harness.Service.Applied));
        Assert.Equal(ComplianceChangeKind.CatalogDeploy, change.Kind);
        Assert.Equal(["LAB00010"], change.TargetCollectionIds);
        Assert.False(change.RemediateAfter);
        Assert.Equal("True", change.Values["DetectOnly"]);
        Assert.Equal("LAB", change.Values["SiteCode"]);
        Assert.Equal("Servers", change.Values["CollectionName"]);
        Assert.True(viewModel.Shared.HasUndo);
        Assert.Contains("1 changed", viewModel.Shared.ResultSummary);
        Assert.Single(harness.History.Entries);
    }

    [Fact]
    public async Task ADeployWithRemediationNeedsTheSecondConfirmationThatNamesTheDevices()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Plans.AcknowledgeExtra = false;
        harness.Dialogs.Deploy = f =>
        {
            f.Field("CollectionName")!.Value = "Workstations";
            f.Field("DetectOnly")!.IsChecked = false;
            Assert.True(f.Remediate);
            Deploy(f);
            Assert.False(f.Accepted);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(ComplianceCatalogViewModel.RemediationCheck(1, 4, false), plan.ExtraConfirmation);
        Assert.Contains(@"HKLM\SOFTWARE\Policies\Example (Value1)", plan.Items[0].Detail);
        Assert.Contains("need no restart", plan.Items[0].Detail);
        Assert.Empty(harness.Service.Applied);

        harness.Plans.AcknowledgeExtra = true;
        harness.Dialogs.Deploy = f =>
        {
            f.Field("CollectionName")!.Value = "Workstations";
            f.Field("DetectOnly")!.IsChecked = false;
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.True(Assert.Single(Assert.Single(harness.Service.Applied)).RemediateAfter);
    }

    [Theory]
    [InlineData("-Force", "CollectionName cannot start with a dash.")]
    [InlineData("Servers\nWorkstations", "CollectionName contains a line break or another control character.")]
    [InlineData("Unknown collection", "No device collection named Unknown collection exists.")]
    [InlineData("All Systems", "All Systems is a built-in collection. Pick another collection.")]
    public async Task AHostileOrUnknownValueIsRefusedBeforeAnyPlan(string value, string message)
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Dialogs.Deploy = f =>
        {
            f.Field("CollectionName")!.Value = value;
            Deploy(f);
            Assert.Equal(message, f.ValidationMessage);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal(0, harness.Service.PrepareCalls);
    }

    [Fact]
    public void TheParameterRulesRefuseLongTextBadSiteValuesAndAllowQuotesAndSemicolons()
    {
        var name = ComplianceHarness.Text("ClientDownloadMode");
        Assert.Equal(CatalogParameterRules.TooLong("ClientDownloadMode"), CatalogParameterRules.Check(name, new string('x', 257)));
        Assert.Null(CatalogParameterRules.Check(name, "it's; fine \"quoted\""));
        Assert.Equal(CatalogParameterRules.LeadingDash("ClientDownloadMode"), CatalogParameterRules.Check(name, "  \u2013Value"));
        Assert.Equal(CatalogParameterRules.BadSiteCode, CatalogParameterRules.Check(ComplianceHarness.Text("SiteCode", true), "LAB;"));
        Assert.Equal(CatalogParameterRules.BadSiteServer, CatalogParameterRules.Check(ComplianceHarness.Text("SiteServer", true), "cm01 & calc"));
        Assert.Equal(CatalogParameterRules.Required("SiteServer"), CatalogParameterRules.Check(ComplianceHarness.Text("SiteServer", true), " "));
    }

    [Fact]
    public async Task EmptySiteSettingsLeaveTheSiteFieldsEditable()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Settings.ProviderMachine = "";
        harness.Dialogs.Deploy = f =>
        {
            Assert.False(f.Field("SiteServer")!.IsReadOnly);
            Deploy(f);
            Assert.Equal(CatalogParameterRules.Required("SiteServer"), f.ValidationMessage);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.Equal(1, harness.Dialogs.DeployShows);
    }

    [Fact]
    public async Task AMissingDependencyBlocksTheDeployAndCreateRunsItsOwnPlan()
    {
        var (harness, viewModel) = await CatalogOn("specexec-mitigations");
        harness.Service.Fail = change =>
        {
            if (change.Values.ContainsKey(ComplianceRequests.CreateDependencyKey)) harness.Collections.Add("LAB00013", "Devices: HT Disabled", members: 1);
            return null;
        };
        harness.Dialogs.Deploy = f =>
        {
            Assert.True(f.HasMissingDependency);
            var missing = f.Dependencies.Single(d => !d.Satisfied);
            Assert.Equal("Devices: HT Disabled", missing.Name);
            Assert.True(missing.CanCreate);
            Deploy(f);
            Assert.Equal(CatalogDeployViewModel.DependencyMissing, f.ValidationMessage);
            Assert.Empty(harness.Plans.Shown);

            FakeComplianceDialogs.Run(f.CreateDependencyCommand.ExecuteAsync(missing));
            Assert.Equal("Create Devices: HT Disabled", Assert.Single(harness.Plans.Shown).Title);
            Assert.False(f.HasMissingDependency);
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.Plans.Shown.Count);
        var deploy = harness.Service.Applied[1].Single();
        Assert.Equal(["LAB00012", "LAB00013"], deploy.TargetCollectionIds);
        Assert.Null(harness.Plans.Shown[1].ExtraConfirmation);
        Assert.Contains("Deploys to Devices: HT Disabled (LAB00013): 1 member now.", harness.Plans.Shown[1].Items[0].Detail);
    }

    [Fact]
    public async Task SkipDeployNeedsNoDependencyAndDeploysToNoCollection()
    {
        var (harness, viewModel) = await CatalogOn("specexec-mitigations");
        harness.Dialogs.Deploy = f =>
        {
            f.Field("SkipDeploy")!.IsChecked = true;
            Assert.False(f.HasMissingDependency);
            Deploy(f);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.Empty(Assert.Single(Assert.Single(harness.Service.Applied)).TargetCollectionIds);
        Assert.Contains("Deploys to no collection.", harness.Plans.Shown[0].Items[0].Detail);
    }

    [Fact]
    public async Task ThePickerSetsOneCollectionAndRefusesTwo()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Dialogs.Picker = p =>
        {
            Assert.DoesNotContain(p.Items, i => i.Row.IsBuiltIn);
            p.Items[0].IsChecked = true;
            p.Items[1].IsChecked = true;
            FakeComplianceDialogs.Run(p.AcceptCommand.ExecuteAsync(null));
            Assert.Equal(ComplianceCatalogViewModel.PickOne, p.ValidationMessage);
            p.Items[1].IsChecked = false;
            FakeComplianceDialogs.Run(p.AcceptCommand.ExecuteAsync(null));
            Assert.True(p.Accepted);
        };
        harness.Dialogs.Deploy = f =>
        {
            FakeComplianceDialogs.Run(f.PickCollectionCommand.ExecuteAsync(f.Field("CollectionName")));
            Assert.Equal("Devices: HT Enabled", f.Field("CollectionName")!.Value);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.Equal(1, harness.Dialogs.PickerShows);
    }

    [Fact]
    public async Task ADoubleClickOnDeployOpensOneForm()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        var gate = new TaskCompletionSource();
        harness.Collections.ListGate = gate;
        var first = viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        var second = viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, harness.Dialogs.DeployShows);
    }

    [Fact]
    public async Task ADeclinedPlanChangesNothingAndKeepsTheFormOpen()
    {
        var (harness, viewModel) = await CatalogOn("autorun-disable");
        harness.Plans.Confirm = false;
        harness.Dialogs.Deploy = f =>
        {
            Deploy(f);
            Assert.False(f.Accepted);
        };
        await viewModel.Catalog.DeployCommand.ExecuteAsync(null);
        Assert.Empty(harness.Service.Applied);
        Assert.Equal(ComplianceShared.NotConfirmed, viewModel.Shared.StatusMessage);
    }

    [Fact]
    public async Task EnableRemediationNeedsTheSecondConfirmationAndUndoSetsTheModeBack()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        live.SetSelectedDeployments(live.Deployments);
        await live.EnableRemediationCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(2, plan.Items.Count);
        Assert.Contains("1 collection", plan.ExtraConfirmation);
        Assert.Contains("Mode now: Detect only. New mode: Remediate.", plan.Items[0].Detail);
        Assert.All(harness.Service.Deployments, d => Assert.True(d.RemediateNoncompliant));

        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Plans.Shown.Count);
        Assert.False(harness.Service.Deployments.Single(d => d.DeploymentId == harness.SmbOnServers.DeploymentId).RemediateNoncompliant);
        Assert.True(harness.Service.Deployments.Single(d => d.DeploymentId == harness.SmbOnWorkstations.DeploymentId).RemediateNoncompliant);
        Assert.False(viewModel.Shared.HasUndo);
    }

    [Fact]
    public async Task DisableRemediationHasNoSecondCheckAndAModeAlreadySetIsNoPlan()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnServers.DeploymentId));
        await live.DisableRemediationCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Equal(ComplianceLiveViewModel.AlreadyInMode, viewModel.Shared.StatusMessage);

        live.SetSelectedDeployments(live.Deployments);
        await live.DisableRemediationCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Null(plan.ExtraConfirmation);
        Assert.Single(plan.Items);
    }

    [Fact]
    public async Task DeleteIsDestructiveNamesTheCountsAndUndoRecreates()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.BaselineId == "CB-TLS"));
        await live.DeleteDeploymentsCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Contains("3 compliant, 1 non-compliant, 0 error, 1 unknown", plan.Items[0].Detail);
        Assert.Contains("new ID", plan.Items[0].Detail);
        Assert.DoesNotContain(harness.Service.Deployments, d => d.BaselineId == "CB-TLS");
        await live.LoadTask;
        Assert.Equal(2, live.Deployments.Count);

        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Contains(harness.Service.Deployments, d => d.BaselineId == "CB-TLS");
        Assert.Contains(ComplianceWriter.RecreatedNote, harness.Plans.Shown[1].Items[0].Detail);
    }

    [Fact]
    public async Task AWithoutTheDestructiveCheckDeleteRunsNothing()
    {
        var harness = new ComplianceHarness();
        harness.Plans.AcknowledgeDestructive = false;
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
        await viewModel.Live.DeleteDeploymentsCommand.ExecuteAsync(null);
        Assert.Empty(harness.Service.Applied);
        Assert.Equal(3, harness.Service.Deployments.Count);
    }

    [Fact]
    public async Task RedeployShowsTheOrderAndTheFallback()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments.Take(1));
        await viewModel.Live.RedeployCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.True(plan.IsDestructive);
        Assert.Contains(ComplianceLiveViewModel.RedeployNote, plan.Items[0].Detail);
        Assert.Equal(ComplianceChangeKind.Redeploy, harness.Service.Applied.Single().Single().Kind);
    }

    [Fact]
    public async Task ReassignTakesOneOtherCollectionAndAsksTheSecondCheckForARemediatingDeployment()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnWorkstations.DeploymentId));
        harness.Dialogs.Picker = p =>
        {
            p.Items.Single(i => i.CollectionId == "LAB00011").IsChecked = true;
            FakeComplianceDialogs.Run(p.AcceptCommand.ExecuteAsync(null));
            Assert.Equal(ComplianceLiveViewModel.SameCollection, p.ValidationMessage);
            p.Items.Single(i => i.CollectionId == "LAB00011").IsChecked = false;
            p.Items.Single(i => i.CollectionId == "LAB00010").IsChecked = true;
            FakeComplianceDialogs.Run(p.AcceptCommand.ExecuteAsync(null));
            Assert.True(p.Accepted);
        };
        await live.ReassignCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Contains("Servers (LAB00010) (3 devices now)", plan.ExtraConfirmation);
        Assert.Contains(ComplianceLiveViewModel.ReassignNote, plan.Items[0].Detail);
        Assert.Equal("LAB00010", harness.Service.Applied.Single().Single().ToCollectionId);
    }

    [Fact]
    public async Task AStaleDeploymentIsASkipLineInABulkPlanAndStopsASinglePlan()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        var live = viewModel.Live;
        harness.Service.Deployments.RemoveAll(d => d.DeploymentId == harness.TlsOnServers.DeploymentId);
        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.CollectionId == "LAB00010"));
        await live.DeleteDeploymentsCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.Equal(["Delete", "Skip"], plan.Items.Select(i => i.Action).Order());
        Assert.Contains("1 changed, 0 failed, 1 skipped.", viewModel.Shared.ResultSummary);

        live.SetSelectedDeployments(live.Deployments.Where(d => d.Row.DeploymentId == harness.TlsOnServers.DeploymentId));
        if (live.SelectedDeployments.Count == 0) live.SetSelectedDeployments([new DeploymentGridRow(harness.TlsOnServers)]);
        await live.RedeployCommand.ExecuteAsync(null);
        Assert.Single(harness.Plans.Shown);
        Assert.Equal("The deployment of TLS Legacy Disable to Servers no longer exists.", viewModel.Shared.StatusMessage);
    }

    [Fact]
    public async Task ADoubleClickOnAnActionShowsOnePlan()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
        var first = viewModel.Live.RedeployCommand.ExecuteAsync(null);
        var second = viewModel.Live.RedeployCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);
        Assert.Single(harness.Plans.Shown);
    }

    [Fact]
    public async Task ThePlanOfAWriteShowsTheImpactLinesOfTheService()
    {
        var harness = new ComplianceHarness();
        harness.Service.Impact.Add("Clients evaluate the baseline again at the next policy cycle.");
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments.Take(1));
        await viewModel.Live.RedeployCommand.ExecuteAsync(null);
        Assert.Contains("Clients evaluate the baseline again at the next policy cycle.", harness.Plans.Shown[0].Items[0].Detail);
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

    [Fact]
    public async Task TheEditorReadsTheSettingsOfTheSelectedItemOnly()
    {
        var (harness, viewModel) = await EditorOnSmb();
        Assert.Equal(1, harness.Service.SettingCalls);
        Assert.Equal(2, viewModel.Editor.Settings.Count);
        Assert.Equal("2 settings", viewModel.Editor.SettingsMessage);
        Assert.Equal("SMB Signing (CI-SMB) revision 1", viewModel.Editor.SelectedItemText);
    }

    [Fact]
    public async Task AddingARegistrySettingShowsTheNewValuesAndTheRevisionNote()
    {
        var (harness, viewModel) = await EditorOnSmb();
        harness.Dialogs.SettingEditor = e =>
        {
            e.Name = "EnableSecuritySignature";
            e.KeyPath = @"\SYSTEM\Lanman\";
            e.ValueName = "EnableSecuritySignature";
            e.DataType = "Integer";
            e.ExpectedValue = "one";
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
            Assert.Equal(CiSettingEditorViewModel.NotInteger("one"), e.ValidationMessage);
            e.ExpectedValue = "1";
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
            Assert.True(e.Accepted);
        };
        viewModel.Editor.AddSettingCommand.Execute(null);
        var detail = Assert.Single(Assert.Single(harness.Plans.Shown).Items).Detail!;
        Assert.Contains(@"Registry value LocalMachine\SYSTEM\Lanman\EnableSecuritySignature, 64-bit view.", detail);
        Assert.Contains("rule EnableSecuritySignature rule: IsEquals 1.", detail);
        Assert.Contains(ComplianceEditorViewModel.RevisionNote, detail);
        Assert.Contains(ComplianceEditorViewModel.ReevaluateNote, detail);
        var change = harness.Service.Applied.Single().Single();
        Assert.Equal(@"SYSTEM\Lanman", change.SettingAfter!.KeyName);
        await viewModel.Editor.SettingsLoad;
        Assert.Equal(3, viewModel.Editor.Settings.Count);
    }

    [Fact]
    public async Task EditingAScriptSettingShowsOldAndNewScriptTextInThePlanAndNeverInTheLog()
    {
        var (harness, viewModel) = await EditorOnSmb();
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Script");
        harness.Dialogs.SettingEditor = e =>
        {
            Assert.True(e.CanChooseDataType);
            Assert.Equal("Boolean", e.DataType);
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
            Assert.Equal(CiSettingEditorViewModel.NoChange, e.ValidationMessage);
            e.DiscoveryScript = "Get-SecretProbe -Token 42";
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
        };
        viewModel.Editor.EditSettingCommand.Execute(null);
        var detail = harness.Plans.Shown.Single().Items.Single().Detail!;
        Assert.Contains("Old: Script", detail);
        Assert.Contains("(Get-SmbServerConfiguration).RequireSecuritySignature", detail);
        Assert.Contains("New: Script", detail);
        Assert.Contains("Get-SecretProbe -Token 42", detail);
        Assert.DoesNotContain(harness.Log.Entries.Select(e => e.Message), l => l.Contains("SecretProbe", StringComparison.Ordinal) || l.Contains("Signing check", StringComparison.Ordinal));
        Assert.Equal("Get-SecretProbe -Token 42", harness.Service.Settings["CI-SMB"].Single(s => s.SettingId == "S-2").DiscoveryScript);
    }

    [Fact]
    public async Task RemovingASettingIsDestructiveAndUndoAddsItAgain()
    {
        var (harness, viewModel) = await EditorOnSmb();
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Registry");
        await viewModel.Editor.RemoveSettingCommand.ExecuteAsync(null);
        var plan = harness.Plans.Shown.Single();
        Assert.True(plan.IsDestructive);
        Assert.Contains("new ID", plan.Items[0].Detail);
        Assert.Single(harness.Service.Settings["CI-SMB"]);
        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Service.Settings["CI-SMB"].Count);
    }

    [Fact]
    public async Task ASettingRemovedWhileTheDialogIsOpenStopsThePlanWithAPlainMessage()
    {
        var (harness, viewModel) = await EditorOnSmb();
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Registry");
        harness.Dialogs.SettingEditor = e =>
        {
            harness.Service.Settings["CI-SMB"].RemoveAll(s => s.SettingId == "S-1");
            e.ExpectedValue = "0";
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
            Assert.False(e.Accepted);
            Assert.Equal("The setting RequireSecuritySignature no longer exists.", e.ValidationMessage);
        };
        viewModel.Editor.EditSettingCommand.Execute(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.Empty(harness.Service.Applied);
    }

    [Fact]
    public async Task ASettingsReadThatEndsAfterAnotherSelectionIsDiscarded()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.EditorTab);
        await viewModel.Editor.LoadTask;
        var gate = new TaskCompletionSource();
        harness.Service.SettingsGate = gate;
        viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-SMB");
        var late = viewModel.Editor.SettingsLoad;
        // The read starts on a pool thread; the gate is released for the next read only after this read holds it.
        while (Volatile.Read(ref harness.Service.SettingCalls) == 0) await Task.Delay(1);
        harness.Service.SettingsGate = null;
        viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-TLS");
        await viewModel.Editor.SettingsLoad;
        gate.SetResult();
        await late;
        Assert.Empty(viewModel.Editor.Settings);
        Assert.Equal("0 settings", viewModel.Editor.SettingsMessage);
    }

    [Fact]
    public async Task EditSettingsOnTheLiveTabOpensTheItemInTheEditor()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SelectedItem = viewModel.Live.Items.Single(i => i.CiId == "CI-TLS");
        viewModel.Live.EditSettingsCommand.Execute(null);
        Assert.Equal(ComplianceViewModel.EditorTab, viewModel.SelectedTabIndex);
        await viewModel.Editor.LoadTask;
        Assert.Equal("CI-TLS", viewModel.Editor.SelectedItem?.CiId);
    }

    [Fact]
    public async Task AuditingFiltersByStateAndStaleAndExportsEveryRowOfBothLists()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.AuditingTab);
        var auditing = viewModel.Auditing;
        Assert.Equal((1, 1), (harness.Service.DriftCalls, harness.Service.ComplianceCalls));
        Assert.Equal(3, auditing.DriftRows.Count);
        auditing.DriftState = "Behind";
        Assert.Equal(["LSA Protection"], auditing.DriftRows.Select(r => r.BaselineName));
        await auditing.ExportCsvCommand.ExecuteAsync(null);
        var written = harness.Reports.Written.Single();
        Assert.Equal(["LSA Protection", "SMB Signing Required", "TLS Legacy Disable"], written.Drift.Select(r => r.BaselineName));
        Assert.Equal(2, written.Compliance.Count);
        Assert.Equal(@"Saved the CSV report to C:\data\Collections\Reports\compliance.csv: 3 drift rows and 2 compliance rows, all rows of the last read without the filters.",
            viewModel.Shared.StatusMessage);
        Assert.Empty(harness.Dialogs.OpenedFolders);

        auditing.SelectedTabIndex = ComplianceAuditingViewModel.ComplianceTab;
        auditing.OnlyStale = true;
        Assert.Equal(["SMB Signing Required on Workstations, Stale"], auditing.ComplianceRows.Select(r => r.ToString()));
        Assert.Equal("1 of 2 rows, 1 stale", auditing.ComplianceCount);
        harness.Reports.Failure = new IOException("disk text");
        await auditing.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(ComplianceAuditingViewModel.ReportFailed("HTML"), viewModel.Shared.StatusMessage);
        Assert.DoesNotContain("disk text", viewModel.Shared.StatusMessage);
    }

    [Fact]
    public async Task AnHtmlExportRightAfterACsvExportWritesItsOwnReportAndOpensNoWindow()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.AuditingTab);
        var auditing = viewModel.Auditing;
        Assert.False(auditing.OpenReportsFolderCommand.CanExecute(null));
        await auditing.ExportCsvCommand.ExecuteAsync(null);
        Assert.True(auditing.ExportHtmlCommand.CanExecute(null));
        await auditing.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(2, harness.Reports.Written.Count);
        Assert.All(harness.Reports.Written, w => Assert.Equal((3, 2), (w.Drift.Count, w.Compliance.Count)));
        Assert.StartsWith(@"Saved the HTML report to C:\data\Collections\Reports\compliance.html:", viewModel.Shared.StatusMessage);
        Assert.Empty(harness.Dialogs.OpenedFolders);

        Assert.True(auditing.OpenReportsFolderCommand.CanExecute(null));
        auditing.OpenReportsFolderCommand.Execute(null);
        Assert.Equal([@"C:\data\Collections\Reports"], harness.Dialogs.OpenedFolders);
    }

    [Fact]
    public async Task LeavingThePageDuringAReadDiscardsItAndReleasesEveryTab()
    {
        var harness = new ComplianceHarness();
        var gate = new TaskCompletionSource();
        harness.Service.ListGate = gate;
        var viewModel = harness.Create();
        await viewModel.OnNavigatedToAsync(true);
        viewModel.SelectedTabIndex = ComplianceViewModel.LiveTab;
        var load = viewModel.Live.LoadTask;
        Assert.True(viewModel.Live.IsLoading);
        await viewModel.OnNavigatedFromAsync();
        gate.SetResult();
        await load;
        Assert.Empty(viewModel.Live.Items);
        Assert.Empty(viewModel.Catalog.Rows);
        Assert.False(viewModel.Live.IsLoaded);
        Assert.Equal("", viewModel.Shared.StatusMessage);

        harness.Service.ListGate = null;
        await viewModel.OnNavigatedToAsync(true);
        await viewModel.Live.LoadTask;
        Assert.Equal(2, viewModel.Live.Items.Count);
    }

    [Fact]
    public async Task ACancelledReadSaysSoAndKeepsNoRows()
    {
        var harness = new ComplianceHarness();
        harness.Service.ListGate = new TaskCompletionSource();
        var viewModel = harness.Create();
        await viewModel.OnNavigatedToAsync(true);
        viewModel.SelectedTabIndex = ComplianceViewModel.LiveTab;
        var load = viewModel.Live.LoadTask;
        viewModel.Live.CancelLoadCommand.Execute(null);
        await load;
        Assert.Equal(ComplianceTabViewModel.ReadCancelled, viewModel.Shared.StatusMessage);
        Assert.Empty(viewModel.Live.Items);
    }

    [Fact]
    public async Task AReadFailureShowsAPlainMessageWithoutTheExceptionText()
    {
        var harness = new ComplianceHarness();
        harness.Service.ListFailure = new InvalidOperationException("raw provider text 0x80041001");
        var viewModel = await LiveAsync(harness);
        Assert.Equal(ComplianceLiveViewModel.ReadFailedText, viewModel.Shared.StatusMessage);
        Assert.DoesNotContain(harness.Log.Entries.Select(e => e.Message), l => l.Contains("raw provider text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavingThePageDuringAPlanDiscardsItsOutcome()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        harness.Service.PrepareFails = _ =>
        {
            ScriptedPlanDialogs.Wait(viewModel.OnNavigatedFromAsync());
            return null;
        };
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments.Take(1));
        var rows = viewModel.Live.SelectedDeployments.ToList();
        await viewModel.Live.RedeployCommand.ExecuteAsync(null);
        Assert.Empty(harness.Plans.Shown);
        Assert.False(viewModel.Shared.HasResults);
        Assert.Single(rows);
    }

    [Fact]
    public async Task TheTabFiltersAndPanelsComeBackAndAnOlderFileUsesTheDefaults()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync();
        Assert.Equal(ComplianceViewModel.CatalogTab, viewModel.SelectedTabIndex);
        viewModel.Catalog.SubcategoryFilter = "Hardening";
        viewModel.Catalog.ShowDetails = false;
        viewModel.SelectedTabIndex = ComplianceViewModel.AuditingTab;
        viewModel.Auditing.DriftState = "Untagged";
        viewModel.Auditing.OnlyStale = true;
        viewModel.Live.ShowDeployments = false;
        await viewModel.OnNavigatedFromAsync();
        Assert.Equal(ComplianceViewModel.AuditingTab, harness.Saved!.SelectedTab);

        var again = await harness.OpenAsync(ComplianceViewModel.AuditingTab);
        Assert.Equal(ComplianceViewModel.AuditingTab, again.SelectedTabIndex);
        Assert.Equal("Hardening", again.Catalog.SubcategoryFilter);
        Assert.False(again.Catalog.ShowDetails);
        Assert.False(again.Live.ShowDeployments);
        Assert.Equal("Untagged", again.Auditing.DriftState);
        Assert.True(again.Auditing.OnlyStale);

        var older = new ComplianceHarness { Saved = new ComplianceSettings { SelectedTab = 9, DriftState = "Bogus", CatalogText = null! } };
        var opened = await older.OpenAsync();
        Assert.Equal(ComplianceViewModel.CatalogTab, opened.SelectedTabIndex);
        Assert.Equal(ComplianceAuditingViewModel.AllStates, opened.Auditing.DriftState);
        Assert.Equal("", opened.Catalog.TextFilter);
    }

    [Fact]
    public async Task LeavingThePageWaitsForTheSaveOfTheChoices()
    {
        var harness = new ComplianceHarness();
        harness.BeforeSave = _ => Thread.Sleep(100);
        var viewModel = await harness.OpenAsync();
        viewModel.Catalog.TextFilter = "a";
        viewModel.Catalog.TextFilter = "ab";
        viewModel.Catalog.TextFilter = "abc";
        await viewModel.OnNavigatedFromAsync();
        Assert.Equal("abc", harness.Saved!.CatalogText);
    }

}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CompliancePreferencesFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apd-compliance-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable);

    public CompliancePreferencesFileTests() => Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApplicationPackagerEngine.Services.AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void EachSectionKeepsTheOtherSectionsChoicesAndAnOlderFileLoadsWithoutThem()
    {
        var settings = new SettingsService();
        Assert.Null(CollectionCompliancePreferences.LoadCompliance(settings));
        Assert.True(CollectionCompliancePreferences.Save(settings, new CollectionComplianceSettings { SelectedTab = 2, Section = 1 }));
        Assert.Null(CollectionCompliancePreferences.LoadCompliance(settings));
        Assert.True(CollectionCompliancePreferences.SaveCompliance(settings, new ComplianceSettings { SelectedTab = 3, CatalogText = "smb" }));
        Assert.True(CollectionCompliancePreferences.Save(settings, new CollectionComplianceSettings { SelectedTab = 1, Section = 1 }));
        var stored = CollectionCompliancePreferences.Load(settings)!;
        Assert.Equal(1, stored.SelectedTab);
        Assert.Equal(1, stored.Section);
        Assert.Equal(3, stored.Compliance!.SelectedTab);
        Assert.Equal("smb", CollectionCompliancePreferences.LoadCompliance(settings)!.CatalogText);
    }
}
