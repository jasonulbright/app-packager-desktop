using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Xml.Linq;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using DataGrid = System.Windows.Controls.DataGrid;
using Size = System.Windows.Size;

namespace ApplicationPackagerEngine.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DiscoveryWindowTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Markup()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        static string Relative(DirectoryInfo f) => Path.Combine(f.FullName, "src", "ApplicationPackagerUI", "Views", "Windows", "DiscoveryWindow.xaml");
        while (folder is not null && !File.Exists(Relative(folder))) folder = folder.Parent;
        Assert.NotNull(folder);
        return XDocument.Load(Relative(folder));
    }

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static void RunSta(Action action) => StaTestHelper.Run(action);

    [Theory]
    [InlineData("Application", "Name")]
    [InlineData("Vendor", "Vendor")]
    public void ALongApplicationOrVendorIsTrimmedWithTheFullTextInATooltip(string header, string path)
    {
        var column = Markup().Descendants().Single(e => e.Name.LocalName == "DataGridTextColumn" && (string?)e.Attribute("Header") == header);
        var setters = column.Descendants().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => (string)e.Attribute("Property")!, e => (string)e.Attribute("Value")!);
        Assert.Equal("CharacterEllipsis", setters["TextTrimming"]);
        Assert.Equal("{Binding " + path + "}", setters["ToolTip"]);
    }

    [Fact]
    public void TheCheckColumnHasAnAutomationNameAndSpaceTogglesTheFocusedRow()
    {
        var doc = Markup();
        var column = doc.Descendants().Single(e => e.Name.LocalName == "DataGridTemplateColumn");
        var headerName = column.Descendants().Single(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "AutomationProperties.Name");
        Assert.Equal("Include", (string?)headerName.Attribute("Value"));

        var binding = Named(doc, "gridDiscover").Descendants().Single(e => e.Name.LocalName == "KeyBinding");
        Assert.Equal("Space", (string?)binding.Attribute("Key"));
        Assert.Equal("{Binding ToggleCheckedCommand}", (string?)binding.Attribute("Command"));
        Assert.Equal("{Binding SelectedItem, ElementName=gridDiscover}", (string?)binding.Attribute("CommandParameter"));
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCheckColumnHeaderUsesTheThemedHeaderLook(ApplicationTheme theme)
    {
        var gridMarkup = Named(Markup(), "gridDiscover").ToString();
        var hostMarkup =
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\">"
            + "<Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>"
            + $"<ui:ThemesDictionary Theme=\"{theme}\" /><ui:ControlsDictionary />"
            + "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources>"
            + gridMarkup + "</Grid>";
        RunSta(() =>
        {
            var host = (Grid)System.Windows.Markup.XamlReader.Parse(hostMarkup);
            var grid = host.Children.OfType<DataGrid>().Single();
            host.Measure(new Size(600, 300));
            host.Arrange(new Rect(0, 0, 600, 300));
            host.UpdateLayout();

            var headers = Descendants(grid).OfType<DataGridColumnHeader>().Where(h => h.Column is not null).ToList();
            var check = headers.Single(h => h.Column is DataGridTemplateColumn);
            var application = headers.Single(h => (h.Column.Header as string) == "Application");
            Assert.Equal("Include", AutomationProperties.GetName(check));
            Assert.Equal(application.Template, check.Template);
            Assert.Equal(Describe(application.Background), Describe(check.Background));
        });
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void TheCheckBoxOfARowIsNotClippedByItsCell(ApplicationTheme theme)
    {
        var gridMarkup = Named(Markup(), "gridDiscover").ToString();
        var hostMarkup =
            "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\">"
            + "<Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>"
            + $"<ui:ThemesDictionary Theme=\"{theme}\" /><ui:ControlsDictionary />"
            + "</ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources>"
            + gridMarkup + "</Grid>";
        RunSta(() =>
        {
            var host = (Grid)System.Windows.Markup.XamlReader.Parse(hostMarkup);
            var grid = host.Children.OfType<DataGrid>().Single();
            grid.ItemsSource = new DiscoveryDialogViewModel([new DiscoveryCandidate("a", "A", "V", "A app")], "").Rows;
            host.Measure(new Size(600, 300));
            host.Arrange(new Rect(0, 0, 600, 300));
            host.UpdateLayout();

            var box = Descendants(grid).OfType<System.Windows.Controls.CheckBox>().Single();
            var cell = Descendants(grid).OfType<System.Windows.Controls.DataGridCell>().First(c => c.Column is DataGridTemplateColumn);
            var bounds = box.TransformToAncestor(cell).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
            Assert.True(bounds.Left >= 0 && bounds.Right <= cell.ActualWidth,
                $"The check box spans {bounds.Left}..{bounds.Right} in a cell of width {cell.ActualWidth}.");
            // The default cell template padded the box to 36 px and cut the 20 px glyph on the right; the centered cell gives it 42 px.
            Assert.True(box.ActualWidth >= 40, $"The check box is {box.ActualWidth} px wide.");
        });
    }

    private static string Describe(System.Windows.Media.Brush? brush) => brush switch
    {
        null => "none",
        SolidColorBrush solid => solid.Color.ToString(),
        _ => brush.GetType().Name
    };

    [Fact]
    public void EnableSelectedIsTheDefaultButtonAndNotNowTheCancelButton()
    {
        var doc = Markup();
        Assert.Equal("True", (string?)Named(doc, "btnEnableSelected").Attribute("IsDefault"));
        Assert.Equal("True", (string?)Named(doc, "btnNotNow").Attribute("IsCancel"));
        foreach (var other in new[] { "btnEnableSelected", "btnEnableAll", "btnDismiss" })
            Assert.Null(Named(doc, other).Attribute("IsCancel"));
    }

    private static readonly string[] Buttons = ["btnEnableSelected", "btnEnableAll", "btnNotNow", "btnDismiss"];

    [Fact]
    public void TheFourButtonsHaveDistinctAccessKeys()
    {
        var doc = Markup();
        var keys = Buttons.Select(b =>
        {
            var content = (string?)Named(doc, b).Attribute("Content") ?? "";
            var index = content.IndexOf('_');
            Assert.True(index >= 0 && index + 1 < content.Length, b + " has no access key");
            return char.ToUpperInvariant(content[index + 1]);
        }).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void TheButtonTemplateTurnsTheUnderscoreIntoAnAccessKey()
    {
        RunSta(() =>
        {
            var button = new Wpf.Ui.Controls.Button { Content = "_Enable selected" };
            button.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            button.Resources.MergedDictionaries.Add(new ControlsDictionary());
            button.Measure(new Size(400, 100));
            button.Arrange(new Rect(0, 0, 400, 100));
            button.UpdateLayout();
            var access = Descendants(button).OfType<AccessText>().SingleOrDefault();
            Assert.NotNull(access);
            Assert.Equal('E', access.AccessKey);
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

    [Fact]
    public void ToggleCheckedFlipsTheRowAndIgnoresOtherItems()
    {
        var vm = new DiscoveryDialogViewModel([new DiscoveryCandidate("a", "A", "V", "A app")], "");
        var row = vm.Rows[0];
        vm.ToggleCheckedCommand.Execute(row);
        Assert.True(row.Selected);
        vm.ToggleCheckedCommand.Execute(row);
        Assert.False(row.Selected);
        vm.ToggleCheckedCommand.Execute(null);
        vm.ToggleCheckedCommand.Execute("not a row");
        Assert.False(row.Selected);
    }

    [Fact]
    public void AMinimizedOwnerIsRestoredBeforeThePromptOpens()
    {
        RunSta(() =>
        {
            var owner = new Window
            {
                Width = 200, Height = 100, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, WindowState = WindowState.Minimized
            };
            owner.Show();
            try
            {
                Assert.Equal(WindowState.Minimized, owner.WindowState);
                Assert.Same(owner, DiscoveryPrompt.UsableOwner(owner));
                Assert.Equal(WindowState.Normal, owner.WindowState);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void AnOwnerThatIsMissingOrNotShownIsNotUsed()
    {
        RunSta(() =>
        {
            Assert.Null(DiscoveryPrompt.UsableOwner(null));
            Assert.Null(DiscoveryPrompt.UsableOwner(new Window()));
        });
    }
}
