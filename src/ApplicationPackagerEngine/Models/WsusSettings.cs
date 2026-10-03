using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The WSUS server a publish writes to and what the publish does after it.
/// An empty ServerName means WSUS publishing is not configured.
/// </summary>
public sealed partial class WsusSettings
{
    public const int HttpPort = 8530;
    public const int SslPort = 8531;

    /// <summary>The package type every published update carries.</summary>
    public const string UpdatePackageType = "Update";

    public string ServerName { get; set; } = "";
    public int PortNumber { get; set; } = HttpPort;
    public bool UseSsl { get; set; }
    public string Classification { get; set; } = WsusClassifications.Updates;
    /// <summary>Empty publishes without an approval.</summary>
    public string ApprovalGroup { get; set; } = "";
    public bool DeclineSuperseded { get; set; }

    /// <summary>
    /// A retired setting. A stored value is dropped on load; a value handed
    /// to <see cref="Normalize"/> other than Update is refused.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageType { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerName);

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostNamePattern();

    /// <summary>
    /// The settings a publish uses. Refuses a value that would publish
    /// somewhere other than intended (a malformed server name, an unknown
    /// classification, a package type other than Update) and fills defaults
    /// for absent values. An out-of-range port becomes 8531 with SSL and
    /// 8530 without.
    /// </summary>
    public WsusSettings Normalize()
    {
        var server = (ServerName ?? "").Trim();
        if (server.Length > 0 && !HostNamePattern().IsMatch(server))
            throw new WsusSettingsException($"WSUS server name '{server}' is not a host name.");

        var port = PortNumber is < 1 or > 65535 ? (UseSsl ? SslPort : HttpPort) : PortNumber;

        var packageType = (PackageType ?? "").Trim();
        if (packageType.Length > 0 && packageType != UpdatePackageType)
            throw new WsusSettingsException(
                $"WSUS package type '{packageType}' is not available. AppPackager publishes updates for installed products only: an update never installs where the product is missing. Remove the PackageType setting.");

        var classification = string.IsNullOrWhiteSpace(Classification) ? WsusClassifications.Updates : Classification;
        if (!WsusClassifications.Names.Contains(classification, StringComparer.Ordinal))
            throw new WsusSettingsException(
                $"WSUS classification '{classification}' is not one of: {string.Join(", ", WsusClassifications.Names)}.");

        return new WsusSettings
        {
            ServerName = server,
            PortNumber = port,
            UseSsl = UseSsl,
            Classification = classification,
            ApprovalGroup = (ApprovalGroup ?? "").Trim(),
            DeclineSuperseded = DeclineSuperseded
        };
    }

    /// <summary>
    /// The settings as a settings file holds them, read leniently: an unknown
    /// or out-of-range value keeps its default and the valid values beside
    /// it survive. A stored package type is dropped. Null reads as the
    /// defaults.
    /// </summary>
    public static WsusSettings FromStored(WsusSettings? stored)
    {
        var result = new WsusSettings();
        if (stored is null) return result;
        result.ServerName = (stored.ServerName ?? "").Trim();
        if (stored.PortNumber is >= 1 and <= 65535) result.PortNumber = stored.PortNumber;
        result.UseSsl = stored.UseSsl;
        if (WsusClassifications.Names.Contains(stored.Classification ?? "", StringComparer.Ordinal))
            result.Classification = stored.Classification!;
        result.ApprovalGroup = (stored.ApprovalGroup ?? "").Trim();
        result.DeclineSuperseded = stored.DeclineSuperseded;
        return result;
    }
}

/// <summary>A WSUS setting that would publish somewhere other than intended.</summary>
public sealed class WsusSettingsException(string message) : Exception(message);

/// <summary>The update classifications a published update can carry.</summary>
public static class WsusClassifications
{
    public const string Updates = "Updates";

    public static readonly IReadOnlyList<string> Names =
        [Updates, "SecurityUpdates", "CriticalUpdates", "FeaturePacks", "ServicePacks", "Tools", "UpdateRollups"];
}
