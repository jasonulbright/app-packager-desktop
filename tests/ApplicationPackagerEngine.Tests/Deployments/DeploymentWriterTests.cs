using System.Reflection;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentWriterTests
{
    private static DeploymentOptions Options(string purpose = "Available", DateTime? available = null, DateTime? deadline = null) =>
        new(purpose, available ?? new DateTime(2026, 10, 20, 10, 0, 0), deadline, false, "DisplayAll", false, false, false,
            DeploymentText.NetworkBehaviors[0].Value, DeploymentText.RerunBehaviors[0].Value, true, false, true, true, false);

    private static DeploymentChange Create(DeploymentHarness h, string purpose = "Available", string collection = "LAB00010", DateTime? available = null) =>
        new(DeploymentChangeKind.Create, h.SevenZip, new DeploymentCollection(collection, "Pilot Workstations", "", 3),
            Options(purpose, available, purpose == "Required" ? new DateTime(2026, 10, 22, 8, 0, 0) : null), [], false, false, false, null,
            DeploymentText.CheckNames.Select(n => new PreflightCheck(n, "Pass", "")).ToList(), [], null);

    /// <summary>A plan made by hand, so a test can give a Required change a plan without the second check box.</summary>
    private static DeploymentPlan PlanOf(DeploymentChange change, string? extra = null) =>
        new("Create deployment", "Deploy 7-Zip", "Create deployment", [DeploymentPlanEntry.Write(new ChangePlanItem("Create deployment", "7-Zip", null), change)],
            extraConfirmation: extra);

    [Fact]
    public async Task ARequiredCreateWithoutTheSecondConfirmationIsHeldBackAndNeverReachesTheService()
    {
        var h = new DeploymentHarness();
        h.Service.InConfirmedRun = null;
        var plan = PlanOf(Create(h, "Required") with { RequiredConfirmed = true });
        var writer = h.Writer();
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(h.Service.Applied);
        var row = Assert.Single(dialog.ResultRows);
        Assert.True(row.Skipped);
        Assert.Equal(DeploymentWriter.NotAcknowledgedText, row.Message);
    }

    [Fact]
    public async Task TheServiceRefusesARequiredCreateThatArrivesWithoutTheAcknowledgement()
    {
        var h = new DeploymentHarness();
        h.Service.InConfirmedRun = null;
        var outcomes = await h.Service.ApplyAsync([Create(h, "Required")], new Progress<DeploymentOutcome>(), CancellationToken.None);
        Assert.False(outcomes.Single().Succeeded);
        Assert.Equal(FakeDeploymentService.RequiredRefused, outcomes.Single().Message);
        Assert.Empty(h.Service.Deployments);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void TheWriterSetsTheFlagsOnlyForAPlanThatAskedTheSecondConfirmation(bool acknowledged, bool expected)
    {
        var h = new DeploymentHarness();
        var available = Create(h) with { RequiredConfirmed = true, BackdatedAcknowledged = true };
        var required = Create(h, "Required");
        var distribute = new DeploymentChange(DeploymentChangeKind.Distribute, h.SevenZip, null, null, ["All DPs"], false, true, true, null, [], [], null);
        var (send, held) = DeploymentWriter.Confirmed([available, required, distribute], acknowledged);
        Assert.False(send.Single(c => c.Kind == DeploymentChangeKind.Create && c.Options!.Purpose == "Available").RequiredConfirmed);
        Assert.Equal(expected, send.Single(c => c.Kind == DeploymentChangeKind.Create && c.Options!.Purpose == "Available").BackdatedAcknowledged);
        Assert.Equal(acknowledged ? 0 : 1, held.Count);
        if (acknowledged) Assert.True(send.Single(c => c.Options?.Purpose == "Required").RequiredConfirmed);
        var sentDistribute = send.Single(c => c.Kind == DeploymentChangeKind.Distribute);
        Assert.False(sentDistribute.RequiredConfirmed || sentDistribute.BackdatedAcknowledged);
    }

    [Fact]
    public async Task TheWriterRefusesAConfirmedPlanThatIsNotItsPlan()
    {
        var h = new DeploymentHarness();
        h.Service.InConfirmedRun = null;
        var plan = PlanOf(Create(h));
        var other = PlanOf(Create(h));
        ConfirmedChangePlan? confirmed = null;
        var dialog = new ChangePlanViewModel(other.Plan, (c, _, _) =>
        {
            confirmed = c;
            return Task.FromResult(new ChangePlanResult([]));
        }, (_, _, _) => Task.FromResult(new ChangePlanResult([])));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Writer().ApplyAsync(confirmed!, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Writer().UndoAsync(confirmed!, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public async Task ACancelInAMultiChangePlanKeepsTheDoneChangeAndSkipsTheRest()
    {
        var h = new DeploymentHarness();
        h.Service.InConfirmedRun = null;
        var distribute = new DeploymentChange(DeploymentChangeKind.Distribute, h.Notepad, null, null, ["All DPs"], false, false, false, null, [], ["Distribution point group All DPs: NotTargeted now."], null);
        var create = Create(h) with { Object = h.Notepad, DpGroups = ["All DPs"] };
        var (plan, problem) = DeploymentText.BuildDeployPlan([distribute, create], h.Clock);
        Assert.Null(problem);
        var writer = h.Writer();
        var dialog = new ChangePlanViewModel(plan!.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        h.Service.AfterChange = i =>
        {
            if (i == 0) dialog.RequestCancel();
        };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(["Changed", "Skipped"], dialog.ResultRows.Select(r => r.OutcomeText));
        Assert.Equal(ChangePlanStop.Cancelled, dialog.Outcome.Result!.Stop);
        Assert.Empty(h.Service.Deployments);
        Assert.Equal(SiteOperationOutcome.Cancelled, h.History.Entries.Single().Outcome);
        Assert.False(dialog.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ARunThatThrowsKeepsTheOutcomesReportedBeforeIt()
    {
        var h = new DeploymentHarness();
        var create = Create(h);
        var plan = PlanOf(create);
        var writer = new DeploymentWriter(new ThrowingAfterOutcome(), h.History, h.Log, () => "LAB");
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        var row = Assert.Single(dialog.ResultRows);
        Assert.True(row.Succeeded);
        Assert.Single(plan.UndoChanges());
        Assert.Equal(ChangePlanViewModel.FailedText + " Before the failure: 1 changed, 0 failed.", dialog.ResultText);
    }

    private sealed class ThrowingAfterOutcome : IDeploymentService
    {
        public Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct) => Task.FromResult(request);

        public Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct)
        {
            var change = changes[0];
            var undo = change with { Kind = DeploymentChangeKind.RemoveDeployment, CreatedId = "{DEP-1}" };
            progress.Report(new DeploymentOutcome(change, true, "Created deployment {DEP-1}.", undo, []));
            throw new InvalidOperationException("session lost");
        }
    }

    [Fact]
    public void EveryWriterMethodThatWritesTakesAConfirmedPlan()
    {
        var methods = typeof(DeploymentWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(Task<ChangePlanResult>)).ToList();
        Assert.Equal(2, methods.Count);
        Assert.All(methods, m => Assert.Equal(typeof(ConfirmedChangePlan), m.GetParameters()[0].ParameterType));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ApplicationPackagerUI"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "src");
    }

    private static IReadOnlyList<string> UiFilesWith(Regex pattern)
    {
        var root = SourceRoot();
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root, "ApplicationPackagerUI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) && !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(f => pattern.IsMatch(MaintenanceWindowWriterTests.Code(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public void OnlyTheWriterCallsTheDeploymentServiceWrite()
    {
        var calls = UiFilesWith(new Regex(@"\b(service|Deployments)\s*\.\s*ApplyAsync\b", RegexOptions.CultureInvariant));
        Assert.Contains(Path.Combine("ApplicationPackagerUI", "Services", "DeploymentPlan.cs"), calls);
        var deploymentPage = UiFilesWith(new Regex(@"\bDeployment", RegexOptions.CultureInvariant)).Where(f => f.Contains("Deployment", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(deploymentPage);
        foreach (var file in deploymentPage.Where(f => !f.EndsWith("DeploymentPlan.cs", StringComparison.Ordinal) && !f.EndsWith("DeploymentServiceFallbacks.cs", StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"\bApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), file))));
    }

    [Fact]
    public void OnlyTheWriterThePageServicesAndTheRegistrationsNameTheDeploymentService()
    {
        Assert.Equal(
        [
            Path.Combine("ApplicationPackagerUI", "Services", "DeploymentHelperPrompts.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "DeploymentPlan.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "DeploymentServiceFallbacks.cs"),
            Path.Combine("ApplicationPackagerUI", "ViewModels", "Pages", "DeploymentShared.cs")
        ], UiFilesWith(new Regex(@"\bIDeploymentService\b", RegexOptions.CultureInvariant)).Where(f => !f.EndsWith("PackagerServiceRegistration.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterAndThePageServicesHoldsTheDeploymentService()
    {
        var allowed = new[] { typeof(DeploymentWriter), typeof(DeploymentPageServices) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(DeploymentWriter).Assembly.GetTypes()
            .Where(t => t.GetFields(all).Any(f => typeof(IDeploymentService).IsAssignableFrom(f.FieldType))
                || t.GetProperties(all).Any(p => typeof(IDeploymentService).IsAssignableFrom(p.PropertyType)))
            .Select(t => { while (t.DeclaringType is { } outer) t = outer; return t; })
            .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal) && t.Namespace?.StartsWith("ApplicationPackagerUI", StringComparison.Ordinal) == true)
            .Distinct().ToList();
        Assert.Contains(typeof(DeploymentWriter), holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }
}
