using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>
/// The final text and bytes of a Script-kind global condition and their
/// identity. Sha256 covers the bytes the site stores: the signed bytes when
/// the requirement category signed the script, otherwise the UTF-8 text
/// without a byte-order mark.
/// </summary>
public sealed record ConditionScriptIdentity(
    string Text, byte[] Bytes, string Sha256, bool Signed, string Thumbprint, string Status)
{
    /// <summary>The 8 hex characters a content-versioned condition name carries.</summary>
    public string ContentHash => Sha256[..8];

    /// <summary>
    /// Resolves a template under a policy. With SignRequirements or
    /// RequireRequirements set, the script is signed in a temporary file; a
    /// required signature that is not produced throws before any site object
    /// is written.
    /// </summary>
    public static ConditionScriptIdentity Resolve(ConditionTemplate template, SigningPolicy? policy = null,
        ScriptSigningService? signer = null)
    {
        var text = ConditionTemplateService.ResolveScriptText(template);
        var normalized = SigningPolicy.Normalize(policy);
        var status = SigningStatus.NotRequested;
        var thumbprint = "";
        // The Authenticode hash covers the byte-order mark, so the script is
        // written without one and the signed bytes travel to the site verbatim.
        var bytes = new UTF8Encoding(false).GetBytes(text);

        if (normalized.SignRequirements || normalized.RequireRequirements)
        {
            var temporary = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ps1");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                var result = (signer ?? new ScriptSigningService())
                    .SignForCategory(temporary, normalized, SigningCategory.Requirements);
                status = result.Status;
                thumbprint = result.Thumbprint;
                if (SigningStatus.IsSigned(status))
                {
                    bytes = File.ReadAllBytes(temporary);
                    text = Encoding.UTF8.GetString(bytes);
                }
                else if (normalized.RequireRequirements)
                {
                    throw new InvalidOperationException(
                        $"Requirement script signing is required and did not succeed for condition '{template.GlobalConditionName}': {status} ({result.Reason}).");
                }
            }
            finally
            {
                ScriptSignatureVerifier.TryDelete(temporary);
            }
        }

        return new ConditionScriptIdentity(text, bytes, ScriptSignatureVerifier.Sha256Hex(bytes),
            SigningStatus.IsSigned(status), thumbprint, status);
    }
}
