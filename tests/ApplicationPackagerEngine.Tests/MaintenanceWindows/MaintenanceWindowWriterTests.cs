using System.Reflection;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

public sealed class MaintenanceWindowWriterTests
{
    private static MaintenanceChange Create(CollectionInfo c, string name) =>
        new(ChangeKind.Create, c.CollectionId, c.Name, name, null, null,
            new MaintenanceWindowInfo(c.CollectionId, c.Name, name, "", "Any", true, FakeMaintenanceService.Weekly(new DateTime(2026, 11, 1, 2, 0, 0)), "Weekly", null, ""));

    private static MaintenanceWindowsHarness NewHarness()
    {
        var harness = new MaintenanceWindowsHarness();
        harness.Service.InConfirmedRun = null;
        return harness;
    }

    private static MaintenancePlan PlanOf(params MaintenancePlanEntry[] entries) => new("Create", "New windows", "Create windows", entries);

    private static MaintenancePlanEntry Write(MaintenanceChange change) =>
        MaintenancePlanEntry.Write(new ChangePlanItem("Create", change.CollectionName + ": " + change.WindowName, null), change);

    [Fact]
    public async Task OnlyTheConfirmedPlanRunsAndOnlyItsChangesReachTheService()
    {
        var harness = NewHarness();
        var writer = harness.Writer();
        var plan = PlanOf(Write(Create(harness.Kiosks, "A")), MaintenancePlanEntry.Skip(new ChangePlanItem("Skip", "Servers: A", null), "Exists."));
        var other = PlanOf(Write(Create(harness.Kiosks, "B")));

        var mismatched = new ChangePlanViewModel(other.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await mismatched.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(ChangePlanStop.Failed, mismatched.Outcome.Result!.Stop);
        Assert.Empty(harness.Service.Applied);

        var confirmed = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await confirmed.ConfirmCommand.ExecuteAsync(null);
        var applied = Assert.Single(harness.Service.Applied);
        Assert.Equal(["A"], applied.Select(c => c.WindowName));
        var result = confirmed.Outcome.Result!;
        Assert.Equal(["Changed", "Skipped"], result.Items.Select(i => i.OutcomeText));
        Assert.Equal("1 changed, 0 failed, 1 skipped.", result.Summary);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void EveryWriterMethodThatWritesTakesAConfirmedPlan()
    {
        var methods = typeof(MaintenanceWindowWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(Task<ChangePlanResult>)).ToList();
        Assert.Equal(2, methods.Count);
        Assert.All(methods, m => Assert.Equal(typeof(ConfirmedChangePlan), m.GetParameters()[0].ParameterType));
    }

    private static string SourceRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerUI"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder!.FullName, "src");
    }

    private static readonly Regex Comments = new(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Strings = new(@"@""(?:[^""]|"""")*""|""(?:\\.|[^""\\\r\n])*""", RegexOptions.CultureInvariant);

    /// <summary>The source text without string literals and comments, so a mention in either is not counted.</summary>
    internal static string Code(string source) => Comments.Replace(Strings.Replace(source, "\"\""), "");

    private static List<string> FilesWith(Regex pattern)
    {
        var root = SourceRoot();
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .Where(f => pattern.IsMatch(Code(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [Fact]
    public void TheCodeFilterIgnoresCommentsAndStringsButNotACallOrAMethodGroup()
    {
        var name = new Regex(@"\bApplyAsync\b");
        Assert.DoesNotMatch(name, Code("// service.ApplyAsync(x)\n/* ApplyAsync */ var s = \"ApplyAsync\";"));
        Assert.Matches(name, Code("Func<int> f = service.ApplyAsync;"));
        Assert.Matches(name, Code("var t = service . ApplyAsync (changes, p, ct); // comment"));
    }

    [Fact]
    public void OnlyTheWriterCallsTheServiceWrite()
    {
        // Each file has a reason: a contract declares the method, a service implements it, a
        // writer is its only caller, and the WSUS dialog has an unrelated private method of the same name.
        string[] required =
        [
            Path.Combine("ApplicationPackagerEngine", "Services", "Collections", "CollectionsContract.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Collections", "CollectionService.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Compliance", "ComplianceContract.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Compliance", "ComplianceService.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "MaintenanceWindows", "MaintenanceWindowsContract.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "MaintenanceWindows", "MaintenanceWindowService.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Deployments", "DeploymentsContract.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Deployments", "DeploymentService.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "MaintenanceWindowsPlan.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "CollectionsPlan.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Compliance", "ComplianceContract.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "CompliancePlan.cs"),
            Path.Combine("ApplicationPackagerEngine", "Services", "Deployments", "DeploymentsContract.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "DeploymentPlan.cs"),
            Path.Combine("ApplicationPackagerUI", "ViewModels", "Windows", "WsusUpdatesViewModel.cs")
        ];
        var found = FilesWith(new Regex(@"\bApplyAsync\b", RegexOptions.CultureInvariant));
        Assert.All(required, f => Assert.Contains(f, found));
        Assert.All(found, f => Assert.Contains(f, required));
        Assert.DoesNotContain("Maintenance", Code(File.ReadAllText(Path.Combine(SourceRoot(), "ApplicationPackagerUI", "ViewModels", "Windows", "WsusUpdatesViewModel.cs"))));
    }

    [Fact]
    public void TheServiceIsNamedOnlyByTheWriterThePageAndTheRegistration()
    {
        Assert.Equal(
        [
            Path.Combine("ApplicationPackagerUI", "Services", "MaintenanceWindowsPlan.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "MaintenanceWindowsPrompts.cs"),
            Path.Combine("ApplicationPackagerUI", "Services", "PackagerServiceRegistration.cs"),
            Path.Combine("ApplicationPackagerUI", "ViewModels", "Pages", "MaintenanceWindowsViewModel.cs")
        ], FilesWith(new Regex(@"\bIMaintenanceWindowService\b", RegexOptions.CultureInvariant)).Where(f => f.StartsWith("ApplicationPackagerUI", StringComparison.Ordinal)));
    }

    private static bool IsService(Type type) => typeof(IMaintenanceWindowService).IsAssignableFrom(type);

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer) type = outer;
        return type;
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterAndThePageHoldsTheService()
    {
        var allowed = new[] { typeof(MaintenanceWindowWriter), typeof(ApplicationPackagerUI.ViewModels.Pages.MaintenanceWindowsViewModel),
            typeof(ApplicationPackagerUI.ViewModels.Pages.MaintenanceWindowsPageServices) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(MaintenanceWindowWriter).Assembly.GetTypes()
            .Where(t => IsService(t)
                || t.GetConstructors(all).Any(c => c.GetParameters().Any(p => IsService(p.ParameterType)))
                || t.GetFields(all).Any(f => IsService(f.FieldType))
                || t.GetProperties(all).Any(p => IsService(p.PropertyType)))
            .Select(Outermost)
            .Distinct()
            .ToList();
        Assert.NotEmpty(holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
        Assert.Contains(typeof(MaintenanceWindowWriter), holders);
    }

    [Fact]
    public async Task EachOutcomeIsReportedOnceAndTheSiteHistoryGetsCountsOnly()
    {
        var harness = NewHarness();
        var writer = harness.Writer();
        harness.Service.Fail = c => c.WindowName == "B" ? "The collection does not exist any more." : null;
        var plan = PlanOf(Write(Create(harness.Kiosks, "A")), Write(Create(harness.Kiosks, "B")));
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal([true, false], dialog.ResultRows.Select(r => r.Succeeded));
        Assert.Equal("The collection does not exist any more.", dialog.ResultRows[1].Message);
        var entry = Assert.Single(harness.History.Entries);
        Assert.Equal(("Maintenance Windows", "Create", "LAB", SiteOperationOutcome.PartlySucceeded), (entry.Tool, entry.Action, entry.Target, entry.Outcome));
        Assert.Equal(1, entry.Counts["changed"]);
        Assert.Equal(1, entry.Counts["failed"]);
        Assert.All(harness.Log.Entries, e => Assert.DoesNotContain("Kiosks", e.Message));
        Assert.Single(plan.UndoChanges());
    }

    [Fact]
    public async Task UndoRunsTheUndoChangesOfTheChangesThatSucceeded()
    {
        var harness = NewHarness();
        var writer = harness.Writer();
        var window = harness.Service.Windows.First(w => w.WindowName == "Nightly");
        var remove = new MaintenanceChange(ChangeKind.Remove, window.CollectionId, window.CollectionName, window.WindowName, null, window, null);
        var plan = new MaintenancePlan("Remove", "Remove", "Remove 1 window",
            [MaintenancePlanEntry.Write(new ChangePlanItem("Remove", "Workstations: Nightly", null), remove)], isDestructive: true);
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan)) { DestructiveAcknowledged = true };
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.DoesNotContain(harness.Service.Windows, w => w.WindowName == "Nightly");

        await dialog.UndoCommand.ExecuteAsync(null);
        var recreated = Assert.Single(harness.Service.Windows, w => w.WindowName == "Nightly");
        Assert.Equal(window.Schedule, recreated.Schedule);
        Assert.NotEqual(window.ServiceWindowId, recreated.ServiceWindowId);
        Assert.Equal(ChangeKind.Create, harness.Service.Applied[1].Single().Kind);
        Assert.Contains(MaintenanceWindowWriter.RecreatedNote, dialog.Outcome.UndoResult!.Items.Single().Message);
        Assert.Equal(["Remove", "Undo Remove"], harness.History.Entries.Select(e => e.Action));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACancelKeepsTheReportedOutcomesForTheUndo(bool throwOnCancel)
    {
        var harness = NewHarness();
        harness.Service.ThrowOnCancel = throwOnCancel;
        var writer = harness.Writer();
        var plan = PlanOf(Write(Create(harness.Kiosks, "A")), Write(Create(harness.Kiosks, "B")), Write(Create(harness.Kiosks, "C")));
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        harness.Service.AfterChange = i => { if (i == 0) dialog.RequestCancel(); };
        await dialog.ConfirmCommand.ExecuteAsync(null);

        var result = dialog.Outcome.Result!;
        Assert.Equal(ChangePlanStop.Cancelled, result.Stop);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(throwOnCancel ? (2, 0) : (0, 2), (result.FailedCount, result.SkippedCount));
        Assert.Single(plan.UndoChanges());
        Assert.Equal(SiteOperationOutcome.Cancelled, harness.History.Entries.Single().Outcome);
        Assert.True(dialog.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACancelDuringTheLastChangeOfARunThatMadeEveryChangeRecordsItAsSucceeded()
    {
        var harness = NewHarness();
        var writer = harness.Writer();
        var plan = PlanOf(Write(Create(harness.Kiosks, "A")), Write(Create(harness.Kiosks, "B")));
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        harness.Service.AfterChange = i => { if (i == 1) dialog.RequestCancel(); };
        await dialog.ConfirmCommand.ExecuteAsync(null);

        var result = dialog.Outcome.Result!;
        Assert.Equal(ChangePlanStop.Completed, result.Stop);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(SiteOperationOutcome.Succeeded, harness.History.Entries.Single().Outcome);
    }

    [Fact]
    public async Task ACancelDuringTheLastUndoChangeOfAnUndoThatMadeEveryChangeRecordsItAsSucceeded()
    {
        var harness = NewHarness();
        var writer = harness.Writer();
        var plan = PlanOf(Write(Create(harness.Kiosks, "A")), Write(Create(harness.Kiosks, "B")));
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        harness.Service.AfterChange = i => { if (i == 1) dialog.RequestCancel(); };
        await dialog.UndoCommand.ExecuteAsync(null);

        Assert.Equal(ChangePlanStop.Completed, dialog.Outcome.UndoResult!.Stop);
        Assert.Equal(SiteOperationOutcome.Succeeded, harness.History.Entries[1].Outcome);
    }

    [Fact]
    public async Task AFailedRenameThatCreatedTheNewWindowCountsAsAChangeInTheHistoryAndTheSummary()
    {
        var harness = NewHarness();
        harness.Service.RenameRemoveFails = true;
        var writer = harness.Writer();
        var window = harness.Service.Windows.First(w => w.WindowName == "Nightly");
        var rename = new MaintenanceChange(ChangeKind.Rename, window.CollectionId, window.CollectionName, window.WindowName, "Nightly 2", window, window);
        var plan = new MaintenancePlan("Rename", "Rename", "Rename 1 window",
            [MaintenancePlanEntry.Write(new ChangePlanItem("Rename", "Workstations: Nightly", null), rename)]);
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);

        var result = dialog.Outcome.Result!;
        Assert.Equal(1, result.FailedCount);
        Assert.True(result.Items.Single().PartialChange);
        Assert.Equal("0 changed, 1 failed. 1 failed change was made in part; Undo can reverse it.", result.Summary);
        var entry = Assert.Single(harness.History.Entries);
        Assert.Equal(1, entry.Counts["changed"]);
        Assert.Equal(1, entry.Counts["failed"]);
        Assert.Equal(SiteOperationOutcome.PartlySucceeded, entry.Outcome);
    }

    [Fact]
    public void APlanWithOnlySkippedLinesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => PlanOf(MaintenancePlanEntry.Skip(new ChangePlanItem("Skip", "x", null), "Exists.")));
    }

    [Fact]
    public void ASkippedResultCountsNeitherAsChangedNorAsFailed()
    {
        var item = new ChangePlanItem("Skip", "Servers: A", null);
        var result = new ChangePlanResult([ChangePlanItemResult.Skip(item, "Exists."), new ChangePlanItemResult(item, true, "Created.")]);
        Assert.Equal((1, 0, 1), (result.SucceededCount, result.FailedCount, result.SkippedCount));
        Assert.Equal("Skipped", result.Items[0].OutcomeText);
        Assert.Equal("1 changed, 0 failed.", new ChangePlanResult([new ChangePlanItemResult(item, true, "")]).Summary);
    }
}
