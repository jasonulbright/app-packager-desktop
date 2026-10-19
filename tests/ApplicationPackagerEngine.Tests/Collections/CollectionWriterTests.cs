using System.Reflection;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>The write path of the Collection and Compliance page: only a confirmed plan reaches the service.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CollectionWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-collections-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public CollectionWriterTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static CollectionChange Evaluate(CollectionRow row) => new(CollectionChangeKind.Evaluate, row.CollectionId, row.Name, row, null, null, null, null, null, []);

    private static CollectionPlan PlanOf(params CollectionChange[] changes) =>
        new("Evaluate", "Update membership", "Update", changes.Select(c => CollectionPlanEntry.Write(new ChangePlanItem("Evaluate", c.CollectionName, null), c)), hasUndo: false);

    [Fact]
    public async Task AConfirmedPlanOfOtherItemsIsRefusedAndWritesNothing()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.InConfirmedRun = null;
        var writer = harness.Writer();
        var plan = PlanOf(Evaluate(harness.Servers));
        var other = PlanOf(Evaluate(harness.Workstations));
        var dialog = new ChangePlanViewModel(other.Plan, (confirmed, progress, ct) => writer.ApplyAsync(confirmed, plan, progress, ct));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(harness.Service.Applied);
        Assert.Empty(harness.History.Entries);
    }

    [Fact]
    public async Task EachOutcomeCountsOnceAndTheHistoryGetsCountsOnly()
    {
        var harness = new CollectionComplianceHarness();
        harness.Service.InConfirmedRun = null;
        harness.Service.Fail = c => c.CollectionId == "LAB00011" ? "Access is denied." : null;
        var writer = harness.Writer();
        var plan = PlanOf(Evaluate(harness.Servers), Evaluate(harness.Workstations));
        var dialog = new ChangePlanViewModel(plan.Plan, writer.RunFor(plan));
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal([true, false], dialog.ResultRows.Select(r => r.Succeeded));
        var entry = Assert.Single(harness.History.Entries);
        Assert.Equal(("Collection and Compliance", "Evaluate", "LAB", SiteOperationOutcome.PartlySucceeded), (entry.Tool, entry.Action, entry.Target, entry.Outcome));
        Assert.Equal((1, 1, 0), (entry.Counts["changed"], entry.Counts["failed"], entry.Counts["skipped"]));
        Assert.All(harness.Log.Entries, e => Assert.DoesNotContain("Servers", e.Message));
    }

    [Fact]
    public void ASkippedOutcomeOfACancelledRunCountsAsSkipped()
    {
        Assert.StartsWith(CollectionWriter.SkippedPrefix, FakeCollectionService.CancelledSkip, StringComparison.Ordinal);
    }

    private static bool IsService(Type type) => typeof(ICollectionService).IsAssignableFrom(type);

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer) type = outer;
        return type;
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterAndThePageHoldsTheCollectionService()
    {
        var allowed = new[] { typeof(CollectionWriter), typeof(CollectionComplianceViewModel), typeof(CollectionCompliancePageServices) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(CollectionWriter).Assembly.GetTypes()
            .Where(t => !t.Name.StartsWith("Unavailable", StringComparison.Ordinal))
            .Where(t => IsService(t)
                || t.GetConstructors(all).Any(c => c.GetParameters().Any(p => IsService(p.ParameterType)))
                || t.GetFields(all).Any(f => IsService(f.FieldType))
                || t.GetProperties(all).Any(p => IsService(p.PropertyType)))
            .Select(Outermost)
            .Distinct()
            .ToList();
        Assert.Contains(typeof(CollectionWriter), holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }

    [Fact]
    public void ThePageServicesResolveAndARegisteredServiceIsKept()
    {
        var service = new FakeCollectionService();
        var store = new FakeQueryTemplateStore();
        var services = new ServiceCollection().AddPackagerServices();
        services.AddSingleton<ICollectionService>(service);
        services.AddSingleton<IQueryTemplateStore>(store);
        services.AddSingleton<IChangePlanDialogs>(new ScriptedPlanDialogs());
        services.AddCollectionCompliancePage();
        using var provider = services.BuildServiceProvider();

        Assert.Same(service, provider.GetRequiredService<ICollectionService>());
        Assert.Same(store, provider.GetRequiredService<IQueryTemplateStore>());
        Assert.NotNull(provider.GetRequiredService<ICollectionReportWriter>());
        Assert.Same(provider.GetRequiredService<CollectionComplianceViewModel>(), provider.GetRequiredService<CollectionComplianceViewModel>());
        Assert.NotNull(provider.GetRequiredService<CollectionWriter>());
        Assert.IsType<CollectionCompliancePrompts>(provider.GetRequiredService<ICollectionCompliancePrompts>());
    }

    [Fact]
    public async Task ThePageChoicesAreStoredInTheSettingsFileWithoutChangingOtherValues()
    {
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.SiteCode = "LAB";
        settings.Save(stored);
        Assert.Null(CollectionCompliancePreferences.Load(settings));
        Assert.True(CollectionCompliancePreferences.Save(settings, new ApplicationPackagerUI.Models.CollectionComplianceSettings { SelectedTab = 3, TextFilter = "pilot" }));
        var again = settings.Load();
        Assert.Equal("LAB", again.SiteCode);
        Assert.Equal((3, "pilot"), (again.CollectionCompliance!.SelectedTab, again.CollectionCompliance.TextFilter));
        await Task.CompletedTask;
    }
}
