using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>A temp folder with installer files built in the test, a real sandbox and the services over them.</summary>
internal sealed class AddFromInstallerTestBed : IDisposable
{
    public const string ProductCode = "{6F1A2B3C-4D5E-4F60-8A7B-9C0D1E2F3A4B}";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "apd-afi-" + Guid.NewGuid().ToString("N"));
    public string Sandbox => Path.Combine(Root, "sandbox");
    public string Definitions => Path.Combine(Root, "definitions");

    public AddFromInstallerTestBed()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sandbox);
    }

    public string PathOf(string name) => Path.Combine(Root, name);

    /// <summary>The service over the real Installer Analysis service with a sandbox in the temp folder.</summary>
    public AddFromInstallerService Real(AnalysisLimits? limits = null) =>
        new(new InstallerAnalysisService(new AnalysisSandbox(Sandbox)), new DroppedInstallerAnalysisService(new NativeMsiInspector()), limits);

    /// <summary>The service over fixed reports; the local proposal reads the real file with no MSI reader.</summary>
    public AddFromInstallerService Over(FakeInstallerAnalysisService fake) =>
        new(fake, new DroppedInstallerAnalysisService(new FakeMsiInspector { Throws = true }));

    public string Fixture(string name, string asFileName)
    {
        var target = PathOf(asFileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), target, true);
        return target;
    }

    /// <summary>A PE-looking file with an ASCII engine marker, as the signature scanner reads it.</summary>
    public string Marked(string fileName, string? marker)
    {
        var bytes = new List<byte> { (byte)'M', (byte)'Z' };
        bytes.AddRange(Enumerable.Repeat((byte)0, 510));
        if (marker is not null) bytes.AddRange(Encoding.ASCII.GetBytes(marker));
        var path = PathOf(fileName);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    public string Msi(string fileName = "contoso.msi", string allUsers = "1", string template = "x64;1033")
    {
        var path = PathOf(fileName);
        using (var b = new TestMsiBuilder(path))
        {
            b.Property("ProductCode", ProductCode)
             .Property("ProductName", "Contoso Tool")
             .Property("Manufacturer", "Contoso Ltd")
             .Property("ProductVersion", "4.5.6")
             .Property("UpgradeCode", "{0A1B2C3D-0000-4000-8000-000000000001}");
            if (allUsers.Length > 0) b.Property("ALLUSERS", allUsers);
            b.Template(template);
        }
        return path;
    }

    public string Msix(string fileName = "contoso.msix", string name = "Contoso.Tool", bool withDependency = true, string architecture = "x64", byte[]? signature = null)
    {
        var dependency = withDependency
            ? "<Dependencies><TargetDeviceFamily Name=\"Windows.Desktop\" MinVersion=\"10.0.17763.0\" MaxVersionTested=\"10.0.22621.0\"/><PackageDependency Name=\"Microsoft.VCLibs.140.00.UWPDesktop\" MinVersion=\"14.0.30704.0\" Publisher=\"CN=Microsoft Corporation\"/></Dependencies>"
            : "<Dependencies><TargetDeviceFamily Name=\"Windows.Desktop\" MinVersion=\"10.0.17763.0\" MaxVersionTested=\"10.0.22621.0\"/></Dependencies>";
        var manifest = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">"
            + $"<Identity Name=\"{name}\" Publisher=\"CN=Contoso Ltd\" Version=\"2.3.4.0\" ProcessorArchitecture=\"{architecture}\"/>"
            + "<Properties><DisplayName>Contoso Tool</DisplayName><PublisherDisplayName>Contoso Ltd</PublisherDisplayName></Properties>"
            + dependency
            + "<Applications><Application Id=\"App\" Executable=\"Contoso.exe\"/></Applications></Package>";
        var entries = new List<(string Name, byte[] Data)>
        {
            ("AppxManifest.xml", InstallerAnalysisTestFiles.Text(manifest)),
            ("[Content_Types].xml", InstallerAnalysisTestFiles.Text("<Types/>")),
            ("Contoso.exe", [(byte)'M', (byte)'Z', 0, 0])
        };
        if (signature is not null) entries.Add(("AppxSignature.p7x", signature));
        return InstallerAnalysisTestFiles.WriteZip(PathOf(fileName), [.. entries]);
    }

    /// <summary>
    /// A bundle as the Store and winget ship it: the identity is in AppxMetadata\AppxBundleManifest.xml, the bundle
    /// Identity has no architecture, and each application package names its own.
    /// </summary>
    public string MsixBundle(string fileName = "contoso.msixbundle", string name = "Contoso.Tool", string version = "3001.2.3.0",
        string[]? architectures = null, byte[]? signature = null)
    {
        var packages = string.Concat((architectures ?? ["x64", "x86"]).Select(a =>
            $"<Package Type=\"application\" Version=\"1.2.3.0\" Architecture=\"{a}\" FileName=\"{name}_1.2.3.0_{a}.msix\" Offset=\"0\" Size=\"1\"/>"))
            + $"<Package Type=\"resource\" Version=\"1.2.3.0\" ResourceId=\"split.scale-100\" FileName=\"{name}_scale-100.msix\" Offset=\"0\" Size=\"1\"/>";
        var manifest = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<Bundle xmlns=\"http://schemas.microsoft.com/appx/2013/bundle\" SchemaVersion=\"5.0\">"
            + $"<Identity Name=\"{name}\" Publisher=\"CN=Contoso Ltd\" Version=\"{version}\"/>"
            + $"<Packages>{packages}</Packages></Bundle>";
        var entries = new List<(string Name, byte[] Data)>
        {
            ("AppxMetadata/AppxBundleManifest.xml", InstallerAnalysisTestFiles.Text(manifest)),
            ("[Content_Types].xml", InstallerAnalysisTestFiles.Text("<Types/>"))
        };
        if (signature is not null) entries.Add(("AppxSignature.p7x", signature));
        return InstallerAnalysisTestFiles.WriteZip(PathOf(fileName), [.. entries]);
    }

    public static AnalysisReport Report(string path, string type, IReadOnlyList<AnalysisFact> deployment,
        IReadOnlyList<MetadataBlock>? metadata = null, IReadOnlyList<InstallMode>? modes = null, EffectiveDetection? postPatch = null,
        string architecture = "x64") => new(
        Path: path,
        FileName: Path.GetFileName(path),
        Size: new FileInfo(path).Length,
        Sha256: StagingService.ComputeSha256(path),
        Type: type,
        Architecture: architecture,
        Signature: null,
        Deployment: deployment,
        Modes: modes ?? [],
        Metadata: metadata ?? [],
        MsiProperties: [],
        PostPatch: postPatch,
        Notes: [])
    {
        TypeName = InstallerAnalysisService.TypeDisplayName(type)
    };

    public static AnalysisFact Fact(string name, string value, string confidence, string source) =>
        new(Name: name, Value: value, Confidence: confidence, Source: source);

    public void Dispose()
    {
        TestCleanup.DeleteDirectory(Root);
    }
}
