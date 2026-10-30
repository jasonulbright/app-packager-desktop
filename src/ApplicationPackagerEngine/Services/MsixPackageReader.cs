using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace ApplicationPackagerEngine.Services;

/// <summary>Package identity read from AppxManifest.xml, or from AppxMetadata\AppxBundleManifest.xml for a bundle.</summary>
public sealed class MsixIdentity
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Full publisher distinguished name, e.g. "CN=Contoso, O=Contoso".</summary>
    public string Publisher { get; init; } = "";
    /// <summary>The architecture of the package full name; "neutral" for a bundle.</summary>
    public string ProcessorArchitecture { get; init; } = "";
    /// <summary>The resource id of the package full name; "~" for a bundle, empty for a main package.</summary>
    public string ResourceId { get; init; } = "";
    public bool IsBundle { get; init; }
    /// <summary>The architectures the package runs on: its own, or those of the application packages of a bundle.</summary>
    public IReadOnlyList<string> PackageArchitectures { get; init; } = [];
    /// <summary>Package DisplayName from the Properties element, when present.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>Signature facts read from an MSIX/APPX AppxSignature.p7x.</summary>
public sealed class MsixSignature
{
    public string Thumbprint { get; init; } = "";
    public string Subject { get; init; } = "";
    /// <summary>
    /// True when the CMS signature is well formed and verifies over the signed data it carries. The digests in that data are
    /// not compared with the package files.
    /// </summary>
    public bool SignatureValid { get; init; }
    /// <summary>
    /// True when the signer certificate chains to a trusted root on this machine, at the current time or, for an
    /// expired certificate, at the time of a valid timestamp that falls inside the validity of the certificate.
    /// </summary>
    public bool ChainTrusted { get; init; }

    /// <summary>The time of the valid timestamp the trust decision used; null when none was used.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// True when the chain fails only because the signer certificate is outside its validity period and no valid
    /// timestamp inside that period exists, or the certificate has the lifetime signing usage.
    /// </summary>
    public bool CertificateExpired { get; init; }
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

    /// <summary>The largest AppxSignature.p7x read. A real part is a few kilobytes; the cap keeps a crafted part from exhausting memory.</summary>
    public const int MaxSignatureBytes = 1024 * 1024;

    /// <summary>The roots a chain is built against instead of the machine stores; null uses the machine stores.</summary>
    private readonly X509Certificate2Collection? _customRoots;

    public MsixPackageReader() { }

    internal MsixPackageReader(X509Certificate2Collection customRoots) => _customRoots = customRoots;

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

