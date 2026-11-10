using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>Estimated and maximum runtime minutes for one deployment type, and where they came from.</summary>
public sealed record ResolvedTiming(int EstimatedRuntimeMins, int MaximumRuntimeMins, string Source);

/// <summary>Resolves and validates deployment type runtime minutes.</summary>
public static class DeploymentTypeTiming
{
    public const int MinimumMinutes = 1;

    /// <summary>The site refuses a maximum run time below 15 minutes (lab: ConfigMgr 5.00.9146, range 15 to 720).</summary>
    public const int MinimumMaximumRuntimeMinutes = 15;

    /// <summary>The site refuses a maximum run time above 720 minutes, so the publish refuses it before it writes anything.</summary>
    public const int MaximumAllowedMinutes = 720;

    /// <summary>
    /// The manifest's Timing wins over the caller's defaults. The estimate is a whole number of minutes from 1 to 720, the
    /// maximum a whole number from 15 to 720, and the estimate may not exceed the maximum. These are the limits of the
    /// ConfigMgr cmdlet, so a value outside them is refused here, before the site is touched.
    /// </summary>
    public static ResolvedTiming Resolve(StageTiming? timing, int defaultEstimated = 15, int defaultMaximum = 30,
        string context = "deployment type", Action<string>? onLog = null)
    {
        var estimated = defaultEstimated;
        var maximum = defaultMaximum;
        var source = "parameters";
        if (timing?.EstimatedMinutes is int e) { estimated = e; source = "manifest"; }
        if (timing?.MaximumMinutes is int m) { maximum = m; source = "manifest"; }

        foreach (var (name, value, low) in new[]
                 {
                     ("estimated installation time", estimated, MinimumMinutes),
                     ("maximum allowed run time", maximum, MinimumMaximumRuntimeMinutes)
                 })
        {
            if (value < low || value > MaximumAllowedMinutes)
                throw new InvalidOperationException(
                    $"Timing for the {context} is out of range: {name} is {value} minutes; use a whole number of minutes from {low} to {MaximumAllowedMinutes}.");
        }
        if (estimated > maximum)
            throw new InvalidOperationException(
                $"Timing for the {context} is inconsistent: estimated installation time {estimated} minutes exceeds the maximum allowed run time {maximum} minutes.");

        return new ResolvedTiming(estimated, maximum, source);
    }
}
