namespace ApplicationPackagerUI.Models;

/// <summary>The last tab, object type and template of the Deployment Helper page, and the audit log path.</summary>
public sealed class DeploymentHelperSettings
{
    /// <summary>0 Deploy, 1 History, 2 Options.</summary>
    public int SelectedTab { get; set; }

    /// <summary>0 Applications, 1 Packages, 2 Task Sequences, 3 Software Update Groups.</summary>
    public int ObjectType { get; set; }

    /// <summary>The id of the template last chosen; empty for none.</summary>
    public string TemplateId { get; set; } = "";

    /// <summary>The audit log file; empty for the default under the data root. A UNC path is allowed.</summary>
    public string AuditLogPath { get; set; } = "";
}
