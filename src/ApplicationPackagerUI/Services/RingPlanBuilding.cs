using System.Globalization;
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

/// <summary>
/// Compares names with each run of digits read as a number, so "Zone 2" sorts before "Zone 10".
/// The other characters compare with the invariant culture, ignoring case; equal names then compare ordinally.
/// </summary>
public sealed class NaturalNameComparer : IComparer<string?>
{
    public static NaturalNameComparer Instance { get; } = new();

    private static readonly CompareInfo Invariant = CultureInfo.InvariantCulture.CompareInfo;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var xDigit = char.IsAsciiDigit(x[i]);
            var yDigit = char.IsAsciiDigit(y[j]);
            var xEnd = Run(x, i, xDigit);
            var yEnd = Run(y, j, yDigit);
            int result;
            if (xDigit && yDigit)
            {
                var a = x.AsSpan(i, xEnd - i).TrimStart('0');
                var b = y.AsSpan(j, yEnd - j).TrimStart('0');
                result = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.SequenceCompareTo(b);
            }
            else result = Invariant.Compare(x, i, xEnd - i, y, j, yEnd - j, CompareOptions.IgnoreCase);
            if (result != 0) return result;
            i = xEnd;
            j = yEnd;
        }
        var length = (x.Length - i).CompareTo(y.Length - j);
        return length != 0 ? length : string.CompareOrdinal(x, y);
    }

    private static int Run(string text, int start, bool digits)
    {
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]) == digits) end++;
        return end;
    }
}

/// <summary>
/// The plan editor helpers over the ring services' <see cref="RingPlanBuilder"/>. The builder refuses
/// input that breaks the plan rules with a plain message; each helper returns that message and no rings.
/// </summary>
public static class RingPlanHelpers
{
    private static bool Required(RingDefinition ring) => string.Equals(ring.Purpose, DeploymentPurposes.Required, StringComparison.OrdinalIgnoreCase);

    private static DeploymentCollection Collection(CollectionRow row) => new(row.CollectionId, row.Name, row.FolderPath, row.MemberCount);

    /// <summary>One Available ring per collection, in the given order or sorted by name (numbers as numbers), named after the collection.</summary>
    public static (IReadOnlyList<RingDefinition> Rings, string? Problem) FromCollections(IReadOnlyList<CollectionRow> collections, bool sortByName,
        int firstOffsetDays, int daysBetween, int deadlineDays)
    {
        var ordered = sortByName ? collections.OrderBy(c => c.Name, NaturalNameComparer.Instance).ToList() : collections.ToList();
        try
        {
            return (RingPlanBuilder.FromCollections(ordered.Select(Collection).ToList(), DeploymentPurposes.Available, firstOffsetDays, daysBetween, deadlineDays), null);
        }
        catch (DeploymentServiceException ex) { return ([], ex.Message); }
    }

    /// <summary>The rings with the offsets of the helper fields; every other setting of each ring stays as it is.</summary>
    public static (IReadOnlyList<RingDefinition> Rings, string? Problem) FillOffsets(IReadOnlyList<RingDefinition> rings, int firstOffsetDays, int daysBetween, int deadlineDays)
    {
        try
        {
            var normal = rings.Select(r => Required(r) ? r with { Purpose = DeploymentPurposes.Required } : r).ToList();
            var filled = RingPlanBuilder.FillOffsets(normal, firstOffsetDays, daysBetween, deadlineDays);
            return (filled.Select((r, i) => r with { Purpose = rings[i].Purpose }).ToList(), null);
        }
        catch (DeploymentServiceException ex) { return ([], ex.Message); }
    }

    /// <summary>
    /// Every ring with the chosen settings of <paramref name="source"/>. A ring that becomes Required
    /// without a deadline after its available time gets one <paramref name="deadlineDays"/> days after it;
    /// an option the source leaves at its default becomes the default on every ring.
    /// </summary>
    public static (IReadOnlyList<RingDefinition> Rings, string? Problem) ApplyToAll(IReadOnlyList<RingDefinition> rings, RingDefinition source, RingSettings settings, int deadlineDays)
    {
        var options = settings.HasFlag(RingSettings.Options);
        var bulk = new RingBulkSettings(
            Purpose: settings.HasFlag(RingSettings.Purpose) ? (Required(source) ? DeploymentPurposes.Required : DeploymentPurposes.Available) : null,
            UserNotification: settings.HasFlag(RingSettings.Notification) ? source.UserNotification : null,
            OverrideServiceWindow: options ? source.OverrideServiceWindow : null,
            RebootOutsideServiceWindow: options ? source.RebootOutsideServiceWindow : null,
            AllowMetered: options ? source.AllowMetered : null,
            AllowBoundaryFallback: options ? source.AllowBoundaryFallback : null,
            AllowMicrosoftUpdate: options ? source.AllowMicrosoftUpdate : null,
            RequirePostRebootFullScan: options ? source.RequirePostRebootFullScan : null,
            ShowTaskSequenceProgress: options ? source.ShowTaskSequenceProgress : null,
            TaskSequenceAvailability: options ? source.TaskSequenceAvailability : null);
        IReadOnlyList<RingDefinition> applied;
        try { applied = RingPlanBuilder.ApplyToAll(rings, bulk); }
        catch (DeploymentServiceException ex) { return ([], ex.Message); }
        // A null option means "leave as it is" to the builder, so the default of the source is set here.
        return (applied.Select(r =>
        {
            var ring = options
                ? r with
                {
                    FastNetworkOption = source.FastNetworkOption, SlowNetworkOption = source.SlowNetworkOption, RerunBehavior = source.RerunBehavior,
                    TaskSequenceDeploymentOption = source.TaskSequenceDeploymentOption
                }
                : r;
            if (Required(ring) && (ring.DeadlineOffsetDays is not { } d || d <= ring.AvailableOffsetDays))
                ring = ring with { DeadlineOffsetDays = ring.AvailableOffsetDays + Math.Max(1, deadlineDays) };
            return ring;
        }).ToList(), null);
    }
}
