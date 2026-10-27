using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One sidebar entry on the Settings page.</summary>
public sealed partial class SidebarSettingsRow : ObservableObject
{
    private readonly Action<SidebarSettingsRow, bool> _shownChanged;
    private bool _syncing;

    public SidebarSettingsRow(string id, string title, bool canHide, Models.SidebarGroup? group, Action<SidebarSettingsRow, bool> shownChanged,
        Action<SidebarSettingsRow> moveUp, Action<SidebarSettingsRow> moveDown)
    {
        Id = id;
        Title = title;
        CanHide = canHide;
        GroupCaption = group?.Caption;
        GroupAutomationName = group?.AutomationName;
        _shownChanged = shownChanged;
        MoveUpCommand = new RelayCommand(() => moveUp(this));
        MoveDownCommand = new RelayCommand(() => moveDown(this));
    }

    public string Id { get; }
    public string Title { get; }
    public bool CanHide { get; }
    public string? GroupCaption { get; }
    public string? GroupAutomationName { get; }
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }

    [ObservableProperty] private bool _shown;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;

    /// <summary>True for the first row of a captioned group; the Settings page shows the caption above it.</summary>
    [ObservableProperty] private bool _isGroupStart;

    partial void OnShownChanged(bool value)
    {
        if (!_syncing) _shownChanged(this, value);
    }

    internal void Sync(bool shown, bool canMoveUp, bool canMoveDown, bool isGroupStart)
    {
        _syncing = true;
        try { Shown = shown; }
        finally { _syncing = false; }
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        IsGroupStart = isGroupStart;
    }
}

/// <summary>
/// The Sidebar section of the Settings page. It edits <see cref="SidebarService"/> directly,
/// so every change applies at once, without Save Settings, and the sidebar and this list
/// always show the same layout.
/// </summary>
public sealed partial class SidebarSettingsEditor : ObservableObject
{
    private readonly SidebarService _service;

    public SidebarSettingsEditor(SidebarService service)
    {
        _service = service;
        service.Changed += (_, _) => Sync();
        Sync();
    }

    public ObservableCollection<SidebarSettingsRow> Rows { get; } = [];

    [RelayCommand]
    private void Reset() => _service.Reset();

    private void OnShownChanged(SidebarSettingsRow row, bool shown)
    {
        var applied = shown ? _service.Show(row.Id) : _service.Hide(row.Id);
        if (!applied) Sync();
    }

    /// <summary>
    /// A shown row lands past the next shown row of its group in that direction, so the
    /// sidebar changes with every press; with no shown row left that way it goes to that end
    /// of its group. A hidden row moves one place. No row leaves its group.
    /// </summary>
    private void MoveBy(SidebarSettingsRow row, int delta)
    {
        var all = _service.All;
        var index = IndexOf(all, row.Id);
        if (index < 0) return;
        var (first, last) = _service.MoveRange(all[index], Ids(all));
        var target = index + delta;
        if (!_service.IsHidden(row.Id))
        {
            target = delta < 0 ? first : last;
            for (var j = index + delta; j >= first && j <= last; j += delta)
            {
                if (_service.IsHidden(all[j].Id)) continue;
                target = j;
                break;
            }
        }
        _service.Move(row.Id, target);
    }

    private static string[] Ids(IReadOnlyList<Models.SidebarItemDefinition> items) => items.Select(i => i.Id).ToArray();

    private static int IndexOf(IReadOnlyList<Models.SidebarItemDefinition> items, string id)
    {
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Brings the rows to the service layout, reusing each row object, so a check box or
    /// button that raised the change keeps its row.
    /// </summary>
    private void Sync()
    {
        var all = _service.All;
        var ids = Ids(all);
        for (var i = 0; i < all.Count; i++)
        {
            var item = all[i];
            var current = -1;
            for (var j = i; j < Rows.Count; j++)
            {
                if (!string.Equals(Rows[j].Id, item.Id, StringComparison.OrdinalIgnoreCase)) continue;
                current = j;
                break;
            }
            if (current < 0)
                Rows.Insert(i, new SidebarSettingsRow(item.Id, item.Title, !item.IsPinned, _service.GroupOf(item), OnShownChanged, r => MoveBy(r, -1), r => MoveBy(r, 1)));
            else if (current != i)
                Rows.Move(current, i);
            var (first, last) = _service.MoveRange(item, ids);
            var groupStart = _service.GroupOf(item) is not null && (i == 0 || !SidebarRegistry.SameGroup(all[i - 1].Group, item.Group));
            Rows[i].Sync(!_service.IsHidden(item.Id), !item.IsPinned && i > first, !item.IsPinned && i < last, groupStart);
        }
        while (Rows.Count > all.Count) Rows.RemoveAt(Rows.Count - 1);
    }
}
