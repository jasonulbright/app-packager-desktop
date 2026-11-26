using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Settings chrome, ConfigMgr connection and Application Settings (overhaul V1, phase 7b, Q-126): the ids of the header buttons, the
/// help of each field that cannot be changed, the grid that fills its page and the text of the connection signal.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsPagesLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument Page() => XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));

    [Theory]
    [InlineData("btnSaveSettings", "SaveSettingsCommand")]
    [InlineData("btnDiscardChanges", "DiscardChangesCommand")]
    public void TheHeaderButtonsHaveAnAutomationId(string id, string command)
    {
        var button = Page().Descendants().Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == id);

        Assert.Equal($"{{Binding {command}}}", (string?)button.Attribute("Command"));
    }

    [Theory]
    [InlineData("cboConfigMgrVersionRule")]
    [InlineData("cboIntuneVersionRule")]
    [InlineData("cboSupersedenceMode")]
    public void EveryDefaultShowsItsHelpUnderTheBoxAndGivesItToAScreenReader(string id)
    {
        var box = Page().Descendants(Presentation + "ComboBox").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == id);
        var help = (string?)box.Attribute("AutomationProperties.HelpText");

        Assert.False(string.IsNullOrWhiteSpace(help));
        Assert.Contains(box.Parent!.Elements(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == help);
    }

    /// <summary>An application that offers no choice, so that every column that can be fixed is fixed.</summary>
    private static SupersedenceRow FixedRow()
    {
        var row = new SupersedenceRow { Uid = "app1", ApplicationId = "catalog:app1", DefinitionId = "app1", Application = "Contoso One", Vendor = "Contoso", Architecture = "x64" };
        var work = new ApplicationWork();
        foreach (var column in ApplicationColumns.All)
        {
            var reason = column.NotApplicable(work, row);
            var value = column.Get(work, row);
            if (column.Choices is { } choices) row.Lists[column.Id] = choices.Contains(value) ? choices : [value];
            row.Cells[column.Id] = value;
            row.Editable[column.Id] = reason is null;
            row.Tips[column.Id] = reason ?? column.Tooltip;
        }
        return row;
    }

    [Fact]
    public void EveryFixedFieldHasAHelpThatSaysWhy()
    {
        var row = FixedRow();
        var work = new ApplicationWork();
        var fixedColumns = ApplicationColumns.All.Where(c => c.NotApplicable(work, row) is not null).ToList();

        Assert.Contains(fixedColumns, c => c.Id == "source");
        Assert.Contains(fixedColumns, c => c.Id == "scope");
        foreach (var column in fixedColumns)
        {
            var reason = column.NotApplicable(work, row)!;
            Assert.False(string.IsNullOrWhiteSpace(reason), column.Id);
            Assert.Equal($"Contoso One, {column.Header}: {reason}", ApplicationColumns.FieldHelp(row, column));
        }
    }

    /// <summary>The compiled page over a stand-in for the view model: the page needs the rows, the chosen columns and the search box only.</summary>
    private static SettingsPage CompiledPage()
    {
        var page = (SettingsPage)RuntimeHelpers.GetUninitializedObject(typeof(SettingsPage));
        typeof(Page).GetConstructor(Type.EmptyTypes)!.Invoke(page, null);
        page.DataContext = new
        {
            Sidebar = new SidebarSettingsEditor(new SidebarService(SidebarRegistry.CreateDefault(), new InMemorySidebarStore(null))),
            SupersedenceRows = new List<SupersedenceRow> { FixedRow() }
        };
        page.InitializeComponent();
        page.SelectPage("supersedence");
        return page;
    }

    [Theory]
    [InlineData(ApplicationTheme.Light, 1920, 1032)]
    [InlineData(ApplicationTheme.Dark, 1024, 720)]
    public void TheApplicationGridFillsItsPage(ApplicationTheme theme, int width, int height)
    {
        PageHeaderRenderTests.With("Settings", theme, () =>
        {
            var page = CompiledPage();
            var window = new Window { Width = width - 230, Height = height - 48, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Settle(window);
                Settle(window);
                var scroll = (ScrollViewer)page.FindName("page_supersedence");
                var grid = (DataGrid)page.FindName("dgApplications");
                var body = (FrameworkElement)page.FindName("pnlApplicationSettings");
                if (height >= 1000)
                {
                    Assert.True(grid.ActualHeight > 360, $"The grid is {grid.ActualHeight:0} px tall.");
                    Assert.True(scroll.ViewportHeight - (body.ActualHeight + body.Margin.Bottom) < 2, "The grid leaves room under the page.");
                }
                else
                    Assert.True(grid.ActualHeight >= 240 - 0.5, $"The grid is {grid.ActualHeight:0} px tall in a small window.");
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(SystemState.Connected, "Connected to Site MCM")]
    [InlineData(SystemState.Ready, "Site MCM: connection not tested")]
    [InlineData(SystemState.Failed, "Connection to Site MCM failed")]
    [InlineData(SystemState.Testing, "Testing the connection to Site MCM")]
    [InlineData(SystemState.NotConfigured, "ConfigMgr not configured")]
    [InlineData(SystemState.Off, "ConfigMgr off")]
    public void OnlyATestedConnectionReadsConnected(SystemState state, string text) =>
        Assert.Equal(text, SettingsViewModel.SignalText(new ContextStripState("MCM", "cm01.contoso.com", [new SystemStatus("ConfigMgr", state, "")])));

    [Fact]
    public void TheDirtyLineIsUnderTheListOfPagesAndIsALiveRegion()
    {
        var line = Page().Descendants(Presentation + "TextBlock").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "txtUnsavedChanges");

        Assert.Equal("{Binding UnsavedChangesText}", (string?)line.Attribute("Text"));
        Assert.Equal("Polite", (string?)line.Attribute("AutomationProperties.LiveSetting"));
        // The tree shares its place with the results of the settings search.
        Assert.Contains(line.Parent!.Descendants(Presentation + "TreeView"), _ => true);
    }
}
