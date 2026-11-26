using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerUI.Helpers.SettingsSearchHints;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace ApplicationPackagerUI.Views.Pages;

/// <summary>
/// The search over every page and field of Settings. The index is read from the tree and the pages as they are, so a new field needs no list
/// entry. The rows of a list (Sidebar, Install Parameters, Local Installer Sources, Packager Files) come from the data of the list, because
/// their controls exist only after the page shows.
/// </summary>
public partial class SettingsPage
{
    private const int FocusAttempts = 20;
    private static readonly TimeSpan CardOpenTime = TimeSpan.FromMilliseconds(500);

    /// <summary>Every result that the search can give now.</summary>
    public IReadOnlyList<SettingsSearchEntry> BuildSearchIndex()
    {
        var index = new List<SettingsSearchEntry>();
        void Visit(ItemCollection items, string parentPath)
        {
            foreach (var node in items.OfType<TreeViewItem>())
            {
                var path = parentPath.Length == 0 ? (string)node.Header : $"{parentPath} > {node.Header}";
                if (node.Tag is string id && FindName("page_" + id) is FrameworkElement page) IndexPage(index, id, node, path, page);
                Visit(node.Items, path);
            }
        }
        Visit(OptionsTree.Items, "");
        return index;
    }

    private void IndexPage(List<SettingsSearchEntry> index, string id, TreeViewItem node, string path, FrameworkElement page)
    {
        var labelStyle = (Style)Resources["FieldLabel"];
        var headerStyle = (Style)Resources["SectionHeader"];
        var elements = LogicalDescendants(page).ToList();
        var heading = elements.OfType<TextBlock>().FirstOrDefault(t => ReferenceEquals(t.Style, headerStyle))?.Text;
        index.Add(new SettingsSearchEntry { PageId = id, Title = (string)node.Header, Path = path, Keywords = $"{heading} {GetKeywords(node)}" });

        void Add(FrameworkElement element, string title, string keywords = "") => index.Add(new SettingsSearchEntry
        {
            PageId = id, Title = title, Path = path, Keywords = $"{keywords} {GetKeywords(element)}",
            Locate = () => element, Unavailable = () => UnavailableReason(element, page)
        });

        var usedLabels = new HashSet<TextBlock>();
        foreach (var element in elements.OfType<FrameworkElement>())
        {
            var isField = IsField(element);
            if (!isField && element is ItemsControl { ItemsSource: not null } list && list is not (System.Windows.Controls.TreeView or ListBox))
            {
                IndexRows(index, id, path, list);
                continue;
            }

            var button = element is ButtonBase and not ToggleButton;
            if (!isField && !button) continue;
            if (GetExclude(element)) continue;

            var label = button || element is ToggleButton or DataGrid ? null : PrecedingLabel(element, page, labelStyle);
            var content = element is ButtonBase press ? press.Content as string : null;
            var title = FirstText(GetTitle(element), content, label?.Text, AutomationProperties.GetName(element), (element as Wpf.Ui.Controls.TextBox)?.PlaceholderText);
            if (title is null) continue;
            if (label is not null) usedLabels.Add(label);

            if (element is DataGrid grid)
            {
                // As in the PowerShell app, a grid is one result, found by the names of its columns, the hidden ones too.
                var columns = grid.Columns.Select(c => c.Header as string);
                if (ReferenceEquals(grid, dgApplications)) columns = columns.Concat(ApplicationColumns.All.Select(c => c.Header));
                Add(element, title, string.Join(' ', columns.Distinct()));
            }
            else Add(element, title);
        }

        // A field name that no input took is a result of its own: a status line, or a group of check boxes.
        foreach (var label in elements.OfType<TextBlock>().Where(t => ReferenceEquals(t.Style, labelStyle) && !usedLabels.Contains(t)))
            Add(label, FirstText(GetTitle(label), label.Text) ?? "");
    }

