using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>A classification as the settings page lists it.</summary>
public sealed record WsusClassificationOption(string Name, string Label)
{
    /// <summary>The label, which is also the name a screen reader announces for the list item.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The WSUS Publishing section of Settings: the server, the signing certificate and
/// what a publish does after it. Server buttons act on the values in the boxes before
/// Save stores them; each runs off the UI thread and can be canceled. Written to the
/// store only by Save Settings.
/// </summary>
public partial class WsusPublishingEditor : ObservableObject
{
    public const string IntroText =
        "Publish to WSUS publishes the installer of each checked app as a locally published update. WSUS Updates on the App Packager page lists and manages the published updates. This computer needs the WSUS console or RSAT WSUS tools at the server's version, an account in the WSUS Administrators group, and file-share access to the server. When this computer is not the WSUS server, it must also trust the signing certificate.";

    public const string ClientTrustText =
        "Clients install locally published updates only when the signing certificate is in their Trusted Publishers store (and Trusted Root Certification Authorities when it is self-signed) and the policy Allow signed updates from an intranet Microsoft update service location is enabled: HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate, AcceptTrustedPublisherCerts = 1 (DWORD).";

    public const string PolicyText =
        "Each update installs only where an older version of the application is installed. A computer without the application does not get the update.";

    public const string CertificatePromptText = "Test the connection to read the server's signing certificate.";

    private const string Cross = "✗ ";
    private const string Check = "✓ ";

    private readonly IWsusAdminClient _client;
    private readonly Func<WsusApiStatus> _apiProbe;
    private readonly IWsusPrompts? _prompts;
    private bool _portAuto = true;
    private bool _settingPort;
    private int _storedPort = WsusSettings.HttpPort;
    private CancellationTokenSource? _cts;

    public static IReadOnlyList<WsusClassificationOption> ClassificationOptions { get; } =
        WsusClassifications.Names.Select(n => new WsusClassificationOption(n, ClassificationLabel(n))).ToList();

    public ObservableCollection<string> Groups { get; } = [];

    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private string _port = WsusSettings.HttpPort.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private bool _useSsl;
    [ObservableProperty] private string _classification = WsusClassifications.Updates;
    [ObservableProperty] private string _approvalGroup = "";
    [ObservableProperty] private bool _declineSuperseded;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _certificateText = CertificatePromptText;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _apiAvailable;

    public bool ServerButtonsEnabled => ApiAvailable && !Busy;

    public WsusPublishingEditor(IWsusAdminClient client, Func<WsusApiStatus>? apiProbe = null, IWsusPrompts? prompts = null)
    {
        _client = client;
        _apiProbe = apiProbe ?? WsusApiProbe.Probe;
        _prompts = prompts;
    }

    public static string ClassificationLabel(string name) => name switch
    {
        "SecurityUpdates" => "Security Updates",
        "CriticalUpdates" => "Critical Updates",
        "FeaturePacks" => "Feature Packs",
        "ServicePacks" => "Service Packs",
        "UpdateRollups" => "Update Rollups",
        _ => name
    };

    partial void OnBusyChanged(bool value) => NotifyButtons();
    partial void OnApiAvailableChanged(bool value) => NotifyButtons();

