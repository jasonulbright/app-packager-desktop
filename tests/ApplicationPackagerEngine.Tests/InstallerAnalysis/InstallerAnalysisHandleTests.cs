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
    private const int WarmUpCycles = 10;
    private const int Windows = 5;
    private const int CyclesPerWindow = 20;
    private const int MinimumWindowGrowthForLeak = 5;
    private const int TotalGrowthCap = 100;

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

    /// <summary>
    /// The process handle count also moves with the thread pool size (a thread and its events per
    /// pool thread), so one reading can grow without a leak. A handle kept per cycle grows every
    /// window by at least the window length; pool growth stops after the first windows.
    /// </summary>
    [Fact]
    public async Task OpenAndCloseCyclesKeepNoHandle()
    {
        string[] files = [SevenZipMsi, Fixture("nsis-mk-lzma-solid-unicode.bin"), Fixture("inno-admin-670.bin"), Fixture("nsis-mk-oninit-pf64.bin")];
        for (var i = 0; i < WarmUpCycles; i++) await CycleAsync(files);

        var readings = new List<(int Handles, long PrivateBytes)> { Settled() };
        for (var window = 0; window < Windows; window++)
        {
            for (var i = 0; i < CyclesPerWindow; i++) await CycleAsync(files);
            readings.Add(Settled());
        }

        var growth = readings.Zip(readings.Skip(1), (before, after) => after.Handles - before.Handles).ToArray();
        var total = readings[^1].Handles - readings[0].Handles;
        output.WriteLine($"Handles per reading: {string.Join(" -> ", readings.Select(r => r.Handles))}; growth per window of {CyclesPerWindow} cycles: {string.Join(", ", growth)}.");
        output.WriteLine($"Private bytes {readings[0].PrivateBytes / (1024 * 1024)} MB -> {readings[^1].PrivateBytes / (1024 * 1024)} MB after {Windows * CyclesPerWindow} cycles of {files.Length} files.");
        Assert.True(growth.Min() < MinimumWindowGrowthForLeak,
            $"The handle count grew in every window of {CyclesPerWindow} cycles: {string.Join(", ", growth)}.");
        Assert.True(total < TotalGrowthCap,
            $"The handle count grew from {readings[0].Handles} to {readings[^1].Handles} over {Windows * CyclesPerWindow} cycles.");
    }
}
