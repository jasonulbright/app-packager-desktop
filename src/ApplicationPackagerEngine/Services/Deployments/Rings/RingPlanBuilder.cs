using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>The settings <see cref="RingPlanBuilder.ApplyToAll"/> sets on every ring; a null member leaves that setting of each ring as it is.</summary>
public sealed record RingBulkSettings(
    string? Purpose = null,
    string? UserNotification = null,
    bool? OverrideServiceWindow = null,
    bool? RebootOutsideServiceWindow = null,
    bool? AllowMetered = null,
    string? FastNetworkOption = null,
    string? SlowNetworkOption = null,
    string? RerunBehavior = null,
    bool? AllowBoundaryFallback = null,
    bool? AllowMicrosoftUpdate = null,
    bool? RequirePostRebootFullScan = null,
    bool? ShowTaskSequenceProgress = null,
    string? TaskSequenceAvailability = null,
    string? TaskSequenceDeploymentOption = null,
    string? DpGroup = null);

/// <summary>
/// The plan editor helpers: rings made from a list of collections, offsets filled from a first
/// offset and a spacing, and one setting applied to every ring. Pure functions; any number of rings.
/// </summary>
public static class RingPlanBuilder
{
    /// <summary>
    /// One ring per collection in the given order (or sorted by name), named after the collection,
    /// with the offsets of <see cref="FillOffsets"/>. Built-in and repeated collections are left
    /// out; more collections than the cap are refused.
    /// </summary>
    /// <param name="purpose">Available or Required for every ring.</param>
    public static IReadOnlyList<RingDefinition> FromCollections(IReadOnlyList<DeploymentCollection> collections, string purpose, int firstAvailableOffsetDays, int daysBetweenRings,
        int availableToDeadlineDays, bool sortByName = false, string userNotification = "DisplayAll")
    {
        ArgumentNullException.ThrowIfNull(collections);
        var chosen = collections.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.CollectionId) && !DeploymentService.IsBuiltInCollection(c.CollectionId))
            .GroupBy(c => c.CollectionId.Trim().ToUpperInvariant()).Select(g => g.First()).ToList();
        if (sortByName) chosen = [.. chosen.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
        if (chosen.Count > RingPlanLimits.MaxRings)
            throw new DeploymentServiceException($"{chosen.Count.ToString(CultureInfo.InvariantCulture)} collections were chosen. A plan can have at most {RingPlanLimits.MaxRings.ToString(CultureInfo.InvariantCulture)} rings.");
        var rings = chosen.Select(c => new RingDefinition(RingName(c), c.CollectionId.Trim().ToUpperInvariant(), c.Name ?? "", purpose, 0, null, userNotification,
            false, false, false, "", null, null, null, null)).ToList();
        return FillOffsets(rings, firstAvailableOffsetDays, daysBetweenRings, availableToDeadlineDays);
    }

    private static string RingName(DeploymentCollection c)
    {
        var name = new string((c.Name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (name.Length == 0) name = c.CollectionId.Trim().ToUpperInvariant();
        return name.Length > RingPlanRules.MaxNameLength ? name[..RingPlanRules.MaxNameLength].TrimEnd() : name;
    }

    /// <summary>
    /// Ring n (from 0) is available <paramref name="firstAvailableOffsetDays"/> + n × <paramref name="daysBetweenRings"/>
    /// days after the plan start; a Required ring's deadline is <paramref name="availableToDeadlineDays"/>
    /// later; an Available ring has no deadline. Refuses values that break the plan rules
    /// (negative values, no gap to the deadline, deadlines that do not grow).
    /// </summary>
    public static IReadOnlyList<RingDefinition> FillOffsets(IReadOnlyList<RingDefinition> rings, int firstAvailableOffsetDays, int daysBetweenRings, int availableToDeadlineDays)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (firstAvailableOffsetDays < 0) throw new DeploymentServiceException("The first available offset must be 0 days or more.");
        if (daysBetweenRings < 0) throw new DeploymentServiceException("The days between rings must be 0 or more.");
        if (rings.Any(r => r.Purpose == DeploymentPurposes.Required) && availableToDeadlineDays < 1)
            throw new DeploymentServiceException("The days from a ring's available time to its deadline must be 1 or more for Required rings.");
        if (rings.Count(r => r.Purpose == DeploymentPurposes.Required) > 1 && daysBetweenRings < 1)
            throw new DeploymentServiceException("With more than one Required ring the days between rings must be 1 or more, so each deadline is later than the one before.");
        var last = (long)firstAvailableOffsetDays + (long)Math.Max(rings.Count - 1, 0) * daysBetweenRings + availableToDeadlineDays;
        if (last > RingPlanRules.MaxOffsetDays)
            throw new DeploymentServiceException($"The last ring would be {last.ToString(CultureInfo.InvariantCulture)} days after the plan start. Offsets can be at most {RingPlanRules.MaxOffsetDays.ToString(CultureInfo.InvariantCulture)} days.");
        return rings.Select((r, n) =>
        {
            var available = firstAvailableOffsetDays + n * daysBetweenRings;
            return r with { AvailableOffsetDays = available, DeadlineOffsetDays = r.Purpose == DeploymentPurposes.Required ? available + availableToDeadlineDays : null };
        }).ToList();
    }

    /// <summary>
    /// Every ring with the given settings. A change to Available drops the deadline; a change to
    /// Required needs deadline offsets, which <see cref="FillOffsets"/> gives.
    /// </summary>
    public static IReadOnlyList<RingDefinition> ApplyToAll(IReadOnlyList<RingDefinition> rings, RingBulkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Purpose is { } p && p is not (DeploymentPurposes.Available or DeploymentPurposes.Required)) throw new DeploymentServiceException("Select the purpose: Available or Required.");
        if (settings.UserNotification is { } n && !DeploymentScheduleRules.Notifications.Contains(n)) throw new DeploymentServiceException("Select the user notification.");
        return rings.Select(r => r with
        {
            Purpose = settings.Purpose ?? r.Purpose,
            DeadlineOffsetDays = (settings.Purpose ?? r.Purpose) == DeploymentPurposes.Required ? r.DeadlineOffsetDays : null,
            UserNotification = settings.UserNotification ?? r.UserNotification,
            OverrideServiceWindow = settings.OverrideServiceWindow ?? r.OverrideServiceWindow,
            RebootOutsideServiceWindow = settings.RebootOutsideServiceWindow ?? r.RebootOutsideServiceWindow,
            AllowMetered = settings.AllowMetered ?? r.AllowMetered,
            FastNetworkOption = settings.FastNetworkOption ?? r.FastNetworkOption,
            SlowNetworkOption = settings.SlowNetworkOption ?? r.SlowNetworkOption,
            RerunBehavior = settings.RerunBehavior ?? r.RerunBehavior,
            AllowBoundaryFallback = settings.AllowBoundaryFallback ?? r.AllowBoundaryFallback,
            AllowMicrosoftUpdate = settings.AllowMicrosoftUpdate ?? r.AllowMicrosoftUpdate,
            RequirePostRebootFullScan = settings.RequirePostRebootFullScan ?? r.RequirePostRebootFullScan,
            ShowTaskSequenceProgress = settings.ShowTaskSequenceProgress ?? r.ShowTaskSequenceProgress,
            TaskSequenceAvailability = settings.TaskSequenceAvailability ?? r.TaskSequenceAvailability,
            TaskSequenceDeploymentOption = settings.TaskSequenceDeploymentOption ?? r.TaskSequenceDeploymentOption,
            DpGroup = settings.DpGroup ?? r.DpGroup
        }).ToList();
    }
}
