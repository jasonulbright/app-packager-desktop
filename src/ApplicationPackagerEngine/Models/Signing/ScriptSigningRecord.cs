using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Models.Signing;

/// <summary>The status vocabulary every signing result and manifest entry uses.</summary>
public static class SigningStatus
{
    public const string NotRequested = "NotRequested";
    public const string NotApplicable = "NotApplicable";
    public const string ExistingSignatureValid = "ExistingSignatureValid";
    public const string SignedAndVerified = "SignedAndVerified";
    public const string Failed = "Failed";

    /// <summary>True for the two statuses that satisfy a Require switch.</summary>
    public static bool IsSigned(string? status) =>
        status is SignedAndVerified or ExistingSignatureValid;

    /// <summary>
    /// Category status from its members: Failed outranks SignedAndVerified,
    /// which outranks ExistingSignatureValid, then NotRequested; no members
    /// is NotApplicable.
    /// </summary>
    public static string Aggregate(IEnumerable<string>? statuses)
    {
        var list = statuses?.ToList() ?? [];
        if (list.Count == 0) return NotApplicable;
        if (list.Contains(Failed)) return Failed;
        if (list.Contains(SignedAndVerified)) return SignedAndVerified;
        if (list.Contains(ExistingSignatureValid)) return ExistingSignatureValid;
        if (list.Contains(NotRequested)) return NotRequested;
        return NotApplicable;
    }
}

/// <summary>
/// The ScriptSigning block a build records in its stage manifest. Property
/// names are the manifest field names.
/// </summary>
public sealed class ScriptSigningRecord
{
    public string PolicyDigest { get; set; } = "";
    public DetectionSigningRecord Detection { get; set; } = new();
    public RequirementsSigningRecord Requirements { get; set; } = new();
    public DeploymentSigningRecord Deployment { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public JsonNode ToJsonNode() => JsonSerializer.SerializeToNode(this, JsonOptions)!;

    public static ScriptSigningRecord? FromJsonNode(JsonNode? node) =>
        node is null ? null : node.Deserialize<ScriptSigningRecord>(JsonOptions);
}

public sealed class DetectionSigningRecord
{
    public string Status { get; set; } = SigningStatus.NotApplicable;
    public string Thumbprint { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Timestamped { get; set; }
    /// <summary>Stage-root-relative path of the finalized detection script; null when none was written.</summary>
    public string? File { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class RequirementsSigningRecord
{
    public string Status { get; set; } = SigningStatus.NotApplicable;
    public RequirementSigningItem[] Items { get; set; } = [];
}

public sealed class RequirementSigningItem
{
    public string RuleId { get; set; } = "";
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class DeploymentSigningRecord
{
    public string Status { get; set; } = SigningStatus.NotApplicable;
    public DeploymentSigningFile[] Files { get; set; } = [];
    public bool LaunchersBypassFree { get; set; } = true;
}

public sealed class DeploymentSigningFile
{
    public const string OwnerAppPackager = "AppPackager";
    public const string OwnerThirdParty = "ThirdParty";

    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    public string Status { get; set; } = "";
    public string Owner { get; set; } = OwnerAppPackager;
    public string Reason { get; set; } = "";
}
