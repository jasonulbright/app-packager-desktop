using System.Text.Json;
using System.Windows.Threading;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AddFromWebViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-add-web-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public AddFromWebViewModelTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Harness
    {
        public FakeAddService Service { get; } = new();
        public FakeResultActions Actions { get; } = new();
        public FakeSnackbar Snackbar { get; } = new();
        public AddAppViewModel ViewModel { get; }
        public string DefinitionsRoot { get; }

        public Harness(string root, string? downloadRoot)
        {
            DefinitionsRoot = Path.Combine(root, "definitions");
            var settings = new SettingsService();
            settings.ApplyRunOverrides(s => s.DownloadRoot = downloadRoot ?? "");
            ViewModel = new AddAppViewModel(settingsService: settings, snackbarService: Snackbar, service: Service, results: new AddResultViewModel(Actions))
            {
                DefinitionsRoot = DefinitionsRoot,
                Scrape = (_, _) => Task.FromResult<(string?, string?)>(("1.2.3.0", "1.2.3"))
            };
            ViewModel.Vendor = "Contoso";
            ViewModel.ApplicationName = "Contoso App";
            ViewModel.ScraperUrl = "https://example.com/releases";
            ViewModel.ScraperPattern = @"(\d+\.\d+\.\d+)";
            ViewModel.DownloadUrl = "https://example.com/contoso-{display_version}.msi";
        }

        public void Scrape()
        {
            var run = ViewModel.TestScrapeCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => run.IsCompleted);
        }

        public void GoTo(int step)
        {
            while (ViewModel.CurrentStep < step) ViewModel.NextStepCommand.Execute(null);
        }

        public void Analyze(AddAnalysisResult result, int index = 0)
        {
            ViewModel.AnalyzeCommand.Execute(null);
            Service.WaitForAnalysis(index).Result.SetResult(result);
            StaTestHelper.PumpUntil(() => ViewModel.HasAnalysis && !ViewModel.IsBusy);
        }
    }

    private void OnSta(Action<Harness> test, bool withDownloadRoot = true) => StaTestHelper.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        test(new Harness(_root, withDownloadRoot ? Path.Combine(_root, "downloads") : null));
    });

    [Fact]
    public void TheWizardHasTheAnalysisAndDetectionSteps() => OnSta(h =>
    {
        var titles = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            titles.Add(h.ViewModel.StepTitle);
            h.ViewModel.NextStepCommand.Execute(null);
        }
        Assert.Equal(["Step 1 of 7: Basic information", "Step 2 of 7: Version source", "Step 3 of 7: Download configuration",
            "Step 4 of 7: Installer analysis", "Step 5 of 7: Install and uninstall", "Step 6 of 7: Detection", "Step 7 of 7: Review and save"], titles);
        Assert.True(h.ViewModel.CanSave);
        Assert.Equal(["HttpRegex", "JsonQuery", "FileVersion"], h.ViewModel.ScraperTypeOptions);
    });

    [Fact]
    public void TheAnalysisFetchesTheInstallerOnceAndAnalyzesIt() => OnSta(h =>
    {
        h.Scrape();
        h.ViewModel.InstallerFileName = "contoso-{version}.msi";
        h.Analyze(FakeAddService.Result(path: Path.Combine(_root, "downloads", "setup.msi")));

        var request = Assert.Single(h.Service.Fetches);
        Assert.Equal("https://example.com/contoso-1.2.3.msi", request.Url);
        Assert.Equal("contoso-1.2.3.0.msi", request.FileName);
        Assert.Equal(Path.Combine(_root, "downloads"), request.DownloadRoot);
        Assert.Equal(Path.Combine(_root, "downloads", "setup.msi"), Assert.Single(h.Service.Analyses).Path);
        Assert.Equal(["Installer type", "Framework", "Install arguments", "Uninstall arguments", "Install context"], h.ViewModel.AnalysisRows.Select(r => r.Label));
        Assert.Equal("Predicted, not verified", h.ViewModel.AnalysisRows.Single(r => r.Label == "Uninstall arguments").MarkerText);
    });

    [Fact]
    public void TheAnalysisNeedsTheScrapedVersionWhenTheUrlHasAToken() => OnSta(h =>
    {
        h.ViewModel.AnalyzeCommand.Execute(null);
        StaTestHelper.Flush();
        Assert.Empty(h.Service.Fetches);
        Assert.Contains("Test Scrape", h.ViewModel.AnalysisStatus);
    });

    [Fact]
    public void TheAnalysisNeedsTheDownloadRoot() => OnSta(h =>
    {
        h.Scrape();
        h.ViewModel.AnalyzeCommand.Execute(null);
        StaTestHelper.Flush();
        Assert.Empty(h.Service.Fetches);
        Assert.Contains("download root", h.ViewModel.AnalysisStatus);
    }, withDownloadRoot: false);

    [Fact]
    public void ProgressAndCancelWorkOnTheAnalysis() => OnSta(h =>
    {
        h.Scrape();
        h.ViewModel.AnalyzeCommand.Execute(null);
        var pending = h.Service.WaitForAnalysis(0);
        pending.Progress.Report("Reading the MSI database");
        StaTestHelper.PumpUntil(() => h.ViewModel.AnalysisProgress == "Reading the MSI database");

        h.ViewModel.CancelAnalysisCommand.Execute(null);
        StaTestHelper.PumpUntil(() => !h.ViewModel.IsBusy);
        Assert.True(pending.Token.IsCancellationRequested);
        Assert.False(h.ViewModel.HasAnalysis);
        Assert.Equal("The analysis was cancelled.", h.ViewModel.AnalysisStatus);
    });

    [Fact]
    public void ALateAnalysisResultIsDiscarded() => OnSta(h =>
    {
        h.Scrape();
        h.ViewModel.AnalyzeCommand.Execute(null);
        var first = h.Service.WaitForAnalysis(0);
        h.ViewModel.AnalyzeCommand.Execute(null);
        var second = h.Service.WaitForAnalysis(1);

        first.Result.TrySetResult(FakeAddService.Result(path: "first"));
        StaTestHelper.Flush();
        Assert.False(h.ViewModel.HasAnalysis);

        second.Result.SetResult(FakeAddService.Result(path: "second"));
        StaTestHelper.PumpUntil(() => h.ViewModel.HasAnalysis);
        Assert.Equal("second", h.ViewModel.AnalyzedFile);
    });

    [Fact]
    public void AcceptCopiesOnlyTheAcceptedProposalsIntoTheFields() => OnSta(h =>
    {
        h.Scrape();
        h.Analyze(FakeAddService.Result());
        h.ViewModel.AnalysisRows.Single(r => r.Label == "Uninstall arguments").IsAccepted = false;
        h.ViewModel.InstallerType = "EXE";

        h.ViewModel.AcceptProposalsCommand.Execute(null);

        Assert.Equal("MSI", h.ViewModel.InstallerType);
        Assert.Equal("NativeMSI", h.ViewModel.InstallerFramework);
        Assert.Equal("/qn /norestart", h.ViewModel.InstallArgs);
        Assert.Equal("", h.ViewModel.UninstallArgs);
        Assert.Equal("Machine", h.ViewModel.InstallContext);
        Assert.Equal("Confirmed", h.ViewModel.InstallArgsMarker);
        Assert.Equal(DetectionKind.MsiProductCode, h.ViewModel.Detection.Kind);

        h.ViewModel.InstallArgs = "/qn";
        Assert.Equal("Manual", h.ViewModel.InstallArgsMarker);
    });

    [Fact]
    public void TheReviewWarnsWhileDetectionIsTheDefault() => OnSta(h =>
    {
        h.GoTo(7);
        Assert.True(h.ViewModel.ShowDetectionWarning);
        Assert.Contains("default", h.ViewModel.DetectionWarning, StringComparison.OrdinalIgnoreCase);

        h.ViewModel.Detection.Kind = DetectionKind.RegistryVersion;
        h.ViewModel.Detection.Key = @"SOFTWARE\Contoso";
        Assert.False(h.ViewModel.ShowDetectionWarning);
    });

    private static AddAnalysisResult Analyzed(string type, string context = "User") => FakeAddService.Result() with
    {
        InstallerType = type,
        InstallContext = new ProposedValue<string>(Value: context, Evidence: Evidence.Confirmed, Source: "analysis"),
        Architecture = new ProposedValue<string>(Value: "ARM64", Evidence: Evidence.Confirmed, Source: "pe-header")
    };

    private static (AddAnalysisResult Reviewed, AddSaveOptions Options) SaveOnce(Harness h)
    {
        h.GoTo(7);
        var save = h.ViewModel.SaveDefinitionCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);
        return Assert.Single(h.Service.Saves);
    }

    [Fact]
    public void ValuesNobodyAcceptedOrChanged_AreNotSentSoTheAnalysisValuesStay() => OnSta(h =>
    {
        h.Scrape();
        h.Analyze(Analyzed("MSP"));
        foreach (var row in h.ViewModel.AnalysisRows) row.IsAccepted = false;
        h.ViewModel.AnalysisRows.Single(r => r.Label == "Framework").IsAccepted = true;
        h.ViewModel.AcceptProposalsCommand.Execute(null);

        var (_, options) = SaveOnce(h);

        Assert.Null(options.InstallerType);
        Assert.Null(options.Architecture);
        Assert.Null(options.InstallContext);
        Assert.Null(options.InstallArgs);
        Assert.Null(options.UninstallArgs);
    });

    [Theory]
    [InlineData("MSI")]
    [InlineData("EXE")]
    [InlineData("ZIP")]
    [InlineData("MSIX")]
    [InlineData("MSP")]
    [InlineData("ODT")]
    public void AnAcceptedInstallerTypeIsSent_ForEveryTypeTheAnalysisCanReturn(string type) => OnSta(h =>
    {
        h.Scrape();
        h.Analyze(Analyzed(type));
        Assert.Equal(type, h.ViewModel.AnalysisRows.Single(r => r.Label == "Installer type").Value);
        Assert.True(h.ViewModel.AnalysisRows.Single(r => r.Label == "Installer type").IsChoice);
        h.ViewModel.AcceptProposalsCommand.Execute(null);

        var (_, options) = SaveOnce(h);

        Assert.Equal(type, options.InstallerType);
    });

    [Fact]
    public void ChangedValues_AreSent() => OnSta(h =>
    {
        h.Scrape();
        h.Analyze(Analyzed("EXE", context: "Machine"));
        foreach (var row in h.ViewModel.AnalysisRows) row.IsAccepted = false;
        h.ViewModel.AcceptProposalsCommand.Execute(null);
        h.ViewModel.Architecture = "x86";
        h.ViewModel.InstallerType = "ZIP";
        h.ViewModel.InstallContext = "User";
        h.ViewModel.InstallArgs = "/S";
        h.ViewModel.UninstallArgs = "/S /remove";

        var (_, options) = SaveOnce(h);

        Assert.Equal(("x86", "ZIP", "User", "/S", "/S /remove"),
            (options.Architecture, options.InstallerType, options.InstallContext, options.InstallArgs, options.UninstallArgs));
    });

    private static AppDefinition ReadDefinition(string path) => JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(path))!;

    [Fact]
    public void SaveWritesTheDetectionAndEndsOnTheResultPanel() => OnSta(h =>
    {
        h.Scrape();
        h.ViewModel.InstallerFileName = "contoso-{version}.msi";
        h.Analyze(FakeAddService.Result());
        h.ViewModel.AcceptProposalsCommand.Execute(null);
        h.GoTo(7);

        var save = h.ViewModel.SaveDefinitionCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        Assert.True(h.ViewModel.IsResultVisible);
        Assert.True(h.ViewModel.Result.IsDoneDefault);
        var (reviewed, options) = Assert.Single(h.Service.Saves);
        Assert.Equal(DetectionKind.MsiProductCode, reviewed.Detection.Kind);
        Assert.False(reviewed.Detection.IsDefault);
        Assert.Equal(("/qn /norestart", "/x {ProductCode} /qn", "Machine"), (options.InstallArgs, options.UninstallArgs, options.InstallContext));
        Assert.Equal(("Contoso App", "Contoso", null), (options.ApplicationName, options.Vendor, options.Architecture));
        Assert.Equal(("MSI", "NativeMSI", "contoso-{version}.msi"), (options.InstallerType, options.InstallerFramework, options.InstallerFileName));
        Assert.Equal("https://example.com/contoso-{display_version}.msi", options.DownloadUrl);
        Assert.Equal("https://example.com/releases", options.VersionScraper!.Url);
        Assert.Equal(Path.Combine(_root, "definitions"), options.DefinitionsFolder);
        Assert.Equal(@"C:\defs\contoso.json", h.ViewModel.Result.DefinitionPath);
        Assert.Equal(["contoso0001"], h.Actions.Selected);
        Assert.Empty(h.Actions.Opened);
        Assert.Empty(h.Actions.Staged);
    });

    [Fact]
    public void AnEmptyRegistryKeyIsNeverSavedAndTheResultAsksForReview() => OnSta(h =>
    {
        h.ViewModel.Detection.Kind = DetectionKind.RegistryVersion;
        h.ViewModel.Detection.Key = "";
        h.GoTo(7);

        var save = h.ViewModel.SaveDefinitionCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        var definition = ReadDefinition(h.ViewModel.Result.DefinitionPath);
        Assert.Null(definition.Detection.RegistryKeyTemplate);
        Assert.True(h.ViewModel.Result.IsWorkbenchDefault);
        Assert.Equal("Detection needs review.", h.ViewModel.Result.ReasonText);
    });

    [Fact]
    public void SaveAndOpenInWorkbenchOpensTheNewDefinition() => OnSta(h =>
    {
        h.GoTo(7);
        var save = h.ViewModel.SaveAndOpenWorkbenchCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        var id = ReadDefinition(h.ViewModel.Result.DefinitionPath).Id;
        Assert.Equal([id], h.Actions.Opened);
        Assert.True(h.ViewModel.IsResultVisible);
        Assert.Empty(h.Actions.Staged);
    });

    [Fact]
    public void DoneStartsANewWizard() => OnSta(h =>
    {
        h.GoTo(7);
        var save = h.ViewModel.SaveDefinitionCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        h.ViewModel.Result.DoneCommand.Execute(null);
        Assert.False(h.ViewModel.IsResultVisible);
        Assert.Equal(1, h.ViewModel.CurrentStep);
        Assert.Equal("", h.ViewModel.ApplicationName);
    });
}

internal sealed class FakeSnackbar : Wpf.Ui.ISnackbarService
{
    public List<(string Title, string Message)> Shown { get; } = [];
    public TimeSpan DefaultTimeOut { get; set; } = TimeSpan.FromSeconds(5);
    public void SetSnackbarPresenter(Wpf.Ui.Controls.SnackbarPresenter contentPresenter) { }
    public Wpf.Ui.Controls.SnackbarPresenter? GetSnackbarPresenter() => null;
    public void Show(string title, string message, Wpf.Ui.Controls.ControlAppearance appearance, Wpf.Ui.Controls.IconElement? icon, TimeSpan timeout) =>
        Shown.Add((title, message));
}
