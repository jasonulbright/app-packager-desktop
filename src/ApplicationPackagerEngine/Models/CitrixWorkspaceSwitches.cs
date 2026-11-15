namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Citrix Workspace app install settings in the citrix-workspace-switches.json
/// shape. A null value is "not set": the builder emits no switch for it and
/// the installer default applies.
/// </summary>
public sealed class CitrixWorkspaceSwitches
{
    public CwaStore? Store { get; set; }
    public CwaInstallation? Installation { get; set; }
    public CwaPlugins? Plugins { get; set; }
    public CwaUpdateAndTelemetry? UpdateAndTelemetry { get; set; }
    public CwaStorePolicy? StorePolicy { get; set; }
    public CwaComponents? Components { get; set; }
    public CwaHdx? Hdx { get; set; }
}

public sealed class CwaStore
{
    public string? Name { get; set; }
    public string? Url { get; set; }
}

public sealed class CwaInstallation
{
    public bool? CleanInstall { get; set; }
    public bool? IncludeSSON { get; set; }
    public bool? EnableSSON { get; set; }
    public bool? AppProtection { get; set; }
    public bool? SessionPreLaunch { get; set; }
    public bool? SelfServiceMode { get; set; }
    /// <summary>/forceinstall: cleans old Citrix data when an install or an upgrade of an unsupported version fails. It replaces rcu.</summary>
    public bool? ForceInstall { get; set; }
    /// <summary>A fixed client device name (CLIENT_NAME with ENABLE_DYNAMIC_CLIENT_NAME=No); empty keeps the computer name.</summary>
    public string? ClientName { get; set; }
}

public sealed class CwaPlugins
{
    public bool? MSTeamsPlugin { get; set; }
    public bool? ZoomPlugin { get; set; }
    public bool? WebExPlugin { get; set; }
    public bool? UberAgent { get; set; }
    public bool? UberAgentSkipUpgrade { get; set; }
    public bool? EPAClient { get; set; }
    public bool? SessionRecording { get; set; }
    /// <summary>/SkipSRAgentUpgrade with /InstallSRAgent (Current Release): keeps the Session Recording agent that is already on the device.</summary>
    public bool? SessionRecordingSkipUpgrade { get; set; }
}

public sealed class CwaUpdateAndTelemetry
{
    /// <summary>auto, manual or disabled; empty is not set.</summary>
    public string? AutoUpdateCheck { get; set; }
    public bool? EnableCEIP { get; set; }
    public bool? EnableTracing { get; set; }
    /// <summary>LTSR or Current; empty is not set. Works only with AutoUpdateCheck.</summary>
    public string? AutoUpdateStream { get; set; }
    /// <summary>Auto, Fast, Medium or Slow; empty is not set. Works only with AutoUpdateCheck.</summary>
    public string? AURolloutPriority { get; set; }
}

/// <summary>HDX features and the display of the app: each switch is written only when it is set.</summary>
public sealed class CwaHdx
{
    /// <summary>ALLOW_BIDIRCONTENTREDIRECTION=1|0: content redirection between the client and the host in both directions.</summary>
    public bool? BidirectionalContentRedirection { get; set; }
    /// <summary>FORCE_LAA=1|0: installs Local App Access. Needs an admin install.</summary>
    public bool? LocalAppAccess { get; set; }
    /// <summary>ALLOW_CLIENTHOSTEDAPPSURL=1|0: URL redirection on the device.</summary>
    public bool? ClientHostedAppsUrl { get; set; }
    /// <summary>LEGACYFTAICONS=True|False: True hides the file-type icons of subscribed apps.</summary>
    public bool? LegacyFileTypeIcons { get; set; }
    /// <summary>DisableSetting 0 to 3: 0 shows Shortcuts and Reconnect, 1 Reconnect only, 2 Shortcuts only, 3 hides both; empty is not set.</summary>
    public string? DisableSetting { get; set; }
}

public sealed class CwaStorePolicy
{
    /// <summary>S, A or N; empty is not set.</summary>
    public string? AllowAddStore { get; set; }
    /// <summary>S, A or N; empty is not set.</summary>
    public string? AllowSavePwd { get; set; }
}

public sealed class CwaComponents
{
    public bool? Customize { get; set; }
    public bool? ReceiverInside { get; set; }
    public bool? ICA_Client { get; set; }
    public bool? AM { get; set; }
    public bool? SelfService { get; set; }
    public bool? DesktopViewer { get; set; }
    public bool? WebHelper { get; set; }
    public bool? BCR_Client { get; set; }
    public bool? USB { get; set; }
    public bool? SSON { get; set; }
    /// <summary>The Citrix Enterprise Browser; a component of LTSR 2607. Current releases do not carry it.</summary>
    public bool? CitrixEnterpriseBrowser { get; set; }
}
