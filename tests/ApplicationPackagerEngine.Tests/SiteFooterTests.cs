using System.Xml.Linq;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The footer of the main window: the site code and the site server, lower right (decisions D-05 and D-06 of the suite).</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SiteFooterTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Dot = "·";

    [Theory]
    [InlineData("MCM", "cm01.lab.local", "Site MCM " + Dot + " cm01.lab.local")]
    [InlineData(" MCM ", " cm01.lab.local ", "Site MCM " + Dot + " cm01.lab.local")]
    [InlineData("MCM", "", "Site MCM " + Dot + " server not set")]
    [InlineData("MCM", null, "Site MCM " + Dot + " server not set")]
    [InlineData("", "cm01.lab.local", "Site not set " + Dot + " cm01.lab.local")]
    [InlineData(null, null, "Site not set")]
    [InlineData("  ", "  ", "Site not set")]
    public void TheFooterTextFollowsTheFormatOfTheSuiteHelper(string? code, string? server, string expected) =>
        Assert.Equal(expected, SiteFooter.Format(code, server));

    [Fact]
    public void TheSeparatorIsOneMiddleDot() => Assert.Equal(new[] { (char)0x00B7 }, SiteFooter.Format("A", "b").Where(c => c > 127).ToArray());

    [Fact]
    public void ABareViewModelShowsSiteNotSet() => Assert.Equal("Site not set", new MainWindowViewModel().FooterText);

    private static void WithIsolatedSettings(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "apd-footer-" + Guid.NewGuid().ToString("N"));
        var names = new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, root);
            Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
            Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
            Directory.CreateDirectory(AppDataPaths.Root);
            test(root);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheFooterFollowsWhatTheOperatorSaves() => WithIsolatedSettings(_ =>
    {
        var service = new SettingsService();
        var viewModel = new MainWindowViewModel(service);
        var defaults = new AppSettings();
        Assert.Equal(SiteFooter.Format(defaults.SiteCode, defaults.ProviderMachine), viewModel.FooterText);

        service.SaveOperatorEdit(new AppSettings { SiteCode = "P01", ProviderMachine = "primary.contoso.test" });

        Assert.Equal("Site P01 " + Dot + " primary.contoso.test", viewModel.FooterText);
    });

    [Fact]
    public void ALauncherSuppliedSiteStillShowsWhereTheSavedValueIsBlank() => WithIsolatedSettings(_ =>
    {
        // The launcher supplies the site code; the operator clears the field and saves. Every Load puts the launcher value back, so the
        // footer shows the site that the app uses, not the blank that was saved.
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, "MCM");
        var service2 = new SettingsService();
        var viewModel2 = new MainWindowViewModel(service2);
        Assert.StartsWith("Site MCM", viewModel2.FooterText);

        service2.SaveOperatorEdit(new AppSettings { SiteCode = "", ProviderMachine = "" });

        Assert.Equal(SiteFooter.Format(service2.Load().SiteCode, service2.Load().ProviderMachine), viewModel2.FooterText);
    });

    private static string SourceRoot() => Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");

    [Fact]
    public void TheFooterIsInTheMainWindowLowerRightWithTheFontOfTheSuiteFooter()
    {
        var window = XDocument.Load(Path.Combine(SourceRoot(), "Views", "Windows", "MainWindow.xaml"));
        var footer = window.Descendants(Presentation + "TextBlock").Single(e => (string?)e.Attribute(Xaml + "Name") == "txtSiteFooter");

        // The footer is also the site switcher (Q-112d): the text sits in a button that is aligned to the right.
        var switcher = footer.Ancestors().First(e => e.Name.LocalName == "Button");
        Assert.Equal("btnSiteSwitcher", (string?)switcher.Attribute(Xaml + "Name"));
        Assert.Equal("Right", (string?)switcher.Attribute("HorizontalAlignment"));
        Assert.Equal("Segoe UI", (string?)footer.Attribute("FontFamily"));
        Assert.Equal("12", (string?)footer.Attribute("FontSize"));
        Assert.Equal("Normal", (string?)footer.Attribute("FontWeight"));
        Assert.Equal("Normal", (string?)footer.Attribute("FontStyle"));
        // The color is a theme brush, so the two themes differ only in color.
        Assert.StartsWith("{DynamicResource ", (string?)footer.Attribute("Foreground"));
        // The footer is the last row of the window grid: title bar, navigation (with the context strip at its top), status bar.
        var row = (string?)footer.Ancestors(Presentation + "Border").First().Attribute("Grid.Row");
        Assert.Equal("2", row);
    }

    private static bool NamesTheSite(string? text) =>
        text is not null && (text.Contains("SiteCode", StringComparison.Ordinal) || text.Contains("ProviderMachine", StringComparison.Ordinal));

    [Fact]
    public void OnlyOptionsTheSetupWindowAndTheAddSiteWindowHaveAnEditableSiteField()
    {
        var root = SourceRoot();
        // The Add site window of the footer switcher takes the code and the provider of a new site (Q-112d).
        var allowed = new[] { Path.Combine("Views", "Pages", "SettingsPage.xaml"), Path.Combine("Views", "Windows", "SetupWindow.xaml"), Path.Combine("Views", "Windows", "AddSiteWindow.xaml") };
        // A grid column is not a field: the grid of the DP page lists the site code of each distribution point.
        var editable = new HashSet<string> { "TextBox", "ComboBox", "PasswordBox", "NumberBox", "AutoSuggestBox" };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var relative = Path.GetRelativePath(root, file);
            if (allowed.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var element in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants().Where(e => editable.Contains(e.Name.LocalName)))
            {
                // Every attribute (Text, Value, SelectedItem, SelectedValue, Binding) and every nested Binding element of the control.
                var bound = element.Attributes().Any(a => NamesTheSite((string?)a))
                            || element.Descendants().Any(d => d.Name.LocalName == "Binding" && (NamesTheSite((string?)d.Attribute("Path")) || NamesTheSite((string?)d.Attribute("Mode"))));
                if (bound) offenders.Add($"{relative}:{((System.Xml.IXmlLineInfo)element).LineNumber}");
            }
        }

        Assert.True(offenders.Count == 0, "These controls edit the site code or the site server; only Options and the Setup window may: " + string.Join(", ", offenders));
    }
}
