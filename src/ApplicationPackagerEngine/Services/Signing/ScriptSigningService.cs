using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>
/// Signs scripts in place per category and verifies the result. The Sign and
/// Require switches of a category are independent: Sign alone is best effort
/// and records Failed without refusing, Require without Sign accepts only an
/// already-intact signature, and a TimestampRequired policy throws when no
/// timestamp results.
/// </summary>
public sealed class ScriptSigningService
{
    private readonly SigningCertificateService _certificates;

    public ScriptSigningService(SigningCertificateService? certificates = null)
    {
        _certificates = certificates ?? new SigningCertificateService();
    }

    public SigningCertificateService Certificates => _certificates;

    /// <summary>
    /// Writes an Authenticode signature into the file through the operating
    /// system's signer. Returns the signer's error code, 0 when it reports
    /// success; the caller verifies the file to learn what was written.
    /// Throws ArgumentException for a malformed timestamp URL, an unknown hash
    /// algorithm or a certificate unfit for signing.
    /// </summary>
    public static uint SignFile(string path, X509Certificate2 certificate, string hashAlgorithm, string? timestampServer)
    {
        if (!string.IsNullOrEmpty(timestampServer)
            && (timestampServer.Length <= 7
                || !(timestampServer.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || timestampServer.StartsWith("https://", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("The timestamp server URL must begin with http:// or https://.", nameof(timestampServer));

        string? hashOid = null;
        if (!string.IsNullOrEmpty(hashAlgorithm))
        {
            hashOid = AuthenticodeNative.FindHashOid(hashAlgorithm)
                ?? throw new ArgumentException($"The hash algorithm '{hashAlgorithm}' is not valid.", nameof(hashAlgorithm));
        }

        if (!certificate.HasPrivateKey || !SigningCertificateService.IsCodeSigning(certificate))
            throw new ArgumentException("The certificate is not suitable for code signing.", nameof(certificate));
        if (!File.Exists(path))
            throw new FileNotFoundException("The file to sign was not found.", path);

        var error = AuthenticodeNative.SignFile(Path.GetFullPath(path), certificate, timestampServer, hashOid);
        if (error == AuthenticodeNative.NTE_BAD_ALGID)
            throw new ArgumentException($"The hash algorithm '{hashAlgorithm}' is not valid.", nameof(hashAlgorithm));
        return error;
    }

    /// <summary>
    /// Signs one script for a category and verifies it. PreserveExisting keeps
    /// an already-intact signature on a third-party file; an owned file is
    /// always re-signed under its Sign switch.
    /// </summary>
    public ScriptSigningResult SignForCategory(string path, SigningPolicy policy, SigningCategory category,
        bool preserveExisting = false, X509Certificate2? certificate = null)
    {
        var normalized = SigningPolicy.Normalize(policy);
        var sign = normalized.Sign(category);
        var require = normalized.Require(category);
        var result = new ScriptSigningResult { Path = path, Category = category };

        if (!File.Exists(path))
        {
            result.Status = SigningStatus.NotApplicable;
            result.Reason = $"File not found: {path}";
            return result;
        }

        if (!sign && !require)
        {
            result.Sha256 = ScriptSignatureVerifier.FileSha256Hex(path);
            return result;
        }

        var existing = ScriptSignatureVerifier.Verify(path);
        if (existing.SignatureIntact && (preserveExisting || !sign))
        {
            result.Status = SigningStatus.ExistingSignatureValid;
            result.Thumbprint = existing.Thumbprint;
            result.Timestamped = existing.Timestamped;
            result.TimestampVerified = existing.TimestampVerified;
            result.Sha256 = ScriptSignatureVerifier.FileSha256Hex(path);
            result.Reason = "Existing valid signature preserved.";
            return result;
        }

        if (!sign)
        {
            result.Status = SigningStatus.Failed;
            result.Sha256 = ScriptSignatureVerifier.FileSha256Hex(path);
            result.Reason = $"A valid signature is required for {category} but the file verifies as {existing.Status}. {existing.Reason}";
            return result;
        }

        var cert = certificate ?? _certificates.Resolve(normalized);
        var timestampConfigured = !string.IsNullOrWhiteSpace(normalized.TimestampServer);
        try
        {
            SignFile(path, cert, normalized.HashAlgorithm, timestampConfigured ? normalized.TimestampServer : null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException)
        {
            if (normalized.TimestampRequired)
                throw new InvalidOperationException(
                    $"Signing {category} script '{path}' failed and a timestamp is required: {ex.Message}", ex);
            result.Status = SigningStatus.Failed;
            result.Reason = ex.Message;
            result.Sha256 = ScriptSignatureVerifier.FileSha256Hex(path);
            return result;
        }

        var verify = ScriptSignatureVerifier.Verify(path);
        result.Thumbprint = verify.Thumbprint;
        result.Timestamped = verify.Timestamped;
        result.TimestampVerified = verify.TimestampVerified;
        result.Sha256 = ScriptSignatureVerifier.FileSha256Hex(path);

        if (normalized.TimestampRequired && !verify.Timestamped)
            throw new InvalidOperationException(
                $"TimestampRequired is set but the {category} script '{path}' carries no timestamp counter-signature (server '{normalized.TimestampServer}').");

        if (verify.SignatureIntact)
        {
            result.Status = SigningStatus.SignedAndVerified;
            if (!verify.TrustedOnThisHost)
                result.Reason = "Signature is intact; the signer chain is not trusted on this host, which says nothing about endpoint trust.";
        }
        else
        {
            result.Status = SigningStatus.Failed;
            result.Reason = $"Signature verification returned {verify.Status}. {verify.Reason}";
        }
        return result;
    }

    /// <summary>
    /// Signs and verifies a throwaway script without publishing anything.
    /// The signing call runs on its own thread with a timeout, so a key that
    /// waits for a PIN or a hardware token blocks that thread, not the caller.
    /// Expected failures are returned, not thrown.
    /// </summary>
    public SigningConfigurationTestResult TestConfiguration(SigningPolicy policy, TimeSpan? timeout = null)
    {
        var normalized = SigningPolicy.Normalize(policy);
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var result = new SigningConfigurationTestResult
        {
            Thumbprint = normalized.CertificateThumbprint,
            StoreLocation = normalized.StoreLocation,
            HashAlgorithm = normalized.HashAlgorithm,
            TimestampServer = normalized.TimestampServer,
            TimestampRequired = normalized.TimestampRequired
        };

        X509Certificate2 cert;
        try { cert = _certificates.Resolve(normalized); }
        catch (SigningCertificateUnavailableException ex)
        {
            result.Reason = ex.Message;
            return result;
        }

        result.Subject = cert.Subject;
        result.Issuer = cert.Issuer;
        result.NotAfter = cert.NotAfter;
        result.MayPrompt = SigningCertificateService.ReadPrivateKeyInfo(cert).MayPrompt;

        var temp = ScriptSignatureVerifier.NewTempScriptPath();
        File.WriteAllText(temp, "Write-Output 'AppPackager signing test'\r\nexit 0", new ASCIIEncoding());

        var timestamp = string.IsNullOrWhiteSpace(normalized.TimestampServer) ? null : normalized.TimestampServer;
        var signing = Task.Factory.StartNew(
            () => SignFile(temp, cert, normalized.HashAlgorithm, timestamp),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (!signing.Wait(limit))
            {
                result.TimedOut = true;
                result.Reason = $"Signing did not complete within {(int)limit.TotalSeconds} seconds; the private key may require interactive confirmation.";
                signing.ContinueWith(_ => ScriptSignatureVerifier.TryDelete(temp), TaskScheduler.Default);
                return result;
            }
        }
        catch (AggregateException ex)
        {
            result.Reason = ex.InnerException?.Message ?? "Signing failed.";
            ScriptSignatureVerifier.TryDelete(temp);
            return result;
        }

        try
        {
            var verify = ScriptSignatureVerifier.Verify(temp);
            result.SignatureStatus = verify.Status;
            result.SignatureIntact = verify.SignatureIntact;
            result.TrustedOnThisHost = verify.TrustedOnThisHost;
            result.StatusMessage = verify.Reason;
            result.Timestamped = verify.Timestamped;
            result.TimestampVerified = verify.TimestampVerified;

            if (normalized.TimestampRequired && !verify.TimestampVerified)
            {
                result.Reason = "A timestamp is required but no verifiable timestamp counter-signature was produced.";
                return result;
            }
            if (!verify.SignatureIntact)
            {
                result.Reason = $"Signature verification returned {verify.Status}. {verify.Reason}";
                return result;
            }
            if (!verify.TrustedOnThisHost)
                result.Reason = "Signature is intact; the signer chain is not trusted on this host. Endpoints need the publisher trust chain.";
            result.Ok = true;
            return result;
        }
        finally
        {
            ScriptSignatureVerifier.TryDelete(temp);
        }
    }
}
