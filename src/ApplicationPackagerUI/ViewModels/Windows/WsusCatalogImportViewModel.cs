using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// Imports Microsoft Update Catalog updates into WSUS by update id. The server
/// downloads the metadata itself; each id is one import with its own result line,
/// off the UI thread, and Cancel stops before the next id.
/// </summary>
public partial class WsusCatalogImportViewModel : ObservableObject
{
    public const string CatalogUrl = "https://catalog.update.microsoft.com";

    public const string IntroText =
        "Paste update IDs or catalog links, one or more per line. The Copy button on a catalog update details page copies its update ID. The WSUS server downloads the update metadata itself, so it needs internet access; update files follow the server's Update files setting.";

    public const string FinishedText =
        "Import finished. Server-side details: %ProgramFiles%\\Update Services\\LogFiles\\SoftwareDistribution.log on the WSUS server.";

    private readonly IWsusAdminClient _client;
    private readonly WsusSettings _settings;
    private readonly IWsusPrompts? _prompts;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _resultsText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _busy;

    public bool Idle => !Busy;

    public WsusCatalogImportViewModel(IWsusAdminClient client, WsusSettings settings, IWsusPrompts? prompts = null)
    {
        _client = client;
        _settings = settings;
        _prompts = prompts;
    }

    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(Idle));
        ImportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task ImportAsync()
    {
        var ids = WsusIdentity.CatalogIds(InputText);
        if (ids.Count == 0) { StatusText = "No update IDs found in the text."; return; }
        StatusText = $"Importing {ids.Count} update(s) into {_settings.ServerName}...";
        Busy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            foreach (var id in ids)
            {
                if (token.IsCancellationRequested) { StatusText = "Import canceled before " + id + "."; return; }
                IReadOnlyList<WsusItemResult> outcomes;
                try
                {
                    outcomes = await Task.Run(() => _client.ImportCatalogUpdatesAsync(_settings, [id], null, token), token);
                }
                catch (OperationCanceledException)
                {
                    StatusText = "Import canceled during " + id + ".";
                    return;
                }
                foreach (var outcome in outcomes.DefaultIfEmpty(new WsusItemResult(id.ToString(), "", false, "The server returned no result.")))
                    ResultsText += $"{id}  {(outcome.Ok ? "imported" : "FAILED")}  {outcome.Message}".TrimEnd() + Environment.NewLine;
            }
            StatusText = FinishedText;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(Busy))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void LoadFile()
    {
        if (_prompts?.PickIdFile() is not { } path) return;
        try { InputText = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void OpenCatalog() => _prompts?.OpenUrl(CatalogUrl);
}
