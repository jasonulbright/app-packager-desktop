using System.Globalization;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// First-run setup. Step one picks the systems in use, step two shows the fields those
/// systems need. Save writes through the same settings persistence the Settings page uses,
/// so it applies without a restart, and every selected system becomes a default One Click
/// destination.
/// </summary>
public partial class SetupWindowViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly IPublishActionHost _packagesViewModel;
    private readonly ISettingsReload _settingsViewModel;
    private bool _portAuto = true;
    private bool _settingPort;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private bool _useConfigMgr = true;
    [ObservableProperty] private bool _useIntune;
    [ObservableProperty] private bool _useWsus;
    [ObservableProperty] private bool _dontShowAgain;

    [ObservableProperty] private string _siteCode = "";
    [ObservableProperty] private string _providerMachine = "";
    [ObservableProperty] private string _fileShareRoot = "";
    [ObservableProperty] private string _downloadRoot = "";

    [ObservableProperty] private string _intuneTenantId = "";
    [ObservableProperty] private string _intuneClientId = "";

    /// <summary>Entry only. Empty on save keeps the stored secret; a typed value replaces it.</summary>
    [ObservableProperty] private string _intuneClientSecret = "";

    [ObservableProperty] private bool _hasStoredIntuneSecret;

    [ObservableProperty] private string _wsusServerName = "";
    [ObservableProperty] private string _wsusPort = WsusSettings.HttpPort.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private bool _wsusUseSsl;

    public bool ShowMecmFields => UseConfigMgr;
    public bool ShowIntuneFields => UseIntune;
    public bool ShowWsusFields => UseWsus;
    public bool ShowNoFields => !UseConfigMgr && !UseIntune && !UseWsus;

    public string IntuneSecretHint => HasStoredIntuneSecret
        ? "Stored — leave empty to keep it"
        : "Not set";

    /// <summary>Raised when the window should close; true when settings were saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    public SetupWindowViewModel(
        SettingsService settingsService,
        IPublishActionHost packagesViewModel,
        ISettingsReload settingsViewModel)
    {
        _settingsService = settingsService;
        _packagesViewModel = packagesViewModel;
        _settingsViewModel = settingsViewModel;

        var s = _settingsService.Load();
        var systems = s.Systems ?? new SystemsInUse();
        UseConfigMgr = systems.ConfigMgr;
        UseIntune = systems.Intune;
        UseWsus = systems.Wsus;
        SiteCode = s.SiteCode;
        ProviderMachine = s.ProviderMachine;
        FileShareRoot = s.FileShareRoot;
        DownloadRoot = s.DownloadRoot;
        IntuneTenantId = s.Intune.TenantId;
        IntuneClientId = s.Intune.ClientId;
        HasStoredIntuneSecret = !string.IsNullOrWhiteSpace(s.Intune.ClientSecretProtected);
        _settingPort = true;
        try
        {
            WsusServerName = s.Wsus.ServerName;
            WsusPort = s.Wsus.PortNumber.ToString(CultureInfo.InvariantCulture);
            _portAuto = WsusPort is "8530" or "8531";
            WsusUseSsl = s.Wsus.UseSsl;
        }
        finally { _settingPort = false; }
    }

    partial void OnUseConfigMgrChanged(bool value) => NotifyGroups();
    partial void OnUseIntuneChanged(bool value) => NotifyGroups();
    partial void OnUseWsusChanged(bool value) => NotifyGroups();

    private void NotifyGroups()
    {
        OnPropertyChanged(nameof(ShowMecmFields));
        OnPropertyChanged(nameof(ShowIntuneFields));
        OnPropertyChanged(nameof(ShowWsusFields));
        OnPropertyChanged(nameof(ShowNoFields));
    }

    partial void OnHasStoredIntuneSecretChanged(bool value) => OnPropertyChanged(nameof(IntuneSecretHint));

    /// <summary>The port follows the SSL box until the operator types a port of their own.</summary>
    partial void OnWsusUseSslChanged(bool value)
    {
        if (!_portAuto) return;
        _settingPort = true;
        try { WsusPort = (value ? WsusSettings.SslPort : WsusSettings.HttpPort).ToString(CultureInfo.InvariantCulture); }
        finally { _settingPort = false; }
    }

    partial void OnWsusPortChanged(string value)
    {
        if (_settingPort) return;
        _portAuto = (value ?? "").Trim() is "" or "8530" or "8531";
    }

    [RelayCommand]
    private void Next() => CurrentStep = 2;

    [RelayCommand]
    private void Back() => CurrentStep = 1;

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Load();

        s.Systems = new SystemsInUse { ConfigMgr = UseConfigMgr, Intune = UseIntune, Wsus = UseWsus };
        if (UseConfigMgr)
        {
            s.SiteCode = SiteCode.Trim();
            s.ProviderMachine = ProviderMachine.Trim();
            s.FileShareRoot = FileShareRoot.Trim();
            s.DownloadRoot = DownloadRoot.Trim();
        }
        else
        {
            // The built-in default would otherwise persist as a site code the operator never entered.
            s.SiteCode = "";
        }

        if (UseIntune)
        {
            s.Intune.TenantId = IntuneTenantId.Trim();
            s.Intune.ClientId = IntuneClientId.Trim();
            // The plaintext never reaches settings.json; an empty box keeps the stored secret.
            if (!string.IsNullOrEmpty(IntuneClientSecret))
            {
                s.Intune.ClientSecretProtected = IntuneSecretProtector.Protect(IntuneClientSecret);
                IntuneClientSecret = "";
            }
        }

        if (UseWsus)
        {
            s.Wsus.ServerName = WsusServerName.Trim();
            if (int.TryParse(WsusPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
                s.Wsus.PortNumber = port;
            s.Wsus.UseSsl = WsusUseSsl;
        }

        // One Click runs unattended: every selected system becomes a default destination;
        // the publish buttons choose per run.
        var defaults = new DestinationSet(UseConfigMgr, UseWsus, UseIntune);
        s.OneClick.DefaultDestinations = defaults;
        s.Intune.DeploymentTarget = DeploymentTargets.Normalize(defaults.ToTargetName());

        s.FirstRunCompleted = true;
        // An automatic save writes nothing over an unreadable file, and the wizard would show again at every launch.
        if (UseConfigMgr || _settingsService.IsFileUnreadable) _settingsService.SaveOperatorEdit(s);
        else _settingsService.Save(s);

        _settingsViewModel.ReloadFromStore();
        _packagesViewModel.RefreshPublishActionState();
        _packagesViewModel.AppendLog(OneClickSettingsMap.SetupCompleteLine(defaults));

        CloseRequested?.Invoke(this, true);
    }

    /// <summary>
    /// Skip leaves the wizard due next launch unless "Don't show this again" is checked, which
    /// suppresses it without configuring anything.
    /// </summary>
    [RelayCommand]
    private void Skip()
    {
        if (DontShowAgain)
        {
            var s = _settingsService.Load();
            s.FirstRunCompleted = true;
            if (_settingsService.IsFileUnreadable) _settingsService.SaveOperatorEdit(s);
            else _settingsService.Save(s);
        }

        CloseRequested?.Invoke(this, false);
    }
}
