using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The architecture published to Graph describes the installer payload. The detection's
/// Is64Bit describes the registry or file system view a rule reads, and the two diverge:
/// a per-user x64 product writes its uninstall entry where a 32-bit view finds it.
/// Deriving one from the other publishes an x64 product as x86.
/// </summary>
public sealed class InstallerArchitectureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "packr-arch-" + Guid.NewGuid().ToString("N"));

    public InstallerArchitectureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── Resolution ──────────────────────────────────────────────

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("X64", "x64")]
    [InlineData("x86", "x86")]
    [InlineData("X86", "x86")]
    [InlineData("ARM64", "ARM64")]
    [InlineData("arm64", "ARM64")]
    [InlineData("  x64  ", "x64")]
    public void ASupportedArchitectureResolvesToItsCanonicalSpelling(string declared, string expected) =>
        Assert.Equal(expected, InstallerArchitectures.Resolve(declared));

    /// <summary>
    /// A stage written before the field existed carries no architecture. The chosen
    /// policy reads it as x64, the default the publish options already applied.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentArchitectureTakesTheCompatibilityDefault(string? declared)
    {
        Assert.Equal("x64", InstallerArchitectures.Resolve(declared));
        Assert.Equal("x64", InstallerArchitectures.LegacyDefault);
    }

    [Theory]
    [InlineData("amd64")]
    [InlineData("x32")]
    [InlineData("arm")]
    [InlineData("Any CPU")]
    [InlineData("neutral")]
    public void AnUnsupportedArchitectureIsRefusedRatherThanRemapped(string declared)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InstallerArchitectures.Resolve(declared));
        Assert.Contains(declared, ex.Message);
        Assert.Contains("Unsupported installer architecture", ex.Message);
    }

    // ── The registry view is not the architecture ───────────────

    /// <summary>
    /// The regression case. vscode-user declares x64 and detects through the 32-bit view,
    /// so the old expression published it as x86.
    /// </summary>
    [Fact]
    public void TheRealVsCodeUserDefinitionPublishesAsX64DespiteItsThirtyTwoBitView()
    {
        var def = DefinitionFixture.Load("vscode-user.json");

        Assert.Equal("x64", def.Architecture);
        Assert.False(def.Detection.Is64Bit);

        var manifest = new StageManifest { Architecture = def.Architecture, Detection = { Is64Bit = false } };

        Assert.Equal("x64", InstallerArchitectures.Resolve(manifest));
        // What the detection view would have produced.
        Assert.Equal("x86", manifest.Detection.Is64Bit ? "x64" : "x86");
    }

    [Theory]
    [InlineData("x64", false, "x64")]
    [InlineData("x64", true, "x64")]
    [InlineData("x86", false, "x86")]
    [InlineData("x86", true, "x86")]
    [InlineData("ARM64", false, "ARM64")]
    public void TheDetectionViewNeverChangesTheResolvedArchitecture(
        string declared, bool is64BitView, string expected)
    {
        var manifest = new StageManifest { Architecture = declared, Detection = { Is64Bit = is64BitView } };

        Assert.Equal(expected, InstallerArchitectures.Resolve(manifest));
    }

    // ── Production manifest construction ────────────────────────

    private string WriteInstaller(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x00, 512)]);
        return path;
    }

    private static DroppedInstallerAnalysisService MsiService(string architecture) =>
        new(new FakeMsiInspector
        {
            TemplateArchitecture = architecture,
            Properties = new MsiProperties
            {
                ProductName = "Contoso Tool",
                Manufacturer = "Contoso",
                ProductVersion = "1.2.3",
                ProductCode = "{23170F69-40C1-2702-2408-000001000000}",
                AllUsers = "1"
            }
        });

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("Intel", "x86")]
    [InlineData("Arm64", "ARM64")]
    public async Task ADroppedInstallerCarriesItsAnalyzedArchitectureIntoTheManifest(
        string templateArchitecture, string expected)
    {
        var service = MsiService(templateArchitecture);
        var analysis = await service.AnalyzeAsync(WriteInstaller("contoso.msi"));

        var manifest = service.BuildStageManifest(analysis);

        Assert.Equal(expected, manifest.Architecture);
        Assert.Equal(expected, InstallerArchitectures.Resolve(manifest));
    }

    // ── Serialization ───────────────────────────────────────────

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    [InlineData("ARM64")]
    public void ArchitectureSurvivesAManifestRoundtrip(string architecture)
    {
        var folder = Path.Combine(_root, "stage-" + architecture);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "install.ps1"), "exit 0");
        var path = Path.Combine(folder, "stage-manifest.json");

        var service = new ManifestService();
        service.Write(path, new StageManifest
        {
            AppName = "Contoso Tool",
            Publisher = "Contoso",
            SoftwareVersion = "1.2.3",
            Architecture = architecture
        });

        var read = service.Read(path);

        Assert.Equal(architecture, read.Architecture);
        Assert.Equal(architecture, InstallerArchitectures.Resolve(read));
    }

    /// <summary>A manifest staged before the field existed has no Architecture key at all.</summary>
    [Fact]
    public void AManifestWithNoArchitectureKeyReadsAsX64()
    {
        var folder = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "stage-manifest.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "AppName": "Legacy App",
              "Publisher": "Contoso",
              "SoftwareVersion": "1.0.0",
              "InstallerFile": "setup.exe",
              "InstallerType": "EXE",
              "Detection": { "Type": "RegistryKeyValue", "Is64Bit": false }
            }
            """, Encoding.UTF8);

        var read = new ManifestService().Read(path);

        Assert.Equal("x64", read.Architecture);
        Assert.False(read.Detection.Is64Bit);
        Assert.Equal("x64", InstallerArchitectures.Resolve(read));
    }

    /// <summary>An explicitly bad value in a manifest is refused at resolution, not staged over.</summary>
    [Fact]
    public void AManifestDeclaringAnUnsupportedArchitectureIsRefused()
    {
        var manifest = new StageManifest { Architecture = "amd64" };

        Assert.Throws<InvalidOperationException>(() => InstallerArchitectures.Resolve(manifest));
    }
}
