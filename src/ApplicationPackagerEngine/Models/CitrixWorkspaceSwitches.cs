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
}

public sealed class CwaUpdateAndTelemetry
{
    /// <summary>auto, manual or disabled; empty is not set.</summary>
    public string? AutoUpdateCheck { get; set; }
    public bool? EnableCEIP { get; set; }
    public bool? EnableTracing { get; set; }
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
}
