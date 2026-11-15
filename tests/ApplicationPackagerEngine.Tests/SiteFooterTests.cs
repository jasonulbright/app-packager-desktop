using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The footer of the main window: the site code and the site server, lower right (decisions D-05 and D-06 of the suite).</summary>
public sealed class SiteFooterTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("MCM", "cm01.lab.local", "Site MCM · cm01.lab.local")]
    [InlineData(" MCM ", " cm01.lab.local ", "Site MCM · cm01.lab.local")]
    [InlineData("MCM", "", "Site MCM")]
    [InlineData("MCM", null, "Site MCM")]
    [InlineData("", "cm01.lab.local", "Site not set")]
    [InlineData(null, null, "Site not set")]
    [InlineData("  ", "  ", "Site not set")]
    public void TheFooterTextHasTheSiteAndThenAMiddleDotAndTheServer(string? code, string? server, string expected) =>
        Assert.Equal(expected, SiteFooter.Format(code, server));

    [Fact]
    public void ABareViewModelShowsSiteNotSet() => Assert.Equal("Site not set", new MainWindowViewModel().FooterText);

    [Fact]
    public void TheFooterFollowsWhatTheOperatorSaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-footer-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("APP_PACKAGER_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("APP_PACKAGER_DATA_ROOT", root);
            var service = new SettingsService();
            var viewModel = new MainWindowViewModel(service);
            Assert.Equal("Site not set", viewModel.FooterText);

            service.SaveOperatorEdit(new AppSettings { SiteCode = "P01", ProviderMachine = "primary.contoso.test" });

            Assert.Equal("Site P01 · primary.contoso.test", viewModel.FooterText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_PACKAGER_DATA_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Views() => Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views");

    [Fact]
    public void TheFooterIsInTheMainWindowLowerRightWithOneFontSizeAndStyle()
    {
        var window = XDocument.Load(Path.Combine(Views(), "Windows", "MainWindow.xaml"));
        var footer = window.Descendants(Presentation + "TextBlock").Single(e => (string?)e.Attribute(Xaml + "Name") == "txtSiteFooter");

        Assert.Equal("Right", (string?)footer.Attribute("HorizontalAlignment"));
        Assert.Equal("12", (string?)footer.Attribute("FontSize"));
        Assert.Equal("Normal", (string?)footer.Attribute("FontWeight"));
        Assert.Null(footer.Attribute("FontFamily"));
        Assert.Null(footer.Attribute("FontStyle"));
        // The color is a theme brush, so the two themes differ only in color.
        Assert.StartsWith("{DynamicResource ", (string?)footer.Attribute("Foreground"));
        // The footer is the last row of the window grid.
        var row = (string?)footer.Ancestors(Presentation + "Border").First().Attribute("Grid.Row");
        Assert.Equal("2", row);
    }

    [Fact]
    public void OnlyOptionsAndTheSetupWindowHaveAnEditableSiteField()
    {
        var allowed = new[] { "SettingsPage.xaml", "SetupWindow.xaml" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Views(), "*.xaml", SearchOption.AllDirectories).Where(f => !allowed.Contains(Path.GetFileName(f))))
            foreach (var box in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants()
                         .Where(e => e.Name.LocalName is "TextBox" or "ComboBox" or "PasswordBox" or "NumberBox" or "AutoSuggestBox"))
            {
                var binding = ((string?)box.Attribute("Text") ?? "") + ((string?)box.Attribute("Value") ?? "") + ((string?)box.Attribute("SelectedItem") ?? "");
                if (binding.Contains("SiteCode", StringComparison.Ordinal) || binding.Contains("ProviderMachine", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)box).LineNumber}");
            }

        Assert.True(offenders.Count == 0, "These windows edit the site code or the site server; only Options and the Setup window may: " + string.Join(", ", offenders));
    }
}
