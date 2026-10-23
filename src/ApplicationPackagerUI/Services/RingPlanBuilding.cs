using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Services;

/// <summary>The settings that "apply to all rings" copies from one ring to every ring of a plan.</summary>
[Flags]
public enum RingSettings
{
    None = 0,
    Purpose = 1,
    Notification = 2,
    Options = 4,
    All = Purpose | Notification | Options
}

/// <summary>The generators of the plan editor: rings from collections, offsets for every ring and one ring's settings for all.</summary>
public interface IRingPlanBuilder
{
    /// <summary>One Available ring per collection, in the given order, named after the collection, with the offsets of <see cref="FillOffsets"/>.</summary>
    IReadOnlyList<RingDefinition> FromCollections(IReadOnlyList<CollectionRow> collections, int firstOffsetDays, int daysBetween, int deadlineDays);

    /// <summary>
    /// Ring i becomes available <paramref name="firstOffsetDays"/> + i × <paramref name="daysBetween"/> days after the plan start; a
    /// Required ring gets its deadline <paramref name="deadlineDays"/> days after its available time (at least 1 day).
    /// </summary>
    IReadOnlyList<RingDefinition> FillOffsets(IReadOnlyList<RingDefinition> rings, int firstOffsetDays, int daysBetween, int deadlineDays);

    /// <summary>
    /// Every ring with the chosen settings of <paramref name="source"/>. A ring that becomes Required without a deadline gets one
    /// <paramref name="deadlineDays"/> days after its available time; a ring that becomes Available loses its deadline.
    /// </summary>
    IReadOnlyList<RingDefinition> ApplyToAll(IReadOnlyList<RingDefinition> rings, RingDefinition source, RingSettings settings, int deadlineDays);
}

/// <summary>The editor's own generators; the ring services' builder replaces it with the same results.</summary>
public sealed class LocalRingPlanBuilder : IRingPlanBuilder
{
    public static LocalRingPlanBuilder Instance { get; } = new();

    private static bool Required(RingDefinition ring) => string.Equals(ring.Purpose, "Required", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<RingDefinition> FromCollections(IReadOnlyList<CollectionRow> collections, int firstOffsetDays, int daysBetween, int deadlineDays) =>
        FillOffsets(collections.Take(RingPlanLimits.MaxRings).Select(c => new RingDefinition(Name: c.Name, CollectionId: c.CollectionId, CollectionName: c.Name,
            Purpose: "Available", AvailableOffsetDays: 0, DeadlineOffsetDays: null, UserNotification: "DisplayAll", OverrideServiceWindow: false,
            RebootOutsideServiceWindow: false, AllowMetered: false, DpGroup: "", SuccessThresholdPercent: null, FastNetworkOption: null, SlowNetworkOption: null,
            RerunBehavior: null)).ToList(), firstOffsetDays, daysBetween, deadlineDays);

    public IReadOnlyList<RingDefinition> FillOffsets(IReadOnlyList<RingDefinition> rings, int firstOffsetDays, int daysBetween, int deadlineDays) =>
        rings.Select((ring, i) =>
        {
            var available = Math.Max(0, firstOffsetDays) + i * Math.Max(0, daysBetween);
            return ring with { AvailableOffsetDays = available, DeadlineOffsetDays = Required(ring) ? available + Math.Max(1, deadlineDays) : null };
        }).ToList();

    public IReadOnlyList<RingDefinition> ApplyToAll(IReadOnlyList<RingDefinition> rings, RingDefinition source, RingSettings settings, int deadlineDays) =>
        rings.Select(ring =>
        {
            var copy = ring;
            if (settings.HasFlag(RingSettings.Purpose))
                copy = Required(source)
                    ? copy with { Purpose = source.Purpose, DeadlineOffsetDays = copy.DeadlineOffsetDays is { } d && d > copy.AvailableOffsetDays ? d : copy.AvailableOffsetDays + Math.Max(1, deadlineDays) }
                    : copy with { Purpose = source.Purpose, DeadlineOffsetDays = null };
            if (settings.HasFlag(RingSettings.Notification)) copy = copy with { UserNotification = source.UserNotification };
            if (settings.HasFlag(RingSettings.Options))
                copy = copy with
                {
                    OverrideServiceWindow = source.OverrideServiceWindow, RebootOutsideServiceWindow = source.RebootOutsideServiceWindow, AllowMetered = source.AllowMetered,
                    FastNetworkOption = source.FastNetworkOption, SlowNetworkOption = source.SlowNetworkOption, RerunBehavior = source.RerunBehavior
                };
            return copy;
        }).ToList();
}