        ZipCentralDirectory.Check(path, InstallerSignatureScanner.MaxLayoutEntries, "read");
    }

    /// <summary>Reads Name/Version/Publisher/architecture from the package manifest.</summary>
    public MsixIdentity ReadIdentity(string path)
    {
        ValidateContainer(path);

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(ManifestEntry);
        var bundle = entry is null;
        entry ??= zip.GetEntry(BundleManifestEntry)
            ?? throw new InvalidOperationException($"{ManifestEntry} not found in MSIX: {Path.GetFileName(path)}");

        XDocument doc;
        using (var stream = entry.Open())
        using (var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
            doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidOperationException("MSIX manifest is empty.");

        var identity = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
            ?? throw new InvalidOperationException("MSIX manifest has no Identity element.");

        var name = (string?)identity.Attribute("Name") ?? "";
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("MSIX manifest Identity carries no Name.");

        var displayName = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;

        // The bundle Identity has no architecture: the bundle full name is always neutral with resource id "~",
        // and each application package in the Packages list names its own architecture.
        var arch = bundle ? "neutral" : (string?)identity.Attribute("ProcessorArchitecture") ?? "";
        var packageArchitectures = bundle ? BundleApplicationArchitectures(root) : arch.Length > 0 ? [arch] : [];

        return new MsixIdentity
        {
            Name = name,
            Version = (string?)identity.Attribute("Version") ?? "",
            Publisher = (string?)identity.Attribute("Publisher") ?? "",
            ProcessorArchitecture = arch,
            ResourceId = bundle ? "~" : (string?)identity.Attribute("ResourceId") ?? "",
            IsBundle = bundle,
            PackageArchitectures = packageArchitectures,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName
        };
    }

    /// <summary>
    /// The distinct architectures of the application packages of a bundle manifest, in document order. A package
    /// with no Type attribute is an application package.
    /// </summary>
    public static IReadOnlyList<string> BundleApplicationArchitectures(XElement bundleRoot) =>
        bundleRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "Packages")?.Elements()
            .Where(p => p.Name.LocalName == "Package"
                && ((string?)p.Attribute("Type") ?? "application").Equals("application", StringComparison.OrdinalIgnoreCase))
            .Select(p => ((string?)p.Attribute("Architecture") ?? "").Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    /// <summary>
    /// The architecture a bundle proposes for the definition: x64 when an application package is x64, else neutral
    /// when one is neutral, else the first application package architecture; empty when the bundle lists none.
    /// </summary>
    public static string BundleDefinitionArchitecture(IReadOnlyList<string> packageArchitectures)
    {
        if (packageArchitectures.Any(a => a.Equals("x64", StringComparison.OrdinalIgnoreCase))) return "x64";
        if (packageArchitectures.Any(a => a.Equals("neutral", StringComparison.OrdinalIgnoreCase))) return "neutral";
        return packageArchitectures.Count > 0 ? packageArchitectures[0] : "";
    }

    /// <summary>The package full name: Name_Version_Architecture_ResourceId_PublisherId.</summary>
    public static string PackageFullName(string name, string version, string architecture, string? resourceId, string publisher) =>
        $"{name}_{version}_{architecture}_{resourceId}_{PublisherHash(publisher)}";

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
            if (entry.Length > MaxSignatureBytes) throw TooLarge();
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            // The declared length can understate the stream, so the read stops one byte past the cap.
            stream.CopyTo(new LimitedWriter(buffer, MaxSignatureBytes, TooLarge));
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

        var (chainTrusted, onlyTime) = BuildChain(cert, cms.Certificates, at: null, CodeSigningOid);
        DateTimeOffset? timestamp = null;
        var expired = false;
        if (!chainTrusted && onlyTime)
        {
            timestamp = HasUsage(cert, LifetimeSigningOid) ? null : ValidTimestamp(signer!, cms.Certificates);
            chainTrusted = timestamp is { } at && BuildChain(cert, cms.Certificates, at, CodeSigningOid).Trusted;
            if (!chainTrusted) timestamp = null;
            expired = !chainTrusted;
        }

        return new MsixSignature
        {
            Thumbprint = cert.Thumbprint,
            Subject = cert.Subject,
            SignatureValid = signatureValid,
            ChainTrusted = chainTrusted,
            Timestamp = timestamp,
            CertificateExpired = expired
        };
    }

    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";
    private const string TimeStampingOid = "1.3.6.1.5.5.7.3.8";
    private const string LifetimeSigningOid = "1.3.6.1.4.1.311.10.3.13";

    /// <summary>The RFC 3161 timestamp token attribute of Authenticode, and the one of RFC 5544.</summary>
    private static readonly string[] TimestampTokenOids = ["1.3.6.1.4.1.311.3.3.1", "1.2.840.113549.1.9.16.2.14"];

    private const X509ChainStatusFlags TimeFlags = X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested;

    /// <summary>
    /// Builds the chain of <paramref name="certificate"/> at <paramref name="at"/> (now when null) for the usage.
    /// OnlyTime is true when the chain fails and every failure is a validity-period failure.
    /// </summary>
    private (bool Trusted, bool OnlyTime) BuildChain(X509Certificate2 certificate, X509Certificate2Collection extra, DateTimeOffset? at, string usage)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ExtraStore.AddRange(extra);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage));
            if (at is { } time) chain.ChainPolicy.VerificationTime = time.UtcDateTime;
            if (_customRoots is not null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(_customRoots);
            }
            if (chain.Build(certificate)) return (true, false);
            var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
            return (false, (flags & TimeFlags) != 0 && (flags & ~TimeFlags) == 0);
        }
        catch (CryptographicException)
        {
            return (false, false);
        }
    }

    private static bool HasUsage(X509Certificate2 certificate, string usage) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == usage));

    /// <summary>
    /// The time of the first timestamp token that signs this signer's signature, whose TSA certificate has the time
    /// stamping usage and chains at that time; null when there is none.
    /// </summary>
    private DateTimeOffset? ValidTimestamp(SignerInfo signer, X509Certificate2Collection extra)
    {
        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (!TimestampTokenOids.Contains(attribute.Oid?.Value)) continue;
            foreach (var value in attribute.Values)
            {
                if (!Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _)) continue;
                if (!token.VerifySignatureForSignerInfo(signer, out var tsa, extra) || tsa is null) continue;
                var time = token.TokenInfo.Timestamp;
                var pool = new X509Certificate2Collection(extra);
                pool.AddRange(token.AsSignedCms().Certificates);
                if (HasUsage(tsa, TimeStampingOid) && BuildChain(tsa, pool, time, TimeStampingOid).Trusted) return time;
            }
        }
        return null;
    }

    private static InvalidDataException TooLarge() =>
        new($"{SignatureEntry} is larger than {MaxSignatureBytes} bytes.");

    /// <summary>A write-only stream that throws once more than the limit has been written.</summary>
    private sealed class LimitedWriter(Stream inner, long limit, Func<Exception> exceeded) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (inner.Length + count > limit) throw exceeded();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (inner.Length + buffer.Length > limit) throw exceeded();
            inner.Write(buffer);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private const string CrockfordBase32 = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>
    /// The 13-character publisher id that completes a package full name: the
    /// first 64 bits of the SHA-256 of the UTF-16LE publisher string, padded
    /// with one zero bit to 65 bits and written in 5-bit groups over the
    /// Crockford base32 alphabet. The publisher string is hashed exactly as
    /// the manifest spells it; any whitespace change yields another id.
    /// </summary>
    public static string PublisherHash(string publisher)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(publisher));
        var bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest);
        var chars = new char[13];
        for (var i = 0; i < 13; i++)
        {
            // Group i covers bits [5i, 5i+5) of the 65-bit string; the last
            // group holds the final 4 digest bits and the zero pad bit.
            var shift = 64 - 5 * (i + 1);
            var group = shift >= 0 ? (int)((bits >> shift) & 0x1F) : (int)((bits << 1) & 0x1F);
            chars[i] = CrockfordBase32[group];
        }
        return new string(chars);
    }
}
