namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, object type, template and ring plan of the Deployment Helper page, the audit log path and the ring folders.</summary>
public sealed class DeploymentHelperSettings
{
    /// <summary>0 Deploy, 1 History, 2 Options, 3 Rings.</summary>
    public int SelectedTab { get; set; }

    /// <summary>0 Applications, 1 Packages, 2 Task Sequences, 3 Software Update Groups.</summary>
    public int ObjectType { get; set; }

    /// <summary>The id of the template last chosen; empty for none.</summary>
    public string TemplateId { get; set; } = "";

    /// <summary>The audit log file; empty for the default under the data root. A UNC path is allowed.</summary>
    public string AuditLogPath { get; set; } = "";

    /// <summary>The name of the ring plan last chosen; empty for none.</summary>
    public string? RingPlanName { get; set; } = "";

    /// <summary>The folder of the ring plan files; empty for the default under the data root. A UNC path is allowed.</summary>
    public string? RingsFolder { get; set; } = "";

    /// <summary>The folder of the run files; empty for the default under the rings folder. A UNC path is allowed.</summary>
    public string? RunsFolder { get; set; } = "";
}
