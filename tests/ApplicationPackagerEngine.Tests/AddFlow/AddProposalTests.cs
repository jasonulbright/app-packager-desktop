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
        Assert.True(editor.ToProposal().IsDefault);
        Assert.Null(DetectionEditorViewModel.ToConfig(editor.ToProposal()));
    }

    [Fact]
    public void PatchAndPackageRulesAreShownAsProposedAndKept()
    {
        var editor = new DetectionEditorViewModel();
        var patch = new DetectionProposal(Kind: DetectionKind.MsiPatchCode, Description: "Patch applied",
            Values: new Dictionary<string, string>
            {
                [DetectionValueKeys.PatchCode] = "{AAAAAAAA-0000-0000-0000-000000000001}",
                [DetectionValueKeys.ProductCode] = "{BBBBBBBB-0000-0000-0000-000000000002}"
            }, Evidence: Evidence.Predicted, Source: "https://learn.microsoft.com/windows/win32/api/msi/nf-msi-msienumpatchesexw");
        editor.Load(patch);

        Assert.True(editor.IsFixedRule);
        Assert.False(editor.IsDefault);
        Assert.Contains("{AAAAAAAA-0000-0000-0000-000000000001}", editor.FixedRuleText);
        Assert.Equal(patch.Values, editor.ToProposal().Values);
        Assert.Contains("learn.microsoft.com", editor.MarkerToolTip);
    }

    [Fact]
    public void ARuleThatTheAnalysisMarksAsDefaultIsDefault()
    {
        var editor = new DetectionEditorViewModel();
        editor.Load(DetectionProposal.NoneDerived("No rule could be derived."));
        Assert.True(editor.IsDefault);
        Assert.True(editor.HasWarning);
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
        DetectionProposal Clause(string key) => new(Kind: DetectionKind.RegistryExists, Description: key,
            Values: new Dictionary<string, string> { [DetectionValueKeys.Key] = key }, Evidence: Evidence.Predicted, Source: null);
        var compound = new DetectionProposal(Kind: DetectionKind.Compound, Description: "Two keys",
            Values: new Dictionary<string, string> { [DetectionValueKeys.Connector] = "Or" }, Evidence: Evidence.Predicted, Source: null)
        { Clauses = [Clause(@"SOFTWARE\A"), Clause(@"SOFTWARE\B")] };
        editor.Load(compound);

        Assert.True(editor.IsFixedRule);
        Assert.False(editor.IsDefault);
        Assert.Contains("Workbench", editor.WarningText);
        Assert.Equal(@"SOFTWARE\A or SOFTWARE\B", editor.FixedRuleText);
        Assert.Equal(2, editor.ToProposal().Clauses.Count);
        Assert.Contains(editor.KindOptions, o => o.Kind == DetectionKind.Compound);
    }
}
