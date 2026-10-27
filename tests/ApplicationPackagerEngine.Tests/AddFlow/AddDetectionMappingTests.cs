using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerEngine.Tests.AddFlow;

public sealed class AddDetectionMappingTests
{
    private static DetectionProposal Proposal(DetectionKind kind, params (string Key, string Value)[] values) =>
        new(Kind: kind, Description: "rule", Values: values.ToDictionary(v => v.Key, v => v.Value), Evidence: Evidence.Confirmed, Source: null);

    [Fact]
    public void ARegistryVersionRuleMapsToARegistryValueRule()
    {
        var config = DetectionProposalMapping.ToConfig(Proposal(DetectionKind.RegistryVersion,
            (DetectionValueKeys.Hive, "HKCU"), (DetectionValueKeys.Key, @"SOFTWARE\Contoso"), (DetectionValueKeys.ValueName, "Version"),
            (DetectionValueKeys.ExpectedValue, "{ProductVersion}"), (DetectionValueKeys.Is64Bit, "false")));

        Assert.NotNull(config);
        Assert.Equal("RegistryKeyValue", config.Type);
        Assert.Equal("HKCU", config.RegistryHive);
        Assert.Equal(@"SOFTWARE\Contoso", config.RegistryKeyTemplate);
        Assert.Equal("Version", config.ValueName);
        Assert.Equal("{ProductVersion}", config.ExpectedValue);
        Assert.Equal("Version", config.PropertyType);
        Assert.False(config.Is64Bit);
    }

    [Theory]
    [InlineData(DetectionKind.RegistryVersion)]
    [InlineData(DetectionKind.RegistryExists)]
    public void ARegistryRuleWithANullOrEmptyKeyIsNeverMapped(DetectionKind kind)
    {
        Assert.Null(DetectionProposalMapping.ToConfig(Proposal(kind)));
        Assert.Null(DetectionProposalMapping.ToConfig(Proposal(kind, (DetectionValueKeys.Key, "  "))));
        Assert.True(DetectionProposalMapping.IsDefault(Proposal(kind, (DetectionValueKeys.Key, ""))));
    }

    [Fact]
    public void AnMsiProductCodeRuleReadsTheUninstallKeyOfTheCode()
    {
        var config = DetectionProposalMapping.ToConfig(Proposal(DetectionKind.MsiProductCode, (DetectionValueKeys.ProductCode, "{11111111-2222-3333-4444-555555555555}")));

        Assert.NotNull(config);
        Assert.Equal("RegistryKeyValue", config.Type);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}", config.RegistryKeyTemplate);
        Assert.Equal("DisplayVersion", config.ValueName);
        Assert.Null(DetectionProposalMapping.ToConfig(Proposal(DetectionKind.MsiProductCode)));
    }

    [Fact]
    public void FileRulesNeedAFolderAndAFileName()
    {
        var version = DetectionProposalMapping.ToConfig(Proposal(DetectionKind.FileVersion,
            (DetectionValueKeys.Path, @"%ProgramFiles%\Contoso"), (DetectionValueKeys.FileName, "contoso.exe"), (DetectionValueKeys.Version, "1.2.3")));
        var exists = DetectionProposalMapping.ToConfig(Proposal(DetectionKind.FileExists,
            (DetectionValueKeys.Path, @"%ProgramFiles%\Contoso"), (DetectionValueKeys.FileName, "contoso.exe")));

        Assert.Equal(("File", "Version", "1.2.3"), (version!.Type, version.PropertyType, version.ExpectedValue));
        Assert.Equal(("File", "Existence"), (exists!.Type, exists.PropertyType));
        Assert.Null(DetectionProposalMapping.ToConfig(Proposal(DetectionKind.FileExists, (DetectionValueKeys.Path, @"C:\x"))));
    }

    [Theory]
    [InlineData(DetectionKind.None)]
    [InlineData(DetectionKind.Compound)]
    public void NoneAndCompoundRulesAreNotMapped(DetectionKind kind) =>
        Assert.True(DetectionProposalMapping.IsDefault(Proposal(kind, (DetectionValueKeys.Key, "x"))));
}
