using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using ItemsControl = System.Windows.Controls.ItemsControl;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace ApplicationPackagerUI.Views.Windows;

/// <summary>
/// Fills the navigation view's menu from <see cref="SidebarService"/> and keeps it in step
/// with every change. Each entry keeps one item object for the life of the window: the
/// navigation view indexes items by page type when they are added and never removes them,
/// so a new object for the same page would leave the old one as the navigation target.
/// </summary>
public sealed class SidebarPresenter
{
    private readonly NavigationView _navigation;
    private readonly SidebarService _service;
    private readonly Action<SidebarItemDefinition> _activate;
    private readonly Dictionary<string, NavigationViewItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private bool _attached;

    public SidebarPresenter(NavigationView navigation, SidebarService service, Action<SidebarItemDefinition> activate)
    {
        _navigation = navigation;
        _service = service;
        _activate = activate;
        service.Changed += (_, _) => Sync();
        Sync();
        navigation.Loaded += (_, _) => Attach();
        if (navigation.IsLoaded) Attach();
    }

    /// <summary>The menu of the empty pane area: Show tool and Reset sidebar.</summary>
    public ContextMenu? PaneMenu { get; private set; }

    /// <summary>The pane element that carries <see cref="PaneMenu"/>; null until the template is applied.</summary>
    public Panel? PaneMenuOwner { get; private set; }

    public SidebarDragBehavior? Drag { get; private set; }

    public NavigationViewItem ItemFor(string id) => _items[id];

