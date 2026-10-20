using System.IO;
using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class InstallerAnalysisRegistration
{
    static partial void AddServiceFallbacks(IServiceCollection services)
    {
        services.TryAddSingleton<IInstallerAnalysisService, UnavailableInstallerAnalysisService>();
        services.TryAddSingleton<IAnalysisSandbox, FolderAnalysisSandbox>();
        services.TryAddSingleton<IInstallerReportWriter, UnavailableInstallerReportWriter>();
    }
}

/// <summary>Stands in until the analysis service is registered; every call fails with a plain message.</summary>
internal sealed class UnavailableInstallerAnalysisService : IInstallerAnalysisService
{
    private static InvalidOperationException Unavailable() => new("The installer analysis service is not available in this build.");

    public Task<AnalysisReport> AnalyzeAsync(string path, AnalysisLimits limits, IProgress<string> progress, CancellationToken ct) => Task.FromException<AnalysisReport>(Unavailable());
    public Task<IReadOnlyList<PayloadEntry>> ListPayloadAsync(string path, AnalysisLimits limits, CancellationToken ct) => Task.FromException<IReadOnlyList<PayloadEntry>>(Unavailable());
    public Task<IReadOnlyList<RegistryWrite>> ReadRegistryWritesAsync(string path, AnalysisLimits limits, CancellationToken ct) => Task.FromException<IReadOnlyList<RegistryWrite>>(Unavailable());
    public Task<IReadOnlyList<InterestingString>> ReadStringsAsync(string path, AnalysisLimits limits, CancellationToken ct) => Task.FromException<IReadOnlyList<InterestingString>>(Unavailable());
    public Task<string> ExtractEntryAsync(string path, PayloadEntry entry, string sessionFolder, AnalysisLimits limits, CancellationToken ct) => Task.FromException<string>(Unavailable());
    public Task<string> ComputeSha256Async(string path, CancellationToken ct) => Task.FromException<string>(Unavailable());
}

/// <summary>Stands in until the analysis sandbox is registered: one folder per session under the data root.</summary>
internal sealed class FolderAnalysisSandbox : IAnalysisSandbox
{
    private static string Root => Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Temp");

    public string NewSession() => Directory.CreateDirectory(Path.Combine(Root, Guid.NewGuid().ToString("N"))).FullName;

    public void Release(string session)
    {
        if (AppDataPaths.IsWithin(session, Root) && Directory.Exists(session)) Directory.Delete(session, recursive: true);
    }

    public void ReleaseAll()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var folder in Directory.GetDirectories(Root)) Release(folder);
    }

    public void CleanOldSessions(TimeSpan olderThan)
    {
        if (!Directory.Exists(Root)) return;
        foreach (var folder in Directory.GetDirectories(Root))
            if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > olderThan) Release(folder);
    }
}

/// <summary>Stands in until the report writer is registered.</summary>
internal sealed class UnavailableInstallerReportWriter : IInstallerReportWriter
{
    private static InvalidOperationException Unavailable() => new("The installer report writer is not available in this build.");

    public string Summary(AnalysisReport r) => throw Unavailable();
    public string Json(AnalysisReport r, bool includeKeyMaterial) => throw Unavailable();
    public string WriteCsv(AnalysisReport r, IReadOnlyList<RegistryWrite> reg) => throw Unavailable();
    public string WriteHtml(AnalysisReport r, IReadOnlyList<PayloadEntry> payload, IReadOnlyList<RegistryWrite> reg) => throw Unavailable();
}
