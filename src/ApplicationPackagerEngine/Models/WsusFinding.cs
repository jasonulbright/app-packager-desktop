namespace ApplicationPackagerEngine.Models;

/// <summary>Blocking refuses the publish; Review needs an operator decision; Info records a capability WSUS does not carry.</summary>
public enum WsusSeverity
{
    Blocking,
    Review,
    Info
}

/// <summary>One thing a WSUS publish of a manifest would or would not do.</summary>
public sealed record WsusFinding(WsusSeverity Severity, string Code, string Message);

/// <summary>The finding codes. The blocking ones are the refusal codes a refused publish carries.</summary>
public static class WsusRefusalCodes
{
    public const string MultipleDeploymentTypes = "MultipleDeploymentTypes";
    public const string InstallerTypeUnsupported = "InstallerTypeUnsupported";
    public const string PerUserInstall = "PerUserInstall";
    public const string InteractiveInstall = "InteractiveInstall";
    public const string CustomInstall = "CustomInstall";
    public const string PayloadOutsideStage = "PayloadOutsideStage";
    public const string InstallerMissing = "InstallerMissing";
    public const string InstallerInSubfolder = "InstallerInSubfolder";
    public const string DetectionNotMappable = "DetectionNotMappable";
    public const string PayloadInSubfolder = "PayloadInSubfolder";
    public const string PayloadMissing = "PayloadMissing";
    public const string PayloadTooLarge = "PayloadTooLarge";

    public const string RequirementsNotTranslated = "RequirementsNotTranslated";
    public const string RunningProcessNotClosed = "RunningProcessNotClosed";

    public const string WsusRulesFromMsi = "WsusRulesFromMsi";
    public const string TimingNotApplied = "TimingNotApplied";
    public const string UninstallNotPublished = "UninstallNotPublished";
}
