using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _applicationTitle = "App Packager Desktop";

    /// <summary>The site code and the site server in the footer, lower right: "Site MCM", a middle dot and the server; "Site not set" when empty. Only Options and the Setup window change them.</summary>
    [ObservableProperty]
    private string _footerText = SiteFooter.Format(null, null);

    public MainWindowViewModel() { }

    public MainWindowViewModel(SettingsService settingsService)
    {
        var settings = settingsService.Load();
        FooterText = SiteFooter.Format(settings.SiteCode, settings.ProviderMachine);
        // After a save the footer reads the settings back, as every tool of the suite does: a launcher-supplied site code or server still
        // applies where the saved value is blank, and the footer must show the site that the app uses.
        settingsService.OperatorSaved += _ =>
        {
            var current = settingsService.Load();
            var text = SiteFooter.Format(current.SiteCode, current.ProviderMachine);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) FooterText = text;
            else dispatcher.BeginInvoke(() => FooterText = text);
        };
    }
}

/// <summary>The text of the footer that every ConfigMgr tool of the suite shows, lower right (decision D-05).</summary>
public static class SiteFooter
{
    public const string NotSet = "Site not set";

    /// <summary>
    /// The footer text of the suite (Format-SuiteFooterSite): "Site MCM", a middle dot and the server; "Site not set" when both are empty;
    /// "Site not set", a middle dot and the server without a site code; "Site MCM", a middle dot and "server not set" without a server.
    /// </summary>
    public static string Format(string? siteCode, string? server)
    {
        var code = siteCode?.Trim() ?? "";
        var machine = server?.Trim() ?? "";
        const string dot = "Â·";
        if (code.Length == 0 && machine.Length == 0) return NotSet;
        if (code.Length == 0) return $"{NotSet} {dot} {machine}";
        if (machine.Length == 0) return $"Site {code} {dot} server not set";
        return $"Site {code} {dot} {machine}";
    }
}