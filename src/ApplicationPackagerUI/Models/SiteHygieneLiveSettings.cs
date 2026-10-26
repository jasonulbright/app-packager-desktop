namespace ApplicationPackagerUI.Models;

/// <summary>
/// The Live and Alerts options of the Site Hygiene page. Every value is null until it is set, so a file written
/// before the Live views existed loads with the defaults, and the dashboard import can tell an empty value from
/// a chosen one. The SQL Server and database are the Discover settings.
/// </summary>
public sealed class SiteHygieneLiveSettings
{
    public bool? TrustSqlCertificate { get; set; }

    public int? RefreshMinutes { get; set; }

    public int? InactiveDays { get; set; }

    public bool? AlertsEnabled { get; set; }

    public bool? AlertSiteCritical { get; set; }

    public bool? AlertDpCritical { get; set; }

    public bool? AlertContentFailed { get; set; }

    /// <summary>False turns the compliance alert off; the floor stays stored.</summary>
    public bool? AlertCompliance { get; set; }

    public int? CompliancePctFloor { get; set; }
}
