using System.Globalization;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The 16-digit hexadecimal ConfigMgr schedule token. Bit layout from the most significant
/// bit: start minute (6), hour (5), day (5), month (4), year since 1970 (6), duration
/// minutes (6), hours (5), days (5), recurrence flag (3), then 19 bits that depend on the
/// flag, the lowest of which is the UTC flag.
/// </summary>
internal static class ScheduleToken
{
    private const int NonRecurring = 1;
    private const int Interval = 2;
    private const int Weekly = 3;
    private const int MonthlyByWeekday = 4;
    private const int MonthlyByDate = 5;
    private const int MonthlyByWeekdayOffset = 6;

    public const int FirstYear = 1970;
    public const int LastYear = FirstYear + 63;

    /// <summary>The schedule of a single token, or null for a token this model cannot hold (an hour or minute interval, several tokens, an unknown flag).</summary>
    public static MaintenanceSchedule? Decode(string? token)
    {
        if (token is null || token.Length != 16
            || !ulong.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return null;

        int Bits(int shift, int width) => (int)((v >> shift) & ((1UL << width) - 1));

        var minute = Bits(58, 6);
        var hour = Bits(53, 5);
        var day = Bits(48, 5);
        var month = Bits(44, 4);
        var year = FirstYear + Bits(38, 6);
        if (minute > 59 || hour > 23 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        var start = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        var duration = TimeSpan.FromMinutes(Bits(32, 6) + Bits(27, 5) * 60 + Bits(22, 5) * 1440);
        var utc = (v & 1) == 1;

        MaintenanceSchedule Make(MaintenanceScheduleKind kind, int interval = 1, DayOfWeek? weekday = null, int? dayOfMonth = null, int? week = null, int offset = 0) =>
            new(kind, start, duration, utc, interval, weekday, dayOfMonth, week, offset, null);

        switch (Bits(19, 3))
        {
            case NonRecurring:
                return Make(MaintenanceScheduleKind.OneTime);
            case Interval:
                var days = Bits(3, 5);
                return days == 0 || Bits(8, 5) != 0 || Bits(13, 6) != 0 ? null : Make(MaintenanceScheduleKind.Daily, days);
            case Weekly:
                var weeklyDay = Bits(16, 3);
                var weeks = Bits(13, 3);
                return weeklyDay is < 1 or > 7 || weeks == 0 ? null : Make(MaintenanceScheduleKind.Weekly, weeks, (DayOfWeek)(weeklyDay - 1));
            case MonthlyByWeekday:
            case MonthlyByWeekdayOffset:
                var weekday = Bits(16, 3);
                var months = Bits(12, 4);
                var order = Bits(9, 3);
                var offset = Bits(19, 3) == MonthlyByWeekdayOffset ? Bits(6, 3) : 0;
                if (weekday is < 1 or > 7 || months == 0 || order > 4) return null;
                var dayOfWeek = (DayOfWeek)(weekday - 1);
                var week = order == 0 ? 5 : order;
                if (dayOfWeek == DayOfWeek.Tuesday && week <= 4 && (Bits(19, 3) == MonthlyByWeekdayOffset || week == 2))
                    return Make(MaintenanceScheduleKind.PatchTuesday, months, offset: (week - 2) * 7 + offset);
                return Make(MaintenanceScheduleKind.MonthlyByWeekday, months, dayOfWeek, week: week, offset: offset);
            case MonthlyByDate:
                var monthDay = Bits(14, 5);
                var everyMonths = Bits(10, 4);
                return everyMonths == 0 ? null : Make(MaintenanceScheduleKind.MonthlyByDate, everyMonths, dayOfMonth: monthDay);
            default:
                return null;
        }
    }

    /// <summary>The week order (1 to 4) of a Tuesday and ConfigMgr's offset (0 to 7) for a Patch Tuesday offset of -7 to 21 days.</summary>
    public static (int WeekOrder, int OffsetDays) PatchTuesdayForm(int offset) => offset switch
    {
        < 0 => (1, offset + 7),
        <= 7 => (2, offset),
        <= 14 => (3, offset - 7),
        _ => (4, offset - 14)
    };

    /// <summary>The token New-CMSchedule writes for the schedule; the schedule must pass <see cref="ScheduleProjector.Validate"/> except for its end.</summary>
    public static string Encode(MaintenanceSchedule schedule) => Format(Build(schedule, offsetFlagForZero: false, durationInDays: false));

    /// <summary>Every token that decodes to a schedule with the same occurrences (see <see cref="SameSchedule"/>).</summary>
    public static IReadOnlyList<string> Equivalents(MaintenanceSchedule schedule)
    {
        var tokens = new List<string> { Encode(schedule) };
        var reference = Decode(tokens[0]) ?? schedule;
        foreach (var days in new[] { false, true })
        {
            if (days && schedule.Duration != TimeSpan.FromDays(1)) continue;
            if (schedule.Kind is MaintenanceScheduleKind.PatchTuesday or MaintenanceScheduleKind.MonthlyByWeekday)
            {
                var weekday = schedule.Kind == MaintenanceScheduleKind.PatchTuesday ? DayOfWeek.Tuesday : schedule.DayOfWeek!.Value;
                for (var week = 1; week <= 5; week++)
                    for (var offset = 0; offset <= 7; offset++)
                        foreach (var offsetFlag in new[] { false, true })
                        {
                            if (!offsetFlag && offset > 0) continue;
                            var form = schedule with
                            {
                                Kind = MaintenanceScheduleKind.MonthlyByWeekday, DayOfWeek = weekday, WeekOrder = week, PatchTuesdayOffsetDays = offset
                            };
                            var token = Format(Build(form, offsetFlag, days));
                            if (Decode(token) is { } decoded && SameSchedule(decoded, reference) && !tokens.Contains(token)) tokens.Add(token);
                        }
                continue;
            }
            var plain = Format(Build(schedule, offsetFlagForZero: false, days));
            if (!tokens.Contains(plain)) tokens.Add(plain);
        }
        return tokens;
    }

    /// <summary>
    /// True when the two schedules open at the same times: a Tuesday in week 1 to 4 of the
    /// month plus an offset is the Patch Tuesday schedule with the same distance from the
    /// second Tuesday.
    /// </summary>
    public static bool SameSchedule(MaintenanceSchedule a, MaintenanceSchedule b) => Canonical(a) == Canonical(b);

    private static MaintenanceSchedule Canonical(MaintenanceSchedule s) =>
        s is { Kind: MaintenanceScheduleKind.MonthlyByWeekday, DayOfWeek: System.DayOfWeek.Tuesday, WeekOrder: >= 1 and <= 4 }
            ? s with { Kind = MaintenanceScheduleKind.PatchTuesday, DayOfWeek = null, WeekOrder = null, PatchTuesdayOffsetDays = (s.WeekOrder.Value - 2) * 7 + s.PatchTuesdayOffsetDays }
            : s;

    private static string Format(ulong value) => value.ToString("X16", CultureInfo.InvariantCulture);

    private static ulong Build(MaintenanceSchedule s, bool offsetFlagForZero, bool durationInDays)
    {
        var start = s.Start;
        var minutes = (int)Math.Round(s.Duration.TotalMinutes);
        ulong v = 0;
        void Put(int shift, int width, int value) => v |= ((ulong)value & ((1UL << width) - 1)) << shift;

        Put(58, 6, start.Minute);
        Put(53, 5, start.Hour);
        Put(48, 5, start.Day);
        Put(44, 4, start.Month);
        Put(38, 6, start.Year - FirstYear);
        if (durationInDays && minutes == 1440)
            Put(22, 5, 1);
        else
        {
            Put(32, 6, minutes % 60);
            Put(27, 5, minutes / 60);
        }
        if (s.IsUtc) v |= 1;

        switch (s.Kind)
        {
            case MaintenanceScheduleKind.OneTime:
                Put(19, 3, NonRecurring);
                break;
            case MaintenanceScheduleKind.Daily:
                Put(19, 3, Interval);
                Put(3, 5, s.Interval);
                break;
            case MaintenanceScheduleKind.Weekly:
                Put(19, 3, Weekly);
                Put(16, 3, (int)s.DayOfWeek!.Value + 1);
                Put(13, 3, s.Interval);
                break;
            case MaintenanceScheduleKind.MonthlyByDate:
                Put(19, 3, MonthlyByDate);
                Put(14, 5, s.DayOfMonth!.Value);
                Put(10, 4, s.Interval);
                break;
            case MaintenanceScheduleKind.MonthlyByWeekday:
                PutWeekday((int)s.DayOfWeek!.Value + 1, s.WeekOrder!.Value, s.PatchTuesdayOffsetDays);
                break;
            case MaintenanceScheduleKind.PatchTuesday:
                var (week, offset) = PatchTuesdayForm(s.PatchTuesdayOffsetDays);
                PutWeekday((int)DayOfWeek.Tuesday + 1, week, offset);
                break;
        }
        return v;

        void PutWeekday(int weekday, int week, int offset)
        {
            var useOffset = offset > 0 || offsetFlagForZero;
            Put(19, 3, useOffset ? MonthlyByWeekdayOffset : MonthlyByWeekday);
            Put(16, 3, weekday);
            Put(12, 4, s.Interval);
            Put(9, 3, week == 5 ? 0 : week);
            if (useOffset) Put(6, 3, offset);
        }
    }
}
