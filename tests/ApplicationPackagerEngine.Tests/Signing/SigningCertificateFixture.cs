using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ApplicationPackagerEngine.Models.Signing;

namespace ApplicationPackagerEngine.Tests.Signing;

/// <summary>
/// Throwaway code-signing certificates in CurrentUser\My, removed with their
/// private keys on dispose. Nothing is added to a Root or TrustedPublisher
/// store: that raises an interactive consent dialog and changes machine
/// trust. Assertions therefore use SignatureIntact, not host trust: an
/// untrusted self-signed signer reports UnknownError with an intact signature.
/// </summary>
public sealed class SigningCertificateFixture : IDisposable
{
    public X509Certificate2 Signer { get; }
    public string Thumbprint => Signer.Thumbprint.ToUpperInvariant();
    public string ExpiredThumbprint { get; }
    public string PublicOnlyThumbprint { get; }
    public string Root { get; }

    private readonly List<X509Certificate2> _added = [];

    public SigningCertificateFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "apsign " + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        try
        {
            Signer = AddWithKey("CN=AppPackager Desktop Unit Test Signing", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(2));
            ExpiredThumbprint = AddWithKey("CN=AppPackager Desktop Unit Test Expired", DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now.AddDays(-1)).Thumbprint;
            using var keyless = CreateSelfSigned("CN=AppPackager Desktop Unit Test Keyless", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(2));
            var publicOnly = X509CertificateLoader.LoadCertificate(keyless.RawData);
            AddToStore(publicOnly);
            PublicOnlyThumbprint = publicOnly.Thumbprint;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SigningPolicy Policy(bool sd = false, bool sr = false, bool sp = false,
        bool rd = false, bool rr = false, bool rp = false,
        string timestamp = "", bool timestampRequired = false, string? thumbprint = null) => new()
    {
        SignDetection = sd, SignRequirements = sr, SignDeployment = sp,
        RequireDetection = rd, RequireRequirements = rr, RequireDeployment = rp,
        CertificateThumbprint = thumbprint ?? Thumbprint, StoreLocation = SigningPolicy.CurrentUser,
        TimestampServer = timestamp, TimestampRequired = timestampRequired, HashAlgorithm = "SHA256"
    };

    public string NewDirectory(string name)
    {
        var dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static X509Certificate2 CreateSelfSigned(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private X509Certificate2 AddWithKey(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var ephemeral = CreateSelfSigned(subject, notBefore, notAfter);
        var pfx = ephemeral.Export(X509ContentType.Pfx, "fixture");
        var persisted = X509CertificateLoader.LoadPkcs12(pfx, "fixture",
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        AddToStore(persisted);
        return persisted;
    }

    private void AddToStore(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        _added.Add(certificate);
    }

    public void Dispose()
    {
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var cert in _added)
            {
                foreach (var entry in store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false))
                    store.Remove(entry);
                DeleteKey(cert);
                cert.Dispose();
            }
        }
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DeleteKey(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey) return;
        try
        {
            using var key = cert.GetRSAPrivateKey();
            switch (key)
            {
                case RSACng cng: cng.Key.Delete(); break;
                case RSACryptoServiceProvider csp: csp.PersistKeyInCsp = false; csp.Clear(); break;
            }
        }
        catch (CryptographicException) { }
    }

    /// <summary>
    /// Runs a Windows PowerShell 5.1 command and returns its standard output
    /// lines. Running a -Command string needs no execution-policy argument.
    /// </summary>
    public static string[] RunWindowsPowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        // A PSModulePath inherited from PowerShell 7 makes Windows PowerShell load
        // the 7.x security module, and Get-AuthenticodeSignature is then missing.
        psi.Environment.Remove("PSModulePath");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("$ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " + command);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000), "Windows PowerShell did not return");
        Assert.True(proc.ExitCode == 0, $"Windows PowerShell exited {proc.ExitCode}: {stderr}");
        return stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    public static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";
}

[CollectionDefinition(Name)]
public sealed class SigningCollection : ICollectionFixture<SigningCertificateFixture>
{
    public const string Name = "Signing certificate";
}
