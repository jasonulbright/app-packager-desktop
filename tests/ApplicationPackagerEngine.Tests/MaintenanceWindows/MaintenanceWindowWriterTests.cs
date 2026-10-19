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

    private static string UiSourceRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerUI"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder!.FullName, "src", "ApplicationPackagerUI");
    }

    [Fact]
    public void OnlyTheWriterCallsTheServiceWrite()
    {
        var root = UiSourceRoot();
        var calls = new Regex(@"\.\s*ApplyAsync\s*\(", RegexOptions.CultureInvariant);
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => calls.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.Equal([Path.Combine("Services", "MaintenanceWindowsPlan.cs")], callers);
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

    [Fact]
    public async Task ACancelKeepsTheReportedOutcomesForTheUndo()
    {
        var harness = NewHarness();
        var writer = harness.Writer();
        var plan = PlanOf(Write(Create(harness.Kiosks, "A")), Write(Create(harness.Kiosks, "B")), Write(Create(harness.Kiosks, "C")));
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan), writer.UndoFor(plan));
        harness.Service.AfterChange = i => { if (i == 0) dialog.RequestCancel(); };
        await dialog.ConfirmCommand.ExecuteAsync(null);

        var result = dialog.Outcome.Result!;
        Assert.Equal(ChangePlanStop.Cancelled, result.Stop);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Single(plan.UndoChanges());
        Assert.Equal(SiteOperationOutcome.Cancelled, harness.History.Entries.Single().Outcome);
        Assert.True(dialog.UndoCommand.CanExecute(null));
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
