namespace ApplicationPackagerEngine.Models.Signing;

/// <summary>
/// Authenticode status names as Get-AuthenticodeSignature reports them, plus
/// NotFound for a missing path.
/// </summary>
public static class SignatureStatusName
{
    public const string Valid = "Valid";
    public const string UnknownError = "UnknownError";
    public const string NotSigned = "NotSigned";
    public const string HashMismatch = "HashMismatch";
    public const string NotTrusted = "NotTrusted";
    public const string NotSupportedFileFormat = "NotSupportedFileFormat";
    public const string Incompatible = "Incompatible";
    public const string NotFound = "NotFound";
}

/// <summary>
/// One signature verification. SignatureIntact means the hash and the
/// signature block still match the content; TrustedOnThisHost means this host
/// also trusts the signer chain. Timestamped means a timestamp
/// counter-signature exists; TimestampVerified means the trust provider
/// returned the timestamping certificate.
/// </summary>
public sealed class ScriptSignatureVerification
{
    public bool Valid { get; init; }
    public bool SignatureIntact { get; init; }
    public bool TrustedOnThisHost { get; init; }
    public string Status { get; init; } = SignatureStatusName.NotSigned;
    public string Thumbprint { get; init; } = "";
    public bool Timestamped { get; init; }
    public bool TimestampVerified { get; init; }
    /// <summary>SHA-256 of the verified bytes; set only by byte verification.</summary>
    public string Sha256 { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>One signing attempt on one file for one category.</summary>
public sealed class ScriptSigningResult
{
    public string Path { get; init; } = "";
    public SigningCategory Category { get; init; }
    public string Status { get; set; } = SigningStatus.NotRequested;
    public string Thumbprint { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Timestamped { get; set; }
    public bool TimestampVerified { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>A code-signing certificate offered for selection; the private key is never used.</summary>
public sealed class CodeSigningCandidate
{
    public string Thumbprint { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    public bool HasPrivateKey { get; init; }
    public bool KeyAccessible { get; init; }
    /// <summary>True when the key provider may need a PIN or a hardware token.</summary>
    public bool MayPrompt { get; init; }
    public string KeyProvider { get; init; } = "";
    public string StoreLocation { get; init; } = SigningPolicy.CurrentUser;
    public bool Usable { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>Private-key metadata read without producing a signature.</summary>
public sealed class SigningPrivateKeyInfo
{
    public bool HasPrivateKey { get; init; }
    public bool Accessible { get; set; }
    public bool MayPrompt { get; set; }
    public string Provider { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>Outcome of signing and verifying a throwaway script.</summary>
public sealed class SigningConfigurationTestResult
{
    public bool Ok { get; set; }
    public string Thumbprint { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public DateTime? NotAfter { get; set; }
    public string StoreLocation { get; set; } = SigningPolicy.CurrentUser;
    public string HashAlgorithm { get; set; } = "SHA256";
    public string TimestampServer { get; set; } = "";
    public bool TimestampRequired { get; set; }
    public string SignatureStatus { get; set; } = "NotAttempted";
    public bool SignatureIntact { get; set; }
    public bool TrustedOnThisHost { get; set; }
    public string StatusMessage { get; set; } = "";
    public bool Timestamped { get; set; }
    public bool TimestampVerified { get; set; }
    public bool MayPrompt { get; set; }
    public bool TimedOut { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>The byte, Base64 and text forms of a script plus its encoding.</summary>
public sealed class SignedScriptRepresentation
{
    public string Path { get; init; } = "";
    public byte[] Bytes { get; init; } = [];
    public string Base64 { get; init; } = "";
    public string Text { get; init; } = "";
    public string Encoding { get; init; } = "";
    public bool HasBom { get; init; }
    public string Sha256 { get; init; } = "";
    public int Length { get; init; }
    public bool ExceedsCMLimit { get; init; }
    public int CMScriptMaxBytes { get; init; }
}

/// <summary>One launcher policy finding: the file (or command source) and 1-based line.</summary>
public sealed class LauncherFinding
{
    public string Code { get; init; } = "";
    public string File { get; init; } = "";
    public int LineNumber { get; init; }
    public string Line { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>Result of the deployment launcher-chain audit.</summary>
public sealed class LauncherChainResult
{
    public IReadOnlyList<LauncherFinding> Findings { get; init; } = [];
    public int FilesInspected { get; init; }
    public int ThirdPartySkipped { get; init; }
    public int CommandsInspected { get; init; }
    public bool BypassFree => Findings.Count == 0;
    public bool Enforced { get; init; }
}

/// <summary>A resolved command line the audit inspects, named by where it came from.</summary>
public sealed record LauncherCommand(string Source, string Text);

/// <summary>Raised for every certificate-resolution refusal; the message starts with the type name.</summary>
public sealed class SigningCertificateUnavailableException(string reason)
    : InvalidOperationException("SigningCertificateUnavailable: " + reason);
