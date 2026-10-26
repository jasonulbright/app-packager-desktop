using ApplicationPackagerEngine.Services;
using Microsoft.Win32;

namespace ApplicationPackagerEngine.Tests.DetectionTester;

/// <summary>An in-memory registry keyed by hive, view and path; a denied key throws on open.</summary>
internal sealed class FakeRegistryProvider : IRegistryProvider
{
    private sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (object Value, RegistryValueKind Kind)> Values = new(StringComparer.OrdinalIgnoreCase);
        public bool Denied;
        public bool DenySubKeyList;
    }

    private readonly Dictionary<(RegistryHive, RegistryView), Node> _roots = [];

    public int OpenCount { get; private set; }

    private Node Root(RegistryHive hive, RegistryView view)
    {
        if (!_roots.TryGetValue((hive, view), out var root)) _roots[(hive, view)] = root = new Node();
        return root;
    }

    private Node Ensure(RegistryHive hive, RegistryView view, string path)
    {
        var node = Root(hive, view);
        foreach (var part in Split(path))
        {
            if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = new Node();
            node = child;
        }
        return node;
    }

    private static string[] Split(string path) => path.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    public FakeRegistryProvider AddKey(RegistryHive hive, RegistryView view, string path)
    {
        Ensure(hive, view, path);
        return this;
    }

    public FakeRegistryProvider Set(RegistryHive hive, RegistryView view, string path, string name, object value,
        RegistryValueKind kind = RegistryValueKind.String)
    {
        Ensure(hive, view, path).Values[name] = (value, kind);
        return this;
    }

    public FakeRegistryProvider Deny(RegistryHive hive, RegistryView view, string path)
    {
        Ensure(hive, view, path).Denied = true;
        return this;
    }

    public FakeRegistryProvider DenySubKeyList(RegistryHive hive, RegistryView view, string path)
    {
        Ensure(hive, view, path).DenySubKeyList = true;
        return this;
    }

    public IRegistryKeyReader? OpenKey(RegistryHive hive, RegistryView view, string path)
    {
        OpenCount++;
        var node = Root(hive, view);
        foreach (var part in Split(path))
        {
            if (!node.Children.TryGetValue(part, out var child)) return null;
            node = child;
        }
        if (node.Denied) throw new UnauthorizedAccessException("Requested registry access is not allowed.");
        return new Reader(node);
    }

    private sealed class Reader(Node node) : IRegistryKeyReader
    {
        public IReadOnlyList<string> GetSubKeyNames() =>
            node.DenySubKeyList ? throw new System.Security.SecurityException("Denied.") : node.Children.Keys.ToList();

        public IRegistryKeyReader? OpenSubKey(string name)
        {
            if (!node.Children.TryGetValue(name, out var child)) return null;
            if (child.Denied) throw new UnauthorizedAccessException("Requested registry access is not allowed.");
            return new Reader(child);
        }

        public object? GetValue(string name) => node.Values.TryGetValue(name, out var v) ? v.Value : null;

        public RegistryValueKind GetValueKind(string name) =>
            node.Values.TryGetValue(name, out var v) ? v.Kind : RegistryValueKind.Unknown;

        public void Dispose() { }
    }
}