    /// <summary>Alt+Up and Alt+Down move the focused entry one place among the shown entries.</summary>
    public bool HandleMoveKey(NavigationViewItem item, Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down) || item.Tag is not string id) return false;
        var index = IndexOfShown(id);
        if (index < 0 || !_service.MoveVisible(id, key == Key.Up ? index - 1 : index + 1)) return false;
        item.Focus();
        return true;
    }

    internal NavigationViewItem? ItemFrom(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is NavigationViewItem { Tag: string id } item && _items.TryGetValue(id, out var known) && ReferenceEquals(known, item))
                return item;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private int IndexOfShown(string id)
    {
        var shown = _service.Items;
        for (var i = 0; i < shown.Count; i++)
            if (string.Equals(shown[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private void Attach()
    {
        if (_attached) return;
        _navigation.ApplyTemplate();
        if (_navigation.Template?.FindName("PART_MenuItemsItemsControl", _navigation) is not ItemsControl list) return;
        _attached = true;

        // The pane grid has no background, so a right-click on its empty area would reach
        // the navigation view itself instead of the pane.
        if (_navigation.Template.FindName("PaneGrid", _navigation) is Panel pane)
        {
            pane.Background ??= Brushes.Transparent;
            pane.ContextMenu = PaneMenu;
            PaneMenuOwner = pane;
        }

        Drag = new SidebarDragBehavior(list, _service, ItemFrom, _activate);
        list.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ItemFrom(e.OriginalSource) is { } item && HandleMoveKey(item, key, Keyboard.Modifiers)) e.Handled = true;
        };
    }

    private void Sync()
    {
        var focused = ItemFrom(Keyboard.FocusedElement);
        var menu = (ObservableCollection<object>)_navigation.MenuItems;
        var shown = _service.Items;
        for (var i = 0; i < shown.Count; i++)
        {
            var item = Item(shown[i]);
            var current = menu.IndexOf(item);
            if (current < 0) menu.Insert(i, item);
            else if (current != i) menu.Move(current, i);
        }
        while (menu.Count > shown.Count) menu.RemoveAt(menu.Count - 1);

        foreach (var definition in _service.All)
            Item(definition).ContextMenu = EntryMenu(definition);
        PaneMenu = new ContextMenu();
        AddShowAndReset(PaneMenu);
        if (PaneMenuOwner is not null) PaneMenuOwner.ContextMenu = PaneMenu;

        if (focused is not null && menu.Contains(focused) && !focused.IsKeyboardFocusWithin) focused.Focus();
    }

    private NavigationViewItem Item(SidebarItemDefinition definition)
    {
        if (_items.TryGetValue(definition.Id, out var existing)) return existing;
        var symbol = Enum.TryParse<SymbolRegular>(definition.Symbol, out var parsed) ? parsed : SymbolRegular.Apps24;
        var item = new NavigationViewItem
        {
            Content = definition.Title,
            Icon = new SymbolIcon { Symbol = symbol },
            Tag = definition.Id,
            TargetPageTag = definition.Id
        };
        AutomationProperties.SetAutomationId(item, "nav-" + definition.Id);
        if (definition.Kind == SidebarItemKind.Page)
        {
            item.TargetPageType = definition.PageType;
        }
        else
        {
            // Keyboard and automation invoke arrive here; the mouse path runs through the drag behavior.
            item.Click += (_, e) =>
            {
                e.Handled = true;
                _activate(definition);
            };
        }
        _items[definition.Id] = item;
        return item;
    }

    private ContextMenu EntryMenu(SidebarItemDefinition definition)
    {
        var menu = new ContextMenu();
        if (!definition.IsPinned)
            menu.Items.Add(Command("Hide " + definition.Title, () => _service.Hide(definition.Id)));
        AddShowAndReset(menu);
        return menu;
    }

    private void AddShowAndReset(ContextMenu menu)
    {
        var hidden = _service.Hidden;
        if (hidden.Count > 0)
        {
            var show = new MenuItem { Header = "Show tool" };
            foreach (var item in hidden)
                show.Items.Add(Command(item.Title, () => _service.Show(item.Id)));
            menu.Items.Add(show);
        }
        menu.Items.Add(Command("Reset sidebar", _service.Reset));
    }

    private static MenuItem Command(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}

/// <summary>
/// Drag and drop reorder over the navigation view's entry list. A press starts nothing
/// until the pointer passes the system drag distance; the drag then owns the mouse, shows
/// an insertion marker and drops on release over the list. Escape, a release outside the
/// list or a lost capture cancels. A pinned entry never starts a drag and no gap above it
/// is a target. A press and release on a Window or Tool entry opens it.
/// </summary>
public sealed class SidebarDragBehavior
{
    private readonly SidebarService _service;
    private readonly Func<object?, NavigationViewItem?> _itemFrom;
    private readonly Action<SidebarItemDefinition> _activate;
    private NavigationViewItem? _pressed;
    private SidebarItemDefinition? _pressedDefinition;
    private Point _start;
    private bool _ending;
    private bool _hasCapture;
    private InsertionMarker? _marker;
    private Window? _window;

    public SidebarDragBehavior(ItemsControl list, SidebarService service, Func<object?, NavigationViewItem?> itemFrom, Action<SidebarItemDefinition> activate)
    {
        List = list;
        _service = service;
        _itemFrom = itemFrom;
        _activate = activate;

        list.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        list.PreviewMouseMove += OnPreviewMouseMove;
        list.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        list.LostMouseCapture += (_, e) =>
        {
            if (_hasCapture && !_ending && ReferenceEquals(e.OriginalSource, list)) Cancel();
        };
        HookWindow();
        list.Loaded += (_, _) => HookWindow();
    }

    public ItemsControl List { get; }

    public bool IsDragging { get; private set; }

    public bool MarkerShown => _marker is not null;

    public int? InsertionIndex { get; private set; }

    public Brush? MarkerBrush => _marker?.Brush;

    public void Press(NavigationViewItem item, Point position)
    {
        Cancel();
        if (item.Tag is not string id || _service.All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } definition) return;
        _pressed = item;
        _pressedDefinition = definition;
        _start = position;
    }

    public void MoveTo(Point position)
    {
        if (_pressed is null || _pressedDefinition is null) return;
        if (!IsDragging)
        {
            if (_pressedDefinition.IsPinned) return;
            if (!SidebarDropMath.PastThreshold(position.X - _start.X, position.Y - _start.Y,
                    SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
            IsDragging = true;
            _pressed.Opacity = 0.5;
            _ending = true;
            try { _hasCapture = Mouse.Capture(List, CaptureMode.SubTree) && ReferenceEquals(Mouse.Captured, List); }
            finally { _ending = false; }
        }
        UpdateMarker(position);
    }

    public void Release(Point position)
    {
        if (_pressed is null || _pressedDefinition is null) return;
        var definition = _pressedDefinition;
        var wasDragging = IsDragging;
        int? insertion = null;
        if (wasDragging && Inside(position))
        {
            UpdateMarker(position);
            insertion = InsertionIndex;
        }
        Cancel();

        if (wasDragging)
        {
            var source = ShownIndex(definition.Id);
            if (insertion is { } gap && source >= 0)
                _service.MoveVisible(definition.Id, SidebarDropMath.FinalIndex(source, gap));
        }
        else if (definition.Kind != SidebarItemKind.Page)
        {
            // A window opened inside the mouse handler would run its modal loop before the
            // release finishes routing.
            List.Dispatcher.BeginInvoke(() => _activate(definition));
        }
    }

    public void Cancel()
    {
        _ending = true;
        try
        {
            if (_hasCapture && ReferenceEquals(Mouse.Captured, List)) List.ReleaseMouseCapture();
        }
        finally { _ending = false; }
        _hasCapture = false;
        if (_pressed is not null) _pressed.ClearValue(UIElement.OpacityProperty);
        if (_marker is not null) AdornerLayer.GetAdornerLayer(List)?.Remove(_marker);
        _marker = null;
        _pressed = null;
        _pressedDefinition = null;
        IsDragging = false;
        InsertionIndex = null;
    }

    private void HookWindow()
    {
        var window = Window.GetWindow(List);
        if (window is null || ReferenceEquals(window, _window)) return;
        if (_window is not null) _window.PreviewKeyDown -= OnWindowPreviewKeyDown;
        _window = window;
        window.PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsDragging) return;
        Cancel();
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_itemFrom(e.OriginalSource) is not { } item) return;
        Press(item, e.GetPosition(List));
        // The navigation view raises no Click for the item it already treats as current,
        // so a Window or Tool entry takes the mouse here and opens on release.
        if (_pressedDefinition is { Kind: not SidebarItemKind.Page }) e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // A capture change raises a synthetic mouse move while the capture call runs.
        if (_pressed is null || _ending) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Cancel();
            return;
        }
        MoveTo(e.GetPosition(List));
        if (IsDragging) e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed is null) return;
        var handled = IsDragging || _pressedDefinition is { Kind: not SidebarItemKind.Page };
        Release(e.GetPosition(List));
        if (handled) e.Handled = true;
    }

    private bool Inside(Point position) =>
        position.X >= 0 && position.Y >= 0 && position.X <= List.ActualWidth && position.Y <= List.ActualHeight;

    private int ShownIndex(string id)
    {
        var shown = _service.Items;
        for (var i = 0; i < shown.Count; i++)
            if (string.Equals(shown[i].Id, id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private List<NavigationViewItem> ShownItems() =>
        _service.Items.Select(d => List.Items.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag is string id && string.Equals(id, d.Id, StringComparison.OrdinalIgnoreCase)))
            .OfType<NavigationViewItem>().ToList();

    private void UpdateMarker(Point position)
    {
        var items = ShownItems();
        if (items.Count == 0) return;
        var tops = items.Select(i => i.TranslatePoint(new Point(0, 0), List).Y).ToList();
        var midpoints = items.Select((item, i) => tops[i] + item.ActualHeight / 2).ToList();
        var pinned = _service.Items.TakeWhile(d => d.IsPinned).Count();
        var gap = SidebarDropMath.InsertionIndex(midpoints, position.Y, pinned);
        InsertionIndex = gap;
        var y = gap < items.Count ? tops[gap] : tops[^1] + items[^1].ActualHeight;

        if (_marker is null)
        {
            var layer = AdornerLayer.GetAdornerLayer(List);
            if (layer is null) return;
            _marker = new InsertionMarker(List, List.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? SystemColors.HighlightBrush);
            layer.Add(_marker);
        }
        _marker.Y = y;
    }

    private sealed class InsertionMarker : Adorner
    {
        private double _y;

        public InsertionMarker(UIElement adorned, Brush brush) : base(adorned)
        {
            Brush = brush;
            IsHitTestVisible = false;
        }

        public Brush Brush { get; }

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var width = AdornedElement.RenderSize.Width;
            drawingContext.DrawRectangle(Brush, null, new Rect(4, Math.Max(0, _y - 1), Math.Max(0, width - 8), 2));
        }
    }
}
