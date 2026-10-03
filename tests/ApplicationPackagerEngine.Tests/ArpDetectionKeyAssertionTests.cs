using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.InstallerScripts;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A literal uninstall key in a detection is compared against the key the staged
/// installer's own header names: a renamed key or a moved registry view refuses the
/// stage, and a header that names no key only warns.
/// </summary>
public sealed class ArpDetectionKeyAssertionTests : IDisposable
{
    private const string Arp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apkg-arp-assert-" + Guid.NewGuid().ToString("N"));

    public ArpDetectionKeyAssertionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>A PE image with one .wixburn section whose BURN_SECTION header carries <paramref name="bundleId"/>.</summary>
    internal static byte[] BurnBundle(Guid bundleId)
    {
        var image = new byte[0x400];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(0x80u).CopyTo(image, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, 0x80);
        BitConverter.GetBytes((ushort)0x14C).CopyTo(image, 0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(image, 0x86);
        BitConverter.GetBytes((ushort)0).CopyTo(image, 0x94);
        var section = 0x98;
        Encoding.ASCII.GetBytes(".wixburn").CopyTo(image, section);
        BitConverter.GetBytes(0x40u).CopyTo(image, section + 8);
        BitConverter.GetBytes(0x1000u).CopyTo(image, section + 12);
        BitConverter.GetBytes(0x200u).CopyTo(image, section + 16);
        BitConverter.GetBytes(0x200u).CopyTo(image, section + 20);
        BitConverter.GetBytes(WixBurnBundleReader.SectionMagic).CopyTo(image, 0x200);
        BitConverter.GetBytes(2u).CopyTo(image, 0x204);
        bundleId.ToByteArray().CopyTo(image, 0x208);
        return image;
    }

    private string WriteBundle(Guid bundleId)
    {
        var path = Path.Combine(_dir, "bundle-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, BurnBundle(bundleId));
        return path;
    }

    private static StageDetection Registry(string key, bool is64) => new()
    {
        Type = "RegistryKeyValue", RegistryKeyRelative = key, ValueName = "DisplayVersion", ExpectedValue = "1.0", Is64Bit = is64
    };

    private static DetectionConfig Template(string key) => new() { Type = "RegistryKeyValue", RegistryKeyTemplate = key };

    // ── The comparison ───────────────────────────────────────────

    [Fact]
    public void AMatchingKeyAndViewPass() =>
        ArpDetectionKeyAssertion.Assert(@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", "32", Arp + "Contoso", false);

    [Fact]
    public void TheKeyComparisonIgnoresCase() =>
        ArpDetectionKeyAssertion.Assert(@"HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CONTOSO_is1", "64", Arp + "Contoso_is1", true);

    [Fact]
    public void ARenamedKeyIsRefusedWithTheInstallersKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ArpDetectionKeyAssertion.Assert(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso 2", "64", Arp + "Contoso", true));
        Assert.Equal($@"Installer registers 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso 2' but detection expects '{Arp}Contoso'.", ex.Message);
    }

    [Fact]
    public void AMovedRegistryViewIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ArpDetectionKeyAssertion.Assert(@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Contoso", "32", Arp + "Contoso", true));
        Assert.Equal("Installer writes its ARP key in the 32-bit view but detection expects the other view.", ex.Message);
    }

    [Fact]
    public void AHeaderWithNoKeyOnlyWarns()
    {
        var warnings = new List<string>();
        ArpDetectionKeyAssertion.Assert("", "", Arp + "Contoso", true, warnings.Add);
        Assert.Equal([ArpDetectionKeyAssertion.NoHeaderKeyWarning], warnings);
    }

    [Theory]
    [InlineData(Arp + "Contoso", true)]
    [InlineData(Arp + "{FF8111EB-E2A2-4A6B-830C-4F676D20FB39}", true)]
    [InlineData(Arp + "{ProductCode}", false)]
    [InlineData(Arp + "Contoso {version}", false)]
    [InlineData(@"SOFTWARE\Contoso\Tool", false)]
    [InlineData(null, false)]
    public void OnlyALiteralUninstallKeyTakesPart(string? template, bool expected) =>
        Assert.Equal(expected, ArpDetectionKeyAssertion.IsLiteralUninstallKey(template));

    // ── WiX Burn bundles ─────────────────────────────────────────

    [Fact]
    public void TheBundleIdIsReadFromTheWixburnSection()
    {
        var id = Guid.Parse("28e98351-a452-4868-9b5f-a1ad2ace66e8");
        Assert.Equal("{28E98351-A452-4868-9B5F-A1AD2ACE66E8}", WixBurnBundleReader.ReadBundleId(WriteBundle(id)));
    }

    [Fact]
    public void AFileWithoutAWixburnSectionHasNoBundleId()
    {
        var path = Path.Combine(_dir, "plain.exe");
        File.WriteAllBytes(path, new byte[512]);
        Assert.Null(WixBurnBundleReader.ReadBundleId(path));
    }

    [Fact]
    public void ABundleWhoseIdDiffersFromTheDefinitionKeyIsRefused()
    {
        var installer = WriteBundle(Guid.Parse("28e98351-a452-4868-9b5f-a1ad2ace66e8"));
        var expected = Arp + "{FF8111EB-E2A2-4A6B-830C-4F676D20FB39}";

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ArpDetectionKeyAssertion.CheckStage(Registry(expected, false), Template(expected), installer, "/quiet"));
        Assert.Equal($@"Installer registers 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{28E98351-A452-4868-9B5F-A1AD2ACE66E8}}' but detection expects '{expected}'.", ex.Message);
    }

    [Fact]
    public void ABundleWhoseIdMatchesPassesInEitherView()
    {
        var installer = WriteBundle(Guid.Parse("28e98351-a452-4868-9b5f-a1ad2ace66e8"));
        var key = Arp + "{28E98351-A452-4868-9B5F-A1AD2ACE66E8}";
        var log = new List<string>();

        ArpDetectionKeyAssertion.CheckStage(Registry(key, false), Template(key), installer, "/quiet", log.Add);
        ArpDetectionKeyAssertion.CheckStage(Registry(key, true), Template(key), installer, "/quiet", log.Add);

        Assert.Equal(2, log.Count(l => l.StartsWith("ARP key confirmed", StringComparison.Ordinal)));
    }

    [Fact]
    public void AKeyTheStageResolvedFromAProductCodeTokenIsNotAsserted()
    {
        var installer = WriteBundle(Guid.NewGuid());
        var resolved = Arp + "{11111111-2222-3333-4444-555555555555}";
        ArpDetectionKeyAssertion.CheckStage(Registry(resolved, true), Template(Arp + "{ProductCode}"), installer, "");
    }

    [Fact]
    public void ACompoundPassesWhenAnyLiteralClauseMatches()
    {
        var installer = WriteBundle(Guid.Parse("28e98351-a452-4868-9b5f-a1ad2ace66e8"));
        var good = Arp + "{28E98351-A452-4868-9B5F-A1AD2ACE66E8}";
        var stale = Arp + "{FF8111EB-E2A2-4A6B-830C-4F676D20FB39}";
        var detection = new StageDetection { Type = "Compound", Connector = "Or", Clauses = [Registry(stale, true), Registry(good, false)] };
        var definition = new DetectionConfig { Type = "Compound", Clauses = [Template(stale), Template(good)] };

        ArpDetectionKeyAssertion.CheckStage(detection, definition, installer, "");

        var bothStale = new StageDetection { Type = "Compound", Connector = "Or", Clauses = [Registry(stale, true), Registry(stale, false)] };
        Assert.Throws<InvalidOperationException>(() =>
            ArpDetectionKeyAssertion.CheckStage(bothStale, new DetectionConfig { Type = "Compound", Clauses = [Template(stale), Template(stale)] }, installer, ""));
    }

    [Fact]
    public void AnEngineWithoutAReaderIsNotAsserted()
    {
        var path = Path.Combine(_dir, "unknown.exe");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ nothing to see"));
        var key = Arp + "Contoso";
        var log = new List<string>();
        ArpDetectionKeyAssertion.CheckStage(Registry(key, true), Template(key), path, "", log.Add);
        Assert.Empty(log);
    }
}
