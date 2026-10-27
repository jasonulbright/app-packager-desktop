using System.Windows.Threading;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

public sealed class AddFileViewModelTests
{
    private sealed class Harness
    {
        public FakeAddService Service { get; } = new();
        public FakeResultActions Actions { get; } = new();
        public FakeAddPrompts Prompts { get; } = new();
        public AddFileViewModel ViewModel { get; }

        public Harness() => ViewModel = new AddFileViewModel(service: Service, prompts: Prompts, results: new AddResultViewModel(Actions)) { DefinitionsRoot = @"C:\defs" };
    }

    private static void OnSta(Action<Harness> test) => StaTestHelper.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        test(new Harness());
    });

    private static void Analyze(Harness h, AddAnalysisResult result, int index = 0)
    {
        h.Service.WaitForAnalysis(index).Result.SetResult(result);
        StaTestHelper.PumpUntil(() => h.ViewModel.Stage == AddFileStage.Review);
    }

    [Fact]
    public void ThePickerStartsTheAnalysisOfTheChosenFile() => OnSta(h =>
    {
        h.Prompts.Pick = @"C:\in\contoso.msi";
        h.ViewModel.OpenInstallerCommand.Execute(null);

        var pending = h.Service.WaitForAnalysis(0);
        Assert.Equal(@"C:\in\contoso.msi", pending.Path);
        Assert.Equal(AddFileStage.Analyzing, h.ViewModel.Stage);
        Assert.True(h.ViewModel.IsBusy);
    });

    [Fact]
    public void ACancelledPickerStartsNothing() => OnSta(h =>
    {
        h.ViewModel.OpenInstallerCommand.Execute(null);
        StaTestHelper.Flush();
        Assert.Equal(1, h.Prompts.Picks);
        Assert.Empty(h.Service.Analyses);
        Assert.Equal(AddFileStage.Pick, h.ViewModel.Stage);
    });

    [Fact]
    public void ThePickerFilterAndTheDropZoneNameEveryAcceptedFormat() => OnSta(h =>
    {
        h.ViewModel.OpenInstallerCommand.Execute(null);
        foreach (var extension in new[] { ".msi", ".exe", ".msp", ".msix", ".appx", ".msixbundle", ".appxbundle" })
        {
            Assert.Contains("*" + extension, h.Prompts.Filter);
            Assert.Contains(extension, h.ViewModel.FormatsText);
        }
        Assert.DoesNotContain("*.*", h.Prompts.Filter);
    });

    [Theory]
    [InlineData("patch.msp")]
    [InlineData("app.msix")]
    [InlineData("app.appxbundle")]
    public void PatchesAndPackagesAreAnalyzed(string file) => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\" + file);
        Assert.Equal(@"C:\in\" + file, h.Service.WaitForAnalysis(0).Path);
    });

    [Theory]
    [InlineData("payload.zip")]
    [InlineData("readme.txt")]
    [InlineData("folder")]
    public void AnotherFileIsRefusedWithAPlainMessage(string file) => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\" + file);
        StaTestHelper.Flush();
        Assert.Empty(h.Service.Analyses);
        Assert.Equal(AddFileStage.Pick, h.ViewModel.Stage);
        Assert.Contains("does not take", h.ViewModel.StatusText);
        Assert.Contains(".msixbundle", h.ViewModel.StatusText);
    });

    [Fact]
    public void TheProgressOfTheAnalysisShows() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        h.Service.WaitForAnalysis(0).Progress.Report("Reading the MSI database");
        StaTestHelper.PumpUntil(() => h.ViewModel.ProgressText == "Reading the MSI database");
    });

    [Fact]
    public void CancelStopsTheAnalysisAndReturnsToThePicker() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        var pending = h.Service.WaitForAnalysis(0);
        h.ViewModel.CancelCommand.Execute(null);

        StaTestHelper.PumpUntil(() => !h.ViewModel.IsBusy);
        Assert.True(pending.Token.IsCancellationRequested);
        Assert.Equal(AddFileStage.Pick, h.ViewModel.Stage);
        Assert.Equal("The analysis was cancelled.", h.ViewModel.StatusText);
    });

    [Fact]
    public void ALateResultOfAnEarlierFileIsDiscarded() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\first.msi");
        var first = h.Service.WaitForAnalysis(0);
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\second.msi");
        var second = h.Service.WaitForAnalysis(1);
        Assert.True(first.Token.IsCancellationRequested);

        first.Progress.Report("late progress");
        first.Result.TrySetResult(FakeAddService.Result(path: @"C:\in\first.msi"));
        StaTestHelper.Flush();
        Assert.Equal(AddFileStage.Analyzing, h.ViewModel.Stage);
        Assert.NotEqual("late progress", h.ViewModel.ProgressText);

        second.Result.SetResult(FakeAddService.Result(path: @"C:\in\second.msi"));
        StaTestHelper.PumpUntil(() => h.ViewModel.Stage == AddFileStage.Review);
        Assert.Equal(@"C:\in\second.msi", h.ViewModel.FilePath);
    });

    [Fact]
    public void TheReviewShowsEveryProposedValueWithItsMarker() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        Analyze(h, FakeAddService.Result());

        var rows = h.ViewModel.Rows.ToDictionary(r => r.Label);
        Assert.Equal(["Vendor", "Name", "Version", "Install arguments", "Uninstall arguments", "Install context"], h.ViewModel.Rows.Select(r => r.Label));
        Assert.Equal(("Contoso", "Confirmed"), (rows["Vendor"].Value, rows["Vendor"].MarkerText));
        Assert.Equal(("/x {ProductCode} /qn", "Predicted, not verified"), (rows["Uninstall arguments"].Value, rows["Uninstall arguments"].MarkerText));
        Assert.True(rows["Install context"].IsChoice);
        Assert.Equal("NativeMSI", h.ViewModel.Framework.Value);
        Assert.Equal("MSI", h.ViewModel.InstallerType);
        Assert.Equal(DetectionKind.MsiProductCode, h.ViewModel.Detection.Kind);
        Assert.Equal(["Read from the MSI database."], h.ViewModel.Notes);
        Assert.False(h.ViewModel.ShowReviewWarning);
    });

    [Fact]
    public void NeedsReviewShowsAWarningWithTheReason() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\setup.exe");
        Analyze(h, FakeAddService.Result(path: @"C:\in\setup.exe", needsReview: true, reason: "No silent switch could be derived.", argumentsEvidence: Evidence.None));

        Assert.True(h.ViewModel.ShowReviewWarning);
        Assert.Contains("No silent switch could be derived.", h.ViewModel.ReviewWarning);
    });

    [Fact]
    public void AnInstallerWithNoSilentSwitchGetsAManualFieldAndAWarning() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\setup.exe");
        Analyze(h, FakeAddService.Result(path: @"C:\in\setup.exe", needsReview: true, reason: AddFromInstallerService.NoSilentSwitchReason,
            argumentsEvidence: Evidence.None) with { InstallerType = "EXE", InstallArguments = new ProposedValue<string>(Value: "", Evidence: Evidence.None, Source: null) });

        var install = h.ViewModel.Rows.Single(r => r.Label == "Install arguments");
        Assert.True(h.ViewModel.ShowNoSilentSwitch);
        Assert.Equal("No value", install.MarkerText);
        Assert.False(install.IsReadOnly);

        install.Value = "/quiet";
        Assert.Equal(Evidence.Manual, install.Evidence);
        Assert.False(h.ViewModel.ShowNoSilentSwitch);
        Assert.True(h.ViewModel.ShowReviewWarning);
    });

    [Fact]
    public void APatchShowsItsFactsAndTheBaseProductWarning() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\fix.msp");
        var patch = new PatchFacts(PatchCode: "{AAAAAAAA-0000-0000-0000-000000000001}", Title: "Contoso fix 1",
            TargetProductCodes: ["{BBBBBBBB-0000-0000-0000-000000000002}"], TargetProductName: "Contoso App", PostPatchVersion: "1.2.4",
            BaseProductWarning: "Install Contoso App before this patch.");
        Analyze(h, FakeAddService.Result(path: @"C:\in\fix.msp") with { InstallerType = "MSP", Patch = patch });

        Assert.True(h.ViewModel.HasPatch);
        Assert.False(h.ViewModel.HasPackage);
        Assert.Equal("Install Contoso App before this patch.", h.ViewModel.PatchWarning);
        var facts = h.ViewModel.PatchFacts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("{AAAAAAAA-0000-0000-0000-000000000001}", facts["Patch code"]);
        Assert.Equal("Contoso fix 1", facts["Title"]);
        Assert.Equal("{BBBBBBBB-0000-0000-0000-000000000002}", facts["Target product codes"]);
        Assert.Equal("1.2.4", facts["Version after the patch"]);
    });

    [Theory]
    [InlineData(PackageTrust.Unsigned, "Not signed")]
    [InlineData(PackageTrust.Untrusted, "Signed, not trusted on this computer")]
    public void APackageShowsItsIdentityAndAProminentTrustWarning(PackageTrust trust, string signature) => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\app.msix");
        var package = new PackageFacts(IdentityName: "Contoso.App", Publisher: "CN=Contoso", Version: "1.2.3.0", Architecture: "x64",
            Dependencies: ["Microsoft.VCLibs.140.00"], IsBundle: false, Trust: trust) { TrustWarning = "The package is not trusted on the target." };
        Analyze(h, FakeAddService.Result(path: @"C:\in\app.msix") with { InstallerType = "MSIX", Package = package });

        Assert.True(h.ViewModel.HasPackage);
        Assert.True(h.ViewModel.HasTrustWarning);
        Assert.Equal("The package is not trusted on the target.", h.ViewModel.TrustWarning);
        var facts = h.ViewModel.PackageFacts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal(("Contoso.App", "CN=Contoso", "1.2.3.0", "x64"), (facts["Identity name"], facts["Publisher"], facts["Version"], facts["Architecture"]));
        Assert.Equal("Microsoft.VCLibs.140.00", facts["Dependencies"]);
        Assert.Equal(signature, facts["Signature"]);
    });

    [Fact]
    public void ExtraValuesShowWithTheirMarkersAndTheInstallModeIsSaved() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\setup.exe");
        Analyze(h, FakeAddService.Result(path: @"C:\in\setup.exe") with
        {
            UninstallCommand = new ProposedValue<string>(Value: "\"unins000.exe\" /VERYSILENT", Evidence: Evidence.Confirmed, Source: "inno-script"),
            Architecture = new ProposedValue<string>(Value: "x64", Evidence: Evidence.Confirmed, Source: "pe-header"),
            InstallModes = ["AllUsers", "CurrentUser"],
            DefaultInstallMode = "AllUsers"
        });
        Assert.Equal(["Vendor", "Name", "Version", "Install arguments", "Uninstall arguments", "Uninstall command", "Install context", "Install for", "Architecture"],
            h.ViewModel.Rows.Select(r => r.Label));
        Assert.True(h.ViewModel.Rows.Single(r => r.Label == "Architecture").IsReadOnly);
        h.ViewModel.Rows.Single(r => r.Label == "Install for").Value = "CurrentUser";
        h.ViewModel.PredictedAccepted = true;

        var save = h.ViewModel.SaveCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);
        var (reviewed, options) = Assert.Single(h.Service.Saves);
        Assert.Equal("CurrentUser", options.InstallMode);
        Assert.Equal("\"unins000.exe\" /VERYSILENT", reviewed.UninstallCommand!.Value);
    });

    [Fact]
    public void ARefusalOfTheServiceIsShownAsItIs() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        h.Service.WaitForAnalysis(0).Result.SetException(new AddFromInstallerRefusedException("The path is a folder. Add from file takes one installer file."));
        StaTestHelper.PumpUntil(() => !h.ViewModel.IsBusy);
        Assert.Equal("The path is a folder. Add from file takes one installer file.", h.ViewModel.StatusText);
    });

    [Fact]
    public void SaveNeedsThePredictedValuesAccepted() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        Analyze(h, FakeAddService.Result());
        Assert.True(h.ViewModel.HasPredicted);
        Assert.False(h.ViewModel.SaveCommand.CanExecute(null));

        h.ViewModel.PredictedAccepted = true;
        Assert.True(h.ViewModel.SaveCommand.CanExecute(null));
    });

    [Fact]
    public void SaveSendsTheReviewedValuesAndShowsTheResultPanel() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        Analyze(h, FakeAddService.Result());
        h.ViewModel.Rows.Single(r => r.Label == "Vendor").Value = "Fabrikam";
        h.ViewModel.PredictedAccepted = true;

        var save = h.ViewModel.SaveCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        var (reviewed, options) = Assert.Single(h.Service.Saves);
        Assert.Equal(("Fabrikam", Evidence.Manual), (reviewed.Vendor.Value, reviewed.Vendor.Evidence));
        Assert.Equal(("Contoso App", Evidence.Confirmed), (reviewed.Name.Value, reviewed.Name.Evidence));
        Assert.Equal(DetectionKind.MsiProductCode, reviewed.Detection.Kind);
        Assert.False(reviewed.NeedsReview);
        Assert.Equal(@"C:\defs", options.DefinitionsFolder);
        Assert.Equal(AddFileStage.Result, h.ViewModel.Stage);
        Assert.True(h.ViewModel.Result.IsVisible);
        Assert.True(h.ViewModel.Result.IsDoneDefault);
        Assert.Equal(["contoso0001"], h.Actions.Selected);
        Assert.Empty(h.Actions.Staged);
    });

    [Fact]
    public void ADefaultDetectionMakesOpenInWorkbenchTheDefault() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        Analyze(h, FakeAddService.Result(detection: FakeAddService.DefaultDetection));
        Assert.True(h.ViewModel.ShowReviewWarning);
        h.ViewModel.PredictedAccepted = true;

        var save = h.ViewModel.SaveCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        Assert.True(Assert.Single(h.Service.Saves).Reviewed.NeedsReview);
        Assert.True(h.ViewModel.Result.IsWorkbenchDefault);
        Assert.Equal("Detection needs review.", h.ViewModel.Result.ReasonText);
    });

    [Fact]
    public void ADropDuringTheSaveIsIgnored() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        Analyze(h, FakeAddService.Result());
        h.ViewModel.PredictedAccepted = true;
        h.Service.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = h.ViewModel.SaveCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => h.Service.Saves.Count == 1);

        h.ViewModel.DropFiles([@"C:\in\other.msi"]);
        StaTestHelper.Flush();
        Assert.Single(h.Service.Analyses);
        Assert.Contains("being saved", h.ViewModel.StatusText);

        h.Service.SaveGate.SetResult();
        StaTestHelper.PumpUntil(() => save.IsCompleted);
        Assert.Equal(AddFileStage.Result, h.ViewModel.Stage);
    });

    [Fact]
    public void DoneReturnsToThePicker() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        Analyze(h, FakeAddService.Result());
        h.ViewModel.PredictedAccepted = true;
        var save = h.ViewModel.SaveCommand.ExecuteAsync(null);
        StaTestHelper.PumpUntil(() => save.IsCompleted);

        h.ViewModel.Result.DoneCommand.Execute(null);
        Assert.Equal(AddFileStage.Pick, h.ViewModel.Stage);
        Assert.Empty(h.ViewModel.Rows);
    });

    [Fact]
    public void AFailedAnalysisSaysWhyAndReturnsToThePicker() => OnSta(h =>
    {
        h.ViewModel.AnalyzeFileCommand.Execute(@"C:\in\contoso.msi");
        h.Service.WaitForAnalysis(0).Result.SetException(new InvalidDataException("The file is not an MSI database."));
        StaTestHelper.PumpUntil(() => !h.ViewModel.IsBusy);
        Assert.Equal(AddFileStage.Pick, h.ViewModel.Stage);
        Assert.Equal("The analysis failed: The file is not an MSI database.", h.ViewModel.StatusText);
    });
}
