using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// The WSUS Updates window: the locally published updates on the server, and
/// approval, decline, expiry and removal of the selection with a result per id.
/// Every server call runs off the UI thread.
/// </summary>
public partial class WsusUpdatesViewModel : ObservableObject
{
    private readonly IWsusAdminClient _client;
    private readonly WsusSettings _settings;
    private readonly IWsusPrompts? _prompts;
    private IReadOnlyList<WsusPublishedUpdate> _selection = [];
    private CancellationTokenSource? _cts;

    public ObservableCollection<WsusPublishedUpdate> Updates { get; } = [];
    public ObservableCollection<string> Groups { get; } = [];

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _showOtherPublishers;
    [ObservableProperty] private string _groupText = "";
    [ObservableProperty] private bool _busy;

    public WsusSettings Settings => _settings;
    public bool Idle => !Busy;

    /// <summary>Raised by Catalog Import; the window opens the import dialog for the same server.</summary>
    public event EventHandler? CatalogImportRequested;

    public WsusUpdatesViewModel(IWsusAdminClient client, WsusSettings settings, IWsusPrompts? prompts = null)
    {
        _client = client;
        _settings = settings;
        _prompts = prompts;
        _groupText = settings.ApprovalGroup ?? "";
    }

    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(Idle));
        RefreshCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        DeclineCommand.NotifyCanExecuteChanged();
        ExpireCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowOtherPublishersChanged(bool value) => _ = LoadAsync();

    /// <summary>The rows the grid has selected.</summary>
    public void SetSelection(IEnumerable<WsusPublishedUpdate> selected) => _selection = selected.ToList();

    /// <summary>Lists the updates and, the first time, the computer groups.</summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    public Task LoadAsync()
    {
        // The "show other publishers" box calls this directly. A read that starts while a read or a write works
        // would clear Busy when it ends and unlock the commands under that work. It waits for that work instead,
        // which reads again once it has ended.
        if (Busy)
        {
            _reloadRequested = true;
            return _work ?? Task.CompletedTask;
        }
        return _work = LoadCoreAsync();
    }

    private Task? _work;

    private async Task LoadCoreAsync()
    {
        _reloadRequested = false;
        Busy = true;
        try
        {
            var rows = await Task.Run(() => _client.ListAsync(_settings, ShowOtherPublishers));
            Updates.Clear();
            foreach (var row in rows) Updates.Add(row);
            SummaryText = $"{rows.Count} update(s) on {_settings.ServerName}.";
            if (Groups.Count == 0)
            {
                try
                {
                    foreach (var name in await Task.Run(() => _client.GetComputerGroupNamesAsync(_settings))) Groups.Add(name);
                }
                catch (Exception ex) when (ex is WsusOperationException or InvalidOperationException or IOException)
                {
                    SummaryText += " Computer groups could not be listed: " + ex.Message;
                }
            }
        }
        catch (Exception ex) when (ex is WsusOperationException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Updates.Clear();
            SummaryText = "Listing failed: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
        if (_reloadRequested) await LoadAsync();
    }

    private bool _reloadRequested;

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task ApproveAsync() => ApplyAsync(WsusUpdateActions.Approve);

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task DeclineAsync() => ApplyAsync(WsusUpdateActions.Decline);

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task ExpireAsync() => ApplyAsync(WsusUpdateActions.Expire);

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task RemoveAsync() => ApplyAsync(WsusUpdateActions.Remove);

    [RelayCommand]
    private void CatalogImport() => CatalogImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The confirmation text of Expire or Remove; empty for the reversible actions.</summary>
    public static string ConfirmationText(string action, int count, string server) => action switch
    {
        WsusUpdateActions.Expire =>
            $"Expire {count} update(s) on {server}?\n\nExpiry is permanent: clients stop seeing the update and it cannot be approved again. Declining is the reversible alternative.",
        WsusUpdateActions.Remove =>
            $"Remove {count} update(s) from {server}?\n\nRemoval declines each update and deletes it from the WSUS database. It cannot be undone. Computers that installed an update keep the software.\n\nWith ConfigMgr, expire the update first and let the software update point synchronize, then remove it. WSUS refuses to remove an update that another update still references.",
        _ => ""
    };

    /// <summary>"Approve: 1 of 2 update(s) done. Title: message" from the per-id results.</summary>
    public static string ResultText(string action, int selected, IReadOnlyList<WsusItemResult> outcomes)
    {
        var failed = outcomes.Where(o => !o.Ok).Select(o => o.Title.Length > 0 ? $"{o.Title}: {o.Message}" : o.Message).ToList();
        var done = outcomes.Count(o => o.Ok);
        return $"{action}: {done} of {selected} update(s) done.{(failed.Count > 0 ? " " + string.Join(" | ", failed) : "")}";
    }

    private Task ApplyAsync(string action) => _work = ApplyCoreAsync(action);

    private async Task ApplyCoreAsync(string action)
    {
        var selected = _selection;
        if (selected.Count == 0) { SummaryText = "Select one or more updates first."; return; }
        var group = (GroupText ?? "").Trim();
        if (action == WsusUpdateActions.Approve && group.Length == 0) { SummaryText = "Enter or choose a computer group to approve for."; return; }
        if (action is WsusUpdateActions.Expire or WsusUpdateActions.Remove)
        {
            var title = action == WsusUpdateActions.Expire ? "Expire Updates" : "Remove Updates";
            if (_prompts is null || !await _prompts.ConfirmAsync(title, ConfirmationText(action, selected.Count, _settings.ServerName))) return;
        }

        var ids = selected.Select(u => Guid.TryParse(u.PackageId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
        IReadOnlyList<WsusItemResult> outcomes;
        Busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var token = _cts.Token;
            outcomes = await Task.Run(() => _client.SetStateAsync(_settings, ids, action, group, null, token), token);
        }
        catch (Exception ex) when (ex is WsusOperationException or InvalidOperationException or ArgumentException or IOException or OperationCanceledException)
        {
            outcomes = [new WsusItemResult("", "", false, ex.Message)];
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Busy = false;
        }
        await LoadAsync();
        SummaryText = ResultText(action, selected.Count, outcomes);
    }
}
