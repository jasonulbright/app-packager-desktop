using System.Text.Json.Nodes;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// Resolves the signing certificate while a run snapshot is created, before any
/// packager writes content, so an unusable certificate fails the run with no
/// partial output. The run snapshot writer calls it only for a policy with at
/// least one Sign or Require switch on.
/// </summary>
public interface ISigningCertificateGate
{
    /// <summary>Returns the resolved certificate, or throws when the policy cannot be met.</summary>
    object? ResolveCertificate(JsonNode policy);
}

/// <summary>
/// The gate when no signing service is present. An all-off policy never reaches it;
/// a policy with any switch on is refused, because no build may ship unsigned
/// under an enabled signing policy.
/// </summary>
public sealed class NoSigningServiceGate : ISigningCertificateGate
{
    public static readonly NoSigningServiceGate Instance = new();

    public const string NotLoadedMessage =
        "Script signing is requested but AppPackagerSigning is not loaded; no build may ship unsigned under an enabled signing policy.";

    public object? ResolveCertificate(JsonNode policy) => throw new InvalidOperationException(NotLoadedMessage);
}

/// <summary>Reads the switches of a signing policy without assuming the rest of its shape.</summary>
public static class SigningPolicySwitches
{
    public static readonly IReadOnlyList<string> Names =
        ["SignDetection", "SignRequirements", "SignDeployment", "RequireDetection", "RequireRequirements", "RequireDeployment"];

    /// <summary>True when any Sign or Require switch is on.</summary>
    public static bool IsSigningRequested(JsonNode? policy)
    {
        if (!WorkbenchJson.IsTruthy(policy)) return false;
        foreach (var name in Names)
        {
            var member = WorkbenchJson.Get(policy, name);
            if (member.Found && WorkbenchJson.IsTruthy(member.Value)) return true;
        }
        return false;
    }
}
