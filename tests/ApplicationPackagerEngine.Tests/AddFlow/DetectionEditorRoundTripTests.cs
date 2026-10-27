using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

/// <summary>An unedited proposal leaves the detection editor unchanged, and a version rule needs the version it compares.</summary>
public sealed class DetectionEditorRoundTripTests
{
    private const string ProductCode = "{6F1A2B3C-4D5E-4F60-8A7B-9C0D1E2F3A4B}";

    private static DetectionProposal Proposal(DetectionKind kind, string hive, string @operator, string expected = "1.2.3") => new(
        Kind: kind,
        Description: "rule",
        Values: kind switch
        {
            DetectionKind.FileVersion => new Dictionary<string, string>
            {
                [DetectionValueKeys.FilePath] = @"C:\Program Files\Contoso", [DetectionValueKeys.FileName] = "contoso.exe",
                [DetectionValueKeys.Operator] = @operator, [DetectionValueKeys.Expected] = expected, [DetectionValueKeys.Is64Bit] = "true"
            },
            DetectionKind.MsiProductCode => new Dictionary<string, string>
            {
                [DetectionValueKeys.Hive] = hive, [DetectionValueKeys.ProductCode] = ProductCode,
                [DetectionValueKeys.Key] = DetectionEditorViewModel.UninstallKeyRoot + ProductCode,
                [DetectionValueKeys.ValueName] = "DisplayVersion", [DetectionValueKeys.Operator] = @operator,
                [DetectionValueKeys.Expected] = expected, [DetectionValueKeys.Is64Bit] = "true"
            },
            _ => new Dictionary<string, string>
            {
                [DetectionValueKeys.Hive] = hive, [DetectionValueKeys.Key] = @"SOFTWARE\Contoso", [DetectionValueKeys.ValueName] = "DisplayVersion",
                [DetectionValueKeys.Operator] = @operator, [DetectionValueKeys.Expected] = expected, [DetectionValueKeys.Is64Bit] = "true"
            }
        },
        Evidence: Evidence.Confirmed, Source: "analysis");

    public static TheoryData<DetectionKind, string, string> VersionRules => new()
    {
        { DetectionKind.RegistryVersion, "HKLM", "GreaterEquals" },
        { DetectionKind.RegistryVersion, "HKCU", "IsEquals" },
        { DetectionKind.MsiProductCode, "HKCU", "IsEquals" },
        { DetectionKind.MsiProductCode, "HKLM", "GreaterEquals" },
        { DetectionKind.FileVersion, "HKLM", "GreaterEquals" },
        { DetectionKind.FileVersion, "HKLM", "IsEquals" }
    };

    [Theory]
    [MemberData(nameof(VersionRules))]
    public void AnUneditedProposal_KeepsItsOperatorAndHive(DetectionKind kind, string hive, string @operator)
    {
        var editor = new DetectionEditorViewModel();
        editor.Load(Proposal(kind, hive, @operator));

        var proposal = editor.ToProposal();
        var config = DetectionEditorViewModel.ToConfig(proposal)!;

        Assert.Equal(@operator, proposal.Values[DetectionValueKeys.Operator]);
        Assert.Equal(@operator, config.Operator);
        if (kind != DetectionKind.FileVersion)
        {
            Assert.Equal(hive, proposal.Values[DetectionValueKeys.Hive]);
            Assert.Equal(hive, config.RegistryHive);
        }
        Assert.Equal(Evidence.Confirmed, proposal.Evidence);
    }

    [Theory]
    [InlineData(DetectionKind.RegistryVersion)]
    [InlineData(DetectionKind.MsiProductCode)]
    [InlineData(DetectionKind.FileVersion)]
    public void AVersionRuleWithoutAnExpectedVersion_IsTheDefault(DetectionKind kind)
    {
        var editor = new DetectionEditorViewModel();
        editor.Load(Proposal(kind, "HKLM", "IsEquals", expected: ""));

        Assert.True(editor.IsDefault);
        Assert.True(editor.ToProposal().IsDefault);
        Assert.Null(DetectionEditorViewModel.ToConfig(editor.ToProposal()));

        editor.Expected = "  ";
        Assert.True(editor.IsDefault);
        editor.Expected = "1.2.3";
        Assert.False(editor.IsDefault);
    }

    [Theory]
    [InlineData(DetectionKind.RegistryVersion)]
    [InlineData(DetectionKind.MsiProductCode)]
    [InlineData(DetectionKind.FileVersion)]
    public void TheServiceRefusesAVersionRuleWithoutAnExpectedVersion(DetectionKind kind)
    {
        var rule = Proposal(kind, "HKLM", "IsEquals", expected: "");

        Assert.Throws<AddFromInstallerRefusedException>(() => AddFromInstallerService.ToDetectionConfig(rule, null, null, ""));
        var inCompound = AddFromInstallerService.Compound("And", Proposal(DetectionKind.RegistryExists, "HKLM", "IsEquals"), rule);
        Assert.Throws<AddFromInstallerRefusedException>(() => AddFromInstallerService.ToDetectionConfig(inCompound, null, null, ""));
    }
}
