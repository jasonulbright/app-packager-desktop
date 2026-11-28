using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.AddFlow;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerEngine.Tests.Wsus;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DatePicker = System.Windows.Controls.DatePicker;
using Slider = System.Windows.Controls.Slider;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The search of Settings, over the real page with its real view model: every field of every page is a result, a result opens its page with the
/// keyboard focus on the field, and a search changes no setting.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-settings-search-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable);

    public SettingsSearchTests()
    {
        foreach (var folder in new[] { "", "packagers", "staging" }) Directory.CreateDirectory(Path.Combine(_root, folder));
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _savedRoot);
        TestCleanup.DeleteDirectory(_root);
    }

    private SettingsViewModel CreateViewModel()
    {
        var service = new SettingsService();
        service.Save(new AppSettings
        {
            CompanyName = "Contoso", SiteCode = "", SiteCodeSaved = true, Theme = "Dark", StagingRootMigrated = true,
            PackagersRoot = Path.Combine(_root, "packagers"), DownloadRoot = Path.Combine(_root, "staging"),
            Systems = new SystemsInUse { ConfigMgr = true }
        });
        var packages = (PackagesViewModel)RuntimeHelpers.GetUninitializedObject(typeof(PackagesViewModel));
        packages.Packagers = [];
        // The Discover buttons of the page bind to commands that need the whole App Packager page.
        foreach (var field in new[] { "_discoverCommand", "_discoverAllCommand" })
            typeof(PackagesViewModel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(packages, new AsyncRelayCommand(() => Task.CompletedTask));
        var discovery = new PackagerDiscoveryService(service);
        var viewModel = new SettingsViewModel(service, new PackagerPreferencesService(service, discovery), new FakeSnackbar(), packages, discovery,
            new FakeWsusAdminClient(), new FakeWsusPrompts(), new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(null)),
            gitHubQuery: () => new TaskCompletionSource<GitHubApiStatus>().Task)
        { RefreshPublishState = () => { } };
        StaTestHelper.PumpUntil(() => viewModel.InstallParameterSummary.Length > 0);
        return viewModel;
    }

    private sealed record Host(Window Window, SettingsPage Page, SettingsViewModel ViewModel)
    {
        public T Named<T>(string name) where T : class => (T)Page.FindName(name);
        public TextBox Box => Named<TextBox>("txtSettingsSearch");
        public ListBox Results => Named<ListBox>("lstSettingsSearch");
        public TreeView Tree => Named<TreeView>("OptionsTree");
        public TextBlock Count => Named<TextBlock>("txtSettingsSearchCount");

        public List<ListBoxItem> Search(string query)
        {
            // The box keeps its text after a result opens; the same text again lists nothing new.
            Box.Text = "";
            Box.Text = query;
            Settle(Window);
            return Results.Items.OfType<ListBoxItem>().ToList();
        }
    }

    private void WithPage(ApplicationTheme theme, Action<Host> test) => PageHeaderRenderTests.With("Settings", theme, () =>
    {
        var viewModel = CreateViewModel();
        var page = new SettingsPage(viewModel);
        page.SelectPage("general");
        var window = new Window { Width = 1690, Height = 984, WindowStyle = WindowStyle.None, Content = page, Left = -32000, Top = -32000, ShowInTaskbar = false };
        window.Show();
        try
        {
            window.Activate();
            Settle(window);
            Settle(window);
            test(new Host(window, page, viewModel));
        }
        finally { window.Close(); }
    });

    private static List<string> PageIds(Host host)
    {
        IEnumerable<string> Tags(ItemCollection items) => items.OfType<TreeViewItem>()
            .SelectMany(i => (i.Tag is string tag ? new[] { tag } : []).Concat(Tags(i.Items)));
        return Tags(host.Tree.Items).ToList();
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        // The logical tree holds the parts that a hidden section has not drawn; the visual tree holds the rows of a list.
        var seen = new HashSet<DependencyObject>();
        var queue = new Queue<DependencyObject>([root]);
        while (queue.TryDequeue(out var element))
        {
            var children = LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>();
            if (element is Visual)
                children = children.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element)).Select(i => VisualTreeHelper.GetChild(element, i)));
            foreach (var child in children)
            {
                if (!seen.Add(child)) continue;
                yield return child;
                if (!IsInput(child)) queue.Enqueue(child);
            }
        }
    }

    private static bool IsInput(DependencyObject element) =>
        element is TextBox or System.Windows.Controls.PasswordBox or ComboBox or CheckBox or System.Windows.Controls.RadioButton or Wpf.Ui.Controls.ToggleSwitch or DataGrid;

    private static bool IsOrIsInside(DependencyObject element, HashSet<DependencyObject> targets, DependencyObject page)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, page);
             current = LogicalTreeHelper.GetParent(current) ?? (current is Visual ? VisualTreeHelper.GetParent(current) : null))
            if (targets.Contains(current)) return true;
        return false;
    }

    [Fact]
    public void EveryInputOfEveryPageIsAResultOfTheSearch() => WithPage(ApplicationTheme.Light, host =>
    {
        var index = host.Page.BuildSearchIndex();
        var missing = new List<string>();
        var checkedInputs = new Dictionary<string, int>();
        // The parameters come from the definitions; with no card the walk of their page would prove nothing.
        var groups = host.ViewModel.InstallParameterGroups.Count;
        var parameters = host.ViewModel.InstallParameterGroups.Sum(g => g.Parameters.Count);
        Assert.True(groups > 0 && parameters >= groups, "No definition gave an install parameter.");
        Assert.Equal(parameters, index.Count(e => e.PageId == "installparams" && e.Locate is not null && e.Path.Split(" > ").Length == 3));
        foreach (var id in PageIds(host))
        {
            Assert.Contains(index, e => e.PageId == id && e.Locate is null);
            host.Page.SelectPage(id);
            Settle(host.Window);
            var page = (FrameworkElement)host.Page.FindName("page_" + id);
            foreach (var card in Descendants(page).OfType<Wpf.Ui.Controls.CardExpander>().ToList()) card.IsExpanded = true;
            Settle(host.Window);
            Settle(host.Window);
            if (id == "installparams")
                Assert.Equal(groups, Descendants(page).OfType<Wpf.Ui.Controls.CardExpander>().Count(c => c.Content is InstallerSettingsPanel));

            var targets = new HashSet<DependencyObject>();
            foreach (var entry in index.Where(e => e.PageId == id && e.Locate is not null))
            {
                var target = entry.Locate!();
                if (target is null) missing.Add($"{id}: the result '{entry.Title}' has no control");
                else targets.Add(target);
            }
            Settle(host.Window);

            // A part of a control template belongs to its control and holds no setting.
            var inputs = Tree(page).Where(e => IsInput(e) && ((FrameworkElement)e).TemplatedParent is not System.Windows.Controls.Control).ToList();
            checkedInputs[id] = inputs.Count;
            missing.AddRange(inputs.Where(e => !IsOrIsInside(e, targets, page))
                .Select(e => $"{id}: {e.GetType().Name} '{AutomationProperties.GetAutomationId(e)}' '{AutomationProperties.GetName(e)}' '{(e as ContentControl)?.Content}'"));
        }

        Assert.True(missing.Count == 0, "The settings search does not hold these:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
        // At least one input for each parameter, beside the check box of the page.
        Assert.True(checkedInputs["installparams"] > parameters, $"The walk found {checkedInputs["installparams"]} inputs for {parameters} parameters.");
        // The walk reaches the pages that hold their fields in rows and in code.
        Assert.All(new[] { "sidebar", "configmgr", "wsus", "installparams", "localsources", "packagerfiles", "supersedence", "oneclick" },
            id => Assert.True(checkedInputs[id] > 0, id + " showed no input"));
    });

    /// <summary>Every logical and visual part under an element: closed sections, list rows, grid cells and the parts of templates.</summary>
    private static IEnumerable<DependencyObject> AllParts(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var queue = new Queue<DependencyObject>([root]);
        while (queue.TryDequeue(out var element))
        {
            var children = LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>();
            if (element is Visual)
                children = children.Concat(Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element)).Select(i => VisualTreeHelper.GetChild(element, i)));
            foreach (var child in children)
            {
                if (!seen.Add(child)) continue;
                yield return child;
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>An input or a button that the Tab key can reach. A part of a template counts only while it shows.</summary>
    private static bool TakesTheKeyboard(System.Windows.Controls.Control control) =>
        control is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox or ComboBox or ButtonBase or Slider or DatePicker or DataGrid
        && control.Focusable && control.IsTabStop && (control.TemplatedParent is not System.Windows.Controls.Control || control.IsVisible);

    [Fact]
    public void EveryInputAndButtonOfEveryPageHasAnAutomationName() => WithPage(ApplicationTheme.Light, host =>
    {
        // A row in each grid and every column of the application grid, so the walk reaches each kind of cell editor.
        host.ViewModel.SupersedenceRows.Add(ApplicationSettingsGridRenderTests.Row("one"));
        host.ViewModel.OneClickRows.Add(new OneClickRow { Uid = "one", Application = "Contoso one", Vendor = "Contoso" });
        foreach (var column in ApplicationColumns.All) host.ViewModel.SetColumnVisible(column.Id, true);
        typeof(SettingsPage).GetMethod("BuildApplicationColumns", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Page, null);

        var unnamed = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var walked = new Dictionary<string, int>();
        var seen = new HashSet<DependencyObject>();
        void Check(string id, DependencyObject root)
        {
            walked[id] = 0;
            foreach (var control in AllParts(root).OfType<System.Windows.Controls.Control>().Where(TakesTheKeyboard))
            {
                if (!seen.Add(control)) continue;
                walked[id]++;
                var name = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(control)?.GetName() ?? "";
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                else unnamed.Add($"{id}: {control.GetType().Name} id '{AutomationProperties.GetAutomationId(control)}' part '{control.Name}' " +
                    $"of '{control.TemplatedParent?.GetType().Name}' text '{(control as TextBox)?.Text}' tip '{control.ToolTip}'");
            }
        }

        foreach (var id in PageIds(host))
        {
            host.Page.SelectPage(id);
            Settle(host.Window);
            var page = (FrameworkElement)host.Page.FindName("page_" + id);
            foreach (var card in Descendants(page).OfType<Wpf.Ui.Controls.CardExpander>().ToList()) card.IsExpanded = true;
            Settle(host.Window);
            Settle(host.Window);
            foreach (var grid in AllParts(page).OfType<DataGrid>())
                Assert.All(grid.Columns, c => Assert.True(c is DataGridTemplateColumn || c.IsReadOnly || grid.IsReadOnly,
                    $"{id}: the column '{c.Header}' makes an editor that this walk does not see."));
            Check(id, page);
        }
        Check("(list of pages and buttons)", host.Page);

        Assert.True(unnamed.Count == 0, "These take the keyboard and have no name:" + Environment.NewLine + string.Join(Environment.NewLine, unnamed));
        // The walk reached the rows of the lists and the cell editors of both grids.
        Assert.All(new[] { "sidebar", "installparams", "localsources", "packagerfiles", "oneclick", "supersedence" },
            id => Assert.True(walked[id] > 0, id + " showed no input"));
        Assert.Contains("Company Name", names);
        Assert.Contains("Cadence (days)", names);
        Assert.Contains("Architecture of the application", names);
    });

    [Fact]
    public void TheGridResultHoldsTheNameOfEveryColumnAlsoOfAHiddenOne() => WithPage(ApplicationTheme.Light, host =>
    {
        var grid = host.Page.BuildSearchIndex().Single(e => e.PageId == "supersedence" && e.Title == "Application Overrides");
        foreach (var column in ApplicationColumns.All)
            Assert.Contains(grid, SettingsSearch.Matches([grid], column.Header));
        var tracked = host.Page.BuildSearchIndex().Single(e => e.PageId == "oneclick" && e.Title == "Tracked applications");
        Assert.Contains(tracked, SettingsSearch.Matches([tracked], "cadence days"));
    });

    [Theory]
    [InlineData("use ssl", "wsus", "Use SSL", "Connections > WSUS")]
    [InlineData("client secret", "intune", "Client Secret", "Connections > Intune")]
    [InlineData("not offered", "configmgr", "Applications hidden from Discover", "Connections > ConfigMgr")]
    [InlineData("company", "general", "Company Name", "General")]
    [InlineData("timestamp server", "signing", "Timestamp server", "Security > Script Signing")]
    [InlineData("citrix store url", "citrix", "Store URL (passed as typed)", "Applications > Citrix Workspace App")]
    [InlineData("win32 content", "tools", "Win32 Content Prep Tool", "Tools")]
    public void AFieldIsFoundByItsNameWithThePathOfItsPage(string query, string id, string title, string path) => WithPage(ApplicationTheme.Light, host =>
    {
        var first = (SettingsSearchEntry)host.Search(query)[0].Tag;

        Assert.Equal((id, title, path), (first.PageId, first.Title, first.Path));
        // The name of a result whose control is off also says why it is off.
        var reason = first.Unavailable?.Invoke();
        Assert.Equal(title == "Applications hidden from Discover", reason is not null);
        Assert.Equal($"{title}   -   {path}" + (reason is null ? "" : ". " + reason), AutomationProperties.GetName(host.Results.Items.OfType<ListBoxItem>().First()));
    });

    [Fact]
    public void TheRulesAreThoseOfThePowerShellApp()
    {
        SettingsSearchEntry Field(string title, string keywords = "") =>
            new() { PageId = "p", Title = title, Path = "Group > Page", Keywords = keywords, Locate = () => null };
        var index = new[] { Field("Alpha port"), Field("Port of the server"), Field("Port"), Field("Zeta", "port"), Field("Beta", "port") };

        Assert.Equal(["Port", "Port of the server", "Alpha port", "Beta", "Zeta"], SettingsSearch.Matches(index, "  PORT ").Select(e => e.Title));
        // Every word must be in the text; the name of the page counts.
        Assert.Equal(["Port of the server"], SettingsSearch.Matches(index, "server group").Select(e => e.Title));
        Assert.Empty(SettingsSearch.Matches(index, "port nothing"));
        Assert.Empty(SettingsSearch.Matches(index, "   "));
        Assert.Equal(40, SettingsSearch.Limit);
        Assert.Equal("No setting found.", SettingsSearch.NoResultText);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheResultsTakeThePlaceOfTheTreeAndStateTheirCount(ApplicationTheme theme) => WithPage(theme, host =>
    {
        Assert.Equal("Search settings", AutomationProperties.GetName(host.Box));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(host.Count));

        var one = host.Search("client secret");
        Assert.Equal("1 result", host.Count.Text);
        Assert.Equal("settingsResult0", AutomationProperties.GetAutomationId(Assert.Single(one)));
        Assert.True(host.Results.IsVisible);
        Assert.False(host.Tree.IsVisible);
        var text = Descendants(host.Results).OfType<TextBlock>().First(t => t.IsVisible && t.Text.StartsWith("Client Secret"));
        Assert.True(TextContrast(Pixels(host.Window, text)) >= 4.5, "The result text is not readable.");

        var many = host.Search("e");
        Assert.Equal(SettingsSearch.Limit, many.Count);
        Assert.StartsWith("First 40 of ", host.Count.Text);

        var none = Assert.Single(host.Search("zzzz no such setting"));
        Assert.Equal(SettingsSearch.NoResultText, none.Content);
        Assert.False(none.IsEnabled);
        Assert.Equal("No results", host.Count.Text);
        Assert.False(host.Page.HandleSearchBoxKey(Key.Enter));
        Assert.False(host.Page.HandleSearchBoxKey(Key.Down));

        Assert.True(host.Page.HandleSearchBoxKey(Key.Escape));
        Settle(host.Window);
        Assert.Equal("", host.Box.Text);
        Assert.True(host.Tree.IsVisible);
        Assert.False(host.Results.IsVisible);
        Assert.False(host.Count.IsVisible);

        host.Search("client");
        Assert.True(host.Page.HandleSearchBoxKey(Key.Down));
        Settle(host.Window);
        Assert.True(host.Results.IsKeyboardFocusWithin, "Down does not go to the results.");
        Assert.True(host.Page.HandleSearchListKey(Key.Escape));
        Settle(host.Window);
        Assert.Equal("", host.Box.Text);
        Assert.True(host.Box.IsKeyboardFocusWithin, "Escape in the results does not go back to the box.");

        host.Search("   ");
        Assert.True(host.Tree.IsVisible);
    });

    [Theory]
    [InlineData(ApplicationTheme.Light, "use ssl", "wsus", "Use SSL")]
    [InlineData(ApplicationTheme.Dark, "client secret", "intune", "Client Secret")]
    [InlineData(ApplicationTheme.Light, "timestamp required", "signing", "Timestamp required")]
    [InlineData(ApplicationTheme.Dark, "company", "general", "Company Name")]
    [InlineData(ApplicationTheme.Light, "application overrides", "supersedence", "Application Overrides")]
    public void EnterOpensTheFirstResultWithTheFocusOnItsField(ApplicationTheme theme, string query, string id, string title) => WithPage(theme, host =>
    {
        var entry = (SettingsSearchEntry)host.Search(query)[0].Tag;
        Assert.Equal((id, title), (entry.PageId, entry.Title));

        Assert.True(host.Page.HandleSearchBoxKey(Key.Enter));

        AssertOpenedAndFocused(host, entry);
        Assert.Equal(query, host.Box.Text);
    });

    [Theory]
    [InlineData("installparams")]
    [InlineData("localsources")]
    [InlineData("packagerfiles")]
    [InlineData("sidebar")]
    public void ARowOfAPageThatWasNeverShownOpensWithTheFocusOnIt(string id) => WithPage(ApplicationTheme.Light, host =>
    {
        // The last result of the page is the farthest row: a parameter of a card that is closed, or a row below the fold.
        var wanted = host.Page.BuildSearchIndex().Last(e => e.PageId == id && e.Locate is not null && e.Unavailable() is null);
        var items = host.Search(wanted.Title + " " + wanted.Path.Split(" > ")[^1]);
        var item = items.First(i => i.Tag is SettingsSearchEntry e && e.PageId == id && e.Title == wanted.Title && e.Path == wanted.Path);
        host.Results.SelectedItem = item;

        Assert.True(host.Page.HandleSearchListKey(Key.Enter));

        AssertOpenedAndFocused(host, (SettingsSearchEntry)item.Tag);
    });

    [Fact]
    public void AResultOfAPageOpensThePage() => WithPage(ApplicationTheme.Light, host =>
    {
        var page = host.Search("deployment type defaults").Select(i => (SettingsSearchEntry)i.Tag).First(e => e.Locate is null);
        host.Page.OpenSearchResult(page);
        Settle(host.Window);

        Assert.True(((FrameworkElement)host.Page.FindName("page_dtdefaults")).IsVisible);
        Assert.Equal("dtdefaults", (string)((TreeViewItem)host.Tree.SelectedItem).Tag);
        Assert.True(host.Tree.IsKeyboardFocusWithin, "The tree has no focus.");
    });

    [Fact]
    public void ADisabledFieldSaysWhyAndTheFocusGoesToTheSettingBeforeIt() => WithPage(ApplicationTheme.Light, host =>
    {
        const string Why = "The box is available when Distribute content after Package is selected.";
        var item = host.Search("distribution point group post packaging").First(i => ((SettingsSearchEntry)i.Tag).Title == "Distribution point group");
        Assert.Contains(Why, AutomationProperties.GetName(item));

        host.Page.OpenSearchResult((SettingsSearchEntry)item.Tag);
        StaTestHelper.PumpUntil(() => Keyboard.FocusedElement is CheckBox { Content: "Distribute content after Package" }, TimeSpan.FromSeconds(10));

        host.ViewModel.AutoDistribute = true;
        var enabled = host.Search("distribution point group post packaging").First(i => ((SettingsSearchEntry)i.Tag).Title == "Distribution point group");
        Assert.DoesNotContain(Why, AutomationProperties.GetName(enabled));
    });

    [Fact]
    public void ASearchAndTheOpeningOfItsResultsChangeNoSetting() => WithPage(ApplicationTheme.Light, host =>
    {
        Assert.False(host.ViewModel.HasUnsavedChanges, string.Join(", ", host.ViewModel.ChangedEntries()));
        var index = host.Page.BuildSearchIndex();
        foreach (var id in new[] { "dtdefaults", "installparams", "oneclick", "supersedence", "citrixpolicy", "m365", "intune" })
        {
            var entry = index.First(e => e.PageId == id && e.Locate is not null);
            host.Search(entry.Title);
            host.Page.OpenSearchResult(entry);
            AssertOpenedAndFocused(host, entry);
        }

        host.Box.Focus();
        host.Box.Text = "";
        Settle(host.Window);
        Settle(host.Window);

        host.ViewModel.RefreshDirtyState();
        Assert.False(host.ViewModel.HasUnsavedChanges, string.Join(", ", host.ViewModel.ChangedEntries()));
    });

    private static void Open(Host host, SettingsSearchEntry entry)
    {
        host.Page.ShowSearch();
        host.Page.OpenSearchResult(entry);
        StaTestHelper.PumpUntil(() => Keyboard.FocusedElement is { } focus && !ReferenceEquals(focus, host.Box), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ThePinnedSidebarRowPutsTheFocusOnItsPageInTheTreeAndNotOnReset() => WithPage(ApplicationTheme.Light, host =>
    {
        var pinned = host.Page.BuildSearchIndex().First(e => e.PageId == "sidebar" && e.Locate is not null && e.Unavailable() is not null);

        Open(host, pinned);

        var focus = (DependencyObject)Keyboard.FocusedElement;
        Assert.NotEqual("btnResetSidebar", AutomationProperties.GetAutomationId(focus));
        Assert.Same(host.Tree.SelectedItem, focus);
    });

    [Fact]
    public void NoResultPutsTheFocusOnAButtonThatIsNotTheResult() => WithPage(ApplicationTheme.Light, host =>
    {
        var wrong = new List<string>();
        foreach (var entry in host.Page.BuildSearchIndex())
        {
            Open(host, entry);
            var focus = (DependencyObject)Keyboard.FocusedElement;
            var target = entry.Locate?.Invoke();
            for (var current = focus; current is not null; current = LogicalTreeHelper.GetParent(current))
                if (SettingsSearchHints.GetExclude(current))
                    wrong.Add($"{entry.ResultText}: the focus is on a control that the search leaves out");
            if (focus is ButtonBase and not ToggleButton && !ReferenceEquals(focus, target))
                wrong.Add($"{entry.ResultText}: the focus is on the button '{(focus as ContentControl)?.Content}' '{AutomationProperties.GetName(focus)}'");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    });

    [Theory]
    [InlineData("win32 content", "Win32 Content Prep Tool")]
    public void AFieldNameWhoseOnlyButtonTheSearchLeavesOutKeepsTheFocus(string query, string title) => WithPage(ApplicationTheme.Light, host =>
    {
        var entry = (SettingsSearchEntry)host.Search(query)[0].Tag;
        Assert.Equal(title, entry.Title);

        Open(host, entry);

        Assert.Same(entry.Locate!(), Keyboard.FocusedElement);
    });

    [Fact]
    public void ARepeatOfTheEnterKeyThatOpenedAResultPressesNothing() => WithPage(ApplicationTheme.Light, host =>
    {
        host.Search("reset sidebar");
        Assert.False(host.Page.HandleRepeatedKey(Key.Enter, isRepeat: false));
        Assert.True(host.Page.HandleSearchBoxKey(Key.Enter));
        StaTestHelper.PumpUntil(() => Keyboard.FocusedElement is Wpf.Ui.Controls.Button b && AutomationProperties.GetAutomationId(b) == "btnResetSidebar",
            TimeSpan.FromSeconds(15));

        // The repeat of the held key goes through the page, which takes it before the focused button.
        var button = (UIElement)Keyboard.FocusedElement;
        var repeat = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, 0, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        typeof(System.Windows.Input.KeyEventArgs).GetMethod("SetRepeat", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(repeat, [true]);
        button.RaiseEvent(repeat);
        Assert.True(repeat.Handled, "A repeat of Enter reaches the focused button.");

        // A new press of the key is a press of the person.
        Assert.False(host.Page.HandleRepeatedKey(Key.Enter, isRepeat: false));
        Assert.False(host.Page.HandleRepeatedKey(Key.Enter, isRepeat: true));
    });

    [Fact]
    public void EnterAndDownListTheResultsAgainAfterAResultOpened() => WithPage(ApplicationTheme.Light, host =>
    {
        var entry = (SettingsSearchEntry)host.Search("client secret")[0].Tag;
        Open(host, entry);
        Assert.False(host.Results.IsVisible);

        host.Box.Focus();
        Assert.True(host.Page.HandleSearchBoxKey(Key.Enter));
        Settle(host.Window);
        Assert.True(host.Results.IsVisible, "Enter does not list the results again.");
        Assert.True(host.Box.IsKeyboardFocusWithin);
        Assert.Equal("1 result", host.Count.Text);

        Open(host, entry);
        host.Box.Focus();
        Assert.True(host.Page.HandleSearchBoxKey(Key.Down));
        Settle(host.Window);
        Assert.True(host.Results.IsVisible, "Down does not list the results again.");
        Assert.Same(host.Results.Items[0], Keyboard.FocusedElement);
    });

    private static void AssertOpenedAndFocused(Host host, SettingsSearchEntry entry)
    {
        var page = (FrameworkElement)host.Page.FindName("page_" + entry.PageId);
        FrameworkElement? target = null;
        bool Focused()
        {
            if (!page.IsVisible || (target = entry.Locate!()) is null || Keyboard.FocusedElement is not DependencyObject focus) return false;
            // The result of a field name puts the focus on its first control, or on the name when it has none.
            return IsOrIsInside(focus, [target is TextBlock ? page : target], host.Window);
        }

        double Top() => target!.TranslatePoint(new System.Windows.Point(0, 0), host.Window).Y;
        bool InView() => Top() >= 0 && Top() < host.Window.ActualHeight;

        StaTestHelper.PumpUntil(Focused, TimeSpan.FromSeconds(15));
        // A card that opens still grows after the focus moved, so the field comes into view shortly after.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        StaTestHelper.PumpUntil(() => InView() || DateTime.UtcNow > deadline, TimeSpan.FromSeconds(15));
        Settle(host.Window);
        Assert.Equal(entry.PageId, (string)((TreeViewItem)host.Tree.SelectedItem).Tag);
        Assert.True(host.Tree.IsVisible);
        Assert.False(host.Results.IsVisible);
        Assert.True(InView(), $"{entry.Title} is at {Top():0} in a window of {host.Window.ActualHeight:0}.");
    }
}
