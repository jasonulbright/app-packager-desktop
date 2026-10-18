using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.Views.Pages;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using Point = System.Windows.Point;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The sidebar in a real window with the control styles the application loads: the
/// navigation items, the right-click menus, the drag behavior and the keyboard reorder
/// all act through <see cref="SidebarService"/>.
/// </summary>
public sealed class SidebarWindowTests
{
    private sealed class Harness
    {
        public required Window Window { get; init; }
        public required NavigationView Navigation { get; init; }
        public required SidebarService Service { get; init; }
        public required SidebarPresenter Presenter { get; init; }
        public required List<string> Activated { get; init; }

        public ItemsControl List => Presenter.Drag!.List;

        public string[] Shown() => Navigation.MenuItems.Cast<NavigationViewItem>().Select(i => (string)i.Tag).ToArray();

        public Point Center(string id)
        {
            var item = Presenter.ItemFor(id);
            return item.TranslatePoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2), List);
        }

        public Point Top(string id) => Presenter.ItemFor(id).TranslatePoint(new Point(10, 2), List);
    }

    private static void WithSidebar(Action<Harness> test, SidebarRegistry? registry = null, SidebarLayout? layout = null, ApplicationTheme theme = ApplicationTheme.Dark)
    {
        StaTestHelper.Run(() =>
        {
            var service = new SidebarService(registry ?? SidebarFixtures.Registry(), new InMemorySidebarStore(layout));
            var navigation = new NavigationView { PaneDisplayMode = Wpf.Ui.Controls.NavigationViewPaneDisplayMode.Left, IsPaneOpen = true, OpenPaneLength = 220 };
            var activated = new List<string>();
            var presenter = new SidebarPresenter(navigation, service, item => activated.Add(item.Id));
            var window = new Window
            {
                Width = 900, Height = 700, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, Content = navigation
            };
            window.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
            window.Resources.MergedDictionaries.Add(new ControlsDictionary());
            window.Show();
            try
            {
                window.UpdateLayout();
                StaTestHelper.Flush();
                test(new Harness { Window = window, Navigation = navigation, Service = service, Presenter = presenter, Activated = activated });
            }
            finally { window.Close(); }
        });
    }

    private static MenuItem Entry(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().Single(m => (string)m.Header == header);

    private static string[] Headers(ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(m => (string)m.Header).ToArray();

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    [Fact]
    public void TheMenuItemsFollowTheServiceAndKeepTheirInstancesAcrossChanges()
    {
        WithSidebar(h =>
        {
            Assert.Equal(["home", "a", "b", "w", "t"], h.Shown());
            var bravo = h.Presenter.ItemFor("b");
            Assert.Equal("Bravo", bravo.Content);
            Assert.Equal(typeof(AddAppPage), bravo.TargetPageType);
            Assert.Null(h.Presenter.ItemFor("w").TargetPageType);

            h.Service.Hide("a");
            h.Service.Move("t", 1);
            Assert.Equal(["home", "t", "b", "w"], h.Shown());
            Assert.Same(bravo, h.Presenter.ItemFor("b"));
            Assert.Contains(bravo, h.Navigation.MenuItems.Cast<object>());
        });
    }

    [Fact]
    public void PackagesStaysTheFirstItemAndTheStartPageWhateverTheSavedLayout()
    {
        WithSidebar(h =>
        {
            var first = (NavigationViewItem)h.Navigation.MenuItems[0]!;
            Assert.Equal(SidebarIds.Packages, first.Tag);
            Assert.Equal(typeof(PackagesPage), first.TargetPageType);
            Assert.Equal(SidebarRegistry.CreateDefault().Items.Count - 1, h.Navigation.MenuItems.Count);
        }, SidebarRegistry.CreateDefault(), new SidebarLayout { Order = [SidebarIds.Workbench, SidebarIds.Packages], Hidden = [SidebarIds.Packages, SidebarIds.AddApp] });
    }

    [Fact]
    public void HideOnTheEntryMenuHidesTheEntryAndShowToolBringsItBack()
    {
        WithSidebar(h =>
        {
            var menu = h.Presenter.ItemFor("a").ContextMenu!;
            Assert.Equal(["Hide Alpha", "Reset sidebar"], Headers(menu));
            Click(Entry(menu, "Hide Alpha"));
            Assert.True(h.Service.IsHidden("a"));
            Assert.DoesNotContain("a", h.Shown());

            var other = h.Presenter.ItemFor("b").ContextMenu!;
            Assert.Equal(["Hide Bravo", "Show tool", "Reset sidebar"], Headers(other));
            var show = Entry(other, "Show tool");
            Assert.Equal(["Alpha"], show.Items.OfType<MenuItem>().Select(m => (string)m.Header));
            Click(show.Items.OfType<MenuItem>().Single());
            Assert.False(h.Service.IsHidden("a"));
            Assert.Equal(["home", "a", "b", "w", "t"], h.Shown());
        });
    }

    [Fact]
    public void ThePinnedEntryMenuHasNoHide()
    {
        WithSidebar(h =>
        {
            Assert.Equal(["Reset sidebar"], Headers(h.Presenter.ItemFor("home").ContextMenu!));
            h.Service.Hide("t");
            Assert.Equal(["Show tool", "Reset sidebar"], Headers(h.Presenter.ItemFor("home").ContextMenu!));
        });
    }

    [Fact]
    public void TheEmptyAreaMenuShowsHiddenToolsAndResets()
    {
        WithSidebar(h =>
        {
            var pane = h.Presenter.PaneMenu!;
            Assert.Equal(["Show tool", "Reset sidebar"], Headers(pane));
            Assert.Equal(["Window", "Tool"], Entry(pane, "Show tool").Items.OfType<MenuItem>().Select(m => (string)m.Header));
            Click(Entry(pane, "Reset sidebar"));
            Assert.Empty(h.Service.Hidden);
            Assert.Equal(["Reset sidebar"], Headers(h.Presenter.PaneMenu!));
            Assert.Equal(["home", "a", "b", "w", "t"], h.Shown());
        }, layout: new SidebarLayout { Order = [], Hidden = ["w", "t"] });
    }

    [Fact]
    public void TheEmptyAreaOfThePaneTakesTheRightClick()
    {
        WithSidebar(h =>
        {
            var owner = h.Presenter.PaneMenuOwner!;
            Assert.Same(h.Presenter.PaneMenu, owner.ContextMenu);
            Assert.NotNull(owner.Background);
            Assert.True(owner.IsAncestorOf(h.List));
            Assert.False(owner.IsAncestorOf(h.Navigation.Template.FindName("PART_NavigationViewContentPresenter", h.Navigation) as DependencyObject ?? h.Window));
        });
    }

    [Fact]
    public void ADragPastTheThresholdShowsTheMarkerAndTheDropMovesTheEntry()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            var start = h.Center("t");
            drag.Press(h.Presenter.ItemFor("t"), start);
            drag.MoveTo(new Point(start.X + 1, start.Y - 1));
            Assert.False(drag.IsDragging);
            Assert.False(drag.MarkerShown);

            drag.MoveTo(h.Top("a"));
            Assert.True(drag.IsDragging);
            Assert.True(drag.MarkerShown);
            Assert.Equal(1, drag.InsertionIndex);

            drag.Release(h.Top("a"));
            Assert.False(drag.IsDragging);
            Assert.False(drag.MarkerShown);
            Assert.Equal(["home", "t", "a", "b", "w"], SidebarFixtures.Ids(h.Service.Items));
            Assert.Equal(["home", "t", "a", "b", "w"], h.Shown());
            Assert.Empty(h.Activated);
        });
    }

    [Fact]
    public void ThePinnedEntryIsNotADropTargetAboveIt()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("b"), h.Center("b"));
            drag.MoveTo(new Point(10, 0));
            Assert.Equal(1, drag.InsertionIndex);
            drag.Release(new Point(10, 0));
            Assert.Equal(["home", "b", "a", "w", "t"], SidebarFixtures.Ids(h.Service.Items));
        });
    }

    [Fact]
    public void ThePinnedEntryDoesNotStartADrag()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("home"), h.Center("home"));
            drag.MoveTo(h.Center("t"));
            Assert.False(drag.IsDragging);
            drag.Release(h.Center("t"));
            Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(h.Service.Items));
        });
    }

    [Fact]
    public void EscapeCancelsTheDrag()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("t"), h.Center("t"));
            drag.MoveTo(h.Top("a"));
            Assert.True(drag.IsDragging);

            var escape = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(h.Window)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            h.Window.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(drag.IsDragging);
            Assert.False(drag.MarkerShown);
            drag.Release(h.Top("a"));
            Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(h.Service.Items));
        });
    }

    [Fact]
    public void ADropOutsideTheListChangesNothing()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("t"), h.Center("t"));
            drag.MoveTo(h.Top("a"));
            drag.Release(new Point(h.List.ActualWidth + 300, 20));
            Assert.False(drag.IsDragging);
            Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(h.Service.Items));
        });
    }

    [Fact]
    public void APressAndReleaseOnAWindowEntryOpensItAndADragDoesNot()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("w"), h.Center("w"));
            drag.Release(h.Center("w"));
            StaTestHelper.Flush();
            Assert.Equal(["w"], h.Activated);

            drag.Press(h.Presenter.ItemFor("w"), h.Center("w"));
            drag.MoveTo(h.Top("a"));
            drag.Release(h.Top("a"));
            StaTestHelper.Flush();
            Assert.Equal(["w"], h.Activated);
            Assert.Equal(["home", "w", "a", "b", "t"], SidebarFixtures.Ids(h.Service.Items));
        });
    }

    [Fact]
    public void AKeyboardOrAutomationClickOnAToolEntryOpensIt()
    {
        WithSidebar(h =>
        {
            var tool = h.Presenter.ItemFor("t");
            tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, tool));
            Assert.Equal(["t"], h.Activated);
        });
    }

    [Fact]
    public void AltUpAndAltDownMoveTheFocusedEntryAndKeepItFocused()
    {
        WithSidebar(h =>
        {
            var bravo = h.Presenter.ItemFor("b");
            Assert.True(h.Presenter.HandleMoveKey(bravo, Key.Up, ModifierKeys.Alt));
            Assert.Equal(["home", "b", "a", "w", "t"], h.Shown());
            Assert.False(h.Presenter.HandleMoveKey(bravo, Key.Up, ModifierKeys.Alt));
            Assert.True(h.Presenter.HandleMoveKey(bravo, Key.Down, ModifierKeys.Alt));
            Assert.Equal(["home", "a", "b", "w", "t"], h.Shown());
            Assert.False(h.Presenter.HandleMoveKey(bravo, Key.Down, ModifierKeys.None));
            Assert.False(h.Presenter.HandleMoveKey(bravo, Key.Down, ModifierKeys.Alt | ModifierKeys.Control));
            Assert.False(h.Presenter.HandleMoveKey(h.Presenter.ItemFor("home"), Key.Down, ModifierKeys.Alt));
            Assert.Equal(["home", "a", "b", "w", "t"], h.Shown());
        });
    }

    [Fact]
    public void AltDownSkipsOverAHiddenEntry()
    {
        WithSidebar(h =>
        {
            Assert.True(h.Presenter.HandleMoveKey(h.Presenter.ItemFor("a"), Key.Down, ModifierKeys.Alt));
            Assert.Equal(["home", "w", "a", "t"], h.Shown());
        }, layout: new SidebarLayout { Order = [], Hidden = ["b"] });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheMarkerAndTheMenusUseTheThemeResources(ApplicationTheme theme)
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("t"), h.Center("t"));
            drag.MoveTo(h.Top("a"));
            var accent = h.Window.TryFindResource("AccentFillColorDefaultBrush");
            Assert.NotNull(accent);
            Assert.True(drag.MarkerShown);
            Assert.Equal(accent.ToString(), drag.MarkerBrush?.ToString());
            drag.Cancel();
            var menu = h.Presenter.ItemFor("a").ContextMenu!;
            Assert.NotNull(menu.Style ?? h.Window.TryFindResource(typeof(ContextMenu)));
        }, theme: theme);
    }
}
