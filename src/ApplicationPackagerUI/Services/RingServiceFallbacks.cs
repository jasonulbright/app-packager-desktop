using ApplicationPackagerEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplicationPackagerUI.Services;

public static class RingServiceRegistration
{
    /// <summary>Registers the unavailable ring service when no ring service is registered; a no-op once the ring services exist.</summary>
    public static IServiceCollection AddRingServiceFallback(this IServiceCollection services)
    {
        services.TryAddSingleton<IRingService, UnavailableRingService>();
        return services;
    }
}

/// <summary>The ring service of a build without ring deployments: it lists nothing and refuses every change with a plain message.</summary>
public sealed class UnavailableRingService : IRingService
{
    public const string Text = "Ring deployments are not available in this build. Nothing was changed.";

    public string? PlanMessage => Text;
    public IReadOnlyList<RingPlan> ListPlans() => [];
    public RingPlan? SavePlan(RingPlan plan, bool replace) => throw new DeploymentServiceException(Text);
    public void DeletePlan(string name) => throw new DeploymentServiceException(Text);
    public Task<IReadOnlyList<RingExpansionRow>> ExpandAsync(RingPlan plan, DeployableObject obj, DateTime planStart, CancellationToken ct) => throw new DeploymentServiceException(Text);
    public Task<IReadOnlyList<RingRun>> ListRunsAsync(bool closed, CancellationToken ct) => Task.FromResult<IReadOnlyList<RingRun>>([]);
    public Task<IReadOnlyList<RingLiveCounts>> LiveCountsAsync(RingRun run, CancellationToken ct) => Task.FromResult<IReadOnlyList<RingLiveCounts>>([]);
    public Task<RingRun> ReconcileAsync(string runId, CancellationToken ct) => throw new DeploymentServiceException(Text);
    public Task<RingChange> PrepareAsync(RingChange request, CancellationToken ct) => throw new DeploymentServiceException(Text);
    public Task<IReadOnlyList<RingOutcome>> ApplyAsync(IReadOnlyList<RingChange> changes, IProgress<RingOutcome> progress, CancellationToken ct) => throw new DeploymentServiceException(Text);
    public RingFolderProbe ProbeFolders() => new(PlansFolder: "", PlansWritable: false, RunsFolder: "", RunsWritable: false, LockCreated: false, IsUnc: false,
        Message: Text, StaleLockAge: TimeSpan.FromHours(1));
}
