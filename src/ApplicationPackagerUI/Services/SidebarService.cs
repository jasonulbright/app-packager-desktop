using System.IO;
using ApplicationPackagerUI.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerUI.Services;

public sealed class SidebarChangedEventArgs(bool saved) : EventArgs
{
    /// <summary>False when the layout could not be written; the change lasts until the application closes.</summary>
    public bool Saved { get; } = saved;
}

public interface ISidebarLayoutStore
{
    SidebarLayout? Load();

    /// <summary>Writes the layout; false when nothing was written.</summary>
    bool Save(SidebarLayout layout);
}

/// <summary>
/// Keeps the layout in settings.json. A save reads the file again and replaces only the
/// layout, so values another surface saved meanwhile stay as they are. It uses the
/// automatic save: a launcher site code or provider stays out of the file, and an
/// unreadable file is not replaced with defaults.
/// </summary>
public sealed class SettingsSidebarLayoutStore(SettingsService settings) : ISidebarLayoutStore
{
    public SidebarLayout? Load() => settings.Load().Sidebar;

    public bool Save(SidebarLayout layout)
    {
        try
        {
            var stored = settings.Load();
            if (settings.IsFileUnreadable) return false;
            stored.Sidebar = layout;
            settings.Save(stored);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// The sidebar entries in the operator's order, with the hidden ones. Pinned entries stay
/// first and shown. Every change is saved at once and raises <see cref="Changed"/>.
/// </summary>
public sealed class SidebarService
{
    private readonly SidebarRegistry _registry;
    private readonly ISidebarLayoutStore _store;
    private readonly List<string> _order = [];
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);

    public SidebarService(SidebarRegistry registry, ISidebarLayoutStore store)
    {
        _registry = registry;
        _store = store;
        Apply(store.Load());
    }

    public event EventHandler<SidebarChangedEventArgs>? Changed;

    /// <summary>The shown entries in sidebar order.</summary>
    public IReadOnlyList<SidebarItemDefinition> Items => _order.Where(id => !_hidden.Contains(id)).Select(Definition).ToList();

    /// <summary>The hidden entries in sidebar order.</summary>
    public IReadOnlyList<SidebarItemDefinition> Hidden => _order.Where(_hidden.Contains).Select(Definition).ToList();

    /// <summary>Every entry in sidebar order; a hidden entry keeps its place.</summary>
    public IReadOnlyList<SidebarItemDefinition> All => _order.Select(Definition).ToList();

    private int PinnedCount => _registry.Items.Count(i => i.IsPinned);

    public bool IsHidden(string id) => _hidden.Contains(id);

    /// <summary>Moves an entry to an index of <see cref="All"/>, never above a pinned entry.</summary>
    public bool Move(string id, int index)
    {
        if (Movable(id) is not { } item || _order.Count == 0) return false;
        var from = _order.IndexOf(item.Id);
        var to = Math.Clamp(index, PinnedCount, _order.Count - 1);
        if (from == to) return false;
        _order.RemoveAt(from);
        _order.Insert(to, item.Id);
        Persist();
        return true;
    }

    /// <summary>
    /// Moves a shown entry to an index of <see cref="Items"/>. It lands before the shown entry
    /// that holds that index after the move, or after the last shown entry.
    /// </summary>
    public bool MoveVisible(string id, int visibleIndex)
    {
        if (Movable(id) is not { } item || _hidden.Contains(item.Id)) return false;
        var visible = _order.Where(i => !_hidden.Contains(i)).ToList();
        var from = visible.IndexOf(item.Id);
        var to = Math.Clamp(visibleIndex, PinnedCount, visible.Count - 1);
        if (from == to) return false;

        visible.RemoveAt(from);
        _order.Remove(item.Id);
        var at = to < visible.Count ? _order.IndexOf(visible[to]) : _order.IndexOf(visible[^1]) + 1;
        _order.Insert(at, item.Id);
        Persist();
        return true;
    }

    /// <summary>Hides an entry; false for an unknown, pinned or already hidden entry.</summary>
    public bool Hide(string id)
    {
        if (Movable(id) is not { } item || !_hidden.Add(item.Id)) return false;
        Persist();
        return true;
    }

    /// <summary>Shows a hidden entry again at the place it kept in the order.</summary>
    public bool Show(string id)
    {
        if (_registry.Find(id) is not { } item || !_hidden.Remove(item.Id)) return false;
        Persist();
        return true;
    }

    /// <summary>The registry order with every entry shown.</summary>
    public void Reset()
    {
        _order.Clear();
        _order.AddRange(_registry.Items.Select(i => i.Id));
        _hidden.Clear();
        Persist();
    }

    private SidebarItemDefinition? Movable(string id) => _registry.Find(id) is { IsPinned: false } item ? item : null;

    private SidebarItemDefinition Definition(string id) => _registry.Find(id)!;

    private void Apply(SidebarLayout? layout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pinned in _registry.Items.Where(i => i.IsPinned))
        {
            seen.Add(pinned.Id);
            _order.Add(pinned.Id);
        }
        foreach (var id in layout?.Order ?? [])
        {
            if (_registry.Find(id) is { IsPinned: false } item && seen.Add(item.Id))
                _order.Add(item.Id);
        }
        foreach (var item in _registry.Items.Where(i => seen.Add(i.Id)))
            _order.Add(item.Id);
        foreach (var id in layout?.Hidden ?? [])
        {
            if (Movable(id) is { } item) _hidden.Add(item.Id);
        }
    }

    private void Persist()
    {
        var saved = _store.Save(new SidebarLayout
        {
            Order = _order.ToArray(),
            Hidden = _order.Where(_hidden.Contains).ToArray()
        });
        Changed?.Invoke(this, new SidebarChangedEventArgs(saved));
    }
}

/// <summary>Positions for the sidebar drag, in the coordinates of the entry list.</summary>
public static class SidebarDropMath
{
    /// <summary>
    /// The gap the pointer is over: the count of entries whose vertical midpoint lies above
    /// it, never less than <paramref name="minimum"/>.
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<double> midpoints, double y, int minimum) =>
        Math.Max(minimum, midpoints.Count(m => m < y));

    /// <summary>The index of the dragged entry after the drop into a gap.</summary>
    public static int FinalIndex(int sourceIndex, int insertionIndex) =>
        insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;

    public static bool PastThreshold(double dx, double dy, double minimumX, double minimumY) =>
        Math.Abs(dx) >= minimumX || Math.Abs(dy) >= minimumY;
}

public static class SidebarServiceRegistration
{
    public static IServiceCollection AddSidebar(this IServiceCollection services)
    {
        services.AddSingleton(_ => SidebarRegistry.CreateDefault());
        services.AddSingleton<ISidebarLayoutStore, SettingsSidebarLayoutStore>();
        services.AddSingleton<SidebarService>();
        return services;
    }
}
