using System.IO.Compression;
using System.Security.Cryptography.Pkcs;
using System.Xml.Linq;

namespace ApplicationPackagerEngine.Services;

/// <summary>Package identity read from an MSIX/APPX AppxManifest.xml.</summary>
public sealed class MsixIdentity
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Full publisher distinguished name, e.g. "CN=Contoso, O=Contoso".</summary>
    public string Publisher { get; init; } = "";
    public string ProcessorArchitecture { get; init; } = "";
    /// <summary>Package DisplayName from the Properties element, when present.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>Signature facts read from an MSIX/APPX AppxSignature.p7x.</summary>
public sealed class MsixSignature
{
    public string Thumbprint { get; init; } = "";
    public string Subject { get; init; } = "";
    /// <summary>True when the CMS signature verifies over the package's signed content.</summary>
    public bool SignatureValid { get; init; }
    /// <summary>True when the signer certificate chains to a trusted root on this machine.</summary>
    public bool ChainTrusted { get; init; }
}

/// <summary>
/// Reads identity and signing facts out of an MSIX/APPX container without
/// installing it. An MSIX is an OPC (zip) package: identity lives in
/// AppxManifest.xml and the signature in AppxSignature.p7x.
/// </summary>
public sealed class MsixPackageReader
{
    private const string ManifestEntry = "AppxManifest.xml";
    private const string BundleManifestEntry = "AppxMetadata/AppxBundleManifest.xml";
    private const string SignatureEntry = "AppxSignature.p7x";

    /// <summary>
    /// Throws unless the file starts with the zip local-file header. A vendor
    /// redirect that lands on an HTML error page would otherwise stage as a
    /// valid-looking package and fail only when the AppX cmdlet opens it.
    /// </summary>
    public static void ValidateContainer(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("MSIX package not found", path);

        Span<byte> head = stackalloc byte[4];
        using (var fs = File.OpenRead(path))
        {
            if (fs.Read(head) < 4)
                throw new InvalidOperationException($"Downloaded MSIX is too small to be a package: {Path.GetFileName(path)}");
        }

        if (head[0] != 0x50 || head[1] != 0x4B)
            throw new InvalidOperationException(
                $"Downloaded file is not an MSIX container (no zip header) - the vendor endpoint likely returned an HTML page: {Path.GetFileName(path)}");
    }

    /// <summary>Reads Name/Version/Publisher/architecture from the package manifest.</summary>
    public MsixIdentity ReadIdentity(string path)
    {
        ValidateContainer(path);

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(ManifestEntry) ?? zip.GetEntry(BundleManifestEntry)
            ?? throw new InvalidOperationException($"{ManifestEntry} not found in MSIX: {Path.GetFileName(path)}");

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var root = doc.Root ?? throw new InvalidOperationException("MSIX manifest is empty.");

        var identity = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
            ?? throw new InvalidOperationException("MSIX manifest has no Identity element.");

        var name = (string?)identity.Attribute("Name") ?? "";
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("MSIX manifest Identity carries no Name.");

        // A bundle manifest spells the architecture per-package; the bundle
        // Identity itself has none.
        var arch = (string?)identity.Attribute("ProcessorArchitecture")
            ?? (string?)identity.Attribute("Architecture")
            ?? "";

        var displayName = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;

        return new MsixIdentity
        {
            Name = name,
            Version = (string?)identity.Attribute("Version") ?? "",
            Publisher = (string?)identity.Attribute("Publisher") ?? "",
            ProcessorArchitecture = arch,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName
        };
    }

    /// <summary>
    /// Reads the signing certificate from AppxSignature.p7x and reports whether
    /// the CMS signature verifies and whether the signer chains to a trusted
    /// root. The AppX cmdlets refuse an unsigned or untrusted package, so the
    /// thumbprint is pinned into the install wrapper at stage time.
    /// Returns null when the package carries no signature part.
    /// </summary>
    public MsixSignature? ReadSignature(string path)
    {
        ValidateContainer(path);

        byte[] p7x;
        using (var zip = ZipFile.OpenRead(path))
        {
            var entry = zip.GetEntry(SignatureEntry);
            if (entry is null) return null;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            p7x = buffer.ToArray();
        }

        // A .p7x is the 4-byte 'PKCX' marker followed by the DER PKCS#7 blob.
        var offset = p7x.Length >= 4 && p7x[0] == (byte)'P' && p7x[1] == (byte)'K'
            && p7x[2] == (byte)'C' && p7x[3] == (byte)'X' ? 4 : 0;
        if (p7x.Length <= offset)
            throw new InvalidOperationException($"{SignatureEntry} is empty in {Path.GetFileName(path)}.");

        var cms = new SignedCms();
        cms.Decode(p7x.AsSpan(offset).ToArray());

        var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
        var cert = signer?.Certificate
            ?? throw new InvalidOperationException($"{SignatureEntry} carries no signer certificate.");

        var signatureValid = true;
        try { cms.CheckSignature(verifySignatureOnly: true); }
        catch (System.Security.Cryptography.CryptographicException) { signatureValid = false; }

        var chainTrusted = false;
        try
        {
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            chainTrusted = chain.Build(cert);
        }
        catch (System.Security.Cryptography.CryptographicException) { }

        return new MsixSignature
        {
            Thumbprint = cert.Thumbprint,
            Subject = cert.Subject,
            SignatureValid = signatureValid,
            ChainTrusted = chainTrusted
        };
    }
}
