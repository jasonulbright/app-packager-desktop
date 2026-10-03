using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>Estimated and maximum runtime minutes for one deployment type, and where they came from.</summary>
public sealed record ResolvedTiming(int EstimatedRuntimeMins, int MaximumRuntimeMins, string Source);

/// <summary>Resolves and validates deployment type runtime minutes.</summary>
public static class DeploymentTypeTiming
{
    public const int MinimumMinutes = 1;
    public const int MaximumAllowedMinutes = 1440;
    public const int MonitoringCeilingMinutes = 720;

    /// <summary>
    /// The manifest's Timing wins over the caller's defaults. Both values are whole
    /// minutes from 1 to 1440 and the estimate may not exceed the maximum. A maximum
    /// above the 12-hour monitoring ceiling is accepted and reported, because a
    /// maximum longer than a maintenance window keeps the deployment type from running.
    /// </summary>
    public static ResolvedTiming Resolve(StageTiming? timing, int defaultEstimated = 15, int defaultMaximum = 30,
        string context = "deployment type", Action<string>? onLog = null)
    {
        var estimated = defaultEstimated;
        var maximum = defaultMaximum;
        var source = "parameters";
        if (timing?.EstimatedMinutes is int e) { estimated = e; source = "manifest"; }
        if (timing?.MaximumMinutes is int m) { maximum = m; source = "manifest"; }

        foreach (var (name, value) in new[] { ("estimated installation time", estimated), ("maximum allowed run time", maximum) })
        {
            if (value < MinimumMinutes || value > MaximumAllowedMinutes)
                throw new InvalidOperationException(
                    $"Timing for the {context} is out of range: {name} is {value} minutes; use a whole number of minutes from 1 to 1440.");
        }
        if (estimated > maximum)
            throw new InvalidOperationException(
                $"Timing for the {context} is inconsistent: estimated installation time {estimated} minutes exceeds the maximum allowed run time {maximum} minutes.");
        if (maximum > MonitoringCeilingMinutes)
            onLog?.Invoke($"Maximum allowed run time      : {maximum} minutes exceeds the 12-hour monitoring ceiling; a maintenance window shorter than this blocks the deployment type.");

        return new ResolvedTiming(estimated, maximum, source);
    }
}
