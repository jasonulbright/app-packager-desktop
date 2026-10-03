using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Services.Signing;

/// <summary>
/// Verifies script signatures and reads scripts in the byte-exact forms a
/// signature survives. Intact versus broken is decided by the trust result
/// and a chain build that ignores trust, never by localized status text.
/// </summary>
public static class ScriptSignatureVerifier
{
    /// <summary>ConfigMgr refuses a detection script larger than this; the signature block counts toward it.</summary>
    public const int ConfigMgrDetectionScriptMaxBytes = 32768;

    /// <summary>The RFC 3161 timestamp token attribute inside an Authenticode signer.</summary>
    public const string Rfc3161TimestampOid = "1.3.6.1.4.1.311.3.3.1";

    public const string BeginMarker = "# SIG # Begin signature block";
    public const string EndMarker = "# SIG # End signature block";

    private const string TempPrefix = "appackager-sign-";

    static ScriptSignatureVerifier()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string NewTempScriptPath() =>
        Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.ps1");

    public static ScriptSignatureVerification Verify(string path)
    {
        if (!File.Exists(path))
        {
            return new ScriptSignatureVerification
            {
                Status = SignatureStatusName.NotFound,
                Reason = $"File not found: {path}"
            };
        }

        var (error, signer, timestamper) = AuthenticodeNative.VerifyFile(Path.GetFullPath(path));
        using (signer)
        using (timestamper)
        {
            var status = StatusFromError(error);
            var thumbprint = signer?.Thumbprint.ToUpperInvariant() ?? "";
            var timestampVerified = timestamper is not null;
            var timestamped = timestampVerified || HasCounterSignature(path);

            // A hash or content failure reports its own status, so UnknownError
            // with a signer present is a chain-policy failure; rebuilding the
            // chain with trust ignored separates an untrusted signer from a
            // broken signature.
            var trusted = status == SignatureStatusName.Valid;
            var intact = trusted;
            if (!intact && signer is not null && status == SignatureStatusName.UnknownError)
                intact = ChainIgnoringTrust(signer);

            return new ScriptSignatureVerification
            {
                Valid = trusted,
                SignatureIntact = intact,
                TrustedOnThisHost = trusted,
                Status = status,
                Thumbprint = thumbprint,
                Timestamped = timestamped,
                TimestampVerified = timestampVerified,
                Reason = error == 0 ? "Signature verified." : new Win32Exception(unchecked((int)error)).Message
            };
        }
    }

    /// <summary>Verifies a script held as bytes by materializing it unchanged in a temporary file.</summary>
    public static ScriptSignatureVerification VerifyBytes(byte[] bytes)
    {
        var temp = NewTempScriptPath();
        try
        {
            File.WriteAllBytes(temp, bytes);
            var result = Verify(temp);
            return new ScriptSignatureVerification
            {
                Valid = result.Valid,
                SignatureIntact = result.SignatureIntact,
                TrustedOnThisHost = result.TrustedOnThisHost,
                Status = result.Status,
                Thumbprint = result.Thumbprint,
                Timestamped = result.Timestamped,
                TimestampVerified = result.TimestampVerified,
                Sha256 = Sha256Hex(bytes),
                Reason = result.Reason
            };
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Builds the signer chain with an unknown authority allowed and no
    /// revocation lookup: a self-signed or privately issued signer passes, an
    /// expired signer or an explicitly distrusted one does not.
    /// </summary>
    public static bool ChainIgnoringTrust(X509Certificate2 certificate)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            return chain.Build(certificate);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the script's signature block carries a timestamp: a
    /// counter-signer or an RFC 3161 token attribute. The attribute is
    /// present even when this host does not trust the timestamping chain.
    /// </summary>
    public static bool HasCounterSignature(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "(?s)" + Regex.Escape(BeginMarker) + "(.*?)" + Regex.Escape(EndMarker));
            if (!match.Success) return false;
            var b64 = string.Concat(match.Groups[1].Value.Split('\n')
                .Select(l => Regex.Replace(l, @"^\s*#\s?", "").Trim()));
            if (string.IsNullOrWhiteSpace(b64)) return false;
            var cms = new SignedCms();
            cms.Decode(Convert.FromBase64String(b64));
            foreach (var signer in cms.SignerInfos)
            {
                if (signer.CounterSignerInfos.Count > 0) return true;
                foreach (var attribute in signer.UnsignedAttributes)
                    if (attribute.Oid?.Value == Rfc3161TimestampOid) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a script in its byte, Base64 and text forms. Text is decoded with
    /// the file's own encoding, so writing it back with that encoding
    /// reproduces the original bytes; any other encoding breaks the signature.
    /// </summary>
    public static SignedScriptRepresentation Represent(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (name, hasBom, encoding) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes);
        if (hasBom && text.Length > 0 && text[0] == '﻿') text = text[1..];
        return new SignedScriptRepresentation
        {
            Path = path,
            Bytes = bytes,
            Base64 = Convert.ToBase64String(bytes),
            Text = text,
            Encoding = name,
            HasBom = hasBom,
            Sha256 = Sha256Hex(bytes),
            Length = bytes.Length,
            ExceedsCMLimit = bytes.Length > ConfigMgrDetectionScriptMaxBytes,
            CMScriptMaxBytes = ConfigMgrDetectionScriptMaxBytes
        };
    }

    /// <summary>
    /// The encoding of script bytes: a byte-order mark decides first; without
    /// one, all-ASCII content is ASCII, strict UTF-8 is UTF8NoBOM, and
    /// anything else is the ANSI code page Windows PowerShell assumes.
    /// </summary>
    public static (string Name, bool HasBom, Encoding Encoding) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return ("UTF8BOM", true, new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return ("UTF16LE", true, new UnicodeEncoding(false, true));
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return ("UTF16BE", true, new UnicodeEncoding(true, true));
        if (bytes.All(b => b <= 0x7F))
            return ("ASCII", false, new ASCIIEncoding());
        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return ("UTF8NoBOM", false, new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            return ("ANSI", false, AnsiEncoding());
        }
    }

    /// <summary>The encoding a manifest names for script text, or UTF-8 without a BOM.</summary>
    public static Encoding EncodingByName(string? name) => name switch
    {
        "UTF8BOM" => new UTF8Encoding(true),
        "UTF16LE" => new UnicodeEncoding(false, true),
        "UTF16BE" => new UnicodeEncoding(true, true),
        "ASCII" => new ASCIIEncoding(),
        "ANSI" => AnsiEncoding(),
        _ => new UTF8Encoding(false)
    };

    public static Encoding AnsiEncoding() =>
        Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string FileSha256Hex(string path) => Sha256Hex(File.ReadAllBytes(path));

    /// <summary>The status name Get-AuthenticodeSignature reports for a WinVerifyTrust result.</summary>
    public static string StatusFromError(uint error) => error switch
    {
        0 => SignatureStatusName.Valid,
        AuthenticodeNative.NTE_BAD_ALGID => SignatureStatusName.Incompatible,
        AuthenticodeNative.TRUST_E_NOSIGNATURE => SignatureStatusName.NotSigned,
        AuthenticodeNative.TRUST_E_BAD_DIGEST or AuthenticodeNative.CRYPT_E_BAD_MSG => SignatureStatusName.HashMismatch,
        AuthenticodeNative.TRUST_E_PROVIDER_UNKNOWN => SignatureStatusName.NotSupportedFileFormat,
        AuthenticodeNative.TRUST_E_EXPLICIT_DISTRUST => SignatureStatusName.NotTrusted,
        _ => SignatureStatusName.UnknownError
    };

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
