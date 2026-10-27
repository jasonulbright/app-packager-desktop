using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.AddFlow;

/// <summary>An analysis service whose calls wait until the test completes them.</summary>
internal sealed class FakeAddService : IAddFromInstallerService
{
    public sealed record PendingAnalysis(string Path, IProgress<string> Progress, CancellationToken Token, TaskCompletionSource<AddAnalysisResult> Result);

    public List<PendingAnalysis> Analyses { get; } = [];
    public List<(AddAnalysisResult Reviewed, AddSaveOptions Options)> Saves { get; } = [];
    public List<DownloadRequest> Fetches { get; } = [];
    public Func<DownloadRequest, string> FetchResult { get; set; } = r => Path.Combine(r.DownloadRoot, "setup.msi");
    public Func<AddAnalysisResult, AddSaveResult> SaveResult { get; set; } =
        r => new AddSaveResult(FilePath: @"C:\defs\contoso.json", DefinitionId: "contoso0001", Warnings: []);

    public IReadOnlyList<string> AcceptedExtensions { get; } = [".msi", ".exe", ".msp", ".msix", ".appx", ".msixbundle", ".appxbundle"];

    public string? RefusalReason(string installerPath) =>
        AcceptedExtensions.Contains(Path.GetExtension(installerPath).ToLowerInvariant()) ? null : "Add from file does not take this file. Use an .msi, .exe, .msp, .msix, .appx, .msixbundle or .appxbundle file.";

    public Task<AddAnalysisResult> AnalyzeAsync(string installerPath, IProgress<string> progress, CancellationToken ct)
    {
        var pending = new PendingAnalysis(installerPath, progress, ct, new TaskCompletionSource<AddAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (Analyses) Analyses.Add(pending);
        ct.Register(() => pending.Result.TrySetCanceled(ct));
        return pending.Result.Task;
    }

    public PendingAnalysis WaitForAnalysis(int index)
    {
        StaTestHelper.PumpUntil(() => { lock (Analyses) return Analyses.Count > index; });
        lock (Analyses) return Analyses[index];
    }

    /// <summary>When set, a save completes only after the test completes the gate.</summary>
    public TaskCompletionSource? SaveGate { get; set; }

    public async Task<AddSaveResult> SaveAsync(AddAnalysisResult reviewed, AddSaveOptions options, CancellationToken ct)
    {
        Saves.Add((reviewed, options));
        if (SaveGate is not null) await SaveGate.Task;
        return SaveResult(reviewed);
    }

    public Task<string> FetchInstallerAsync(DownloadRequest request, IProgress<string> progress, CancellationToken ct)
    {
        Fetches.Add(request);
        progress.Report("Downloading");
        return Task.FromResult(FetchResult(request));
    }

    public static AddAnalysisResult Result(string path = @"C:\in\contoso.msi", bool needsReview = false, string? reason = null,
        DetectionProposal? detection = null, Evidence argumentsEvidence = Evidence.Confirmed) => new(
        FilePath: path,
        InstallerType: "MSI",
        Framework: new ProposedValue<string>(Value: "NativeMSI", Evidence: Evidence.Confirmed, Source: "OLE header"),
        Vendor: new ProposedValue<string?>(Value: "Contoso", Evidence: Evidence.Confirmed, Source: "Manufacturer"),
        Name: new ProposedValue<string?>(Value: "Contoso App", Evidence: Evidence.Confirmed, Source: "ProductName"),
        Version: new ProposedValue<string?>(Value: "1.2.3", Evidence: Evidence.Confirmed, Source: "ProductVersion"),
        InstallArguments: new ProposedValue<string>(Value: "/qn /norestart", Evidence: argumentsEvidence, Source: "https://learn.microsoft.com/windows/win32/msi/command-line-options"),
        UninstallArguments: new ProposedValue<string>(Value: "/x {ProductCode} /qn", Evidence: Evidence.Predicted, Source: "https://learn.microsoft.com/windows/win32/msi/command-line-options"),
        InstallContext: new ProposedValue<string>(Value: "Machine", Evidence: Evidence.Confirmed, Source: "ALLUSERS"),
        Detection: detection ?? new DetectionProposal(Kind: DetectionKind.MsiProductCode, Description: "MSI product code",
            Values: new Dictionary<string, string> { [DetectionValueKeys.ProductCode] = "{11111111-2222-3333-4444-555555555555}", [DetectionValueKeys.Hive] = "HKLM", [DetectionValueKeys.Expected] = "1.2.3", [DetectionValueKeys.Key] = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-2222-3333-4444-555555555555}" },
            Evidence: Evidence.Confirmed, Source: "ProductCode"),
        NeedsReview: needsReview,
        ReviewReason: reason,
        Notes: ["Read from the MSI database."]);

    public static DetectionProposal DefaultDetection { get; } = DetectionProposal.NoneDerived("No rule could be derived.");
}

internal sealed class FakeResultActions : IAddResultActions
{
    public List<string> Selected { get; } = [];
    public List<string> Opened { get; } = [];
    public List<string> Staged { get; } = [];

    public Task SelectRowAsync(string definitionId)
    {
        Selected.Add(definitionId);
        return Task.CompletedTask;
    }

    public void OpenWorkbench(string definitionId) => Opened.Add(definitionId);

    public Task<string> StageAsync(string definitionId)
    {
        Staged.Add(definitionId);
        return Task.FromResult("Staged.");
    }
}

internal sealed class FakeAddPrompts : IAddFilePrompts
{
    public string? Pick { get; set; }
    public int Picks { get; private set; }

    public string? Filter { get; private set; }

    public string? PickInstaller(string filter)
    {
        Filter = filter;
        Picks++;
        return Pick;
    }
}
