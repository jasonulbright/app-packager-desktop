using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>Signing state an Intune script detection rule is built from.</summary>
public sealed class IntuneScriptRuleContext
{
    /// <summary>ScriptSigning.Detection.Status of the stage manifest.</summary>
    public string DetectionStatus { get; init; } = "";
    /// <summary>RequireDetection of the policy in force at publish time.</summary>
    public bool RequireDetection { get; init; }
    /// <summary>x64 or x86.</summary>
    public string ScriptHost { get; init; } = DeploymentLauncher.HostX64;
    /// <summary>Absolute path of the finalized detection script; null when the stage carries none.</summary>
    public string? DetectionScriptFile { get; init; }

    /// <summary>
    /// The context for a stage folder: the record's detection status, the
    /// policy's RequireDetection, and the finalized detection file resolved
    /// against the stage folder.
    /// </summary>
    public static IntuneScriptRuleContext FromManifest(Models.StageManifest manifest, SigningPolicy? policy, string stageFolder) =>
        From(ScriptSigningRecord.FromJsonNode(manifest.ScriptSigning), policy, stageFolder,
            string.Equals(manifest.Execution?.ScriptHost, DeploymentLauncher.HostX86, StringComparison.OrdinalIgnoreCase)
                ? DeploymentLauncher.HostX86 : DeploymentLauncher.HostX64);

    public static IntuneScriptRuleContext From(ScriptSigningRecord? record, SigningPolicy? policy,
        string stageFolder, string scriptHost = DeploymentLauncher.HostX64) => new()
    {
        DetectionStatus = record?.Detection.Status ?? "",
        RequireDetection = SigningPolicy.Normalize(policy).RequireDetection,
        ScriptHost = scriptHost,
        DetectionScriptFile = string.IsNullOrWhiteSpace(record?.Detection.File)
            ? null
            : Path.IsPathRooted(record.Detection.File) ? record.Detection.File : Path.Combine(stageFolder, record.Detection.File)
    };
}
