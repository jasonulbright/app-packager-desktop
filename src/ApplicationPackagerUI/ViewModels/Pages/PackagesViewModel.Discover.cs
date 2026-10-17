using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class PackagesViewModel
{
    private DiscoveryRunner? _discoveryRunner;
    private IDiscoveryPrompt? _discoveryPrompt;
    private CancellationTokenSource? _discoveryCts;
    private IAsyncRelayCommand? _discoverCommand;

    [ObservableProperty] private bool _isDiscoverAvailable;
    [ObservableProperty] private string _discoverToolTip = "";

    public IAsyncRelayCommand DiscoverCommand =>
        _discoverCommand ??= new AsyncRelayCommand(DiscoverAsync, () => CanRunOperation() && IsDiscoverAvailable && _discoveryRunner is not null);

    /// <summary>Raised after Discover changed the hidden, enabled or dismissed lists in the stored settings.</summary>
    public event EventHandler? HiddenApplicationsChanged;

    private void InitializeDiscovery(ApplicationPackagerEngine.Services.IDiscoveryService discovery, IDiscoveryPrompt prompt)
    {
        _discoveryPrompt = prompt;
        _discoveryRunner = new DiscoveryRunner(
            discovery,
            _settingsService.Load,
            _settingsService.Save,
            DiscoveryPackagerSource.LoadAsync,
            DiscoveryRunner.ListCmSiteDrivesAsync);
    }

    private void RefreshDiscoverState(AppSettings settings)
    {
        (IsDiscoverAvailable, DiscoverToolTip) = DiscoveryRunner.Availability(settings);
        DiscoverCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Stops a running discovery; the main window calls it while closing.</summary>
    public void StopDiscovery()
    {
        try { _discoveryCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task DiscoverAsync()
    {
        if (_discoveryRunner is null || _discoveryPrompt is null) return;
        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        _discoveryCts = _operationCts;
        try
        {
            await _discoveryRunner.RunAsync(
                new ScanEndPrompt(this, _discoveryPrompt),
                AddLogLine,
                text => StatusText = text,
                () => LoadPackagersCommand.ExecuteAsync(null),
                () => HiddenApplicationsChanged?.Invoke(this, EventArgs.Empty),
                _operationCts.Token);
        }
        finally
        {
            EndDiscoveryOperation();
        }
    }

    private void EndDiscoveryOperation()
    {
        if (_discoveryCts is null) return;
        _discoveryCts = null;
        IsOperationRunning = false;
        _operationCts?.Dispose();
        _operationCts = null;
    }

    /// <summary>
    /// Ends the operation before the prompt opens, so the Cancel button no longer shows while
    /// the prompt waits and the grid reload that follows a choice is not refused as busy.
    /// </summary>
    private sealed class ScanEndPrompt(PackagesViewModel owner, IDiscoveryPrompt inner) : IDiscoveryPrompt
    {
        public ViewModels.Windows.DiscoveryChoice Show(ViewModels.Windows.DiscoveryDialogViewModel viewModel)
        {
            owner.EndDiscoveryOperation();
            return inner.Show(viewModel);
        }
    }
}
