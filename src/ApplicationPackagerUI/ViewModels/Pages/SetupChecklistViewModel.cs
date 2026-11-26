using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Wpf.Ui.Controls;

namespace ApplicationPackagerUI.ViewModels.Pages;

public enum SetupStepKey { Systems, Site, Folders, Discovery }

public enum SetupStepState { Open, Complete, NotNeeded, Skipped }

/// <summary>What the rules say about one step: its state, the text under its title and the actions it offers.</summary>
public sealed record SetupStepResult(SetupStepKey Key, SetupStepState State, string Detail,
    bool OffersSetup = false, bool OffersTest = false, bool OffersScan = false, bool OffersSkip = false, bool Failed = false)
{
    public bool IsDone => State != SetupStepState.Open;
}

/// <summary>
/// The rules of the first-start checklist, without a window. Every step state comes from the stored settings, the last ConfigMgr connection test
/// and the scan record of the active site. No step state is stored.
/// </summary>
public static class SetupChecklistRules
{
    /// <summary>The checklist is in progress: a new install that has not finished every step.</summary>
    public const string InProgress = "Open";

    /// <summary>Every step was done once. The checklist comes back only for a site that has no scan record.</summary>
    public const string Finished = "Done";

    /// <summary>The user selected Do Not Show Again. The checklist opens only when the user opens it.</summary>
    public const string Dismissed = "Dismissed";

    /// <summary>The file share of a new settings file. It is an example, so it does not count as a content folder.</summary>
    public static string PlaceholderShare { get; } = new AppSettings().FileShareRoot;

    private static SystemsInUse Systems(AppSettings s) => s.Systems ?? new SystemsInUse();

    private static bool FoldersSet(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.DownloadRoot)) return false;
        if (!Systems(s).ConfigMgr) return true;
        var share = (s.FileShareRoot ?? "").Trim();
        return share.Length > 0 && !string.Equals(share, PlaceholderShare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the stored settings alone show a configured install: a system, a saved site when ConfigMgr is in use, and the folders.</summary>
    public static bool SettingsComplete(AppSettings s)
    {
        var systems = Systems(s);
        if (!systems.ConfigMgr && !systems.Intune && !systems.Wsus) return false;
        if (systems.ConfigMgr && (s.SiteCodeSaved != true || string.IsNullOrWhiteSpace(s.SiteCode))) return false;
        return FoldersSet(s);
    }

    /// <summary>The site code that the suite launcher supplies, or null.</summary>
    public static string? LauncherSite() =>
        Environment.GetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable)?.Trim() is { Length: > 0 } code ? code : null;

    /// <summary>The four steps in their order: systems, site connection, content folders, discovery.</summary>
    public static IReadOnlyList<SetupStepResult> Steps(AppSettings settings, ConfigMgrConnectionState connection, DiscoveryScanRecord? record) =>
        [SystemsStep(settings), SiteStep(settings, connection), FoldersStep(settings), DiscoveryStep(settings, record)];

    private static SetupStepResult SystemsStep(AppSettings settings)
    {
        var s = Systems(settings);
        var inUse = new[] { (s.ConfigMgr, "ConfigMgr"), (s.Intune, "Intune"), (s.Wsus, "WSUS") }.Where(x => x.Item1).Select(x => x.Item2).ToList();
        return inUse.Count == 0
            ? new(SetupStepKey.Systems, SetupStepState.Open, "No system is in use. Select the systems that you publish to.", OffersSetup: true)
            : new(SetupStepKey.Systems, SetupStepState.Complete, "In use: " + string.Join(", ", inUse) + ".", OffersSetup: true);
    }

    private static SetupStepResult SiteStep(AppSettings settings, ConfigMgrConnectionState connection)
    {
        if (!Systems(settings).ConfigMgr)
            return new(SetupStepKey.Site, SetupStepState.NotNeeded, "ConfigMgr is not in use.");
        var code = (settings.SiteCode ?? "").Trim();
        if (connection == ConfigMgrConnectionState.Connected)
            return new(SetupStepKey.Site, SetupStepState.Complete, $"Site {code} is connected.", OffersSetup: true);
        if (connection == ConfigMgrConnectionState.Testing)
            return new(SetupStepKey.Site, SetupStepState.Open, $"The connection to site {code} is being tested.", OffersSetup: true);
        if (connection == ConfigMgrConnectionState.Failed)
            return new(SetupStepKey.Site, SetupStepState.Open, $"The connection to site {code} failed. Check the site code and the SMS Provider. The log shows the error.",
                OffersSetup: true, OffersTest: true, Failed: true);
        // The site code of a new settings file is an example until the user saves one (or the suite launcher supplies one).
        var named = code.Length > 0 && (settings.SiteCodeSaved == true || LauncherSite() is not null);
        return named
            ? new(SetupStepKey.Site, SetupStepState.Open, $"Site {code} is configured. The connection is not tested.", OffersSetup: true, OffersTest: true)
            : new(SetupStepKey.Site, SetupStepState.Open, "Enter the site code and the SMS Provider in Setup.", OffersSetup: true);
    }

    private static SetupStepResult FoldersStep(AppSettings settings) =>
        FoldersSet(settings)
            ? new(SetupStepKey.Folders, SetupStepState.Complete, "The content folders are set.", OffersSetup: true)
            : new(SetupStepKey.Folders, SetupStepState.Open,
                Systems(settings).ConfigMgr
                    ? "Set the File Share Root, where the application content goes, and the Download Root, where installers are staged."
                    : "Set the Download Root, where installers are staged.",
                OffersSetup: true);

    private static SetupStepResult DiscoveryStep(AppSettings settings, DiscoveryScanRecord? record)
    {
        if (!Systems(settings).ConfigMgr)
            return new(SetupStepKey.Discovery, SetupStepState.NotNeeded, "ConfigMgr is not in use.");
        if (record is { Skipped: false })
            return new(SetupStepKey.Discovery, SetupStepState.Complete, record.MatchedText + ".", OffersScan: true);
        if (record is not null)
            return new(SetupStepKey.Discovery, SetupStepState.Skipped, "You skipped the scan of this site. You can still scan it.", OffersScan: true);
        // A scan that cannot run says why; the step stays open.
        var (available, reason) = DiscoveryRunner.Availability(settings);
        var detail = available
            ? "Find the products that the site inventory shows on devices and that have no ConfigMgr application."
            : reason[DiscoveryRunner.LogPrefix.Length..];
        return new(SetupStepKey.Discovery, SetupStepState.Open, detail, OffersScan: true, OffersSkip: true);
    }
}

