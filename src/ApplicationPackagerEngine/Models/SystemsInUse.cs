namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The systems the operator publishes to. A publish action for a system not
/// in use stays disabled whatever else is configured.
/// </summary>
public sealed class SystemsInUse
{
    public bool ConfigMgr { get; set; } = true;
    public bool Intune { get; set; }
    public bool Wsus { get; set; }

    /// <summary>
    /// The systems for a loaded settings file. A file written before the
    /// setting existed carries none: ConfigMgr stays in use, Intune is in use
    /// when a tenant id is stored, and WSUS when a server name is stored.
    /// </summary>
    public static SystemsInUse Resolve(SystemsInUse? stored, string? intuneTenantId, string? wsusServerName) =>
        stored is not null
            ? new SystemsInUse { ConfigMgr = stored.ConfigMgr, Intune = stored.Intune, Wsus = stored.Wsus }
            : new SystemsInUse
            {
                ConfigMgr = true,
                Intune = !string.IsNullOrWhiteSpace(intuneTenantId),
                Wsus = !string.IsNullOrWhiteSpace(wsusServerName)
            };
}
