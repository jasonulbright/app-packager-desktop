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

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>The Pivots and Scripts writer: it sends only the prepared change of a confirmed plan, and the run flags only as that plan asked them.</summary>
public sealed class PivotsScriptsWriterTests
{
    private static PivotsPlan Plan(PivotsScriptsHarness h, PivotsChangeKind kind, string collection = "LAB00012")
    {
        h.Service.InConfirmedRun = null;
        var targets = h.Service.ResolveTargetsAsync(new RunTarget(RunTargetKind.Collection, collection), CancellationToken.None).Result;
        var request = new PivotsChange(Kind: kind, Targets: kind is PivotsChangeKind.RunPivot or PivotsChangeKind.RunScript ? targets : null, QueryText: kind == PivotsChangeKind.RunPivot ? "OS" : null,
            ScriptGuid: kind == PivotsChangeKind.RunPivot ? null : h.DiskScript.Guid, ScriptHash: null, Parameters: null, SubmitName: null, SubmitText: null, Comment: null,
            RunConfirmed: false, EveryDeviceAcknowledged: false, Impact: [], Checks: [], Fingerprint: null);
        var prepared = h.Service.PrepareAsync(request, CancellationToken.None).Result;
        var (plan, problem) = PivotsScriptsText.BuildPlan(prepared);
        Assert.Null(problem);
        return plan!;
    }

    private static ChangePlanViewModel Confirm(PivotsPlan plan, ChangePlanRun run, bool destructive = true, bool extra = true, bool second = true)
    {
        var dialog = new ChangePlanViewModel(plan.Plan, run) { DestructiveAcknowledged = destructive, ExtraAcknowledged = extra, SecondAcknowledged = second };
        ScriptedPlanDialogs.Wait(dialog.ConfirmCommand.ExecuteAsync(null));
        return dialog;
    }

    [Fact]
    public void AScriptRunCarriesRunConfirmedOnlyWhenTheConfirmedPlanAskedBothBoxes()
    {
        var h = new PivotsScriptsHarness();
        var plan = Plan(h, PivotsChangeKind.RunScript);
        Confirm(plan, h.Writer().RunFor(plan));
        Assert.True(h.Service.Applied.Single().RunConfirmed);

        var h2 = new PivotsScriptsHarness();
        var real = Plan(h2, PivotsChangeKind.RunScript);
        var bare = new PivotsPlan(real.Action, real.Change with { RunConfirmed = true, EveryDeviceAcknowledged = true },
            new ChangePlan(real.Plan.Title, real.Plan.ActionName, real.Plan.Items), real.Entries);
        var dialog = Confirm(bare, h2.Writer().RunFor(bare));
        var sent = h2.Service.Applied.Single();
        Assert.False(sent.RunConfirmed);
        Assert.False(sent.EveryDeviceAcknowledged);
        Assert.All(dialog.Outcome.Result!.Items, r => Assert.Equal(FakePivotsService.NotConfirmedRefusal, r.Message));
        Assert.All(dialog.Outcome.Result!.Items, r => Assert.True(r.Skipped));
        Assert.Null(bare.Outcome!.OperationId);
    }

    [Fact]
    public void TheEveryDeviceFlagTravelsOnlyForABuiltInCollectionWhosePlanAskedIt()
    {
        var h = new PivotsScriptsHarness();
        var plan = Plan(h, PivotsChangeKind.RunPivot, "SMS00001");
        Assert.NotNull(plan.Plan.SecondConfirmation);
        Confirm(plan, h.Writer().RunFor(plan));
        Assert.True(h.Service.Applied.Single().EveryDeviceAcknowledged);

        var h2 = new PivotsScriptsHarness();
        var normal = Plan(h2, PivotsChangeKind.RunPivot);
        Assert.Null(normal.Plan.SecondConfirmation);
        Confirm(normal, h2.Writer().RunFor(normal));
        Assert.False(h2.Service.Applied.Single().EveryDeviceAcknowledged);
    }