/// <summary>One step as the panel shows it. The four objects live as long as the panel, so a refresh keeps the keyboard focus on its button.</summary>
public sealed partial class SetupStepItem : ObservableObject
{
    public SetupStepItem(SetupStepKey key) => Key = key;

    public SetupStepKey Key { get; }

    public string Title => Key switch
    {
        SetupStepKey.Systems => "Choose systems",
        SetupStepKey.Site => "Connect to your site",
        SetupStepKey.Folders => "Set content folders",
        _ => "Discover unmanaged software"
    };

    public string TitleAutomationId => "txtSetupStep" + Key;
    public string SetupAutomationId => "btnSetupOpen" + Key;
    public string SetupName => "Open Setup: " + Title;

    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _stateKey = "";
    [ObservableProperty] private SymbolRegular _glyph = SymbolRegular.Circle24;
    [ObservableProperty] private string _detail = "";
    /// <summary>What a screen reader reads for the step: its place, its title and its state.</summary>
    [ObservableProperty] private string _accessibleName = "";
    [ObservableProperty] private bool _showSetup;
    [ObservableProperty] private bool _showTest;
    [ObservableProperty] private bool _showScan;
    [ObservableProperty] private bool _showSkip;
    [ObservableProperty] private string _scanText = "";

    public void Apply(SetupStepResult result, int number, int count)
    {
        StateWord = result.State switch
        {
            SetupStepState.Complete => "Complete",
            SetupStepState.NotNeeded => "Not needed",
            SetupStepState.Skipped => "Skipped",
            _ => result.Failed ? "Failed" : "To do"
        };
        StateKey = result.Failed ? "Failed" : result.State.ToString();
        // The glyph and the word say the state too, so no state is shown by color alone (R-18).
        Glyph = result.State switch
        {
            SetupStepState.Complete => SymbolRegular.CheckmarkCircle24,
            SetupStepState.NotNeeded or SetupStepState.Skipped => SymbolRegular.SubtractCircle24,
            _ => result.Failed ? SymbolRegular.Warning24 : SymbolRegular.Circle24
        };
        Detail = result.Detail;
        AccessibleName = $"Step {number} of {count}, {Title}: {StateWord}";
        ShowSetup = result.OffersSetup;
        ShowTest = result.OffersTest;
        ShowScan = result.OffersScan;
        ShowSkip = result.OffersSkip;
        ScanText = result.State == SetupStepState.Complete ? "Scan Again" : "Scan Site Inventory";
    }
}

