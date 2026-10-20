using ApplicationPackagerEngine.Services.Logging;
namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// One tab of the Deployment Helper page: nothing is read before its first visit, a read runs off
/// the UI thread and can be cancelled, and a read that ends after the operator left the page or
/// started a newer read is discarded.
/// </summary>
public abstract partial class DeploymentTabViewModel(DeploymentShared shared) : ObservableObject
{
    public const string ReadCancelled = "The read was cancelled.";

    private CancellationTokenSource? _cts;
    private Task? _loading;
    private int _loadGeneration;

    public DeploymentShared Shared { get; } = shared;

    protected DeploymentPageServices Services => Shared.Services;

    /// <summary>True once a read of this visit succeeded.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>The running read, or a completed task.</summary>
    public Task LoadTask => _loading ?? Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CancelLoadCommand))]
    private bool _isLoading;

    /// <summary>False for a tab that reads nothing from ConfigMgr.</summary>
    protected virtual bool NeedsSite => true;

    /// <summary>The status text when the read fails.</summary>
    protected abstract string ReadFailed { get; }

    /// <summary>Reads the data off the UI thread; the returned action shows it on the UI thread.</summary>
    protected abstract Task<Action> ReadAsync(CancellationToken ct);

    /// <summary>Drops the data of the tab.</summary>
    protected abstract void ReleaseData();

    public Task EnsureLoadedAsync() => IsLoaded ? Task.CompletedTask : LoadAsync();

    /// <summary>Reads the data; a call while a read runs waits for that read.</summary>
    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: false } pending) return pending;
        var task = RunLoadAsync();
        _loading = task.IsCompleted ? null : task;
        return task;
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => LoadAsync();

    private bool CanCancelLoad() => IsLoading;

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        CancelOtherReads();
    }

    /// <summary>Cancels the reads of the tab that run outside <see cref="LoadAsync"/>.</summary>
    protected virtual void CancelOtherReads() { }

    private async Task RunLoadAsync()
    {
        var generation = Shared.Generation;
        var mine = ++_loadGeneration;
        if (NeedsSite && Shared.ConnectionProblem() is not null)
        {
            IsLoaded = false;
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Shared.VisitToken);
        _cts = cts;
        IsLoading = true;
        try
        {
            var show = await Task.Run(() => ReadAsync(cts.Token), cts.Token);
            // A read that does not observe the token still ends as cancelled when the operator cancelled it.
            cts.Token.ThrowIfCancellationRequested();
            if (generation != Shared.Generation || mine != _loadGeneration) return;
            show();
            IsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            if (generation == Shared.Generation && mine == _loadGeneration) Shared.StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (generation != Shared.Generation || mine != _loadGeneration) return;
            Shared.StatusMessage = DeploymentShared.PlainMessage(ex, ReadFailed);
            Services.Log.Warning(DeploymentShared.LogArea, $"{GetType().Name.Replace("ViewModel", "", StringComparison.Ordinal)}: a read failed ({DeploymentShared.FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            if (mine == _loadGeneration) IsLoading = false;
        }
    }

    /// <summary>Waits for a read that started before the write, then reads again, when the tab has data.</summary>
    public async Task RefreshAfterWriteAsync()
    {
        var hadData = IsLoaded;
        while (_loading is { IsCompleted: false } pending)
        {
            hadData = true;
            await pending;
        }
        if (hadData && Shared.IsActive) await LoadAsync();
    }

    /// <summary>Drops the data and discards a read that still runs.</summary>
    public void Release()
    {
        _loadGeneration++;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts = null;
        _loading = null;
        IsLoading = false;
        IsLoaded = false;
        ReleaseData();
    }

    protected static bool Contains(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>Changes <paramref name="rows"/> with the fewest changes, so a grid keeps its selection and scroll position.</summary>
    protected static void Replace<T>(ObservableCollection<T> rows, IReadOnlyList<T> wanted)
    {
        var keep = new HashSet<T>(wanted);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(rows[i])) rows.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= rows.Count || !Equals(rows[i], wanted[i])) rows.Insert(i, wanted[i]);
        while (rows.Count > wanted.Count) rows.RemoveAt(rows.Count - 1);
    }
}
