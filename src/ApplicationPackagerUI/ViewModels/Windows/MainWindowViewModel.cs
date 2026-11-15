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
        settingsService.OperatorSaved += saved =>
        {
            var text = SiteFooter.Format(saved.SiteCode, saved.ProviderMachine);
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

    /// <summary>"Site MCM" then a middle dot then the server; with no site code "Site not set"; with no server the code alone.</summary>
    public static string Format(string? siteCode, string? server)
    {
        var code = siteCode?.Trim() ?? "";
        var machine = server?.Trim() ?? "";
        if (code.Length == 0) return NotSet;
        return machine.Length == 0 ? "Site " + code : $"Site {code} Â· {machine}";
    }
}