/// <summary>
/// The first-start checklist (overhaul V1, phase 7d). A new install sees it beside the grid until every step is done. An install that was set
/// up before never sees it unless the user opens it. After a new install finished it, it comes back for a site that has no scan record.
/// </summary>
public sealed partial class SetupChecklistViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly DiscoveryScanStore _scans;
    private readonly Func<ConfigMgrConnectionState> _connection;
    private readonly Func<bool> _busy;
    // What Skip for Now hid: "" for a checklist in progress (whatever the site is), else the key of the new site. Null: nothing is hidden.
    private string? _hiddenKey;
    private string _currentKey = "";
    private bool _discoveryAvailable;

    public SetupChecklistViewModel(SettingsService settings, DiscoveryScanStore scans, Func<ConfigMgrConnectionState> connection,
        Func<bool> busy, Func<Task> scan, Func<Task> testConnection)
    {
        _settings = settings;
        _scans = scans;
        _connection = connection;
        _busy = busy;
        ScanCommand = new AsyncRelayCommand(scan, () => !_busy() && _discoveryAvailable);
        TestConnectionCommand = new AsyncRelayCommand(testConnection, () => !_busy());
        SkipScanCommand = new RelayCommand(SkipScan);
        CloseCommand = new RelayCommand(Close);
        DoNotShowAgainCommand = new RelayCommand(Dismiss);
    }

    public IReadOnlyList<SetupStepItem> Steps { get; } =
        [new(SetupStepKey.Systems), new(SetupStepKey.Site), new(SetupStepKey.Folders), new(SetupStepKey.Discovery)];

    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand TestConnectionCommand { get; }
    public IRelayCommand SkipScanCommand { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand DoNotShowAgainCommand { get; }

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _stripTitle = "";
    [ObservableProperty] private string _stripText = "";
    [ObservableProperty] private string _closeText = "";
    [ObservableProperty] private bool _canOpenSetup = true;

    /// <summary>The checklist has work for this user now: the page shows the panel, or the strip when the panel is closed.</summary>
    [ObservableProperty] private bool _isDue;

    /// <summary>The panel is wanted: it is due and was not skipped, or the user opened it.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>The user opened the panel, so a window that started narrow shows it too.</summary>
    [ObservableProperty] private bool _openedByUser;

    /// <summary>Raised after every refresh, so the page can place the panel and the strip.</summary>
    public event Action? StateChanged;

    /// <summary>Reads the settings, the connection test and the scan record again.</summary>
    public void Refresh()
    {
        var settings = _settings.Load();
        // With an unreadable file the load gives defaults, which would look like a new install.
        var unreadable = _settings.IsFileUnreadable;
        if (!unreadable && settings.SetupChecklist is null && FirstRunDecision.IsWizardDue(settings.FirstRunCompleted))
        {
            // A settings file that already holds a whole setup is not a new install: it gets no checklist.
            if (SetupChecklistRules.SettingsComplete(settings)) settings.FirstRunCompleted = true;
            else settings.SetupChecklist = SetupChecklistRules.InProgress;
            Store(settings);
        }

        var record = _scans.Find(settings.SiteCode, settings.ProviderMachine);
        var results = SetupChecklistRules.Steps(settings, _connection(), record);
        for (var i = 0; i < results.Count; i++) Steps[i].Apply(results[i], i + 1, results.Count);
        var done = results.Count(r => r.IsDone);
        ProgressText = $"{done} of {results.Count} complete";

        if (!unreadable && settings.SetupChecklist == SetupChecklistRules.InProgress && done == results.Count)
        {
            settings.SetupChecklist = SetupChecklistRules.Finished;
            settings.FirstRunCompleted = true;
            Store(settings);
        }

        var inProgress = settings.SetupChecklist == SetupChecklistRules.InProgress && done < results.Count;
        // After the first finish only a new site opens the checklist again: its discovery step has no record.
        var newSite = settings.SetupChecklist == SetupChecklistRules.Finished && !string.IsNullOrWhiteSpace(settings.SiteCode)
                      && !results.Single(r => r.Key == SetupStepKey.Discovery).IsDone;
        var due = !unreadable && (inProgress || newSite);
        _currentKey = inProgress ? "" : SiteIdentity.Key(settings.SiteCode, settings.ProviderMachine);

        IsDue = due;
        IsOpen = OpenedByUser || (due && _hiddenKey != _currentKey);
        CloseText = due ? "Skip for Now" : "Close";
        StripTitle = results.Single(r => r.Key == SetupStepKey.Site).IsDone
            ? "Finish the setup checklist"
            : "Connect a site to check for updates";
        StripText = ProgressText + ".";

        _discoveryAvailable = DiscoveryRunner.Availability(settings).Enabled;
        RefreshBusy();
        StateChanged?.Invoke();
    }

    /// <summary>A run started or ended: the buttons that start work follow it.</summary>
    public void RefreshBusy()
    {
        CanOpenSetup = !_busy();
        ScanCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the panel on request (the strip, the More menu).</summary>
    public void Show()
    {
        _hiddenKey = null;
        OpenedByUser = true;
        Refresh();
    }

    /// <summary>Skip for Now and Close: the panel closes until the next start. Nothing is stored.</summary>
    private void Close()
    {
        OpenedByUser = false;
        _hiddenKey = _currentKey;
        Refresh();
    }

    /// <summary>The checklist does not open by itself again. No other setting changes.</summary>
    public void Dismiss()
    {
        var settings = _settings.Load();
        settings.SetupChecklist = SetupChecklistRules.Dismissed;
        settings.FirstRunCompleted = true;
        Store(settings);
        OpenedByUser = false;
        Refresh();
    }

    private void SkipScan()
    {
        var settings = _settings.Load();
        try { _scans.Skip(settings.SiteCode, settings.ProviderMachine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Refresh();
    }

    // A file that cannot be written leaves the checklist as it is; the next refresh tries again.
    private void Store(AppSettings settings)
    {
        try { _settings.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
