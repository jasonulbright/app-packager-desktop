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

    public IReadOnlyList<SidebarGroup> Groups => _registry.Groups;

    public SidebarGroup? GroupOf(SidebarItemDefinition item) => _registry.FindGroup(item.Group);

    public bool IsHidden(string id) => _hidden.Contains(id);

    /// <summary>
    /// The indexes of <paramref name="ids"/> that an entry of the group of <paramref name="item"/>
    /// can take: from the first entry of the group that is not pinned to the last entry of the group.
    /// </summary>
    public (int First, int Last) MoveRange(SidebarItemDefinition item, IReadOnlyList<string> ids)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < ids.Count; i++)
        {
            var other = Definition(ids[i]);
            if (!SidebarRegistry.SameGroup(other.Group, item.Group)) continue;
            if (first < 0) first = i;
            if (other.IsPinned) first = i + 1;
            last = i;
        }
        return (first, last);
    }

    /// <summary>Moves an entry to an index of <see cref="All"/>, never above a pinned entry and never out of its group.</summary>
    public bool Move(string id, int index)
    {
        if (Movable(id) is not { } item) return false;
        var from = _order.IndexOf(item.Id);
        var (first, last) = MoveRange(item, _order);
        var to = Math.Clamp(index, first, last);
        if (from == to) return false;
        _order.RemoveAt(from);
        _order.Insert(to, item.Id);
        Persist();
        return true;
    }

    /// <summary>
    /// Moves a shown entry to an index of <see cref="Items"/>, never out of its group. It lands
    /// before the shown entry that holds that index after the move when that entry is in the
    /// same group, else after the shown entry before that index.
    /// </summary>
    public bool MoveVisible(string id, int visibleIndex)
    {
        if (Movable(id) is not { } item || _hidden.Contains(item.Id)) return false;
        var visible = _order.Where(i => !_hidden.Contains(i)).ToList();
        var from = visible.IndexOf(item.Id);
        var (first, last) = MoveRange(item, visible);
        var to = Math.Clamp(visibleIndex, first, last);
        if (from == to) return false;

        visible.RemoveAt(from);
        _order.Remove(item.Id);
        var at = to < visible.Count && SidebarRegistry.SameGroup(Definition(visible[to]).Group, item.Group)
            ? _order.IndexOf(visible[to])
            : _order.IndexOf(visible[to - 1]) + 1;
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

    /// <summary>
    /// Builds the order group by group, in registry group order: the pinned entries, then the
    /// saved ids of the group in their saved relative order, then the entries the saved order
    /// does not name. A saved order from before the groups loads the same way.
    /// </summary>
    private void Apply(SidebarLayout? layout)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = (layout?.Order ?? []).Select(_registry.Find).OfType<SidebarItemDefinition>().Where(i => !i.IsPinned).ToList();
        var groups = new List<string?>();
        foreach (var item in _registry.Items)
            if (!groups.Exists(g => SidebarRegistry.SameGroup(g, item.Group))) groups.Add(item.Group);
        foreach (var group in groups)
        {
            var members = _registry.Items.Where(i => SidebarRegistry.SameGroup(i.Group, group)).ToList();
            foreach (var item in members.Where(i => i.IsPinned).Concat(saved.Where(i => SidebarRegistry.SameGroup(i.Group, group))).Concat(members))
                if (seen.Add(item.Id)) _order.Add(item.Id);
        }
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
    /// it, never less than <paramref name="minimum"/> and never more than <paramref name="maximum"/>.
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<double> midpoints, double y, int minimum, int maximum = int.MaxValue) =>
        Math.Max(minimum, Math.Min(maximum, midpoints.Count(m => m < y)));

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
