using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerEngine.Tests.InstallerAnalysis;
using static ApplicationPackagerEngine.Tests.AddFromInstaller.AddFromInstallerTestBed;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// The version after a patch comes from the UpdatedVersion element of the patch applicability XML that
/// MsiExtractPatchXMLData returns. A minor upgrade patch has the element; a small update keeps the ProductVersion and
/// has none.
/// </summary>
public sealed class PatchVersionTests : IDisposable
{
    private readonly AddFromInstallerTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private const string MinorUpgrade = """
        <MsiPatch xmlns="http://www.microsoft.com/msi/patch_applicability.xsd" SchemaVersion="1.0.0.0" PatchGUID="{858190B5-08FE-472C-8642-125C06035569}" MinMsiVersion="5">
          <TargetProduct MinMsiVersion="500">
            <TargetProductCode Validate="true">{6D3C1E6A-7F43-4C11-9E51-2B7A3C0D9A11}</TargetProductCode>
            <TargetVersion Validate="true" ComparisonType="Equal" ComparisonFilter="MajorMinorUpdate">1.0.0</TargetVersion>
            <UpdatedVersion>1.0.1</UpdatedVersion>
            <TargetLanguage Validate="false">0</TargetLanguage>
            <UpgradeCode Validate="true">{0B2A8F3E-91C4-4D6A-8E2F-5C1D7B3A6E22}</UpgradeCode>
          </TargetProduct>
          <TargetProductCode>{6D3C1E6A-7F43-4C11-9E51-2B7A3C0D9A11}</TargetProductCode>
          <SequenceData><PatchFamily>Family</PatchFamily><Sequence>1.0.1.0</Sequence><Attributes>1</Attributes></SequenceData>
        </MsiPatch>
        """;

    private const string Target = "{6D3C1E6A-7F43-4C11-9E51-2B7A3C0D9A11}";

