using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class SiteHygieneViewModel
{
    public const string RelationsNotRead = "The relationships were not read. Select Read relationships.";
    public const string RelationsCancelled = "The read of the relationships was cancelled.";
    public const string RelationsFailed = "The relationships could not be read. The log has the details.";

    private CancellationTokenSource? _relationsCts;
    private Task? _relationsLoading;
    private int _relationsGeneration;
    private RelationInventory? _relations;
    private IReadOnlyList<RelationEdgeRow> _allEdges = [];
    private string? _edgeKey;

    public IReadOnlyList<string> EdgeKinds { get; } = [AllText, SiteHygieneLabels.KindText(SiteHygieneLabels.Supersedes), SiteHygieneLabels.KindText(SiteHygieneLabels.DependsOn)];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadRelationsCommand), nameof(CancelRelationsCommand))]
    private bool _isLoadingRelations;

    [ObservableProperty] private string _relationsSummary = RelationsNotRead;
    [ObservableProperty] private string _edgeText = "";
    [ObservableProperty] private string _edgeKind = AllText;
    [ObservableProperty] private IReadOnlyList<RelationEdgeRow> _edgeRows = [];
    [ObservableProperty] private string _edgeCount = "";
    [ObservableProperty] private RelationEdgeRow? _selectedEdge;
    [ObservableProperty] private IReadOnlyList<RelationTreeNode> _treeRoots = [];

    public bool IsRelationsLoaded => _relations is not null;

    public RelationInventory? Relations => _relations;

    public Task RelationsTask => _relationsLoading ?? Task.CompletedTask;

    partial void OnEdgeTextChanged(string value) => Debounce(EdgesGrid, ApplyEdgeFilter);
    partial void OnEdgeKindChanged(string value) => Filtered(ApplyEdgeFilter);

    partial void OnSelectedEdgeChanged(RelationEdgeRow? value)
    {
        if (!_restoring) _edgeKey = value?.Key;
    }

    private bool CanLoadRelations() => !IsLoadingRelations;

    [RelayCommand(CanExecute = nameof(CanLoadRelations))]
    private Task LoadRelations() => LoadRelationsAsync();

    private bool CanCancelRelations() => IsLoadingRelations;

    [RelayCommand(CanExecute = nameof(CanCancelRelations))]
    private void CancelRelations()
    {
        _relationsGeneration++;
        try { _relationsCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Reads every supersedence and dependency edge; a call while a read runs waits for that read.</summary>
    public Task LoadRelationsAsync()
    {
        if (_relationsLoading is { IsCompleted: false } pending) return pending;
        var task = RunRelationsAsync();
        _relationsLoading = task.IsCompleted ? null : task;
        return task;
    }

    private async Task RunRelationsAsync()
    {
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        var mine = ++_relationsGeneration;
        var site = SiteKey();
        using var cts = new CancellationTokenSource();
        _relationsCts = cts;
        IsLoadingRelations = true;
        StatusMessage = "Reading the supersedence and dependency relationships.";
        try
        {
            var inventory = await Task.Run(() => Services.Hygiene.LoadRelationsAsync(cts.Token), cts.Token);
            if (cts.IsCancellationRequested || mine != _relationsGeneration)
            {
                StatusMessage = RelationsCancelled;
                return;
            }
            if (!string.Equals(SiteKey(), site, StringComparison.Ordinal))
            {
                StatusMessage = SiteChanged;
                return;
            }
            var tree = await Task.Run(() => SiteHygieneTreeText.Build(inventory.Edges));
            if (mine != _relationsGeneration) return;
            ShowRelations(inventory, tree);
            StatusMessage = RelationsSummary;
            Services.Log.Info(LogArea, $"Read {inventory.Edges.Count} relationship edges; truncated={inventory.Truncated}.");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = RelationsCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _relationsGeneration) return;
            StatusMessage = PlainMessage(ex, RelationsFailed);
            Services.Log.Warning(LogArea, $"The relationship read failed ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_relationsCts, cts)) _relationsCts = null;
            IsLoadingRelations = false;
        }
    }

    private void ShowRelations(RelationInventory inventory, IReadOnlyList<RelationTreeNode> tree)
    {
        _relations = inventory;
        _allEdges = inventory.Edges.Select(e => new RelationEdgeRow(e)).ToList();
        TreeRoots = tree;
        var supersedes = inventory.Edges.Count(e => string.Equals(e.Kind, SiteHygieneLabels.Supersedes, StringComparison.OrdinalIgnoreCase));
        var depends = inventory.Edges.Count(e => string.Equals(e.Kind, SiteHygieneLabels.DependsOn, StringComparison.OrdinalIgnoreCase));
        RelationsSummary = $"{SiteHygieneLabels.Count(inventory.Edges.Count, "relationship")}: {SiteHygieneLabels.Number(supersedes)} supersedence, {SiteHygieneLabels.Number(depends)} dependency."
            + (inventory.Truncated ? " The read was truncated, so the list and the chains are not complete." : "")
            + (inventory.Note is { Length: > 0 } note ? " " + SecretMask.Apply(note) : "");
        OnPropertyChanged(nameof(IsRelationsLoaded));
        OnPropertyChanged(nameof(Relations));
        ApplyEdgeFilter();
    }

    private void ApplyEdgeFilter()
    {
        var text = EdgeText.Trim();
        var rows = _allEdges
            .Where(r => EdgeKind == AllText || string.Equals(r.Kind, EdgeKind, StringComparison.Ordinal))
            .Where(r => text.Length == 0 || Has(r.Edge.FromName, text) || Has(r.Edge.FromKey, text) || Has(r.Edge.ToName, text) || Has(r.Edge.ToKey, text) || Has(r.Edge.Health, text));
        var list = Sorted(EdgesGrid, rows).ToList();
        _restoring = true;
        try
        {
            EdgeRows = list;
            SelectedEdge = _edgeKey is null ? null : list.FirstOrDefault(r => r.Key == _edgeKey);
        }
        finally { _restoring = false; }
        EdgeCount = list.Count == _allEdges.Count ? SiteHygieneLabels.Count(list.Count, "relationship") : $"{SiteHygieneLabels.Number(list.Count)} of {SiteHygieneLabels.Count(_allEdges.Count, "relationship")}";
    }
}
