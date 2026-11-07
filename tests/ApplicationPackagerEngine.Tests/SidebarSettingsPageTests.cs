using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using IInvokeProvider = System.Windows.Automation.Provider.IInvokeProvider;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Sidebar section of the compiled Settings page in a real window. The page runs its
/// generated InitializeComponent over the editor alone: the full Settings view model queries
/// the GitHub API when it is built.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SidebarSettingsPageTests
{
    private sealed class Harness
    {
        public required Window Window { get; init; }
        public required SettingsPage Page { get; init; }
        public required SidebarService Service { get; init; }
        public required SidebarSettingsEditor Editor { get; init; }
    }

    private static SettingsPage CompiledPage(object dataContext)
    {
        var page = (SettingsPage)RuntimeHelpers.GetUninitializedObject(typeof(SettingsPage));
        typeof(Page).GetConstructor(Type.EmptyTypes)!.Invoke(page, null);
        page.DataContext = dataContext;
        page.InitializeComponent();
        return page;
    }

    /// <summary>
    /// Runs the test with the theme and control dictionaries merged into an application
    /// object, as App.xaml merges them. A row container that a move takes out of the tree
    /// finds its implicit style only in the application resources; with the dictionaries on
    /// a window or the page it falls back to the system style while a template animation
    /// runs, and WPF throws. The application object is removed afterwards so no other test
    /// sees Application.Current.
    /// </summary>
    private static void WithPage(Action<Harness> test, SidebarLayout? layout = null, ApplicationTheme theme = ApplicationTheme.Dark, SidebarRegistry? registry = null)
    {
        StaTestHelper.Run(() =>
        {
            Assert.Null(Application.Current);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                application.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = theme });
                application.Resources.MergedDictionaries.Add(new ControlsDictionary());
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ApplicationPackagerEngine.Tests.Compliance.RenderKit.ToolTipStyles, UriKind.Absolute) });
                var service = new SidebarService(registry ?? SidebarFixtures.Registry(), new InMemorySidebarStore(layout));
                var editor = new SidebarSettingsEditor(service);
                var page = CompiledPage(new { Sidebar = editor });
                var window = new Window
                {
                    Width = 900, Height = 900, Left = -32000, Top = -32000, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = page
                };
                window.Show();
                try
                {
                    window.Activate();
                    window.UpdateLayout();
                    StaTestHelper.Flush();
                    test(new Harness { Window = window, Page = page, Service = service, Editor = editor });
                }
                finally { window.Close(); }
            }
            finally
            {
                application.Resources = new ResourceDictionary();
                var statics = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                typeof(Application).GetField("_appInstance", statics)!.SetValue(null, null);
                typeof(Application).GetField("_appCreatedInThisAppDomain", statics)!.SetValue(null, false);
            }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static ItemsControl SidebarList(Harness h) =>
        Descendants(h.Page).OfType<ItemsControl>().Single(c => AutomationProperties.GetAutomationId(c) == "lstSidebar");

    private static Wpf.Ui.Controls.Button RowButton(Harness h, string name) =>
        Descendants(SidebarList(h)).OfType<Wpf.Ui.Controls.Button>().Single(b => AutomationProperties.GetName(b) == name);

    private static void Press(System.Windows.Controls.Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        StaTestHelper.Flush();
    }

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSidebarSectionShowsOneRowPerEntryWithThePinnedRowLocked(ApplicationTheme theme)
    {
        WithPage(h =>
        {
            Assert.Contains(Descendants(h.Page).OfType<TextBlock>(), t => t.Text == "Sidebar");
            var boxes = Descendants(SidebarList(h)).OfType<CheckBox>().ToList();
            Assert.Equal(["Home", "Alpha", "Bravo", "Window", "Tool"], boxes.Select(b => (string)b.Content));
            Assert.False(boxes[0].IsEnabled);
            Assert.True(boxes[1].IsEnabled);
            Assert.Equal([true, true, false, true, true], boxes.Select(b => b.IsChecked == true));
            Assert.False(RowButton(h, "Move Home up").IsEnabled);
            Assert.False(RowButton(h, "Move Alpha up").IsEnabled);
            Assert.False(RowButton(h, "Move Tool down").IsEnabled);

            boxes[3].IsChecked = false;
            Assert.True(h.Service.IsHidden("w"));
            boxes[2].IsChecked = true;
            Assert.False(h.Service.IsHidden("b"));
        }, new SidebarLayout { Order = [], Hidden = ["b"] }, theme);
    }

    [WindowFact]
    public void TheButtonsOfARowMoveItAndResetRestoresTheDefault()
    {
        WithPage(h =>
        {
            Press(RowButton(h, "Move Tool up"));
            Assert.Equal(["home", "a", "b", "t", "w"], SidebarFixtures.Ids(h.Service.All));
            Press(RowButton(h, "Move Alpha down"));
            Assert.Equal(["home", "b", "a", "t", "w"], SidebarFixtures.Ids(h.Service.All));
            Assert.Equal(["Home", "Bravo", "Alpha", "Tool", "Window"], Descendants(SidebarList(h)).OfType<CheckBox>().Select(b => (string)b.Content));

            var reset = Descendants(h.Page).OfType<Wpf.Ui.Controls.Button>().Single(b => AutomationProperties.GetAutomationId(b) == "btnResetSidebar");
            Press(reset);
            Assert.Equal(["home", "a", "b", "w", "t"], SidebarFixtures.Ids(h.Service.All));
        });
    }

    [WindowTheory]
    [InlineData("Move Window up", "w", new[] { "home", "a", "w", "b", "t" })]
    [InlineData("Move Alpha down", "a", new[] { "home", "b", "a", "w", "t" })]
    [InlineData("Move Tool up", "t", new[] { "home", "a", "b", "t", "w" })]
    public void TheMovedRowKeepsKeyboardFocusOnTheButtonThatMovedIt(string name, string id, string[] expected)
    {
        WithPage(h =>
        {
            var button = RowButton(h, name);
            Keyboard.Focus(button);
            Assert.True(button.IsKeyboardFocused);

            Press(button);
            Assert.Equal(expected, SidebarFixtures.Ids(h.Service.All));
            var focused = Assert.IsAssignableFrom<FrameworkElement>(Keyboard.FocusedElement);
            Assert.Same(button, focused);
            Assert.Equal(id, ((SidebarSettingsRow)focused.DataContext).Id);
            Assert.Equal(name, AutomationProperties.GetName(focused));
        });
    }

    [WindowFact]
    public void UpStepsOverAHiddenRowAndKeepsFocus()
    {
        WithPage(h =>
        {
            var button = RowButton(h, "Move Tool up");
            Keyboard.Focus(button);
            Press(button);
            Assert.Equal(["home", "a", "t", "b", "w"], SidebarFixtures.Ids(h.Service.All));
            Assert.Equal(["home", "a", "t", "b"], SidebarFixtures.Ids(h.Service.Items));
            Assert.Same(button, Keyboard.FocusedElement);
        }, new SidebarLayout { Order = [], Hidden = ["w"] });
    }

    private static string[] ShownCaptions(Harness h) =>
        Descendants(SidebarList(h)).OfType<TextBlock>().Where(t => t.IsVisible && t.Text is "MAIN" or "MORE" or "TOOLS").Select(t => t.Text).ToArray();

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheSidebarSectionShowsEachGroupCaptionAboveItsRowsAndMovesStayInTheGroup(ApplicationTheme theme)
    {
        WithPage(h =>
        {
            Assert.Equal(["MAIN", "MORE"], ShownCaptions(h));
            var captions = Descendants(SidebarList(h)).OfType<TextBlock>().Where(t => t.IsVisible && t.Text is "MAIN" or "MORE").ToList();
            Assert.Equal(["Main group", "More group"], captions.Select(AutomationProperties.GetName));
            var boxes = Descendants(SidebarList(h)).OfType<CheckBox>().ToList();
            Assert.True(captions[1].TranslatePoint(new System.Windows.Point(0, 0), h.Page).Y >= boxes[2].TranslatePoint(new System.Windows.Point(0, boxes[2].ActualHeight), h.Page).Y);
            Assert.True(captions[1].TranslatePoint(new System.Windows.Point(0, captions[1].ActualHeight), h.Page).Y <= boxes[3].TranslatePoint(new System.Windows.Point(0, 0), h.Page).Y);

            Assert.False(RowButton(h, "Move Window up").IsEnabled);
            Assert.False(RowButton(h, "Move Bravo down").IsEnabled);
            Press(RowButton(h, "Move Tool up"));
            Assert.Equal(["home", "a", "b", "t", "w", "x"], SidebarFixtures.Ids(h.Service.All));
            Assert.Equal(["MAIN", "MORE"], ShownCaptions(h));
        }, theme: theme, registry: SidebarGroupFixtures.Registry());
    }

    [WindowFact]
    public void TheDefaultSidebarSectionShowsTheFirstGroupWithoutACaptionAndTheToolsUnderTheirCaption()
    {
        WithPage(h =>
        {
            Assert.Equal(["TOOLS"], ShownCaptions(h));
            var boxes = Descendants(SidebarList(h)).OfType<CheckBox>().ToList();
            Assert.Equal(["App Packager", "Add from web", "Add from file", "Workbench", "Installer Analysis", "Detection Tester", "Deployment Helper",
                "Pivots and Scripts", "Collection and Compliance", "Maintenance Windows", "DP Content Manager", "Site Hygiene"], boxes.Select(b => (string)b.Content));
            var tools = Descendants(SidebarList(h)).OfType<TextBlock>().Single(t => t.IsVisible && t.Text == "TOOLS");
            Assert.True(tools.TranslatePoint(new System.Windows.Point(0, 0), h.Page).Y >= boxes[3].TranslatePoint(new System.Windows.Point(0, boxes[3].ActualHeight), h.Page).Y);
            Assert.True(tools.TranslatePoint(new System.Windows.Point(0, tools.ActualHeight), h.Page).Y <= boxes[4].TranslatePoint(new System.Windows.Point(0, 0), h.Page).Y);
            Assert.False(RowButton(h, "Move Add from web up").IsEnabled);
            Assert.True(RowButton(h, "Move Add from web down").IsEnabled);
            Assert.False(RowButton(h, "Move Workbench down").IsEnabled);
            Assert.False(RowButton(h, "Move Installer Analysis up").IsEnabled);

            Press(RowButton(h, "Move Workbench up"));
            Assert.Equal([SidebarIds.Packages, SidebarIds.AddApp, SidebarIds.Workbench, SidebarIds.AddFile], SidebarFixtures.Ids(h.Service.All.Take(4)));
            boxes = Descendants(SidebarList(h)).OfType<CheckBox>().ToList();
            boxes[1].IsChecked = false;
            Assert.True(h.Service.IsHidden(SidebarIds.AddApp));
            Press(Descendants(h.Page).OfType<Wpf.Ui.Controls.Button>().Single(b => AutomationProperties.GetAutomationId(b) == "btnResetSidebar"));
            Assert.Equal(SidebarFixtures.Ids(SidebarRegistry.CreateDefault().Items), SidebarFixtures.Ids(h.Service.Items));
        }, registry: SidebarRegistry.CreateDefault());
    }

    [WindowFact]
    public void EveryIconOnTheSettingsPageIsADefinedSymbol()
    {
        WithPage(h =>
        {
            var icons = Descendants(h.Page).OfType<Wpf.Ui.Controls.Button>().Select(b => b.Icon).OfType<Wpf.Ui.Controls.SymbolIcon>().ToList();
            Assert.NotEmpty(icons);
            Assert.All(icons, i => Assert.True(Enum.IsDefined(i.Symbol), i.Symbol.ToString()));
            var reset = Descendants(h.Page).OfType<Wpf.Ui.Controls.Button>().Single(b => AutomationProperties.GetAutomationId(b) == "btnResetSidebar");
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.ArrowReset24, ((Wpf.Ui.Controls.SymbolIcon)reset.Icon!).Symbol);
            Assert.Contains(icons, i => i.Symbol == Wpf.Ui.Controls.SymbolRegular.ArrowUp24);
            Assert.Contains(icons, i => i.Symbol == Wpf.Ui.Controls.SymbolRegular.ArrowDown24);
        });
    }
}
