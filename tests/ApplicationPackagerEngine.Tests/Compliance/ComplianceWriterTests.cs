using System.Reflection;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Compliance;

public sealed class ComplianceWriterTests
{
    private static CompliancePlan DeployPlan(ComplianceHarness harness, out ComplianceChange change)
    {
        change = ComplianceRequests.Deploy(harness.Autorun, new Dictionary<string, string> { ["DetectOnly"] = "True" }, ["LAB00010"], false);
        var (plan, _) = ComplianceCatalogViewModel.BuildDeployPlan(harness.Autorun, change, harness.Collections.Collections);
        return plan!;
    }

    [Fact]
    public async Task TheStepsOfADeployShowWhileItRunsAndTheReturnedOutcomeIsTheResult()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var harness = new ComplianceHarness();
        harness.Service.InConfirmedRun = null;
        var plan = DeployPlan(harness, out _);
        var writer = harness.Writer();
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        var steps = new List<string>();
        dialog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChangePlanViewModel.StepText) && dialog.StepText.Length > 0) steps.Add(dialog.StepText);
        };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains("Build: running the builder.", steps);
        var row = Assert.Single(dialog.ResultRows);
        Assert.True(row.Succeeded);
        Assert.Equal("Created 1 configuration item and 1 baseline.", row.Message);
        var undo = Assert.Single(plan.UndoChanges());
        Assert.Equal(2, undo.Undo.CreatedIds.Count);
    }

    [Fact]
    public async Task ACancelDuringTheBuildKeepsTheRealStateWithItsUndo()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        var harness = new ComplianceHarness();
        harness.Service.DeployGate = new TaskCompletionSource();
        harness.Service.InConfirmedRun = null;
        var plan = DeployPlan(harness, out _);
        var writer = harness.Writer();
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        var run = dialog.ConfirmCommand.ExecuteAsync(null);
        while (dialog.StepText.Length == 0) await Task.Delay(5);
        dialog.RequestCancel();
        await run;
        var row = Assert.Single(dialog.ResultRows);
        Assert.False(row.Succeeded);
        Assert.True(row.PartialChange);
        Assert.Contains("Undo removes it", row.Message);
        Assert.Single(plan.UndoChanges());
        Assert.Equal(ApplicationPackagerEngine.Services.SiteOperationOutcome.PartlySucceeded, harness.History.Entries.Single().Outcome);
    }

    [Fact]
    public async Task AFailureAfterAStepWasReportedKeepsTheStepAsAFailedResult()
    {
        var harness = new ComplianceHarness();
        harness.Service.InConfirmedRun = null;
        var plan = DeployPlan(harness, out _);
        var service = new ThrowingAfterStep();
        var writer = new ComplianceWriter(service, harness.History, harness.Log, () => "LAB");
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        var row = Assert.Single(dialog.ResultRows);
        Assert.False(row.Succeeded);
        Assert.Equal("Deploy: the builder created 1 baseline.", row.Message);
        Assert.Single(plan.UndoChanges());
    }

    private sealed class ThrowingAfterStep : IComplianceService
    {
        public Task<IReadOnlyList<ConfigurationItemRow>> ListConfigurationItemsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BaselineDeploymentRow>> ListDeploymentsAsync(string? baselineId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CiSetting>> ListSettingsAsync(string ciId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DriftRow>> GetDriftAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ComplianceRow>> GetComplianceAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ComplianceChange> PrepareAsync(ComplianceChange request, CancellationToken ct) => Task.FromResult(request);

        public Task<IReadOnlyList<ComplianceOutcome>> ApplyAsync(IReadOnlyList<ComplianceChange> changes, IProgress<ComplianceOutcome> progress, CancellationToken ct)
        {
            var change = changes[0];
            progress.Report(new ComplianceOutcome(change, true, "Running.", change with { CreatedIds = ["CB-1"] }, ["Deploy: the builder created 1 baseline."], InProgress: true));
            throw new InvalidOperationException("session lost");
        }
    }

    [Fact]
    public async Task TheWriterRefusesAConfirmedPlanThatIsNotItsPlan()
    {
        var harness = new ComplianceHarness();
        harness.Service.InConfirmedRun = null;
        var plan = DeployPlan(harness, out _);
        var other = DeployPlan(harness, out _);
        ConfirmedChangePlan? confirmed = null;
        var dialog = new ChangePlanViewModel(other.Plan, (c, p, ct) =>
        {
            confirmed = c;
            return Task.FromResult(new ChangePlanResult([]));
        }, (c, p, ct) => Task.FromResult(new ChangePlanResult([])));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Writer().ApplyAsync(confirmed!, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        Assert.Empty(harness.Service.Applied);
    }

    [Fact]
    public async Task AConfirmationWithoutTheSecondCheckIsNeverIssuedForARemediationPlan()
    {
        var harness = new ComplianceHarness();
        var change = ComplianceRequests.Deploy(harness.Autorun, new Dictionary<string, string>(), ["LAB00010"], true);
        var (plan, _) = ComplianceCatalogViewModel.BuildDeployPlan(harness.Autorun, change, harness.Collections.Collections);
        Assert.NotNull(plan!.Plan.ExtraConfirmation);
        var dialog = new ChangePlanViewModel(plan!.Plan, harness.Writer().RunFor(plan), harness.Writer().UndoFor(plan)) { DestructiveAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(harness.Service.Applied);
        Assert.Equal(ChangePlanStage.Review, dialog.Stage);
    }

    [Fact]
    public async Task TheHistoryAndTheLogKeepCountsOnly()
    {
        var harness = new ComplianceHarness();
        var viewModel = await harness.OpenAsync(ComplianceViewModel.LiveTab);
        await viewModel.Live.LoadTask;
        viewModel.Live.SetSelectedDeployments(viewModel.Live.Deployments);
        await viewModel.Live.DeleteDeploymentsCommand.ExecuteAsync(null);
        var entry = harness.History.Entries.Single();
        Assert.Equal(ComplianceWriter.ToolName, entry.Tool);
        Assert.Equal(3, entry.Counts["changed"]);
        var text = string.Join("\n", harness.Log.Entries.Select(e => e.Message));
        Assert.DoesNotContain("SMB Signing", text);
        Assert.DoesNotContain("Servers", text);
        Assert.DoesNotContain("DEP-", text);
        Assert.All(harness.Log.Entries, e => Assert.Equal(ComplianceShared.LogArea, e.Area));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ApplicationPackagerUI"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src");
    }

    [Fact]
    public void OnlyTheWriterThePageServicesAndTheRegistrationNameTheComplianceService()
    {
        var root = SourceRoot();
        var pattern = new Regex(@"\bIComplianceService\b", RegexOptions.CultureInvariant);
        var found = Directory.EnumerateFiles(Path.Combine(root, "ApplicationPackagerUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => pattern.IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)).Order().ToList();
        Assert.Equal(
        [
            Path.Combine("ApplicationPackagerUI", "Services", "CompliancePlan.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "CompliancePrompts.cs"),
            Path.Combine("ApplicationPackagerUI", "ViewModels", "Pages", "ComplianceShared.cs")
        ], found.Where(f => !f.EndsWith("PackagerServiceRegistration.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterAndThePageServicesHoldsTheComplianceService()
    {
        var allowed = new[] { typeof(ComplianceWriter), typeof(CompliancePageServices) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(ComplianceWriter).Assembly.GetTypes()
            .Where(t => t.GetFields(all).Any(f => typeof(IComplianceService).IsAssignableFrom(f.FieldType))
                || t.GetProperties(all).Any(p => typeof(IComplianceService).IsAssignableFrom(p.PropertyType)))
            .Select(t => { while (t.DeclaringType is { } outer) t = outer; return t; })
            .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal) && t.Namespace?.StartsWith("ApplicationPackagerUI", StringComparison.Ordinal) == true)
            .Distinct().ToList();
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }
}
