using ApplicationPackagerEngine.Services.Supersedence;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One row of the list: an application of the site and the reason it can or cannot be linked.</summary>
public sealed class OldApplicationRow(OldApplicationCandidate candidate)
{
    public OldApplicationCandidate Candidate { get; } = candidate;
    public string Title => Candidate.Title;
    public string Version => Candidate.SoftwareVersion;

    public string StateText => Candidate.State switch
    {
        OldApplicationState.Linkable => "Can be linked",
        OldApplicationState.Linked => "Linked",
        OldApplicationState.OtherLine => "Other line",
        OldApplicationState.OwnKeywords => "Has keywords",
        OldApplicationState.KeywordsUnreadable => "Keywords unreadable",
        OldApplicationState.Retired => "Retired",
        OldApplicationState.VersionUnreadable => "Version unreadable",
        OldApplicationState.SameTitle => "Same title",
        _ => "Not older"
    };

    /// <summary>The reason an application is not offered; empty for one that can be linked.</summary>
    public string Reason => Candidate.Reason;
}

/// <summary>
/// The Old Application window: the administrator searches the site by name, reads title and version, and links one application as the
/// old application of the line, or takes the link off. Nothing is written before the confirmation, which shows title and version first.
/// </summary>
public partial class OldApplicationViewModel : ObservableObject
{
    private readonly OldApplicationService _service;
    private readonly Func<string, string, Task<bool>> _confirm;
    private CancellationTokenSource? _cts;

    public string NewTitle { get; }
    public string NewVersion { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private OldApplicationRow? _selected;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<OldApplicationRow> Rows { get; } = [];

    public OldApplicationViewModel(OldApplicationService service, string newTitle, string newVersion, Func<string, string, Task<bool>> confirm)
    {
        _service = service;
        NewTitle = newTitle;
        NewVersion = newVersion;
        _confirm = confirm;
        StatusText = "Type part of the name of the older application and search the site.";
    }

    public string Intro => $"Old application of '{NewTitle}' version {NewVersion}. Only applications that are older than this version and have no keywords of their own can be linked.";

    private bool CanSearch => !IsBusy && !string.IsNullOrWhiteSpace(SearchText);
    private bool CanLink => !IsBusy && Selected?.Candidate.CanLink == true;
    private bool CanClear => !IsBusy && Selected?.Candidate.CanClear == true;

    partial void OnSearchTextChanged(string value) => SearchCommand.NotifyCanExecuteChanged();

    partial void OnSelectedChanged(OldApplicationRow? value)
    {
        LinkCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        SearchCommand.NotifyCanExecuteChanged();
        LinkCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => RunSearchAsync(null);

    private async Task RunSearchAsync(string? keepMessage)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = "Reading the site...";
        try
        {
            var found = await _service.SearchAsync(NewTitle, NewVersion, SearchText, _cts.Token);
            Rows.Clear();
            foreach (var candidate in found) Rows.Add(new OldApplicationRow(candidate));
            var linkable = found.Count(c => c.CanLink);
            var summary = found.Count == 0 ? "No application of the site has that text in its name."
                : $"{found.Count} application(s) found, {linkable} can be linked.";
            StatusText = keepMessage is null ? summary : keepMessage + " " + summary;
        }
        catch (OperationCanceledException) { StatusText = "The search was canceled."; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLink))]
    private async Task LinkAsync()
    {
        if (Selected is not { } row) return;
        if (!await _confirm("Use as Old Application", OldApplicationRules.LinkConfirmationText(row.Title, row.Version, NewTitle)))
        {
            StatusText = "Not confirmed. Nothing was changed.";
            return;
        }
        var outcome = await Write(ct => _service.LinkAsync(row.Title, confirmed: true, ct));
        if (outcome is null) return;
        if (outcome.Ok) await RunSearchAsync(outcome.Message);
        else StatusText = outcome.Message;
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAsync()
    {
        if (Selected is not { } row) return;
        if (!await _confirm("Remove Link", OldApplicationRules.ClearConfirmationText(row.Title, row.Version)))
        {
            StatusText = "Not confirmed. Nothing was changed.";
            return;
        }
        var outcome = await Write(ct => _service.ClearAsync(row.Title, confirmed: true, ct));
        if (outcome is null) return;
        if (outcome.Ok) await RunSearchAsync(outcome.Message);
        else StatusText = outcome.Message;
    }

    private async Task<OldApplicationOutcome?> Write(Func<CancellationToken, Task<OldApplicationOutcome>> action)
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = "Writing to the site...";
        try { return await action(_cts.Token); }
        catch (OperationCanceledException) { StatusText = "The write was canceled."; return null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }
}
