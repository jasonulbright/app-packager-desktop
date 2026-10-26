using System.Diagnostics;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using Xunit.Abstractions;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HandleCountCollection
{
    public const string Name = "Process handle count";
}

/// <summary>
/// Open-and-close cycles of the page with the real analysis service, sandbox and report writer
/// on real installer files. Handles are counted after a full collection and the finalizers, so a
/// count that still grows is a handle the page or the service keeps.
/// </summary>
[Collection(HandleCountCollection.Name)]
public sealed class InstallerAnalysisHandleTests(ITestOutputHelper output) : IDisposable
{
    private const int Cycles = 50;
    private const int AllowedGrowth = 20;

    private readonly InstallerAnalysisTempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static (int Handles, long PrivateBytes) Settled()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        using var self = Process.GetCurrentProcess();
        return (self.HandleCount, self.PrivateMemorySize64);
    }

    private InstallerAnalysisViewModel Page()
    {
        var sandboxRoot = _temp.PathOf("sandbox");
        var sandbox = new AnalysisSandbox(sandboxRoot);
        return new InstallerAnalysisViewModel(new InstallerAnalysisPageServices(
            new InstallerAnalysisService(sandbox), sandbox, new InstallerReportWriter(_temp.PathOf("reports")), new FakeInstallerAnalysisPrompts(),
            () => null, _ => { }, new RecordingAnalysisLog(), AnalysisLimits.Default, () => sandboxRoot));
    }

    private async Task CycleAsync(string[] files)
    {
        var page = Page();
        await page.OnNavigatedToAsync();
        foreach (var file in files)
        {
            await page.OpenAsync(file);
            foreach (var tab in new[] { InstallerAnalysisViewModel.PayloadTab, InstallerAnalysisViewModel.RegistryTab, InstallerAnalysisViewModel.StringsTab })
            {
                page.SelectedTabIndex = tab;
                await Wait.UntilAsync(() => !page.IsBusy, "the tab");
            }
            page.SelectedTabIndex = InstallerAnalysisViewModel.OverviewTab;
        }
        await page.OnNavigatedFromAsync();
    }

    [Fact]
    public async Task FiftyOpenAndCloseCyclesKeepNoHandle()
    {
        string[] files = [SevenZipMsi, Fixture("nsis-mk-lzma-solid-unicode.bin"), Fixture("inno-admin-670.bin"), Fixture("nsis-mk-oninit-pf64.bin")];
        for (var i = 0; i < 3; i++) await CycleAsync(files);
        var before = Settled();

        for (var i = 0; i < Cycles; i++) await CycleAsync(files);
        var after = Settled();

        output.WriteLine($"Handles {before.Handles} -> {after.Handles}; private bytes {before.PrivateBytes / (1024 * 1024)} MB -> {after.PrivateBytes / (1024 * 1024)} MB after {Cycles} cycles of {files.Length} files.");
        Assert.True(after.Handles - before.Handles < AllowedGrowth,
            $"The handle count grew from {before.Handles} to {after.Handles} over {Cycles} cycles.");
    }
}
