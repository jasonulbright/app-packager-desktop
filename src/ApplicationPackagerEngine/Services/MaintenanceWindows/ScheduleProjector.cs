using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Projects the occurrences of a maintenance window schedule the way a ConfigMgr client
/// opens them. A local-time window opens at the same wall-clock time every day; a start
/// time that a daylight saving change skips moves forward by the skipped interval. A UTC
/// window is converted with the UTC offset in effect at its effective date, for every
/// occurrence, because the client keeps that offset across daylight saving changes.
/// </summary>
public sealed class ScheduleProjector(TimeZoneInfo? zone = null) : IScheduleProjector
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    private const int MaxSteps = 200_000;
    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;

    public IReadOnlyList<DateTime> NextOccurrences(MaintenanceSchedule schedule, DateTime from, int count)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (count <= 0 || !IsProjectable(schedule)) return [];

        var start = Wall(schedule.Start);
        var bias = schedule.IsUtc ? _zone.GetUtcOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc)) : TimeSpan.Zero;
        var local = from.Kind == DateTimeKind.Utc ? TimeZoneInfo.ConvertTimeFromUtc(from, _zone) : Wall(from);
        // An occurrence is still open at from when it starts after this time.
        var openAfter = local - bias - schedule.Duration;
        DateTime? end = schedule.End is { } e ? Wall(e) : null;

        var result = new List<DateTime>(Math.Min(count, 64));
        foreach (var occurrence in Candidates(schedule, start, openAfter))
        {
            if (end is { } last && occurrence > last) break;
            if (occurrence < start || occurrence <= openAfter) continue;
            result.Add(schedule.IsUtc ? occurrence + bias : LocalStart(occurrence));
            if (result.Count == count) break;
        }
        return result;
    }

    public string Describe(MaintenanceSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var effective = Wall(schedule.Start).ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);
        var n = schedule.Interval.ToString(CultureInfo.InvariantCulture);
        var day = schedule.DayOfWeek?.ToString() ?? "?";
        switch (schedule.Kind)
        {
            case MaintenanceScheduleKind.OneTime:
                return $"Occurs on {effective}";
            case MaintenanceScheduleKind.Daily:
                return $"Occurs every {n} days effective {effective}";
            case MaintenanceScheduleKind.Weekly:
                return $"Occurs every {n} weeks on {day} effective {effective}";
            case MaintenanceScheduleKind.MonthlyByDate:
                return schedule.DayOfMonth == 0
                    ? $"Occurs the last day of every {n} months effective {effective}"
                    : $"Occurs day {schedule.DayOfMonth?.ToString(CultureInfo.InvariantCulture) ?? "?"} of every {n} months effective {effective}";
            case MaintenanceScheduleKind.MonthlyByWeekday:
                return Weekday(schedule.PatchTuesdayOffsetDays, Order(schedule.WeekOrder), day, n, effective);
            case MaintenanceScheduleKind.PatchTuesday:
                var (week, offset) = ScheduleToken.PatchTuesdayForm(schedule.PatchTuesdayOffsetDays);
                return Weekday(offset, Order(week), nameof(DayOfWeek.Tuesday), n, effective);
            default:
                return "Unknown schedule";
        }

        static string Weekday(int offset, string order, string day, string n, string effective) => offset > 0
            ? $"Occurs {offset.ToString(CultureInfo.InvariantCulture)} day(s) after the {order} {day} of every {n} months effective {effective}"
            : $"Occurs the {order} {day} of every {n} months effective {effective}";

        static string Order(int? week) => week switch
        {
            1 => "First",
            2 => "Second",
            3 => "Third",
            4 => "Fourth",
            5 => "Last",
            _ => "?"
        };
    }

    public string? Validate(MaintenanceSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Duration < TimeSpan.FromMinutes(1))
            return "The duration must be at least 1 minute.";
        if (schedule.Duration > MaxDuration)
            return "The duration must be 24 hours or less. ConfigMgr stores no longer maintenance window.";
        if (schedule.Duration.Ticks % TimeSpan.TicksPerMinute != 0)
            return "The duration must be a whole number of minutes.";
        if (schedule.Start.Year is < ScheduleToken.FirstYear or > ScheduleToken.LastYear)
            return $"The start date must be in the years {ScheduleToken.FirstYear} to {ScheduleToken.LastYear}. ConfigMgr cannot store a later date.";
        if (schedule.End is not null)
            return "ConfigMgr stores no end date for a maintenance window. Remove the end date.";

        switch (schedule.Kind)
        {
            case MaintenanceScheduleKind.OneTime:
                return null;
            case MaintenanceScheduleKind.Daily:
                return schedule.Interval is >= 1 and <= 31 ? null : "A daily window recurs every 1 to 31 days.";
            case MaintenanceScheduleKind.Weekly:
                if (schedule.DayOfWeek is null) return "Select the day of the week.";
                return schedule.Interval is >= 1 and <= 4 ? null : "A weekly window recurs every 1 to 4 weeks.";
            case MaintenanceScheduleKind.MonthlyByDate:
                if (schedule.DayOfMonth is not (>= 0 and <= 31)) return "Select a day of the month from 1 to 31, or the last day.";
                return MonthsValid(schedule);
            case MaintenanceScheduleKind.MonthlyByWeekday:
                if (schedule.DayOfWeek is null) return "Select the day of the week.";
                if (schedule.WeekOrder is not (>= 1 and <= 5)) return "Select the week: first, second, third, fourth or last.";
                if (schedule.PatchTuesdayOffsetDays is < 0 or > 7) return "The offset must be 0 to 7 days.";
                return MonthsValid(schedule);
            case MaintenanceScheduleKind.PatchTuesday:
                if (schedule.PatchTuesdayOffsetDays is < -7 or > 21)
                    return "ConfigMgr can store a Patch Tuesday offset from 7 days before to 21 days after Patch Tuesday.";
                return MonthsValid(schedule);
            default:
                return "The schedule kind is not known.";
        }

        static string? MonthsValid(MaintenanceSchedule s) =>
            s.Interval is >= 1 and <= 12 ? null : "A monthly window recurs every 1 to 12 months.";
    }

    private static bool IsProjectable(MaintenanceSchedule s) =>
        s.Duration > TimeSpan.Zero
        && (s.Kind == MaintenanceScheduleKind.OneTime || s.Interval >= 1)
        && s.Kind switch
        {
            MaintenanceScheduleKind.OneTime or MaintenanceScheduleKind.Daily or MaintenanceScheduleKind.PatchTuesday => true,
            MaintenanceScheduleKind.Weekly => s.DayOfWeek is not null,
            MaintenanceScheduleKind.MonthlyByDate => s.DayOfMonth is >= 0 and <= 31,
            MaintenanceScheduleKind.MonthlyByWeekday => s.DayOfWeek is not null && s.WeekOrder is >= 1 and <= 5,
            _ => false
        };

    private static DateTime Wall(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>Wall-clock occurrence starts in ascending order, from shortly before <paramref name="openAfter"/>.</summary>
    private static IEnumerable<DateTime> Candidates(MaintenanceSchedule s, DateTime start, DateTime openAfter)
    {
        switch (s.Kind)
        {
            case MaintenanceScheduleKind.OneTime:
                yield return start;
                yield break;
            case MaintenanceScheduleKind.Daily:
            case MaintenanceScheduleKind.Weekly:
                var first = start;
                var stepDays = s.Interval;
                if (s.Kind == MaintenanceScheduleKind.Weekly)
                {
                    first = start.AddDays(((int)s.DayOfWeek!.Value - (int)start.DayOfWeek + 7) % 7);
                    stepDays = 7 * s.Interval;
                }
                var skip = openAfter > first ? (long)Math.Floor((openAfter - first).TotalDays / stepDays) : 0;
                for (var k = Math.Max(0, skip - 1); k < skip + MaxSteps; k++)
                {
                    var days = k * stepDays;
                    if (days > (DateTime.MaxValue - first).TotalDays) yield break;
                    yield return first.AddDays(days);
                }
                yield break;
            default:
                var firstMonth = start.Year * 12 + start.Month - 1;
                var targetMonth = openAfter.Year * 12 + openAfter.Month - 1;
                var skipMonths = targetMonth > firstMonth ? (targetMonth - firstMonth) / s.Interval : 0;
                for (var k = Math.Max(0, skipMonths - 1); k < skipMonths + MaxSteps; k++)
                {
                    var index = firstMonth + k * s.Interval;
                    var year = index / 12;
                    if (year > DateTime.MaxValue.Year - 1) yield break;
                    if (DayIn(s, year, index % 12 + 1) is { } date)
                        yield return date + start.TimeOfDay;
                }
                yield break;
        }
    }

    /// <summary>The occurrence date in the month, or null for a month without that day of the month.</summary>
    private static DateTime? DayIn(MaintenanceSchedule s, int year, int month)
    {
        var length = DateTime.DaysInMonth(year, month);
        switch (s.Kind)
        {
            case MaintenanceScheduleKind.MonthlyByDate:
                var day = s.DayOfMonth is 0 ? length : s.DayOfMonth!.Value;
                return day <= length ? new DateTime(year, month, day) : null;
            case MaintenanceScheduleKind.MonthlyByWeekday:
                return NthWeekday(year, month, s.DayOfWeek!.Value, s.WeekOrder!.Value).AddDays(s.PatchTuesdayOffsetDays);
            default:
                return NthWeekday(year, month, DayOfWeek.Tuesday, 2).AddDays(s.PatchTuesdayOffsetDays);
        }
    }

    private static DateTime NthWeekday(int year, int month, DayOfWeek day, int week)
    {
        if (week == 5)
        {
            var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            return last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
        }
        var first = new DateTime(year, month, 1);
        return first.AddDays(((int)day - (int)first.DayOfWeek + 7) % 7 + 7 * (week - 1));
    }

    private DateTime LocalStart(DateTime wall)
    {
        if (!_zone.IsInvalidTime(wall)) return wall;
        var gap = _zone.GetUtcOffset(wall.AddHours(6)) - _zone.GetUtcOffset(wall.AddHours(-6));
        return gap > TimeSpan.Zero ? wall + gap : wall.AddHours(1);
    }
}
