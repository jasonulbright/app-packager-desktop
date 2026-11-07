using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.Views.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using MenuItem = System.Windows.Controls.MenuItem;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;
using Point = System.Windows.Point;

namespace ApplicationPackagerEngine.Tests;

public sealed class SidebarGroupWindowTests
{
    private sealed class Harness
    {
        public required Window Window { get; init; }
        public required NavigationView Navigation { get; init; }
        public required SidebarService Service { get; init; }
        public required SidebarPresenter Presenter { get; init; }

        public ItemsControl List => Presenter.Drag!.List;

        /// <summary>The menu as ids, with a caption shown as "[group id]".</summary>
        public string[] Menu() => Navigation.MenuItems.Cast<object>()
            .Select(o => o switch { SidebarGroupCaption c => "[" + c.Group.Id + "]", NavigationViewItem i => (string)i.Tag, _ => "?" }).ToArray();

        public Point Center(string id)
        {
            var item = Presenter.ItemFor(id);
            return item.TranslatePoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2), List);
        }

        public Point Top(string id) => Presenter.ItemFor(id).TranslatePoint(new Point(10, 2), List);

        public Point Bottom(string id)
        {
            var item = Presenter.ItemFor(id);
            return item.TranslatePoint(new Point(10, item.ActualHeight - 2), List);
        }
    }

    private static void WithSidebar(Action<Harness> test, SidebarLayout? layout = null, ApplicationTheme theme = ApplicationTheme.Dark, SidebarRegistry? registry = null)
    {
        StaTestHelper.Run(() =>
        {
            var service = new SidebarService(registry ?? SidebarGroupFixtures.Registry(), new InMemorySidebarStore(layout));
            var navigation = new NavigationView { PaneDisplayMode = Wpf.Ui.Controls.NavigationViewPaneDisplayMode.Left, IsPaneOpen = true, OpenPaneLength = 220 };
            var presenter = new SidebarPresenter(navigation, service, _ => { });
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
                test(new Harness { Window = window, Navigation = navigation, Service = service, Presenter = presenter });
            }
            finally { window.Close(); }
        });
    }

    private static string[] Headers(System.Windows.Controls.ContextMenu menu) => menu.Items.OfType<MenuItem>().Select(m => (string)m.Header).ToArray();

    [WindowFact]
    public void ACaptionRowStandsBeforeEachGroupAndKeepsItsInstance()
    {
        WithSidebar(h =>
        {
            Assert.Equal(["[main]", "home", "a", "b", "[more]", "w", "t", "x"], h.Menu());
            var more = h.Presenter.CaptionFor("more");
            Assert.Equal("MORE", more.Text.Text);

            h.Service.Hide("w");
            h.Service.Hide("t");
            h.Service.Hide("x");
            Assert.Equal(["[main]", "home", "a", "b"], h.Menu());

            h.Service.Show("t");
            Assert.Equal(["[main]", "home", "a", "b", "[more]", "t"], h.Menu());
            Assert.Same(more, h.Navigation.MenuItems[4]);
        });
    }

    [WindowFact]
    public void AnEntryWithoutAGroupHasNoCaption()
    {
        WithSidebar(h =>
        {
            Assert.Equal(["home", "a", "b", "w", "t"], h.Menu());
        }, registry: SidebarFixtures.Registry());
    }

    [WindowFact]
    public void TheCaptionTakesNoFocusAndAScreenReaderReadsItsGroupName()
    {
        WithSidebar(h =>
        {
            var caption = h.Presenter.CaptionFor("main");
            Assert.False(caption.Focusable);
            Assert.False(KeyboardNavigation.GetIsTabStop(caption));
            Assert.False(caption.IsHitTestVisible && caption.Child.IsHitTestVisible);
            Assert.False(caption.Focus());

            var peer = UIElementAutomationPeer.CreatePeerForElement(caption);
            Assert.Equal("Main group", peer.GetName());
            Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.True(peer.IsControlElement());
            Assert.False(peer.IsKeyboardFocusable());
            Assert.Null(peer.GetChildren());

            var alpha = h.Presenter.ItemFor("a");
            Assert.Equal("Alpha", AutomationProperties.GetName(alpha));
            Assert.Equal("Alpha", alpha.ToolTip);
            Assert.Equal("Alpha", UIElementAutomationPeer.CreatePeerForElement(alpha).GetName());
        });
    }

    [WindowFact]
    public void TabMovesThroughTheEntriesInOrderAndNeverStopsOnACaption()
    {
        WithSidebar(h =>
        {
            h.Window.Activate();
            var home = h.Presenter.ItemFor("home");
            Assert.True(home.Focus());
            Assert.True(home.IsKeyboardFocused);

            var order = h.Service.Items.Select(d => h.Presenter.ItemFor(d.Id)).ToList();
            var visited = new List<NavigationViewItem> { home };
            for (var step = 0; step < 20; step++)
            {
                var focused = (DependencyObject)Keyboard.FocusedElement;
                if (!((UIElement)focused).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next))) break;
                StaTestHelper.Flush();
                var now = (DependencyObject)Keyboard.FocusedElement;
                Assert.False(now is SidebarGroupCaption || h.Navigation.MenuItems.OfType<SidebarGroupCaption>().Any(c => c.IsAncestorOf(now)),
                    $"Tab step {step} stopped on a caption.");
                if (now is NavigationViewItem item && order.Contains(item) && !visited.Contains(item)) visited.Add(item);
                if (ReferenceEquals(now, focused) || !h.List.IsAncestorOf(now)) break;
            }
            var indexes = visited.Select(i => order.IndexOf(i)).ToList();
            Assert.Equal(indexes.Order(), indexes);
        });
    }

    [WindowFact]
    public void AltUpAndAltDownStopAtTheEdgesOfTheGroup()
    {
        WithSidebar(h =>
        {
            var window = h.Presenter.ItemFor("w");
            Assert.False(h.Presenter.HandleMoveKey(window, Key.Up, ModifierKeys.Alt));
            Assert.False(h.Presenter.HandleMoveKey(h.Presenter.ItemFor("b"), Key.Down, ModifierKeys.Alt));
            Assert.False(h.Presenter.HandleMoveKey(h.Presenter.ItemFor("a"), Key.Up, ModifierKeys.Alt));
            Assert.Equal(["[main]", "home", "a", "b", "[more]", "w", "t", "x"], h.Menu());

            Assert.True(h.Presenter.HandleMoveKey(window, Key.Down, ModifierKeys.Alt));
            Assert.Equal(["[main]", "home", "a", "b", "[more]", "t", "w", "x"], h.Menu());
            Assert.True(h.Presenter.HandleMoveKey(window, Key.Down, ModifierKeys.Alt));
            Assert.False(h.Presenter.HandleMoveKey(window, Key.Down, ModifierKeys.Alt));
            Assert.Equal(["[main]", "home", "a", "b", "[more]", "t", "x", "w"], h.Menu());
        });
    }

    [WindowFact]
    public void ADragIntoAnotherGroupDropsAtTheNearestPlaceInsideItsOwnGroup()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("x"), h.Center("x"));
            drag.MoveTo(h.Top("a"));
            Assert.True(drag.IsDragging);
            Assert.Equal(3, drag.InsertionIndex);
            drag.Release(h.Top("a"));
            Assert.Equal(["home", "a", "b", "x", "w", "t"], SidebarFixtures.Ids(h.Service.Items));

            drag.Press(h.Presenter.ItemFor("a"), h.Center("a"));
            drag.MoveTo(h.Bottom("t"));
            Assert.Equal(3, drag.InsertionIndex);
            drag.Release(h.Bottom("t"));
            Assert.Equal(["home", "b", "a", "x", "w", "t"], SidebarFixtures.Ids(h.Service.Items));
            Assert.Equal(["[main]", "home", "b", "a", "[more]", "x", "w", "t"], h.Menu());
        });
    }

    [WindowFact]
    public void TheDropMarkerAtTheEndOfAGroupSitsAboveTheNextCaption()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor("a"), h.Center("a"));
            drag.MoveTo(h.Bottom("x"));
            Assert.True(drag.MarkerShown);
            var bravo = h.Presenter.ItemFor("b");
            var bravoBottom = bravo.TranslatePoint(new Point(0, bravo.ActualHeight), h.List).Y;
            var captionTop = h.Presenter.CaptionFor("more").TranslatePoint(new Point(0, 0), h.List).Y;
            Assert.InRange(drag.MarkerY!.Value, bravoBottom - 0.5, captionTop + 0.5);
            drag.Cancel();
        });
    }

    [WindowFact]
    public void TheEntryMenuNamesTheEntryAndKeepsShowAndReset()
    {
        WithSidebar(h =>
        {
            Assert.Equal(["Hide Xray", "Show tool", "Reset sidebar"], Headers(h.Presenter.ItemFor("x").ContextMenu!));
            Assert.Equal(["Show tool", "Reset sidebar"], Headers(h.Presenter.PaneMenu!));
            h.Service.Reset();
            Assert.Equal(["[main]", "home", "a", "b", "[more]", "w", "t", "x"], h.Menu());
        }, layout: new SidebarLayout { Order = [], Hidden = ["w"] });
    }

    [WindowFact]
    public void AClosedPaneShowsADividerInPlaceOfEachCaption()
    {
        WithSidebar(h =>
        {
            var caption = h.Presenter.CaptionFor("more");
            Assert.True(caption.Text.IsVisible);
            Assert.False(caption.Divider.IsVisible);

            h.Navigation.IsPaneOpen = false;
            h.Window.UpdateLayout();
            StaTestHelper.Flush();
            Assert.False(caption.IsPaneOpen);
            Assert.False(caption.Text.IsVisible);
            Assert.True(caption.Divider.IsVisible);
            Assert.True(caption.Divider.ActualHeight > 0);

            h.Navigation.IsPaneOpen = true;
            h.Window.UpdateLayout();
            Assert.True(caption.Text.IsVisible);
            Assert.False(caption.Divider.IsVisible);
        });
    }

    [WindowFact]
    public void TheDefaultSidebarShowsTheFirstGroupWithoutACaptionThenTheToolsCaption()
    {
        WithSidebar(h =>
        {
            Assert.Equal(
            [
                SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench,
                "[tools]", SidebarIds.InstallerAnalysis, SidebarIds.DetectionTester, SidebarIds.DeploymentHelper, SidebarIds.PivotsScripts,
                SidebarIds.CollectionCompliance, SidebarIds.MaintenanceWindows, SidebarIds.DpContentManager, SidebarIds.SiteHygiene
            ], h.Menu());
            Assert.Equal("TOOLS", h.Presenter.CaptionFor(SidebarGroupIds.Tools).Text.Text);
            Assert.Equal("Tools group", AutomationProperties.GetName(h.Presenter.CaptionFor(SidebarGroupIds.Tools)));

            var packager = h.Presenter.ItemFor(SidebarIds.Packages);
            Assert.Equal("App Packager", packager.Content);
            Assert.Equal("App Packager", packager.ToolTip);
            Assert.Equal("App Packager", AutomationProperties.GetName(packager));
            Assert.Equal("nav-packages", AutomationProperties.GetAutomationId(packager));
            Assert.Equal(["Reset sidebar"], Headers(packager.ContextMenu!));

            var workbench = h.Presenter.ItemFor(SidebarIds.Workbench);
            Assert.Equal("Workbench", workbench.Content);
            Assert.Equal("Workbench", workbench.ToolTip);
            Assert.Equal("Workbench", AutomationProperties.GetName(workbench));
            Assert.Equal(["Hide Workbench", "Reset sidebar"], Headers(workbench.ContextMenu!));

            var web = h.Presenter.ItemFor(SidebarIds.AddApp);
            Assert.Equal("Add from web", web.Content);
            Assert.Equal("Add from web", AutomationProperties.GetName(web));
            Assert.Equal(["Hide Add from web", "Reset sidebar"], Headers(web.ContextMenu!));
            var file = h.Presenter.ItemFor(SidebarIds.AddFile);
            Assert.Equal("Add from file", file.ToolTip);
            Assert.Equal("nav-addfile", AutomationProperties.GetAutomationId(file));

            Assert.False(h.Presenter.HandleMoveKey(workbench, Key.Down, ModifierKeys.Alt));
            Assert.False(h.Presenter.HandleMoveKey(web, Key.Up, ModifierKeys.Alt));
            Assert.False(h.Presenter.HandleMoveKey(h.Presenter.ItemFor(SidebarIds.InstallerAnalysis), Key.Up, ModifierKeys.Alt));
            Assert.True(h.Presenter.HandleMoveKey(web, Key.Down, ModifierKeys.Alt));
            Assert.True(h.Presenter.HandleMoveKey(web, Key.Down, ModifierKeys.Alt));
            Assert.False(h.Presenter.HandleMoveKey(web, Key.Down, ModifierKeys.Alt));
            Assert.Equal([SidebarIds.Packages, SidebarIds.AddFile, SidebarIds.Workbench, SidebarIds.AddApp, "[tools]"], h.Menu().Take(5));

            Assert.True(h.Service.Hide(SidebarIds.AddFile));
            Assert.True(h.Service.Hide(SidebarIds.Workbench));
            Assert.True(h.Service.Hide(SidebarIds.AddApp));
            Assert.Equal([SidebarIds.Packages, "[tools]"], h.Menu().Take(2));
            h.Service.Reset();
            Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, "[tools]", SidebarIds.InstallerAnalysis], h.Menu().Take(6));
        }, registry: SidebarRegistry.CreateDefault());
    }

    [WindowFact]
    public void ADefaultSidebarDragOfAToolAboveTheWorkbenchStaysFirstInTheTools()
    {
        WithSidebar(h =>
        {
            var drag = h.Presenter.Drag!;
            drag.Press(h.Presenter.ItemFor(SidebarIds.SiteHygiene), h.Center(SidebarIds.SiteHygiene));
            drag.MoveTo(h.Top(SidebarIds.Workbench));
            Assert.Equal(4, drag.InsertionIndex);
            drag.Release(h.Top(SidebarIds.Workbench));
            Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.AddFile, SidebarIds.Workbench, "[tools]", SidebarIds.SiteHygiene], h.Menu().Take(6));

            drag.Press(h.Presenter.ItemFor(SidebarIds.Workbench), h.Center(SidebarIds.Workbench));
            drag.MoveTo(h.Top(SidebarIds.AddApp));
            Assert.Equal(1, drag.InsertionIndex);
            drag.Release(h.Top(SidebarIds.AddApp));
            Assert.Equal([SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.AddApp, SidebarIds.AddFile, "[tools]"], h.Menu().Take(5));

            drag.Press(h.Presenter.ItemFor(SidebarIds.AddFile), h.Center(SidebarIds.AddFile));
            drag.MoveTo(h.Bottom(SidebarIds.SiteHygiene));
            Assert.Equal(4, drag.InsertionIndex);
            drag.Release(h.Bottom(SidebarIds.SiteHygiene));
            Assert.Equal([SidebarIds.Packages, SidebarIds.Workbench, SidebarIds.AddApp, SidebarIds.AddFile, "[tools]"], h.Menu().Take(5));
        }, registry: SidebarRegistry.CreateDefault());
    }
}
