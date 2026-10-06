using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Signing;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Models;

/// <summary>
/// Operator settings a definition stage reads besides its install parameters.
/// A definition that declares none of the matching fields ignores them.
/// </summary>
/// <summary>The SHA-256 a staged installer copy must have, and the source it is recorded for.</summary>
public sealed record ExpectedInstaller(string Sha256, string Source);

public sealed class StageInputs
{
    /// <summary>The folder chosen for this definition's local installer source.</summary>
    public string? LocalSourceFolder { get; init; }

    /// <summary>Chosen side-file paths by SideFileConfig.Key.</summary>
    public IReadOnlyDictionary<string, string>? SideFiles { get; init; }

    /// <summary>Citrix Workspace switch settings for a definition that names a switch stream.</summary>
    public CitrixWorkspaceSwitches? CitrixSwitches { get; init; }

    /// <summary>Include optional additional architecture payloads declared by the definition.</summary>
    public bool IncludeArchitectureVariants { get; init; }

    /// <summary>The run snapshot whose profile this build applies; null builds without a profile.</summary>
    public RunSnapshot? RunSnapshot { get; init; }

    /// <summary>The global signing policy; a policy in the run snapshot takes its place.</summary>
    public SigningPolicy? SigningPolicy { get; init; }

    /// <summary>Resolves the signing certificate before any content is written; null uses the signing service.</summary>
    public ISigningCertificateGate? SigningGate { get; init; }

    /// <summary>Signs the stage; null uses a service over the current user's certificate stores.</summary>
    public ScriptSigningService? Signer { get; init; }
}
