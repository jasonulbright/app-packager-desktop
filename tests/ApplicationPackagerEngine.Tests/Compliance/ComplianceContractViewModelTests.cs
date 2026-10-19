using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>The page against the service contract: setting rules, remediation confirmation, create-first moves and the read shapes.</summary>
public sealed class ComplianceContractViewModelTests
{
    private static async Task<(ComplianceHarness Harness, ComplianceViewModel ViewModel)> EditorOnSmb(ComplianceHarness? given = null)
    {
        var harness = given ?? new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.EditorTab);
        await viewModel.Editor.LoadTask;
        viewModel.Editor.SelectedItem = viewModel.Editor.Items.Single(i => i.CiId == "CI-SMB");
        await viewModel.Editor.SettingsLoad;
        return (harness, viewModel);
    }

    private static async Task<ComplianceViewModel> LiveAsync(ComplianceHarness harness)
    {
        var viewModel = await harness.OpenAsync(ComplianceViewModel.LiveTab);
        await viewModel.Live.LoadTask;
        return viewModel;
    }

    private static CiSettingEditorViewModel Editor(CiSettingEditorMode mode = CiSettingEditorMode.Add, CiSetting? original = null, IReadOnlyList<string>? names = null) =>
        new(mode, "SMB Signing (CI-SMB)", _ => Task.FromResult(true), original, names);

    [Fact]
    public void TheDataTypesFollowTheKind()
    {
        var editor = Editor();
        Assert.Contains("StringArray", editor.DataTypes);
        Assert.DoesNotContain("Boolean", editor.DataTypes);
        editor.DataType = "StringArray";
        Assert.Equal("AllOf", editor.Operator);
        Assert.True(editor.IsListValue);
        editor.Kind = CiSettingEditorViewModel.ScriptKind;
        Assert.Contains("Boolean", editor.DataTypes);
        Assert.DoesNotContain("StringArray", editor.DataTypes);
        Assert.Equal("String", editor.DataType);
        Assert.Equal("IsEquals", editor.Operator);
    }

    [Fact]
    public void ADateTimeSettingWithRemediationIsRefusedInTheDialog()
    {
        var editor = Editor();
        editor.Name = "Last patch";
        editor.KeyPath = @"SOFTWARE\Contoso";
        editor.ValueName = "Patched";
        editor.DataType = "DateTime";
        editor.ExpectedValue = "2026-01-31T08:00:00";
        editor.Remediate = true;
        Assert.Equal(SettingRules.Check(editor.Build()), editor.Validate());
        Assert.Contains("does not store a DateTime setting with remediation", editor.Validate());
        editor.Remediate = false;
        Assert.Null(editor.Validate());
    }

    [Fact]
    public void AStringArrayTakesOneValuePerLine()
    {
        var editor = Editor();
        editor.Name = "Servers";
        editor.KeyPath = @"SOFTWARE\Contoso";
        editor.ValueName = "Servers";
        editor.DataType = "StringArray";
        editor.ExpectedValue = "a\r\nb\r\n";
        Assert.Null(editor.Validate());
        Assert.Equal("a\nb", editor.Build().ExpectedValue);
        Assert.Equal("AllOf", editor.Build().Operator);
    }

    [Fact]
    public void AnEmptyRuleNameTakesTheSettingNameAndTheDialogCarriesOperatorSeverityAndRemediation()
    {
        var editor = Editor();
        editor.Name = "Signing";
        editor.KeyPath = @"SYSTEM\Lanman";
        editor.ValueName = "RequireSecuritySignature";
        editor.DataType = "Integer";
        editor.ExpectedValue = "1";
        editor.Operator = "GreaterEquals";
        editor.Severity = "Critical";
        editor.Remediate = true;
        editor.RemediateDword = true;
        editor.Is64Bit = false;
        var setting = editor.Build();
        Assert.Equal(("Signing rule", "GreaterEquals", "Critical", true, true, false, "LocalMachine"),
            (setting.RuleName, setting.Operator, setting.Severity, setting.Remediate, setting.RemediateDword, setting.Is64Bit, setting.Hive));
        Assert.Null(editor.Validate());
    }

    [Fact]
    public void AnExistingNameShowsAWarningAndDoesNotBlockTheDialog()
    {
        var editor = Editor(names: ["RequireSecuritySignature"]);
        editor.Name = "RequireSecuritySignature";
        Assert.True(editor.HasNameWarning);
        Assert.Equal(CiSettingEditorViewModel.NameTaken("RequireSecuritySignature"), editor.NameWarning);
        editor.Name = "Other";
        Assert.False(editor.HasNameWarning);
    }

    [Fact]
    public void AnUnchangedEditIsNoChangeAndAChangedDataTypeReplacesTheSetting()
    {
        var harness = new ComplianceHarness();
        var editor = Editor(CiSettingEditorMode.Edit, harness.SmbValue, ["RequireSecuritySignature", "Signing check"]);
        Assert.False(editor.HasNameWarning);
        Assert.Equal(CiSettingEditorViewModel.NoChange, editor.Validate());
        Assert.False(editor.ReplacesSetting);
        editor.Severity = "Critical";
        Assert.True(editor.ReplacesSetting);
        editor.Severity = "Warning";
        editor.KeyPath = @"SYSTEM\Other";
        Assert.False(editor.ReplacesSetting);
        editor.DataType = "String";
        Assert.True(editor.ReplacesSetting);
        Assert.Null(editor.Validate());
    }

    [Fact]
    public async Task ADataTypeChangeIsPlannedAsANewSettingWithANewRevision()
    {
        var (harness, viewModel) = await EditorOnSmb();
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Registry");
        harness.Dialogs.SettingEditor = e =>
        {
            e.DataType = "String";
            FakeComplianceDialogs.Run(e.SaveCommand.ExecuteAsync(null));
            Assert.True(e.Accepted, e.ValidationMessage);
        };
        viewModel.Editor.EditSettingCommand.Execute(null);
        var detail = Assert.Single(Assert.Single(harness.Plans.Shown).Items).Detail!;
        Assert.Contains(CiSettingEditorViewModel.ReplaceNote, detail);
        Assert.Contains(ComplianceEditorViewModel.RevisionNote, detail);
        Assert.Contains("the restored setting has a new ID", detail);
        Assert.Equal("String", harness.Service.Applied.Single().Single().SettingAfter!.DataType);
    }

    [Fact]
    public async Task TheImpactLinesOfTheServiceReplaceThePageNotesInASettingPlan()
    {
        var harness = new ComplianceHarness();
        harness.Service.Impact.Add("ConfigMgr stores this as a new revision of the configuration item: revision 1 becomes 2.");
        var (_, viewModel) = await EditorOnSmb(harness);
        viewModel.Editor.SelectedSetting = viewModel.Editor.Settings.Single(s => s.Kind == "Registry");
        await viewModel.Editor.RemoveSettingCommand.ExecuteAsync(null);
        var detail = harness.Plans.Shown.Single().Items.Single().Detail!;
        Assert.Contains("revision 1 becomes 2", detail);
        Assert.DoesNotContain(ComplianceEditorViewModel.RevisionNote, detail);
    }

    [Fact]
    public async Task ASettingThePageCannotRestoreIsReadOnly()
    {
        var harness = new ComplianceHarness();
        harness.Service.AddSetting("CI-SMB", harness.SmbValue with { SettingId = "S-9", Name = "Two rules", IsEditable = false });
        var (_, viewModel) = await EditorOnSmb(harness);
        var editor = viewModel.Editor;
        editor.SelectedSetting = editor.Settings.Single(s => s.Name == "Two rules");
        Assert.Equal("Registry, read-only", editor.SelectedSetting.Kind);
        Assert.False(editor.EditSettingCommand.CanExecute(null));
        Assert.False(editor.RemoveSettingCommand.CanExecute(null));
        Assert.Equal(CiSettingEditorViewModel.NotEditable, editor.SelectedSettingNote);
        editor.SelectedSetting = editor.Settings.Single(s => s.Name == "Signing check");
        Assert.True(editor.EditSettingCommand.CanExecute(null));
        Assert.False(editor.HasSelectedSettingNote);
    }

    [Fact]
    public void TheGridsShowTheContractValues()
    {
        Assert.Equal("", new ConfigurationItemGridRow(new ConfigurationItemRow("1", "A", "", 1, -1, 0, null)).SettingCount);
        Assert.Equal("3", new ConfigurationItemGridRow(new ConfigurationItemRow("1", "A", "", 1, 3, 0, null)).SettingCount);
        Assert.Equal("4 compliant, 2 non-compliant, 1 error",
            new BaselineGridRow(new BaselineRow("1", "B", "", 1, null, null, 1, 1, null, Compliant: 4, NonCompliant: 2, Failed: 1)).Compliance);
        var setting = new CiSetting(SettingId: "S", Name: "N", Kind: "Registry", DataType: "StringArray", Detail: "", ExpectedValue: "a\nb", Operator: "AllOf");
        Assert.Equal("AllOf a, b", new SettingGridRow(setting).Rule);
    }

    [Theory]
    [InlineData("Servers*")]
    [InlineData("Server?")]
    [InlineData("[Servers]")]
    public void ACollectionNameWithAWildcardIsRefused(string value)
    {
        var harness = new ComplianceHarness();
        var form = new CatalogDeployViewModel(harness.Autorun, harness.Settings, harness.Collections.Collections, _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        form.Field("CollectionName")!.Value = value;
        Assert.Equal(CatalogParameterRules.Wildcard("CollectionName"), form.Validate());
        form.Field("CollectionName")!.Value = "Servers";
        Assert.Null(form.Validate());
    }

    [Fact]
    public async Task RedeployIsCreateFirstWithoutUndoAndARemediatingOneNeedsTheSecondCheck()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnWorkstations.DeploymentId));
        await viewModel.Live.RedeployCommand.ExecuteAsync(null);
        var plan = Assert.Single(harness.Plans.Shown);
        Assert.False(plan.HasUndo);
        Assert.NotNull(plan.ExtraConfirmation);
        Assert.StartsWith("Creates the deployment again (Remediate", plan.Items[0].Detail);
        Assert.Contains(ComplianceLiveViewModel.RedeployNote, plan.Items[0].Detail);
        var sent = harness.Service.Applied.Single().Single();
        Assert.True(sent.RemediationConfirmed);
        Assert.Equal(2, harness.Service.Deployments.Count(d => d.BaselineId == "CB-SMB"));
        Assert.False(viewModel.Shared.HasUndo);
    }

    [Fact]
    public async Task OnlyChangesThatTurnRemediationOnCarryTheConfirmation()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
        await viewModel.Live.DisableRemediationCommand.ExecuteAsync(null);
        Assert.False(harness.Service.Applied.Single().Single().RemediationConfirmed);

        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        var undoPlan = harness.Plans.Shown[1];
        Assert.NotNull(undoPlan.ExtraConfirmation);
        Assert.True(harness.Service.Applied[1].Single().RemediationConfirmed);
        Assert.True(harness.Service.Deployments.Single(d => d.DeploymentId == harness.SmbOnWorkstations.DeploymentId).RemediateNoncompliant);
    }

    [Fact]
    public async Task UndoOfADeleteOfARemediatingDeploymentAsksTheSecondCheck()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnWorkstations.DeploymentId));
        await viewModel.Live.DeleteDeploymentsCommand.ExecuteAsync(null);
        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        var undoPlan = harness.Plans.Shown[1];
        Assert.NotNull(undoPlan.ExtraConfirmation);
        Assert.Contains(ComplianceWriter.RecreatedNote, undoPlan.Items[0].Detail);
        Assert.Equal(ComplianceChangeKind.CreateDeployment, harness.Service.Applied[1].Single().Kind);
        Assert.Contains(harness.Service.Deployments, d => d.CollectionId == "LAB00011" && d.BaselineId == "CB-SMB" && d.RemediateNoncompliant);
    }

    [Fact]
    public async Task WithoutTheSecondCheckAnUndoThatRestoresRemediationRunsNothing()
    {
        var harness = new ComplianceHarness();
        var viewModel = await LiveAsync(harness);
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments.Where(d => d.Row.DeploymentId == harness.SmbOnWorkstations.DeploymentId));
        await viewModel.Live.DeleteDeploymentsCommand.ExecuteAsync(null);
        harness.Plans.AcknowledgeExtra = false;
        await viewModel.Shared.UndoLastCommand.ExecuteAsync(null);
        Assert.Single(harness.Service.Applied);
        Assert.True(viewModel.Shared.HasUndo);
    }

    [Fact]
    public void ARemediatingCatalogDeployWithoutDeploymentsStillAsksTheSecondCheck()
    {
        var harness = new ComplianceHarness();
        var change = ComplianceRequests.Deploy(harness.SpecExec, new Dictionary<string, string> { ["DetectOnly"] = "False", ["SkipDeploy"] = "True" }, [], true);
        var (plan, _) = ComplianceCatalogViewModel.BuildDeployPlan(harness.SpecExec, change, []);
        Assert.Equal(ComplianceCatalogViewModel.NoTargetRemediationCheck, plan!.Plan.ExtraConfirmation);
    }
}
