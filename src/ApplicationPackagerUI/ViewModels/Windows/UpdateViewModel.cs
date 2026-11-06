using System.IO;
using System.Net.Http;
using ApplicationPackagerEngine.Services.Update;

namespace ApplicationPackagerUI.ViewModels.Windows;

public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Problem
}

/// <summary>
/// The update window. Check looks up the latest release; Download fetches and verifies the installer;
/// Install starts it only on its own click, and then closes the app so the installer can replace the
/// executable. Nothing here installs without that click.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private readonly Func<string, bool> _launch;
    private readonly Action _shutdown;
    private UpdateRelease? _release;
    private VerifiedUpdate? _verified;
    private CancellationTokenSource? _cts;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanCheck), nameof(CanDownload), nameof(CanInstall), nameof(Busy))] private UpdateStage _stage = UpdateStage.Idle;
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _releaseUrl = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;

    public string CurrentVersion { get; }

    public bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading;
    public bool CanCheck => !Busy;
    public bool CanDownload => Stage == UpdateStage.Available;
    public bool CanInstall => Stage == UpdateStage.Ready;

    /// <param name="launch">Starts the installer file; false when it did not start.</param>
    /// <param name="shutdown">Closes the app after the installer started.</param>
    public UpdateViewModel(UpdateService service, string currentVersion, Func<string, bool> launch, Action shutdown)
    {
        _service = service;
        CurrentVersion = currentVersion;
        _launch = launch;
        _shutdown = shutdown;
    }

    /// <summary>Shows a check that already ran, for example the startup check.</summary>
    public void Show(UpdateCheck check) => Apply(check);

    private void Apply(UpdateCheck check)
    {
        _release = check.Release;
        _verified = null;
        LatestVersion = check.Release?.Version ?? "";
        ReleaseUrl = check.Release?.PageUrl ?? "";
        Notes = check.Release?.Notes ?? "";
        StatusText = check.Status == UpdateStatus.RateLimited && check.RetryAfter is { } retry
            ? $"{check.Message} Try again after {retry.ToLocalTime():HH:mm}."
            : check.Message;
        Stage = check.Status switch
        {
            UpdateStatus.Available => UpdateStage.Available,
            UpdateStatus.UpToDate => UpdateStage.UpToDate,
            _ => UpdateStage.Problem
        };
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        Stage = UpdateStage.Checking;
        StatusText = "Checking GitHub for the latest release...";
        try
        {
            Apply(await _service.CheckAsync(CurrentVersion));
        }
        catch (Exception ex)
        {
            // The service reports expected problems as a status. Anything else must not leave the window busy.
            StatusText = $"The check failed: {ex.Message}";
            Stage = UpdateStage.Problem;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (_release is null) return;
        Stage = UpdateStage.Downloading;
        Progress = 0;
        StatusText = $"Downloading version {_release.Version}...";
        _cts = new CancellationTokenSource();
        try
        {
            _verified = await _service.DownloadAsync(_release,
                (read, total) => Progress = total > 0 ? Math.Min(100.0, read * 100.0 / total) : 0, _cts.Token);
            StatusText = $"Version {_verified.Version} is downloaded. The checksum matches, and {UpdateSignaturePolicy.SignerCommonName} signed the file with a valid, timestamped signature. " +
                         "Click Install and close to run the installer. The app closes first.";
            Stage = UpdateStage.Ready;
        }
        catch (UpdateRefusedException ex)
        {
            StatusText = $"The update was refused and deleted: {ex.Message}";
            Stage = UpdateStage.Problem;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            StatusText = "The download was cancelled.";
            Stage = UpdateStage.Available;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            StatusText = $"The download failed: {ex.Message}";
            Stage = UpdateStage.Problem;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install()
    {
        if (_verified is null) return;
        try
        {
            UpdateService.ConfirmUnchanged(_verified);
        }
        catch (UpdateRefusedException ex)
        {
            StatusText = ex.Message;
            Stage = UpdateStage.Problem;
            return;
        }
        if (!_launch(_verified.Path))
        {
            StatusText = "The installer did not start.";
            Stage = UpdateStage.Problem;
            return;
        }
        _shutdown();
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    partial void OnStageChanged(UpdateStage value)
    {
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }
}
