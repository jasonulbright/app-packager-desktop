using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static partial class MaintenanceWindowsRegistration
{
    static partial void AddTemporaryServices(IServiceCollection services)
    {
        services.TryAddSingleton<IScheduleProjector, StubScheduleProjector>();
        services.TryAddSingleton<IMaintenanceWindowService, StubMaintenanceWindowService>();
        services.TryAddSingleton<IMaintenanceTemplateStore, StubMaintenanceTemplateStore>();
        services.TryAddSingleton<IMaintenanceReportWriter, StubMaintenanceReportWriter>();
    }
}

internal static class StubText
{
    public const string Missing = "The maintenance window services are not in this build.";
}

internal sealed class StubScheduleProjector : IScheduleProjector
{
    public IReadOnlyList<DateTime> NextOccurrences(MaintenanceSchedule schedule, DateTime from, int count)
    {
        var list = new List<DateTime>();
        var next = schedule.Start;
        for (var i = 0; i < 1000 && list.Count < count; i++)
        {
            if (next >= from && (schedule.End is null || next <= schedule.End)) list.Add(next);
            if (schedule.Kind == MaintenanceScheduleKind.OneTime) break;
            var step = Math.Max(1, schedule.Interval);
            next = schedule.Kind switch
            {
                MaintenanceScheduleKind.Daily => next.AddDays(step),
                MaintenanceScheduleKind.Weekly => next.AddDays(7 * step),
                _ => next.AddMonths(step)
            };
        }
        return list;
    }

    public string Describe(MaintenanceSchedule schedule) =>
        $"{schedule.Kind} from {schedule.Start:yyyy-MM-dd HH:mm} for {schedule.Duration:hh\\:mm}";
}

internal sealed class StubMaintenanceWindowService : IMaintenanceWindowService
{
    private static ConfigMgrSessionException Missing() => new(ConfigMgrFailure.ScriptFailed, StubText.Missing);

    public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken ct) => throw Missing();

    public Task<IReadOnlyList<MaintenanceWindowInfo>> ListWindowsAsync(CancellationToken ct) => throw Missing();

    public Task<MaintenanceChange> PrepareAsync(MaintenanceChange request, CancellationToken ct) => throw Missing();

    public Task<IReadOnlyList<MaintenanceOutcome>> ApplyAsync(IReadOnlyList<MaintenanceChange> changes, IProgress<MaintenanceOutcome> progress, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MaintenanceOutcome>>(changes.Select(c => new MaintenanceOutcome(c, false, StubText.Missing, null)).ToList());
}

internal sealed class StubMaintenanceTemplateStore : IMaintenanceTemplateStore
{
    private readonly List<MaintenanceTemplate> _templates = [];

    public IReadOnlyList<MaintenanceTemplate> All()
    {
        lock (_templates) return _templates.ToArray();
    }

    public void Save(MaintenanceTemplate t)
    {
        lock (_templates)
        {
            _templates.RemoveAll(x => x.Id == t.Id);
            _templates.Add(t);
        }
    }

    public void Delete(string id)
    {
        lock (_templates) _templates.RemoveAll(x => x.Id == id);
    }
}

internal sealed class StubMaintenanceReportWriter : IMaintenanceReportWriter
{
    public string WriteCsv(IReadOnlyList<MaintenanceWindowInfo> rows) => throw new InvalidOperationException(StubText.Missing);

    public string WriteHtml(IReadOnlyList<MaintenanceWindowInfo> rows) => throw new InvalidOperationException(StubText.Missing);
}