    [Fact]
    public void AMinorUpgradePatch_GivesTheUpdatedVersionOfItsTarget()
    {
        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(MinorUpgrade, Target));
        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(MinorUpgrade, Target.ToLowerInvariant()));
        Assert.Null(MsiDatabase.PatchUpdatedVersion(MinorUpgrade, "{11111111-2222-4333-8444-555555555555}"));
    }

    private static string WithSecondTarget(string code, string targetVersion, string updatedVersion)
    {
        var first = MinorUpgrade[MinorUpgrade.IndexOf("<TargetProduct ", StringComparison.Ordinal)..(MinorUpgrade.IndexOf("</TargetProduct>", StringComparison.Ordinal) + "</TargetProduct>".Length)];
        var second = first.Replace(Target, code, StringComparison.Ordinal)
            .Replace(">1.0.0<", $">{targetVersion}<", StringComparison.Ordinal).Replace(">1.0.1<", $">{updatedVersion}<", StringComparison.Ordinal);
        return MinorUpgrade.Replace(first, first + second, StringComparison.Ordinal);
    }

    [Fact]
    public void APatchWithTwoProducts_PairsEachUpdatedVersionWithItsOwnTarget()
    {
        const string other = "{11111111-2222-4333-8444-555555555555}";
        var xml = WithSecondTarget(other, "2.0.0", "2.0.1");

        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(xml, Target));
        Assert.Equal("2.0.1", MsiDatabase.PatchUpdatedVersion(xml, other));
    }

    [Fact]
    public void TwoVersionsOfOneProduct_AreAmbiguous_UnlessTheBaseVersionMatchesOneTarget()
    {
        var xml = WithSecondTarget(Target, "1.0.5", "1.0.6");

        Assert.Null(MsiDatabase.PatchUpdatedVersion(xml, Target));
        Assert.Equal("1.0.6", MsiDatabase.PatchUpdatedVersion(xml, Target, baseVersion: "1.0.5"));
        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(xml, Target, baseVersion: "1.0.0"));
        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(xml, Target, baseVersion: "1.0.0.7"));
        Assert.Null(MsiDatabase.PatchUpdatedVersion(xml, Target, baseVersion: "9.9.9"));
    }

    [Theory]
    [InlineData("GreaterThanOrEqual", "MajorMinorUpdate", "1.0.0", "1.2.0", true)]
    [InlineData("GreaterThanOrEqual", "MajorMinorUpdate", "1.0.0", "0.9.9", false)]
    [InlineData("LessThan", "MajorMinorUpdate", "2.0.0", "1.9.9", true)]
    [InlineData("LessThan", "MajorMinorUpdate", "2.0.0", "2.0.0", false)]
    [InlineData("Equal", "Major", "1.0.0", "1.7.3", true)]
    [InlineData("Equal", "MajorMinor", "1.0.0", "1.1.0", false)]
    [InlineData("None", "MajorMinorUpdate", "1.0.0", "5.0.0", true)]
    public void TheBaseVersion_IsComparedAsTheTargetVersionConditionStates(string type, string filter, string target, string baseVersion, bool applies)
    {
        var xml = MinorUpgrade
            .Replace("ComparisonType=\"Equal\"", $"ComparisonType=\"{type}\"", StringComparison.Ordinal)
            .Replace("ComparisonFilter=\"MajorMinorUpdate\"", $"ComparisonFilter=\"{filter}\"", StringComparison.Ordinal)
            .Replace(">1.0.0</TargetVersion>", $">{target}</TargetVersion>", StringComparison.Ordinal);

        Assert.Equal(applies ? "1.0.1" : null, MsiDatabase.PatchUpdatedVersion(xml, Target, baseVersion));
    }

    [Fact]
    public void ATargetWithSeveralProductCodes_MatchesAnyOfThem()
    {
        const string other = "{11111111-2222-4333-8444-555555555555}";
        var xml = MinorUpgrade.Replace($"<TargetProductCode Validate=\"true\">{Target}</TargetProductCode>",
                $"<TargetProductCode Validate=\"true\">{other}</TargetProductCode><TargetProductCode Validate=\"true\">{Target}</TargetProductCode>", StringComparison.Ordinal);

        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(xml, Target));
    }

    [Fact]
    public void ASmallUpdateTargetOfTheSameProduct_IsNotTakenForTheMinorUpgradeOfAnother()
    {
        var xml = WithSecondTarget(Target, "1.0.5", "1.0.1");
        var smallUpdateFor105 = xml[..xml.LastIndexOf("<UpdatedVersion>", StringComparison.Ordinal)]
            + xml[(xml.LastIndexOf("</UpdatedVersion>", StringComparison.Ordinal) + "</UpdatedVersion>".Length)..];

        Assert.Null(MsiDatabase.PatchUpdatedVersion(smallUpdateFor105, Target));
        Assert.Null(MsiDatabase.PatchUpdatedVersion(smallUpdateFor105, Target, baseVersion: "1.0.5"));
        Assert.Equal("1.0.1", MsiDatabase.PatchUpdatedVersion(smallUpdateFor105, Target, baseVersion: "1.0.0"));
    }

    [Fact]
    public void ASmallUpdatePatch_GivesNoVersion_NotItsSequence()
    {
        var smallUpdate = MinorUpgrade.Replace("<UpdatedVersion>1.0.1</UpdatedVersion>", "", StringComparison.Ordinal);

        Assert.Null(MsiDatabase.PatchUpdatedVersion(smallUpdate, Target));
        Assert.Null(MsiDatabase.PatchUpdatedVersion("", Target));
        Assert.Null(MsiDatabase.PatchUpdatedVersion("<MsiPatch", Target));
    }

    [Fact]
    public async Task AVersionFromThePatchXml_IsConfirmed_AndNeedsNoVersionReview()
    {
        var path = _bed.PathOf("contoso-update.msp");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        var fake = new FakeInstallerAnalysisService();
        fake.Reports[path] = Report(path, "MSP", [], metadata:
        [
            new MetadataBlock(Title: "MSI patch (contoso-update.msp)", Facts:
            [
                Fact("Primary PatchCode", "{AAAABBBB-0000-4000-8000-000000000002}", "Confirmed", "msi-summary-information"),
                Fact("Target ProductCodes", ProductCode, "Confirmed", "msi-summary-information"),
                Fact("Post-patch version", "1.0.1", "Confirmed", "msi-patch-xml")
            ])
        ]);

        var r = await _bed.Over(fake).AnalyzeAsync(path, new Progress<string>(), CancellationToken.None);

        Assert.Equal("1.0.1", r.Version.Value);
        Assert.Equal(Evidence.Confirmed, r.Version.Evidence);
        Assert.Equal("https://learn.microsoft.com/windows/win32/msi/extracting-patch-information-as-xml", r.Version.Source);
        Assert.DoesNotContain("version", r.ReviewReason ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
