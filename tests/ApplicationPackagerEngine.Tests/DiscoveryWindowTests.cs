using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Size = System.Windows.Size;

namespace ApplicationPackagerEngine.Tests;

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

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

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
        var vm = new DiscoveryDialogViewModel([new DiscoveryCandidate("a", "A", "V", "A app", 1)], "");
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
