using System.Reflection;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.MaintenanceWindows;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.ViewModels.Windows;
using ApplicationPackagerUI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>The Site Hygiene writer: it sends only the prepared change of a confirmed plan, and DeleteConfirmed only as that plan asked it.</summary>
public sealed class SiteHygieneWriterTests
{
    private static (SiteHygienePlan Plan, SiteHygienePageHarness Harness) Plan(HygieneFixKind kind, params Func<SiteHygienePageHarness, HygieneFinding>[] findings)
    {
        var h = new SiteHygienePageHarness();
        h.Service.InConfirmedRun = null;
        var chosen = findings.Select(f => f(h)).ToList();
        var prepared = h.Service.PrepareAsync(new HygieneChange(kind, chosen, false, [], [], null), CancellationToken.None).Result;
        var (plan, problem) = SiteHygieneText.BuildPlan(prepared);
        Assert.Null(problem);
        return (plan!, h);
    }

    private static ChangePlanViewModel Confirmed(SiteHygienePlan plan, ChangePlanRun run, bool destructive = true, bool extra = true)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, run) { DestructiveAcknowledged = destructive, ExtraAcknowledged = extra };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        return dialog;
    }

    [Fact]
    public void ADeleteCarriesDeleteConfirmedOnlyWhenTheConfirmedPlanAskedTheSecondBox()
    {
        var (plan, h) = Plan(HygieneFixKind.RemoveApplication, x => x.UnusedApp);
        Confirmed(plan, h.Writer().RunFor(plan));
        Assert.True(Assert.Single(h.Service.Applied).DeleteConfirmed);

        foreach (var (d, e) in new[] { (false, true), (true, false) })
        {
            var dialog = Confirmed(plan, h.Writer().RunFor(plan), d, e);
            Assert.False(dialog.Outcome.Confirmed);
        }
        Assert.Single(h.Service.Applied);
    }

    [Fact]
    public void APlanWithoutTheSecondBoxReachesTheServiceUnconfirmedAndTheServiceSkipsEveryObject()
    {
        var (plan, h) = Plan(HygieneFixKind.RemoveApplication, x => x.UnusedApp, x => x.UnusedApp2);
        var unconfirmed = new SiteHygienePlan(plan.Action, plan.Change, new ChangePlan(plan.Plan.Title, plan.Plan.ActionName, plan.Plan.Items), plan.Entries);
        var dialog = Confirmed(unconfirmed, h.Writer().RunFor(unconfirmed));
        Assert.False(Assert.Single(h.Service.Applied).DeleteConfirmed);
        Assert.All(dialog.Outcome.Result!.Items, r => Assert.Equal(FakeSiteHygieneService.DeleteRefused, r.Message));
        Assert.All(dialog.Outcome.Result!.Items, r => Assert.True(r.Skipped));
    }

    [Fact]
    public void AChangeKindNeverCarriesDeleteConfirmedEvenWhenTheChangeHadIt()
    {
        var (plan, h) = Plan(HygieneFixKind.SetCollectionRefreshType, x => x.RefreshType);
        var forged = new SiteHygienePlan(plan.Action, plan.Change with { DeleteConfirmed = true }, plan.Plan, plan.Entries);
        Confirmed(forged, h.Writer().RunFor(forged));
        Assert.False(Assert.Single(h.Service.Applied).DeleteConfirmed);
    }

    [Fact]
    public async Task TheWriterRefusesAPlanThatIsNotTheConfirmedOne()
    {
        var (plan, h) = Plan(HygieneFixKind.RemoveApplication, x => x.UnusedApp);
        var (other, _) = Plan(HygieneFixKind.RemoveApplication, x => x.UnusedApp);
        ConfirmedChangePlan? captured = null;
        Confirmed(other, (confirmed, _, _) =>
        {
            captured = confirmed;
            return Task.FromResult(new ChangePlanResult([]));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Writer().ApplyAsync(captured!, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public void AProgressOutcomeIsAStepOnlyAndEachObjectGetsOneResultFromTheFinalOutcome()
    {
        var (plan, h) = Plan(HygieneFixKind.RemoveApplication, x => x.UnusedApp, x => x.UnusedApp2);
        h.Service.ReadBackFails = f => f == h.UnusedApp2;
        var dialog = Confirmed(plan, h.Writer().RunFor(plan));
        var result = dialog.Outcome.Result!;
        Assert.Equal(["Done", "Failed"], result.Items.Select(r => r.OutcomeText));
        Assert.Equal("1 done, 1 failed.", result.Headline);
        Assert.Equal([SiteHygieneLabels.Key(h.UnusedApp)], plan.DoneKeys());
        var entry = h.History.Entries.Single();
        Assert.Equal(new[] { "changed", "failed", "skipped", "unchanged" }, entry.Counts!.Keys.Order());
        Assert.Equal((1, 1), (entry.Counts["changed"], entry.Counts["failed"]));
        Assert.Equal("Delete application", entry.Action);
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("Contoso", StringComparison.Ordinal) || e.Message.Contains("1677730", StringComparison.Ordinal));
    }

    [Fact]
    public void ACancelBetweenObjectsReportsTheRestSkippedAndKeepsTheResultsMade()
    {
        var (plan, h) = Plan(HygieneFixKind.RemoveApplication, x => x.UnusedApp, x => x.UnusedApp2);
        ChangePlanViewModel? dialog = null;
        h.Service.AfterObject = i =>
        {
            if (i == 0) dialog!.RequestCancel();
        };
        dialog = new ChangePlanViewModel(plan.Plan, h.Writer().RunFor(plan)) { DestructiveAcknowledged = true, ExtraAcknowledged = true };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        var result = dialog.Outcome.Result!;
        Assert.Equal(ChangePlanStop.Cancelled, result.Stop);
        Assert.Equal(["Done", "Skipped"], result.Items.Select(r => r.OutcomeText));
        Assert.StartsWith("Skipped", result.Items[1].Message);
        Assert.Equal(SiteOperationOutcome.Cancelled, h.History.Entries.Single().Outcome);
    }

    [Fact]
    public void AnObjectResultKeepsItsWordsAndASkippedMessageStartsWithSkipped()
    {
        var item = new ChangePlanItem("Delete application", "x", null);
        Assert.Equal("Skipped: the object changed", SiteHygieneWriter.Row(item, "Skipped", "the object changed").Message);
        Assert.True(SiteHygieneWriter.Row(item, "Skipped", "x").Skipped);
        Assert.Equal("Failed", SiteHygieneWriter.Row(item, "Odd", "x").OutcomeText);
        Assert.True(SiteHygieneWriter.Row(item, "Done", "x").Succeeded);
        Assert.False(SiteHygieneWriter.Row(item, "Unchanged", "x").Succeeded);
        Assert.Equal("Unchanged", SiteHygieneWriter.Row(item, "Unchanged", "x").OutcomeText);
        Assert.False(SiteHygieneWriter.Row(item, "Failed", "x").Succeeded);
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

    private static string P(params string[] parts) => Path.Combine(["ApplicationPackagerUI", .. parts]);

    [Fact]
    public void OnlyTheWriterCallsTheSiteHygieneServiceWrite()
    {
        var naming = UiFilesWith(new Regex(@"\bISiteHygieneService\b", RegexOptions.CultureInvariant));
        Assert.Equal(
        [
            P("Services", "SiteHygienePlan.cs"), P("Services", "SiteHygienePrompts.cs"),
            P("ViewModels", "Pages", "SiteHygieneViewModel.cs")
        ], naming.Where(f => !f.EndsWith("PackagerServiceRegistration.cs", StringComparison.Ordinal)).ToList());
        Assert.Matches(@"\bservice\s*\.\s*ApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), P("Services", "SiteHygienePlan.cs")))));
        var pageFiles = UiFilesWith(new Regex(@"\bSiteHygiene|\bHygiene", RegexOptions.CultureInvariant))
            .Where(f => !f.EndsWith("SiteHygienePlan.cs", StringComparison.Ordinal)).ToList();
        Assert.Contains(P("ViewModels", "Pages", "SiteHygieneViewModel.Fixes.cs"), pageFiles);
        foreach (var file in pageFiles)
            Assert.DoesNotMatch(@"\bApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), file))));
        // The page reaches the service only with ScanAsync, LoadRelationsAsync and PrepareAsync.
        var calls = pageFiles.SelectMany(f => Regex.Matches(MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), f))), @"\bServices\s*\.\s*Hygiene\s*\.\s*(\w+)").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.Equal(["LoadRelationsAsync", "PrepareAsync", "ScanAsync"], calls.Order());
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterThePageServicesAndTheRegistrationHoldsTheSiteHygieneService()
    {
        var allowed = new[] { typeof(SiteHygieneWriter), typeof(SiteHygienePageServices), typeof(SiteHygieneRegistration) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(SiteHygieneWriter).Assembly.GetTypes()
            .Where(t => typeof(ISiteHygieneService).IsAssignableFrom(t)
                || t.GetFields(all).Any(f => typeof(ISiteHygieneService).IsAssignableFrom(f.FieldType))
                || t.GetProperties(all).Any(p => typeof(ISiteHygieneService).IsAssignableFrom(p.PropertyType)))
            .Select(t => { while (t.DeclaringType is { } outer) t = outer; return t; })
            .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal))
            .Distinct().ToList();
        Assert.Contains(typeof(SiteHygieneWriter), holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteHygieneRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-sh-reg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SiteHygieneRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _saved);
        System.Data.SQLite.SQLiteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection().AddPackagerServices().AddSidebar();
        services.AddSingleton<Wpf.Ui.IContentDialogService, Wpf.Ui.ContentDialogService>();
        services.AddSingleton<IChangePlanDialogs, ChangePlanDialogs>();
        services.AddSiteHygienePage();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    [Fact]
    public void TheApplicationServicesBuildOnePageViewModelThatReadsNothing()
    {
        using var provider = Provider();
        var viewModel = provider.GetRequiredService<SiteHygieneViewModel>();
        Assert.Same(viewModel, provider.GetRequiredService<SiteHygieneViewModel>());
        var services = provider.GetRequiredService<SiteHygienePageServices>();
        Assert.Same(provider.GetRequiredService<SiteHygieneWriter>(), services.Writer);
        Assert.NotNull(provider.GetRequiredService<ISiteHygieneService>());
        Assert.NotNull(provider.GetRequiredService<IHygieneSuppressionStore>());
        Assert.NotNull(provider.GetRequiredService<IHygieneScanStore>());
        Assert.NotNull(provider.GetRequiredService<IHygieneReportWriter>());
        Assert.Equal(TimeSpan.FromMilliseconds(250), services.FilterDelay);
        Assert.Empty(viewModel.FindingRows);
        Assert.False(viewModel.HasResult);
        Assert.False(File.Exists(SettingsService.SettingsPath));
    }

    [Fact]
    public void EveryPageTypeHasOneConstructorSoTheContainerCanBuildIt()
    {
        foreach (var type in new[] { typeof(SiteHygieneViewModel), typeof(SiteHygienePage), typeof(SiteHygieneWriter), typeof(SiteHygieneDialogs), typeof(HygieneFindingsPickerViewModel) })
            Assert.Single(type.GetConstructors());
    }

    [Fact]
    public void TheSavedChoicesRoundTripThroughTheSettingsFileAndKeepOtherValues()
    {
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.SiteCode = "MCM";
        stored.DpContent = new DpContentSettings { SelectedTab = 2 };
        settings.Save(stored);
        Assert.Null(SiteHygienePreferences.Load(settings));
        Assert.True(SiteHygienePreferences.Save(settings, new SiteHygieneSettings
        {
            SelectedTab = 2, Category = "Devices", Areas = ["Applications"], PauseMilliseconds = 250, Sorts = new() { ["findings"] = "Severity|Descending" }
        }));
        var loaded = SiteHygienePreferences.Load(new SettingsService())!;
        Assert.Equal((2, "Devices", 250), (loaded.SelectedTab, loaded.Category, loaded.PauseMilliseconds));
        Assert.Equal(["Applications"], loaded.Areas);
        Assert.Equal("Severity|Descending", loaded.Sorts!["findings"]);
        Assert.Equal("MCM", new SettingsService().Load().SiteCode);
        Assert.Equal(2, new SettingsService().Load().DpContent?.SelectedTab);
    }

    [Fact]
    public void ASettingsFileFromBeforeThePageLoadsWithoutThePageChoices()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, "{ \"FirstRunCompleted\": true, \"SiteCode\": \"MCM\" }");
        var settings = new SettingsService();
        Assert.Null(settings.Load().SiteHygiene);
        Assert.Equal("MCM", settings.Load().SiteCode);
        File.WriteAllText(SettingsService.SettingsPath, "{ \"SiteCode\": \"MCM\", \"SiteHygiene\": { \"SelectedTab\": 1, \"Sorts\": null, \"Areas\": null, \"FindingText\": null } }");
        var loaded = new SettingsService().Load().SiteHygiene!;
        Assert.Equal(1, loaded.SelectedTab);
        Assert.Null(loaded.Areas);
    }

    [Fact]
    public void TheApplicationContainerGivesThePageTheSixRealLiveServicesAndTheSaveWritesTheLiveOptions()
    {
        using var provider = Provider();
        var services = provider.GetRequiredService<SiteHygienePageServices>();
        var live = Assert.IsType<SiteHygieneLiveServices>(services.Live);
        Assert.IsType<SiteHygieneLiveService>(live.Live);
        Assert.Same(provider.GetRequiredService<MetricsHistoryStore>(), live.History);
        Assert.IsType<AlertEvaluator>(live.Alerts);
        Assert.IsType<AlertLog>(live.AlertLog);
        Assert.IsType<LegacyDashboardImport>(live.Import);
        Assert.IsType<LiveReportWriter>(live.Reports);
        Assert.Same(provider.GetRequiredService<IAlertEvaluator>(), live.Alerts);
        var viewModel = provider.GetRequiredService<SiteHygieneViewModel>();
        Assert.True(viewModel.HasLive);
        Assert.True(viewModel.RefreshAllCommand.CanExecute(null));
        Assert.False(File.Exists(SettingsService.SettingsPath));

        var settings = provider.GetRequiredService<SettingsService>();
        var stored = settings.Load();
        stored.SiteHygiene = new SiteHygieneSettings { SelectedTab = 4 };
        settings.Save(stored);
        Assert.True(live.SaveLive(new SiteHygieneLiveSettings { RefreshMinutes = 30, AlertDpCritical = false }, "CM01.contoso.com", "CM_MCM"));
        var after = new SettingsService().Load();
        Assert.Equal((30, (bool?)false, (int?)null), (after.SiteHygieneLive!.RefreshMinutes, after.SiteHygieneLive.AlertDpCritical, after.SiteHygieneLive.InactiveDays));
        Assert.Equal(("CM01.contoso.com", "CM_MCM", 4), (after.DiscoverySqlServer, after.DiscoverySqlDatabase, after.SiteHygiene!.SelectedTab));
        Assert.Equal(30, SiteHygieneLivePreferences.ToLiveSettings(after).RefreshMinutes);
    }

    [Fact]
    public void ThePageGetsTheRealServiceStoresAndReportWriterInTheDataFolder()
    {
        using var provider = Provider();
        var services = provider.GetRequiredService<SiteHygienePageServices>();
        Assert.IsType<SiteHygieneService>(services.Hygiene);
        var suppressions = Assert.IsType<HygieneSuppressionStore>(services.Suppressions);
        var scans = Assert.IsType<HygieneScanStore>(services.ScanStore);
        var reports = Assert.IsType<HygieneReportWriter>(services.Reports);
        var folder = HygieneFolders.Root();
        Assert.StartsWith(_root, folder, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(folder, HygieneSuppressionStore.FileName), suppressions.Path);
        Assert.Equal(Path.Combine(folder, HygieneScanStore.FileName), scans.Path);
        Assert.Equal(HygieneReportWriter.DefaultFolder(), reports.Folder);
        Assert.Equal(reports.Folder, provider.GetRequiredService<SiteHygieneViewModel>().ReportFolder);
        Assert.False(Directory.Exists(folder));
    }
}
