using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.CmImport;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// Converts one ConfigMgr application to Intune apps: Preview shows the plan and its findings and
/// writes nothing; Convert copies the content and writes the files, and builds and publishes when asked.
/// The site is only read. Publishing uses the Intune values stored in Settings.
/// </summary>
public sealed partial class ConvertFromCmViewModel : ObservableObject
{
    public const string IntroText =
        "Enter the exact name of a ConfigMgr application. Each deployment type becomes one Intune Win32 app. " +
        "Preview reads the application from the site and shows the plan. Nothing is written until you click Convert.";

    private readonly ICmImportRunner _runner;
    private readonly Func<IntunePublishOptions?> _publishOptions;
    private CancellationTokenSource? _cts;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ConvertCommand))] private string _applicationName = "";
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private bool _assignByGroup;
    [ObservableProperty] private bool _nativeDetectionOnly;
    [ObservableProperty] private bool _allowUntranslated;
    [ObservableProperty] private bool _skipContent;
    [ObservableProperty] private bool _buildIntuneWin;
    [ObservableProperty] private bool _publish;
    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ConvertCommand), nameof(CancelCommand))] private bool _busy;

    public bool Idle => !Busy;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(Idle));

    /// <summary>Publishing needs the .intunewin files, so a Publish choice turns them on; clearing them clears Publish.</summary>
    partial void OnPublishChanged(bool value)
    {
        if (value) BuildIntuneWin = true;
        if (value) SkipContent = false;
    }

    partial void OnBuildIntuneWinChanged(bool value)
    {
        if (value) SkipContent = false;
        else Publish = false;
    }

    partial void OnSkipContentChanged(bool value)
    {
        if (!value) return;
        BuildIntuneWin = false;
        Publish = false;
    }

    public ConvertFromCmViewModel(ICmImportRunner runner, string defaultOutputFolder, Func<IntunePublishOptions?> publishOptions)
    {
        _runner = runner;
        _outputFolder = defaultOutputFolder;
        _publishOptions = publishOptions;
    }

    private CmImportOptions Options() => new(
        AssignByGroup ? CmImportPlanner.AssignByGroup : CmImportPlanner.PerDeploymentType,
        NativeDetectionOnly, AllowUntranslated);

    private CmImportRequest Request(IntunePublishOptions? publish) =>
        new(ApplicationName, OutputFolder, Options(), SkipContent, BuildIntuneWin, publish);

    private bool CanStart() => Idle && !string.IsNullOrWhiteSpace(ApplicationName);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task PreviewAsync()
    {
        await RunAsync(async (token, log) =>
        {
            var outcome = await _runner.PreviewAsync(Request(null), log, token);
            ReportText = outcome.Report;
            StatusText = $"Preview: {outcome.Plan.DeploymentTypes.Count(d => d.Convert)} of {outcome.Plan.DeploymentTypes.Count} deployment types convert. Nothing was written.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task ConvertAsync()
    {
        IntunePublishOptions? publish = null;
        if (Publish)
        {
            try { publish = _publishOptions(); }
            catch (Exception ex) { StatusText = $"The Intune settings cannot be read: {ex.Message}"; return; }
            if (publish is null)
            {
                StatusText = "Publishing needs the tenant ID, the client ID and the client secret. Enter them in Settings, Intune. Nothing was written.";
                return;
            }
        }
        await RunAsync(async (token, log) =>
        {
            var outcome = await _runner.ConvertAsync(Request(publish), log, token);
            ReportText = outcome.Report;
            var staged = outcome.Stage?.Staged.Count ?? 0;
            var published = outcome.Packages.Count(p => p.AppId.Length > 0);
            StatusText = $"Done: {staged} app(s) staged in {outcome.Stage?.ApplicationFolder}" +
                (outcome.Packages.Count > 0 ? $", {outcome.Packages.Count} package(s) built" : "") +
                (publish is not null ? $", {published} published." : ".");
        });
    }

    [RelayCommand(CanExecute = nameof(Busy))]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Action<string>, Task> work)
    {
        Busy = true;
        LogText = "";
        ReportText = "";
        StatusText = "Working...";
        _cts = new CancellationTokenSource();
        var context = SynchronizationContext.Current;
        void Log(string line)
        {
            if (context is null) LogText += line + Environment.NewLine;
            else context.Post(_ => LogText += line + Environment.NewLine, null);
        }
        try
        {
            await work(_cts.Token, Log);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            // The last stop before the screen: any failure of the site, the disk or Graph becomes a status line.
            StatusText = $"Failed: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Busy = false;
        }
    }
}
