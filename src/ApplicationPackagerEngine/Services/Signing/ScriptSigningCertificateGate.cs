using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Signing;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>
/// The run-snapshot certificate gate backed by the signing service. The run
/// snapshot writer calls it for any policy with a Sign or Require switch on,
/// and it resolves by thumbprint with the full SigningCertificateUnavailable
/// refusal set, so an unusable certificate fails the run before any content
/// is written.
/// </summary>
public sealed class ScriptSigningCertificateGate(SigningCertificateService? certificates = null) : ISigningCertificateGate
{
    private readonly SigningCertificateService _certificates = certificates ?? new SigningCertificateService();

    public object? ResolveCertificate(JsonNode policy) =>
        _certificates.Resolve(SigningPolicy.FromJsonNode(policy));
}