    [Fact]
    public void AScriptActionNeverCarriesTheRunFlags()
    {
        var h = new PivotsScriptsHarness();
        h.Service.CurrentUser = "other";
        var plan = Plan(h, PivotsChangeKind.ApproveScript);
        var forged = new PivotsPlan(plan.Action, plan.Change with { RunConfirmed = true, EveryDeviceAcknowledged = true }, plan.Plan, plan.Entries);
        var dialog = Confirm(forged, h.Writer().RunFor(forged));
        var sent = h.Service.Applied.Single();
        Assert.False(sent.RunConfirmed);
        Assert.False(sent.EveryDeviceAcknowledged);
        Assert.Equal("Approved", dialog.Outcome.Result!.Items.Single().OutcomeText);
    }

    [Fact]
    public async Task TheWriterRefusesAPlanThatIsNotTheConfirmedOne()
    {
        var h = new PivotsScriptsHarness();
        var plan = Plan(h, PivotsChangeKind.RunPivot);
        var other = Plan(h, PivotsChangeKind.RunPivot);
        ConfirmedChangePlan? captured = null;
        Confirm(other, (confirmed, _, _) =>
        {
            captured = confirmed;
            return Task.FromResult(new ChangePlanResult([]));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Writer().ApplyAsync(captured!, plan, new Progress<ChangePlanItemResult>(), CancellationToken.None));
        Assert.Empty(h.Service.Applied);
    }

    [Fact]
    public void EachDeviceGetsItsOwnRowAndAProgressReportIsAStepOnly()
    {
        var h = new PivotsScriptsHarness();
        var plan = Plan(h, PivotsChangeKind.RunPivot);
        var dialog = Confirm(plan, h.Writer().RunFor(plan));
        var result = dialog.Outcome.Result!;
        Assert.Equal(6, result.Items.Count);
        Assert.All(result.Items, r => Assert.Equal("Sent", r.OutcomeText));
        Assert.Equal("6 sent.", result.Headline);
        Assert.DoesNotContain(result.Items, r => r.Interim);
        Assert.Equal(SiteOperationOutcome.Succeeded, h.History.Entries.Single().Outcome);
        Assert.NotNull(plan.Outcome!.OperationId);
    }

    [Fact]
    public void TheRowOfADeviceStateKeepsFailuresAndSkipsApart()
    {
        var item = new ChangePlanItem("Run query", "x", null);
        Assert.True(PivotsScriptsWriter.DeviceRow(item, new DeviceResult(1, "a", PivotsDeviceStates.Sent, "", "")).Succeeded);
        Assert.False(PivotsScriptsWriter.DeviceRow(item, new DeviceResult(1, "a", PivotsDeviceStates.NoResponse, "", "No answer.")).Succeeded);
        Assert.Equal("No response", PivotsScriptsWriter.DeviceRow(item, new DeviceResult(1, "a", PivotsDeviceStates.NoResponse, "", "")).OutcomeText);
        var skipped = PivotsScriptsWriter.DeviceRow(item, new DeviceResult(1, "a", PivotsDeviceStates.Skipped, "", "the run was cancelled"));
        Assert.True(skipped.Skipped);
        Assert.Equal("Skipped: the run was cancelled", skipped.Message);
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
    public void OnlyTheWriterCallsTheServiceApply()
    {
        Assert.Matches(@"\bservice\s*\.\s*ApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), P("Services", "PivotsScriptsPlan.cs")))));
        var pageFiles = UiFilesWith(new Regex(@"\bPivots", RegexOptions.CultureInvariant)).Where(f => !f.EndsWith("PivotsScriptsPlan.cs", StringComparison.Ordinal) && !f.EndsWith("PivotsScriptsServiceFallbacks.cs", StringComparison.Ordinal)).ToList();
        Assert.Contains(P("ViewModels", "Pages", "PivotsScriptsViewModel.Runs.cs"), pageFiles);
        foreach (var file in pageFiles)
            Assert.DoesNotMatch(@"\bApplyAsync\b", MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), file))));
        var calls = pageFiles.SelectMany(f => Regex.Matches(MaintenanceWindowWriterTests.Code(File.ReadAllText(Path.Combine(SourceRoot(), f))), @"\bServices\s*\.\s*Service\s*\.\s*(\w+)").Select(m => m.Groups[1].Value))
            .Distinct().Order().ToList();
        Assert.Equal(["CheckScriptAsync", "LoadScriptsAsync", "PrepareAsync", "ResolveTargetsAsync", "TestConnectionAsync", "WaitForResultsAsync"], calls);
    }

    [Fact]
    public void NoTypeOfTheDesktopButTheWriterAndThePageServicesHoldsTheService()
    {
        var allowed = new[] { typeof(PivotsScriptsWriter), typeof(PivotsScriptsPageServices), typeof(PivotsScriptsRegistration) };
        var all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var holders = typeof(PivotsScriptsWriter).Assembly.GetTypes()
            .Where(t => typeof(IPivotsScriptsService).IsAssignableFrom(t)
                || t.GetFields(all).Any(f => typeof(IPivotsScriptsService).IsAssignableFrom(f.FieldType))
                || t.GetProperties(all).Any(p => typeof(IPivotsScriptsService).IsAssignableFrom(p.PropertyType)))
            .Select(t => { while (t.DeclaringType is { } outer) t = outer; return t; })
            .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal))
            .Distinct().ToList();
        Assert.Contains(typeof(PivotsScriptsWriter), holders);
        Assert.All(holders, t => Assert.Contains(t, allowed));
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PivotsScriptsRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-pas-reg-" + Guid.NewGuid().ToString("N"));
    private readonly string? _saved = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public PivotsScriptsRegistrationTests() => Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);

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
        services.AddPivotsScriptsPage();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    [Fact]
    public void TheApplicationServicesBuildOnePageViewModelThatReadsNothing()
    {
        using var provider = Provider();
        var viewModel = provider.GetRequiredService<PivotsScriptsViewModel>();
        Assert.Same(viewModel, provider.GetRequiredService<PivotsScriptsViewModel>());
        var services = provider.GetRequiredService<PivotsScriptsPageServices>();
        Assert.Same(provider.GetRequiredService<PivotsScriptsWriter>(), services.Writer);
        Assert.NotNull(provider.GetRequiredService<IPivotsScriptsService>());
        Assert.NotNull(provider.GetRequiredService<IPivotsAuditLog>());
        Assert.NotNull(provider.GetRequiredService<IPivotsSessionStore>());
        Assert.NotNull(provider.GetRequiredService<IPivotsReportWriter>());
        Assert.NotNull(provider.GetRequiredService<IPivotsLibrary>());
        Assert.Equal(TimeSpan.FromMilliseconds(250), services.FilterDelay);
        Assert.Empty(viewModel.DeviceRows);
        Assert.False(File.Exists(SettingsService.SettingsPath));
    }

    [Fact]
    public void EveryPageTypeHasOneConstructorSoTheContainerCanBuildIt()
    {
        foreach (var type in new[] { typeof(PivotsScriptsViewModel), typeof(PivotsScriptsPage), typeof(PivotsScriptsWriter), typeof(PivotsDialogs) })
            Assert.Single(type.GetConstructors());
    }

    [Fact]
    public void TheSavedChoicesRoundTripThroughTheSettingsFileAndKeepOtherValues()
    {
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.SiteCode = "LAB";
        stored.DpContent = new DpContentSettings { SelectedTab = 2 };
        settings.Save(stored);
        Assert.Null(PivotsScriptsPreferences.Load(settings));
        Assert.True(PivotsScriptsPreferences.Save(settings, new PivotsScriptsSettings { SelectedTab = 3, WaitMinutes = 10, LibraryFolder = @"D:\Lib", TargetKind = "List" }));
        var loaded = PivotsScriptsPreferences.Load(new SettingsService())!;
        Assert.Equal((3, 10, @"D:\Lib", "List"), (loaded.SelectedTab, loaded.WaitMinutes, loaded.LibraryFolder, loaded.TargetKind));
        Assert.Equal("LAB", new SettingsService().Load().SiteCode);
        Assert.Equal(2, new SettingsService().Load().DpContent?.SelectedTab);
        Assert.DoesNotContain("Get-", File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public void ASettingsFileFromBeforeThePageLoadsWithoutThePageChoices()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, "{ \"FirstRunCompleted\": true, \"SiteCode\": \"LAB\" }");
        Assert.Null(new SettingsService().Load().PivotsScripts);
        File.WriteAllText(SettingsService.SettingsPath, "{ \"SiteCode\": \"LAB\", \"PivotsScripts\": { \"SelectedTab\": 1, \"LibraryFolder\": null, \"TargetKind\": null } }");
        var loaded = new SettingsService().Load().PivotsScripts!;
        Assert.Equal(1, loaded.SelectedTab);
        Assert.Null(loaded.LibraryFolder);
    }
}