    private static void IndexRows(List<SettingsSearchEntry> index, string id, string path, ItemsControl list)
    {
        foreach (var item in list.ItemsSource!)
        {
            switch (item)
            {
                case SidebarSettingsRow row:
                    index.Add(new SettingsSearchEntry
                    {
                        PageId = id, Title = row.Title, Path = path, Keywords = "show hide order move",
                        Locate = () => Row(list, row) is { } container ? VisualDescendants(container).OfType<CheckBox>().FirstOrDefault() : null,
                        Unavailable = () => row.CanHide ? null : "The box is disabled for an entry that must stay visible."
                    });
                    break;
                case PathSettingRow row:
                    index.Add(new SettingsSearchEntry
                    {
                        PageId = id, Title = row.Label, Path = path, Keywords = "local source installer folder packager file",
                        Locate = () => Row(list, row) is { } container ? VisualDescendants(container).OfType<TextBox>().FirstOrDefault() : null
                    });
                    break;
                case InstallParameterGroup group:
                    foreach (var parameter in group.Parameters)
                    {
                        var key = parameter.Key;
                        index.Add(new SettingsSearchEntry
                        {
                            PageId = id, Title = parameter.Label, Path = $"{path} > {group.Application}", Keywords = $"{group.Vendor} {parameter.Group}",
                            Locate = () => ParameterControl(list, group, key)
                        });
                    }
                    break;
            }
        }
    }

    private static FrameworkElement? Row(ItemsControl list, object item)
    {
        list.UpdateLayout();
        return list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
    }

    /// <summary>Opens the card of the application and gives the control that the panel built for the parameter.</summary>
    private static FrameworkElement? ParameterControl(ItemsControl list, InstallParameterGroup group, string key)
    {
        if (Row(list, group) is not { } container || VisualDescendants(container).OfType<Wpf.Ui.Controls.CardExpander>().FirstOrDefault() is not { } card)
            return null;
        card.IsExpanded = true;
        card.UpdateLayout();
        return card.Content is InstallerSettingsPanel panel
            ? LogicalDescendants(panel).OfType<FrameworkElement>().FirstOrDefault(e => GetKey(e) == key)
            : null;
    }

    /// <summary>True for a control that holds a value of the person.</summary>
    public static bool IsField(DependencyObject element) =>
        element is TextBox or System.Windows.Controls.PasswordBox or ComboBox or ToggleButton or DataGrid;

    private static bool HoldsTypedValue(DependencyObject element) => element is TextBox or System.Windows.Controls.PasswordBox or ComboBox;

