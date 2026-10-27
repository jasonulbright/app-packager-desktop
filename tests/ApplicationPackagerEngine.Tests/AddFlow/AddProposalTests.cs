using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

public sealed class AddProposalTests
{
    [Theory]
    [InlineData(Evidence.Confirmed, "Confirmed")]
    [InlineData(Evidence.Predicted, "Predicted, not verified")]
    [InlineData(Evidence.Manual, "Manual")]
    [InlineData(Evidence.None, "No value")]
    public void EveryEvidenceLevelHasAVisibleMarker(Evidence evidence, string marker)
    {
        var row = new ProposalRow(label: "Version", value: "1.0", evidence: evidence, source: "ProductVersion");
        Assert.Equal(marker, row.MarkerText);
        Assert.False(string.IsNullOrWhiteSpace(row.MarkerToolTip));
        Assert.Equal("Version: " + marker, row.MarkerAutomationName);
    }

    [Fact]
    public void APredictedMarkerNamesItsSource()
    {
        var row = new ProposalRow(label: "Install arguments", value: "/S", evidence: Evidence.Predicted, source: "https://learn.microsoft.com/x");
        Assert.Contains("https://learn.microsoft.com/x", row.MarkerToolTip);
        Assert.True(row.IsPredicted);
    }

    [Fact]
    public void AnEditMakesTheValueManualAndTheProposedValueRestoresItsEvidence()
    {
        var row = new ProposalRow(label: "Vendor", value: "Contoso", evidence: Evidence.Confirmed, source: null);
        row.Value = "Fabrikam";
        Assert.Equal(Evidence.Manual, row.Evidence);
        row.Value = "Contoso";
        Assert.Equal(Evidence.Confirmed, row.Evidence);
    }

    [Fact]
    public void ARowWithNoValueIsNotAcceptedByDefault()
    {
        Assert.False(new ProposalRow(label: "Version", value: null, evidence: Evidence.None, source: null).IsAccepted);
        Assert.True(new ProposalRow(label: "Version", value: "1", evidence: Evidence.Predicted, source: null).IsAccepted);
    }

    [Fact]
    public void TheDetectionEditorReadsAProposalAndWarnsOnTheDefault()
    {
        var editor = new DetectionEditorViewModel();
        Assert.True(editor.IsDefault);
        Assert.Contains("default", editor.WarningText, StringComparison.OrdinalIgnoreCase);

        editor.Load(new DetectionProposal(Kind: DetectionKind.RegistryVersion, Description: "ARP DisplayVersion",
            Values: new Dictionary<string, string> { [DetectionValueKeys.Key] = @"SOFTWARE\Contoso", [DetectionValueKeys.Hive] = "HKLM" },
            Evidence: Evidence.Confirmed, Source: "MSI ProductCode"));

        Assert.False(editor.IsDefault);
        Assert.Equal("", editor.WarningText);
        Assert.True(editor.ShowRegistry);
        Assert.Equal("Confirmed", editor.MarkerText);
        Assert.Equal(Evidence.Confirmed, editor.ToProposal().Evidence);

        editor.Key = "";
        Assert.True(editor.IsDefault);
        Assert.Equal(Evidence.Manual, editor.ToProposal().Evidence);
        Assert.Null(DetectionProposalMapping.ToConfig(editor.ToProposal()));
    }

    [Fact]
    public void TheDetectionEditorShowsTheFieldsOfTheKind()
    {
        var editor = new DetectionEditorViewModel { Kind = DetectionKind.FileVersion };
        Assert.True(editor.ShowFile && editor.ShowVersion && !editor.ShowRegistry && !editor.ShowProductCode);
        editor.Kind = DetectionKind.MsiProductCode;
        Assert.True(editor.ShowProductCode && editor.ShowVersion && !editor.ShowFile);
        editor.ProductCode = "{A}";
        Assert.Equal("{A}", editor.ToProposal().Values[DetectionValueKeys.ProductCode]);
        Assert.False(editor.IsDefault);
    }

    [Fact]
    public void ACompoundProposalStaysForTheWorkbench()
    {
        var editor = new DetectionEditorViewModel();
        editor.Load(new DetectionProposal(Kind: DetectionKind.Compound, Description: "Two keys", Values: new Dictionary<string, string>(),
            Evidence: Evidence.Predicted, Source: null));
        Assert.True(editor.IsCompound);
        Assert.True(editor.IsDefault);
        Assert.Contains("Workbench", editor.WarningText);
    }
}