    private void NotifyButtons()
    {
        OnPropertyChanged(nameof(ServerButtonsEnabled));
        TestConnectionCommand.NotifyCanExecuteChanged();
        CreateSelfSignedCommand.NotifyCanExecuteChanged();
        ImportPfxCommand.NotifyCanExecuteChanged();
        ExportCertificateCommand.NotifyCanExecuteChanged();
        LoadGroupsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The port follows the SSL box, 8530 or 8531, until the operator types a port of their own.</summary>
    partial void OnUseSslChanged(bool value)
    {
        if (!_portAuto) return;
        _settingPort = true;
        try { Port = (value ? WsusSettings.SslPort : WsusSettings.HttpPort).ToString(CultureInfo.InvariantCulture); }
        finally { _settingPort = false; }
    }

    partial void OnPortChanged(string value)
    {
        if (_settingPort) return;
        _portAuto = IsDefaultPort(value);
    }

    private static bool IsDefaultPort(string? value) => (value ?? "").Trim() is "" or "8530" or "8531";

    /// <summary>Fills the boxes from the stored settings and states whether the API is on this computer.</summary>
    public void Load(WsusSettings stored)
    {
        var s = WsusSettings.FromStored(stored);
        _settingPort = true;
        try
        {
            ServerName = s.ServerName;
            _storedPort = s.PortNumber;
            Port = s.PortNumber.ToString(CultureInfo.InvariantCulture);
            _portAuto = IsDefaultPort(Port);
            UseSsl = s.UseSsl;
        }
        finally { _settingPort = false; }
        Classification = s.Classification;
        ApprovalGroup = s.ApprovalGroup;
        DeclineSuperseded = s.DeclineSuperseded;
        CertificateText = CertificatePromptText;

        var api = _apiProbe();
        ApiAvailable = api.Available;
        StatusText = api.Available
            ? $"WSUS administration API {api.Version} found."
            : Cross + api.Reason;
    }

    /// <summary>The stored form. A port that is not a number from 1 to 65535 keeps the stored one.</summary>
    public WsusSettings ToSettings() => new()
    {
        ServerName = (ServerName ?? "").Trim(),
        PortNumber = int.TryParse((Port ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port : _storedPort,
        UseSsl = UseSsl,
        Classification = WsusClassifications.Names.Contains(Classification) ? Classification : WsusClassifications.Updates,
        ApprovalGroup = (ApprovalGroup ?? "").Trim(),
        DeclineSuperseded = DeclineSuperseded
    };

    /// <summary>The boxes as a server action uses them; a bad or empty value is the error.</summary>
    private WsusSettings ReadSettings()
    {
        var settings = new WsusSettings
        {
            ServerName = (ServerName ?? "").Trim(),
            PortNumber = int.TryParse((Port ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0,
            UseSsl = UseSsl,
            Classification = Classification,
            ApprovalGroup = (ApprovalGroup ?? "").Trim(),
            DeclineSuperseded = DeclineSuperseded
        }.Normalize();
        if (!settings.IsConfigured) throw new WsusSettingsException("Enter the WSUS server first.");
        return settings;
    }

    /// <summary>The status line and the certificate line of a connection test.</summary>
    public void ShowStatus(WsusServerStatus status)
    {
        if (!status.Connected)
        {
            StatusText = Cross + status.Message;
            CertificateText = "Not connected.";
            return;
        }
        // IsConnectionSecureForApiRemoting also reports a connection to the WSUS server on this computer as secure; the
        // line says secure only when SSL is on, so a plain connection on port 8530 is never called secure.
        var secure = UseSsl && status.SecureConnection;
        StatusText = (Check + $"Connected  -  {status.ServerName}, WSUS {status.Version}, role {status.Role}, {(secure ? "secure" : "unencrypted")} connection. {status.Message}").Trim();
        var cert = status.SigningCertificate;
        if (cert is null)
        {
            CertificateText = status.Message.Contains("could not be read", StringComparison.OrdinalIgnoreCase)
                ? "The signing certificate could not be read; see the status line."
                : Cross + "No signing certificate is set on this server. Publishing fails until one is created or imported.";
            return;
        }
        var flags = new List<string>();
        if (cert.SelfSigned) flags.Add("self-signed");
        if (status.SigningCertificateExpired) flags.Add("EXPIRED");
        CertificateText = $"Subject: {cert.Subject}   Thumbprint: {cert.Thumbprint}   Valid to: {cert.NotAfter.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}   Key: {cert.KeyLength} bits" +
                          (flags.Count > 0 ? $"   ({string.Join(", ", flags)})" : "");
    }

    /// <summary>
    /// Validates the boxes, then runs the action off the UI thread. A validation or
    /// server error lands in the status line; cancel ends the wait.
    /// </summary>
    private async Task<T?> RunOnServerAsync<T>(Func<WsusSettings, CancellationToken, Task<T>> action) where T : class
    {
        WsusSettings settings;
        try { settings = ReadSettings(); }
        catch (WsusSettingsException ex)
        {
            StatusText = Cross + ex.Message;
            return null;
        }
        Busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var token = _cts.Token;
            return await Task.Run(() => action(settings, token), token);
        }
        catch (OperationCanceledException)
        {
            StatusText = Cross + "Canceled.";
            return null;
        }
        catch (Exception ex) when (ex is WsusOperationException or WsusSettingsException or InvalidOperationException
                                   or System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            StatusText = Cross + ex.Message;
            return null;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(ServerButtonsEnabled))]
    private async Task TestConnectionAsync()
    {
        var status = await RunOnServerAsync((s, ct) => _client.GetStatusAsync(s, null, ct));
        if (status is not null) ShowStatus(status);
    }

    [RelayCommand(CanExecute = nameof(ServerButtonsEnabled))]
    private async Task CreateSelfSignedAsync()
    {
        WsusSettings settings;
        try { settings = ReadSettings(); }
        catch (WsusSettingsException ex) { StatusText = Cross + ex.Message; return; }
        if (_prompts is null || !await _prompts.ConfirmAsync("Create Signing Certificate",
                $"Have {settings.ServerName} create a self-signed signing certificate and use it for every update published from now on?\n\nThis replaces any certificate the server uses today. Clients that trust only the current certificate reject updates signed with the new one until the new certificate reaches their Trusted Publishers and Trusted Root Certification Authorities stores. With ConfigMgr, keep ConfigMgr in charge of the certificate if it already manages it."))
            return;
        var status = await RunOnServerAsync((s, ct) => _client.CreateSelfSignedCertificateAsync(s, null, ct));
        if (status is not null) ShowStatus(status);
    }

    [RelayCommand(CanExecute = nameof(ServerButtonsEnabled))]
    private async Task ImportPfxAsync()
    {
        WsusSettings settings;
        try { settings = ReadSettings(); }
        catch (WsusSettingsException ex) { StatusText = Cross + ex.Message; return; }
        if (_prompts is null || _prompts.PickPfxFile() is not { } path) return;
        var password = await _prompts.AskPasswordAsync($"Password for {Path.GetFileName(path)}");
        if (password is null) return;
        if (!await _prompts.ConfirmAsync("Import Signing Certificate",
                $"Make this certificate the signing certificate of {settings.ServerName}?\n\nIt replaces any certificate the server uses today. Clients must trust the new certificate before they install updates signed with it."))
            return;
        var status = await RunOnServerAsync((s, ct) => _client.ImportPfxAsync(s, path, password, null, ct));
        if (status is not null) ShowStatus(status);
    }

    [RelayCommand(CanExecute = nameof(ServerButtonsEnabled))]
    private async Task ExportCertificateAsync()
    {
        WsusSettings settings;
        try { settings = ReadSettings(); }
        catch (WsusSettingsException ex) { StatusText = Cross + ex.Message; return; }
        if (_prompts?.PickCertificateSavePath($"WSUS-signing-{settings.ServerName}.cer") is not { } target) return;
        var written = await RunOnServerAsync((s, ct) => _client.ExportCertificateAsync(s, target, null, ct));
        if (written is not null) StatusText = Check + "Saved " + written;
    }

    [RelayCommand(CanExecute = nameof(ServerButtonsEnabled))]
    private async Task LoadGroupsAsync()
    {
        var names = await RunOnServerAsync((s, ct) => _client.GetComputerGroupNamesAsync(s, null, ct));
        if (names is null) return;
        var typed = ApprovalGroup;
        Groups.Clear();
        foreach (var name in names) Groups.Add(name);
        ApprovalGroup = typed;
        StatusText = $"{names.Count} computer group(s) loaded.";
    }

    [RelayCommand(CanExecute = nameof(Busy))]
    private void Cancel() => _cts?.Cancel();
}
