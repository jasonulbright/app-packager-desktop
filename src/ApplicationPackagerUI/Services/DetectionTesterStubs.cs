using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class DetectionTesterRegistration
{
    static partial void AddPendingServices(IServiceCollection services)
    {
        services.TryAddSingleton<IInstalledApplicationsService, StubInstalledApplicationsService>();
        services.TryAddSingleton<IDetectionTestService, StubDetectionTestService>();
        services.TryAddSingleton<IDetectionHistoryStore, StubDetectionHistoryStore>();
        services.TryAddSingleton<IDetectionReportWriter, StubDetectionReportWriter>();
    }
}

internal sealed class StubInstalledApplicationsService : IInstalledApplicationsService
{
    public IReadOnlyList<InstalledApplication> Enumerate() => [];
}

internal sealed class StubDetectionTestService : IDetectionTestService
{
    public Task<DetectionTestResult> RunAsync(DetectionTestRequest request, CancellationToken ct) =>
        Task.FromResult(new DetectionTestResult(false, request.Negative, "The detection test service is not in this build.", [], null, null, null,
            "Windows PowerShell 5.1, current user, not SYSTEM"));
}

internal sealed class StubDetectionHistoryStore : IDetectionHistoryStore
{
    private readonly List<DetectionHistoryEntry> _entries = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<DetectionHistoryEntry> All()
    {
        lock (_gate) return _entries.ToList();
    }

    public void Add(DetectionHistoryEntry entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }
}

internal sealed class StubDetectionReportWriter : IDetectionReportWriter
{
    public string WriteCsv(IReadOnlyList<DetectionHistoryEntry> entries) => throw new InvalidOperationException("The report writer is not in this build.");

    public string WriteHtml(IReadOnlyList<DetectionHistoryEntry> entries) => throw new InvalidOperationException("The report writer is not in this build.");
}
