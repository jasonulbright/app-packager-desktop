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

    private static string UiRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "src", "ApplicationPackagerUI"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine(folder!.FullName, "src", "ApplicationPackagerUI");
    }

    /// <summary>The code of every UI source file without comments and string literals, by path relative to the UI project.</summary>
    private static Dictionary<string, string> UiCode()
    {
        var root = UiRoot();
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => MaintenanceWindowWriterTests.Code(File.ReadAllText(f)), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyTheWriterCallsTheCollectionServiceWriteAndThePageCallsOnlyItsReads()
    {
        var code = UiCode();
        var writers = code.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Value, @"\bservice\s*\.\s*ApplyAsync\b")).Select(f => f.Key).ToList();
        Assert.Equal([Path.Combine("Services", "CollectionsPlan.cs")], writers.Where(f => !f.Contains("Maintenance", StringComparison.Ordinal)));

        Assert.Equal(
        [
            Path.Combine("Services", "CollectionCompliancePrompts.cs"),
            Path.Combine("Services", "CollectionsPlan.cs"),
            Path.Combine("Services", "PackagerServiceRegistration.cs"),
            Path.Combine("ViewModels", "Pages", "CollectionComplianceViewModel.cs")
        ], code.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Value, @"\bICollectionService\b")).Select(f => f.Key).Order(StringComparer.OrdinalIgnoreCase));

        string[] reads = ["ListCollectionsAsync", "GetCollectionAsync", "GetDetailAsync", "ValidateWqlAsync", "PreviewWqlAsync", "ListSavedQueriesAsync", "PrepareAsync"];
        var page = code.Where(f => f.Key.Contains("CollectionComplianceViewModel", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, page.Count);
        var used = page.SelectMany(f => System.Text.RegularExpressions.Regex.Matches(f.Value, @"\.\s*Collections\s*\.\s*(\w+)").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.NotEmpty(used);
        Assert.All(used, m => Assert.Contains(m, reads));
        Assert.All(page, f => Assert.DoesNotMatch(@"\bApplyAsync\b", f.Value));
    }
}
