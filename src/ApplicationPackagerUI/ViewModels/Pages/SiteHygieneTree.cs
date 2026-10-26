using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// One line of the Relationships tree: a group of one edge kind, an application, or the mark where a
/// chain goes back to an application already on its path. Children are made when the line is first
/// expanded, so a large inventory costs work only for the lines the operator opens.
/// </summary>
public sealed partial class RelationTreeNode : ObservableObject
{
    private static readonly RelationTreeNode Placeholder = new("…", null, null, null, null);

    private readonly RelationGraph? _graph;
    private bool _loaded;

    private RelationTreeNode(string text, string? key, RelationTreeNode? parent, RelationGraph? graph, IEnumerable<RelationTreeNode>? children)
    {
        Text = text;
        Key = key;
        Parent = parent;
        _graph = graph;
        if (children is not null)
        {
            foreach (var child in children) Children.Add(child);
            _loaded = true;
        }
        else if (graph is not null && key is not null && graph.HasChildren(key)) Children.Add(Placeholder);
        else _loaded = true;
    }

    public string Text { get; }

    /// <summary>The object key of an application line; null for a group or a cycle mark.</summary>
    public string? Key { get; }

    public RelationTreeNode? Parent { get; }

    public ObservableCollection<RelationTreeNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public bool IsPlaceholder => ReferenceEquals(this, Placeholder);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) Load();
    }

    /// <summary>Makes the children of an application line; a child already on the path of this line becomes a cycle mark.</summary>
    public void Load()
    {
        if (_loaded || _graph is null || Key is null) return;
        _loaded = true;
        Children.Clear();
        foreach (var edge in _graph.Out(Key))
        {
            if (OnPath(edge.ToKey)) Children.Add(new RelationTreeNode($"↻ {SiteHygieneTreeText.Verb(edge.Kind)} {edge.ToName} ({edge.ToKey}) again: the chain is circular.", null, this, null, []));
            else Children.Add(new RelationTreeNode(SiteHygieneTreeText.Child(edge), edge.ToKey, this, _graph, null));
        }
    }

    private bool OnPath(string key)
    {
        for (var node = this; node is not null; node = node.Parent)
            if (node.Key is not null && string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public override string ToString() => Text;

    internal static RelationTreeNode Group(string text, IEnumerable<RelationTreeNode> roots) => new(text, null, null, null, roots);

    internal static RelationTreeNode Root(RelationGraph graph, string key, string name) => new($"{name} ({key})", key, null, graph, null);
}

/// <summary>The edges of one kind, by the object they start from.</summary>
public sealed class RelationGraph
{
    private readonly Dictionary<string, List<RelationEdge>> _out = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hasIncoming = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public RelationGraph(IEnumerable<RelationEdge> edges)
    {
        foreach (var edge in edges)
        {
            if (!_out.TryGetValue(edge.FromKey, out var list))
            {
                _out[edge.FromKey] = list = [];
                _order.Add(edge.FromKey);
            }
            list.Add(edge);
            _hasIncoming.Add(edge.ToKey);
            _names.TryAdd(edge.FromKey, edge.FromName);
            _names.TryAdd(edge.ToKey, edge.ToName);
        }
    }

    public bool HasChildren(string key) => _out.ContainsKey(key);

    public IReadOnlyList<RelationEdge> Out(string key) => _out.TryGetValue(key, out var list) ? list : [];

    public string Name(string key) => _names.TryGetValue(key, out var name) ? name : key;

    /// <summary>
    /// The start of each chain: every object with no edge into it, then, for each part of the graph
    /// that only cycles reach, its first object in read order. Each object and edge is visited once.
    /// </summary>
    public IReadOnlyList<string> Roots()
    {
        var roots = _order.Where(k => !_hasIncoming.Contains(k)).ToList();
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string start)
        {
            var stack = new Stack<string>();
            if (reached.Add(start)) stack.Push(start);
            while (stack.Count > 0)
                foreach (var edge in Out(stack.Pop()))
                    if (reached.Add(edge.ToKey)) stack.Push(edge.ToKey);
        }
        foreach (var root in roots) Walk(root);
        foreach (var key in _order)
        {
            if (reached.Contains(key)) continue;
            roots.Add(key);
            Walk(key);
        }
        return roots;
    }
}

public static class SiteHygieneTreeText
{
    public static string Verb(string kind) => string.Equals(kind, SiteHygieneLabels.DependsOn, StringComparison.OrdinalIgnoreCase) ? "depends on" : "supersedes";

    public static string Child(RelationEdge edge) => $"{char.ToUpperInvariant(Verb(edge.Kind)[0])}{Verb(edge.Kind)[1..]} {edge.ToName} ({edge.ToKey}), {SiteHygieneLabels.HealthText(edge.Health)}";

    /// <summary>One group line per edge kind, each with its chains.</summary>
    public static IReadOnlyList<RelationTreeNode> Build(IReadOnlyList<RelationEdge> edges)
    {
        var groups = new List<RelationTreeNode>();
        foreach (var (kind, title) in new[] { (SiteHygieneLabels.Supersedes, "Supersedence"), (SiteHygieneLabels.DependsOn, "Dependencies") })
        {
            var graph = new RelationGraph(edges.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)));
            var roots = graph.Roots();
            groups.Add(RelationTreeNode.Group($"{title}: {SiteHygieneLabels.Count(roots.Count, "chain")}", roots.Select(r => RelationTreeNode.Root(graph, r, graph.Name(r)))));
        }
        return groups;
    }
}