    /// <summary>The elements under a page in the order of the page. The parts of a field and of a button are not read.</summary>
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            if (IsField(child) || child is ButtonBase) continue;
            foreach (var nested in LogicalDescendants(child)) yield return nested;
        }
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in VisualDescendants(child)) yield return nested;
        }
    }

    /// <summary>The field name above an input. The walk stops at a heading or at another input, which owns the names before it.</summary>
    private TextBlock? PrecedingLabel(DependencyObject element, DependencyObject page, Style labelStyle)
    {
        var headings = new[] { Resources["SectionHeader"], Resources["SubHeader"] };
        bool Stops(DependencyObject e) => HoldsTypedValue(e) || e is TextBlock t && headings.Contains(t.Style);
        for (var current = element; !ReferenceEquals(current, page) && LogicalTreeHelper.GetParent(current) is { } parent; current = parent)
        {
            if (parent is not System.Windows.Controls.Panel panel || current is not UIElement child) continue;
            for (var i = panel.Children.IndexOf(child) - 1; i >= 0; i--)
            {
                var sibling = panel.Children[i];
                if (sibling is TextBlock text && ReferenceEquals(text.Style, labelStyle)) return text;
                if (Stops(sibling) || LogicalDescendants(sibling).Any(Stops)) return null;
            }
        }
        return null;
    }

    private static string? FirstText(params string?[] texts) => texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>True when the element or a part of the page that holds it is hidden by a setting. The page itself does not count.</summary>
    private static bool IsHiddenOnPage(DependencyObject element, DependencyObject page)
    {
        for (var current = element; current is not null && !ReferenceEquals(current, page); current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return true;
        return false;
    }

    private static string? UnavailableReason(FrameworkElement element, FrameworkElement page) =>
        GetUnavailable(element) is { } reason && (!element.IsEnabled || IsHiddenOnPage(element, page)) ? reason : null;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ShowSearchResults(txtSettingsSearch.Text);

    private void ShowSearchResults(string query)
    {
        lstSettingsSearch.Items.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowTree();
            return;
        }

        var matches = SettingsSearch.Matches(BuildSearchIndex(), query);
        var shown = matches.Take(SettingsSearch.Limit).ToList();
        for (var i = 0; i < shown.Count; i++) lstSettingsSearch.Items.Add(ResultItem(shown[i], i));
        if (shown.Count == 0)
        {
            var none = new ListBoxItem { Content = SettingsSearch.NoResultText, IsEnabled = false };
            AutomationProperties.SetAutomationId(none, "settingsResultNone");
            lstSettingsSearch.Items.Add(none);
        }

        OptionsTree.Visibility = Visibility.Collapsed;
        lstSettingsSearch.Visibility = Visibility.Visible;
        Announce(matches.Count switch
        {
            0 => "No results",
            1 => "1 result",
            > SettingsSearch.Limit => $"First {SettingsSearch.Limit} of {matches.Count} results",
            _ => $"{matches.Count} results"
        });
    }

    private static ListBoxItem ResultItem(SettingsSearchEntry entry, int number)
    {
        var reason = entry.Unavailable();
        var lines = new StackPanel();
        lines.Children.Add(new TextBlock { Text = entry.ResultText, TextWrapping = TextWrapping.Wrap });
        if (reason is not null)
        {
            var why = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            why.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            lines.Children.Add(why);
        }

        var item = new ListBoxItem { Content = lines, Tag = entry };
        AutomationProperties.SetName(item, reason is null ? entry.ResultText : $"{entry.ResultText}. {reason}");
        AutomationProperties.SetAutomationId(item, $"settingsResult{number}");
        return item;
    }

    private void Announce(string text)
    {
        txtSettingsSearchCount.Text = text;
        txtSettingsSearchCount.Visibility = Visibility.Visible;
        UIElementAutomationPeer.CreatePeerForElement(txtSettingsSearchCount)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ShowTree()
    {
        lstSettingsSearch.Visibility = Visibility.Collapsed;
        txtSettingsSearchCount.Visibility = Visibility.Collapsed;
        txtSettingsSearchCount.Text = "";
        OptionsTree.Visibility = Visibility.Visible;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e) => e.Handled = HandleSearchBoxKey(e.Key);

    /// <summary>Enter opens the first result, Down goes to the results and Escape clears the box. False for a key that the box keeps.</summary>
    public bool HandleSearchBoxKey(Key key)
    {
        var listed = lstSettingsSearch.Visibility == Visibility.Visible;
        var first = listed ? lstSettingsSearch.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SettingsSearchEntry) : null;
        if (key == Key.Enter && first is not null) OpenSearchResult((SettingsSearchEntry)first.Tag);
        else if (key == Key.Down && first is not null)
        {
            lstSettingsSearch.SelectedItem = first;
            lstSettingsSearch.UpdateLayout();
            first.Focus();
        }
        else if (key == Key.Escape && txtSettingsSearch.Text.Length > 0) txtSettingsSearch.Text = "";
        else return false;
        return true;
    }

    private void SearchList_PreviewKeyDown(object sender, KeyEventArgs e) => e.Handled = HandleSearchListKey(e.Key);

    /// <summary>Enter opens the selected result; Escape clears the search and goes back to the box.</summary>
    public bool HandleSearchListKey(Key key)
    {
        if (key == Key.Enter && lstSettingsSearch.SelectedItem is ListBoxItem { Tag: SettingsSearchEntry entry }) OpenSearchResult(entry);
        else if (key == Key.Escape)
        {
            txtSettingsSearch.Text = "";
            txtSettingsSearch.Focus();
        }
        else return false;
        return true;
    }

    private void SearchList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Only a click on a result; the scroll bar of the list opens nothing.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(lstSettingsSearch, source) is ListBoxItem { Tag: SettingsSearchEntry entry })
            OpenSearchResult(entry);
    }

    /// <summary>Shows the tree again, opens the page of the result, scrolls to its field and gives it the keyboard focus. The search text stays.</summary>
    public void OpenSearchResult(SettingsSearchEntry entry)
    {
        ShowTree();
        var node = Leaves(OptionsTree.Items).First(n => (string)n.Tag == entry.PageId);
        for (var parent = node.Parent as TreeViewItem; parent is not null; parent = parent.Parent as TreeViewItem) parent.IsExpanded = true;
        SelectPage(entry.PageId);
        UpdateLayout();
        node.BringIntoView();
        if (entry.Locate is null) node.Focus();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, () => FocusResult(entry, 1));
    }

    private void FocusResult(SettingsSearchEntry entry, int attempt)
    {
        // The person went to another page while a row was built.
        if (s_lastPage != entry.PageId) return;
        var page = (FrameworkElement)FindName("page_" + entry.PageId);
        UpdateLayout();
        if (entry.Locate!() is { } target)
        {
            target.UpdateLayout();
            if (IsHiddenOnPage(target, page) || !target.IsEnabled)
            {
                // A disabled or hidden field takes no focus: the page scrolls to it and the focus goes to the field before it,
                // which is where the setting that gates it is.
                ShownAncestor(target, page).BringIntoView();
                if (FocusableBefore(target, page) is { } before) before.Focus();
                else Leaves(OptionsTree.Items).First(n => (string)n.Tag == entry.PageId).Focus();
                return;
            }

            var element = target is TextBlock label ? FocusableAfter(label) ?? MakeFocusable(label) : FirstFocusable(target);
            if (element is not null)
            {
                element.Focus();
                element.BringIntoView();
                target.BringIntoView();
                if (element.IsKeyboardFocusWithin)
                {
                    // A card that opens still grows after the focus moved: the page scrolls again when it has its height.
                    After(CardOpenTime, () => { if (element.IsKeyboardFocusWithin) element.BringIntoView(); });
                    return;
                }
            }
        }

        if (attempt >= FocusAttempts) return;
        After(TimeSpan.FromMilliseconds(50), () => FocusResult(entry, attempt + 1));
    }

    private static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    private static bool TakesFocus(UIElement element) => element.Focusable && element.IsEnabled && element.IsVisible;

    private static bool IsFieldOrButton(DependencyObject element) => IsField(element) || element is ButtonBase;

    private static FrameworkElement? FirstFocusable(FrameworkElement target) =>
        IsFieldOrButton(target)
            ? TakesFocus(target) ? target : null
            : VisualDescendants(target).OfType<FrameworkElement>().FirstOrDefault(e => IsFieldOrButton(e) && TakesFocus(e));

    /// <summary>The first field or button that belongs to a field name: the ones beside it in its panel, before the next name.</summary>
    private FrameworkElement? FocusableAfter(TextBlock label)
    {
        if (label.Parent is not System.Windows.Controls.Panel panel) return null;
        var names = new[] { Resources["FieldLabel"], Resources["SubHeader"] };
        return panel.Children.OfType<FrameworkElement>().SkipWhile(e => !ReferenceEquals(e, label)).Skip(1)
            .TakeWhile(e => !(e is TextBlock t && names.Contains(t.Style)))
            .SelectMany(e => LogicalDescendants(e).OfType<FrameworkElement>().Prepend(e))
            .FirstOrDefault(e => IsFieldOrButton(e) && TakesFocus(e));
    }

    private static FrameworkElement MakeFocusable(TextBlock label)
    {
        label.Focusable = true;
        KeyboardNavigation.SetIsTabStop(label, false);
        return label;
    }

    private static FrameworkElement? FocusableBefore(FrameworkElement target, DependencyObject page) =>
        LogicalDescendants(page).TakeWhile(e => !ReferenceEquals(e, target))
            .OfType<FrameworkElement>().LastOrDefault(e => IsFieldOrButton(e) && TakesFocus(e));

    private static FrameworkElement ShownAncestor(FrameworkElement target, FrameworkElement page)
    {
        var current = target;
        while (!current.IsVisible && !ReferenceEquals(current, page) && LogicalTreeHelper.GetParent(current) is FrameworkElement parent) current = parent;
        return current;
    }
}
