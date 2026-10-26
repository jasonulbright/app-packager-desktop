using System.Globalization;
using ApplicationPackagerEngine.Services;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

public sealed class ScheduleProjectorTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time");

    private static DateTime T(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static MaintenanceSchedule S(K kind, string start, int minutes, int interval = 1, DayOfWeek? day = null,
        int? dayOfMonth = null, int? week = null, int offset = 0, bool utc = false, string? end = null) =>
        new(kind, T(start), TimeSpan.FromMinutes(minutes), utc, interval, day, dayOfMonth, week, offset, end is null ? null : T(end));

    public sealed record Case(string Name, MaintenanceSchedule Schedule, string From, int Count, string[] Expected, string Zone = "Eastern")
    {
        public override string ToString() => Name;
    }

    private static Case C(string name, MaintenanceSchedule schedule, string from, int count, params string[] expected) =>
        new(name, schedule, from, count, expected);

    public static TheoryData<Case> Cases()
    {
        var sat = DayOfWeek.Saturday;
        var data = new TheoryData<Case>
        {
            C("one-time ahead", S(K.OneTime, "2026-11-03 22:30", 180), "2026-10-19 08:00", 5, "2026-11-03 22:30"),
            C("one-time open across midnight", S(K.OneTime, "2026-11-03 22:30", 180), "2026-11-04 00:00", 5, "2026-11-03 22:30"),
            C("one-time ended at its end", S(K.OneTime, "2026-11-03 22:30", 180), "2026-11-04 01:30", 5),
            C("one-time passed", S(K.OneTime, "2026-01-01 00:00", 60), "2026-10-19 00:00", 1),

            C("daily", S(K.Daily, "2026-08-13 04:00", 360), "2026-10-19 01:37", 5,
                "2026-10-19 04:00", "2026-10-20 04:00", "2026-10-21 04:00", "2026-10-22 04:00", "2026-10-23 04:00"),
            C("daily open now", S(K.Daily, "2026-08-13 04:00", 360), "2026-10-19 05:00", 2, "2026-10-19 04:00", "2026-10-20 04:00"),
            C("daily every 2 days from a later effective date", S(K.Daily, "2026-11-03 22:30", 180, 2), "2026-10-19 00:00", 3,
                "2026-11-03 22:30", "2026-11-05 22:30", "2026-11-07 22:30"),
            C("daily every 3 days", S(K.Daily, "2026-01-01 23:00", 60, 3), "2026-01-10 00:00", 2, "2026-01-10 23:00", "2026-01-13 23:00"),
            C("daily across midnight", S(K.Daily, "2026-03-01 23:00", 180), "2026-03-05 01:00", 2, "2026-03-04 23:00", "2026-03-05 23:00"),
            C("daily in the spring gap", S(K.Daily, "2026-03-01 02:30", 60), "2026-03-07 12:00", 3,
                "2026-03-08 03:30", "2026-03-09 02:30", "2026-03-10 02:30"),
            C("daily in the repeated autumn hour", S(K.Daily, "2026-10-25 01:30", 60), "2026-10-31 12:00", 2, "2026-11-01 01:30", "2026-11-02 01:30"),
            C("daily with an end", S(K.Daily, "2026-01-01 08:00", 60, end: "2026-01-03 08:00"), "2025-12-01 00:00", 5,
                "2026-01-01 08:00", "2026-01-02 08:00", "2026-01-03 08:00"),
            C("daily every 31 days from its first minute", S(K.Daily, "2026-01-01 00:00", 60, 31), "2026-01-01 00:00", 3,
                "2026-01-01 00:00", "2026-02-01 00:00", "2026-03-04 00:00"),
            C("daily every 7 days from years back", S(K.Daily, "2020-01-01 04:00", 60, 7), "2026-10-19 00:00", 2, "2026-10-21 04:00", "2026-10-28 04:00"),

            C("weekly", S(K.Weekly, "2026-08-15 06:00", 240, day: sat), "2026-10-19 01:37", 3, "2026-10-24 06:00", "2026-10-31 06:00", "2026-11-07 06:00"),
            C("weekly every 3 weeks from a Tuesday", S(K.Weekly, "2026-11-03 22:30", 180, 3, sat), "2026-10-19 00:00", 3,
                "2026-11-07 22:30", "2026-11-28 22:30", "2026-12-19 22:30"),
            C("weekly on the start day in the repeated hour", S(K.Weekly, "2026-11-01 02:00", 240, day: DayOfWeek.Sunday), "2026-10-01 00:00", 2,
                "2026-11-01 02:00", "2026-11-08 02:00"),
            C("weekly every 2 weeks open now", S(K.Weekly, "2026-10-05 20:00", 120, 2, DayOfWeek.Monday), "2026-10-19 21:00", 2,
                "2026-10-19 20:00", "2026-11-02 20:00"),
            C("weekly across midnight", S(K.Weekly, "2026-01-02 22:00", 360, day: DayOfWeek.Friday), "2026-01-10 03:00", 2,
                "2026-01-09 22:00", "2026-01-16 22:00"),
            C("weekly UTC in winter", S(K.Weekly, "2026-11-03 22:30", 180, day: DayOfWeek.Sunday, utc: true), "2026-10-19 00:00", 2,
                "2026-11-08 17:30", "2026-11-15 17:30"),
            C("weekly UTC keeps the summer offset in winter", S(K.Weekly, "2026-07-05 06:00", 60, day: DayOfWeek.Sunday, utc: true), "2026-12-01 00:00", 2,
                "2026-12-06 02:00", "2026-12-13 02:00"),
            C("weekly every 4 weeks", S(K.Weekly, "2026-02-25 03:00", 60, 4, DayOfWeek.Wednesday), "2026-04-01 00:00", 2,
                "2026-04-22 03:00", "2026-05-20 03:00"),
            C("weekly with an end", S(K.Weekly, "2026-09-01 01:00", 60, day: DayOfWeek.Tuesday, end: "2026-09-20 00:00"), "2026-09-01 00:00", 5,
                "2026-09-01 01:00", "2026-09-08 01:00", "2026-09-15 01:00"),

            C("day 31 skips the short months", S(K.MonthlyByDate, "2026-01-01 02:00", 180, dayOfMonth: 31), "2026-01-01 00:00", 5,
                "2026-01-31 02:00", "2026-03-31 02:00", "2026-05-31 02:00", "2026-07-31 02:00", "2026-08-31 02:00"),
            C("day 31 from a November effective date skips November", S(K.MonthlyByDate, "2026-11-03 22:30", 180, dayOfMonth: 31), "2026-10-19 00:00", 3,
                "2026-12-31 22:30", "2027-01-31 22:30", "2027-03-31 22:30"),
            C("last day every 2 months", S(K.MonthlyByDate, "2026-11-03 22:30", 180, 2, dayOfMonth: 0), "2026-10-19 00:00", 3,
                "2026-11-30 22:30", "2027-01-31 22:30", "2027-03-31 22:30"),
            C("day 29 in a leap year", S(K.MonthlyByDate, "2027-12-01 01:00", 60, dayOfMonth: 29), "2028-01-15 00:00", 3,
                "2028-01-29 01:00", "2028-02-29 01:00", "2028-03-29 01:00"),
            C("day 29 skips a common February", S(K.MonthlyByDate, "2026-12-01 01:00", 60, dayOfMonth: 29), "2027-01-15 00:00", 3,
                "2027-01-29 01:00", "2027-03-29 01:00", "2027-04-29 01:00"),
            C("day 30 every 3 months", S(K.MonthlyByDate, "2026-01-15 05:00", 60, 3, dayOfMonth: 30), "2026-01-01 00:00", 4,
                "2026-01-30 05:00", "2026-04-30 05:00", "2026-07-30 05:00", "2026-10-30 05:00"),
            C("day before the effective date is skipped", S(K.MonthlyByDate, "2026-01-20 05:00", 60, dayOfMonth: 15), "2026-01-01 00:00", 2,
                "2026-02-15 05:00", "2026-03-15 05:00"),
            C("day 1 every 12 months", S(K.MonthlyByDate, "2026-01-01 00:00", 60, 12, dayOfMonth: 1), "2026-06-01 00:00", 2,
                "2027-01-01 00:00", "2028-01-01 00:00"),
            C("day 31 across midnight", S(K.MonthlyByDate, "2026-01-01 23:00", 240, dayOfMonth: 31), "2026-02-01 02:00", 2,
                "2026-01-31 23:00", "2026-03-31 23:00"),
            C("day 8 in the spring gap", S(K.MonthlyByDate, "2026-01-08 02:30", 60, dayOfMonth: 8), "2026-03-01 00:00", 2,
                "2026-03-08 03:30", "2026-04-08 02:30"),
            C("day 2 every 2 months counts from the effective month", S(K.MonthlyByDate, "2026-11-03 22:30", 180, 2, dayOfMonth: 2), "2026-10-19 00:00", 2,
                "2027-01-02 22:30", "2027-03-02 22:30"),
            C("last day in a leap February", S(K.MonthlyByDate, "2028-01-01 00:00", 60, dayOfMonth: 0), "2028-02-01 00:00", 2,
                "2028-02-29 00:00", "2028-03-31 00:00"),

            C("first Saturday", S(K.MonthlyByWeekday, "2026-01-01 01:00", 360, day: sat, week: 1), "2026-01-01 00:00", 4,
                "2026-01-03 01:00", "2026-02-07 01:00", "2026-03-07 01:00", "2026-04-04 01:00"),
            C("last Friday", S(K.MonthlyByWeekday, "2026-11-03 22:30", 180, day: DayOfWeek.Friday, week: 5), "2026-10-19 00:00", 3,
                "2026-11-27 22:30", "2026-12-25 22:30", "2027-01-29 22:30"),
            C("fourth Thursday", S(K.MonthlyByWeekday, "2026-10-01 00:00", 60, day: DayOfWeek.Thursday, week: 4), "2026-10-01 00:00", 3,
                "2026-10-22 00:00", "2026-11-26 00:00", "2026-12-24 00:00"),
            C("third Wednesday every 2 months", S(K.MonthlyByWeekday, "2026-01-01 03:00", 60, 2, DayOfWeek.Wednesday, week: 3), "2026-01-01 00:00", 3,
                "2026-01-21 03:00", "2026-03-18 03:00", "2026-05-20 03:00"),
            C("first Sunday in the repeated hour", S(K.MonthlyByWeekday, "2026-09-01 01:30", 60, day: DayOfWeek.Sunday, week: 1), "2026-10-15 00:00", 2,
                "2026-11-01 01:30", "2026-12-06 01:30"),
            C("second Sunday in the spring gap", S(K.MonthlyByWeekday, "2026-01-01 02:15", 60, day: DayOfWeek.Sunday, week: 2), "2026-03-01 00:00", 2,
                "2026-03-08 03:15", "2026-04-12 02:15"),
            C("last Monday plus 2 days", S(K.MonthlyByWeekday, "2026-01-01 01:00", 60, day: DayOfWeek.Monday, week: 5, offset: 2), "2026-01-01 00:00", 3,
                "2026-01-28 01:00", "2026-02-25 01:00", "2026-04-01 01:00"),
            C("first Monday plus 7 days", S(K.MonthlyByWeekday, "2026-01-01 01:00", 60, day: DayOfWeek.Monday, week: 1, offset: 7), "2026-01-01 00:00", 2,
                "2026-01-12 01:00", "2026-02-09 01:00"),
            C("last Tuesday", S(K.MonthlyByWeekday, "2026-12-01 00:00", 60, day: DayOfWeek.Tuesday, week: 5), "2026-12-01 00:00", 2,
                "2026-12-29 00:00", "2027-01-26 00:00"),
            C("fourth Saturday across midnight", S(K.MonthlyByWeekday, "2026-01-01 23:00", 300, day: sat, week: 4), "2026-01-25 02:00", 2,
                "2026-01-24 23:00", "2026-02-28 23:00"),
        };

        var patchTuesday = new (int Offset, string[] Dates)[]
        {
            (0, ["2026-01-13", "2026-02-10", "2026-03-10"]),
            (-3, ["2026-01-10", "2026-02-07", "2026-03-07"]),
            (-1, ["2026-01-12", "2026-02-09", "2026-03-09"]),
            (1, ["2026-01-14", "2026-02-11", "2026-03-11"]),
            (2, ["2026-01-15", "2026-02-12", "2026-03-12"]),
            (3, ["2026-01-16", "2026-02-13", "2026-03-13"]),
            (5, ["2026-01-18", "2026-02-15", "2026-03-15"]),
            (7, ["2026-01-20", "2026-02-17", "2026-03-17"]),
            (10, ["2026-01-23", "2026-02-20", "2026-03-20"]),
            (14, ["2026-01-27", "2026-02-24", "2026-03-24"]),
            (21, ["2026-02-03", "2026-03-03", "2026-03-31"]),
            (25, ["2026-02-07", "2026-03-07", "2026-04-04"]),
            (30, ["2026-02-12", "2026-03-12", "2026-04-09"]),
        };
        foreach (var (offset, dates) in patchTuesday)
            data.Add(C($"Patch Tuesday {offset:+0;-0;0}", S(K.PatchTuesday, "2026-01-01 02:00", 240, offset: offset), "2026-01-01 00:00", 3,
                dates.Select(d => d + " 02:00").ToArray()));

        data.Add(C("Patch Tuesday -2 in the spring gap", S(K.PatchTuesday, "2026-01-01 02:00", 240, offset: -2), "2026-01-01 00:00", 3,
            "2026-01-11 02:00", "2026-02-08 02:00", "2026-03-08 03:00"));
        data.Add(C("Patch Tuesday +3 every 2 months", S(K.PatchTuesday, "2026-01-01 02:00", 240, 2, offset: 3), "2026-01-01 00:00", 3,
            "2026-01-16 02:00", "2026-03-13 02:00", "2026-05-15 02:00"));
        data.Add(C("Patch Tuesday +3 effective mid-month", S(K.PatchTuesday, "2026-01-20 02:00", 240, offset: 3), "2026-01-01 00:00", 3,
            "2026-02-13 02:00", "2026-03-13 02:00", "2026-04-17 02:00"));
        data.Add(C("Patch Tuesday +30 lands in the next month", S(K.PatchTuesday, "2026-01-01 02:00", 240, offset: 30), "2026-02-11 00:00", 2,
            "2026-02-12 02:00", "2026-03-12 02:00"));
        data.Add(C("Patch Tuesday +3 UTC", S(K.PatchTuesday, "2026-01-01 07:00", 240, offset: 3, utc: true), "2026-06-01 00:00", 2,
            "2026-06-12 02:00", "2026-07-17 02:00"));
        data.Add(C("Patch Tuesday +7 across midnight", S(K.PatchTuesday, "2026-01-01 22:00", 240, offset: 7), "2026-01-21 01:00", 2,
            "2026-01-20 22:00", "2026-02-17 22:00"));
        data.Add(C("Patch Tuesday -3 in 2027", S(K.PatchTuesday, "2027-01-01 02:00", 240, offset: -3), "2027-01-01 00:00", 3,
            "2027-01-09 02:00", "2027-02-06 02:00", "2027-03-06 02:00"));
        data.Add(C("Patch Tuesday with an end", S(K.PatchTuesday, "2026-01-01 02:00", 240, end: "2026-02-28 00:00"), "2026-01-01 00:00", 5,
            "2026-01-13 02:00", "2026-02-10 02:00"));
        data.Add(C("Patch Tuesday +4 after this month's passed", S(K.PatchTuesday, "2026-10-01 02:00", 240, offset: 4), "2026-10-19 00:00", 2,
            "2026-11-14 02:00", "2026-12-12 02:00"));

        data.Add(C("daily UTC from winter into summer", S(K.Daily, "2026-01-01 07:00", 60, utc: true), "2026-07-01 00:00", 2,
            "2026-07-01 02:00", "2026-07-02 02:00"));
        data.Add(C("daily UTC from summer into winter", S(K.Daily, "2026-07-01 06:00", 60, utc: true), "2026-12-01 00:00", 2,
            "2026-12-01 02:00", "2026-12-02 02:00"));
        data.Add(C("one-time UTC", S(K.OneTime, "2026-11-03 22:30", 180, utc: true), "2026-10-19 00:00", 1, "2026-11-03 17:30"));

        data.Add(new Case("no daylight saving in the UTC zone", S(K.Daily, "2026-03-08 02:30", 60), "2026-03-08 00:00", 2,
            ["2026-03-08 02:30", "2026-03-09 02:30"], "UTC"));
        data.Add(new Case("one-time UTC in Tokyo", S(K.OneTime, "2026-11-03 22:30", 180, utc: true), "2026-11-01 00:00", 1,
            ["2026-11-04 07:30"], "Tokyo"));
        data.Add(new Case("daily UTC keeps the southern summer offset", S(K.Daily, "2026-01-01 00:00", 60, utc: true), "2026-07-01 00:00", 2,
            ["2026-07-01 11:00", "2026-07-02 11:00"], "Sydney"));
        data.Add(new Case("local window in the southern spring gap", S(K.Daily, "2026-09-01 02:30", 60), "2026-10-03 12:00", 2,
            ["2026-10-04 03:30", "2026-10-05 02:30"], "Sydney"));
        return data;
    }

    private static TimeZoneInfo Zone(string name) => name switch
    {
        "UTC" => TimeZoneInfo.Utc,
        "Tokyo" => Tokyo,
        "Sydney" => Sydney,
        _ => Eastern
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheProjectorReturnsTheExpectedOccurrences(Case c)
    {
        var projector = new ScheduleProjector(Zone(c.Zone));
        var actual = projector.NextOccurrences(c.Schedule, T(c.From), c.Count)
            .Select(d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(c.Expected, actual);
    }

    [Fact]
    public void TheTableHasAtLeastSixtyCases()
    {
        Assert.True(Cases().Count >= 60);
    }

    [Fact]
    public void AZeroCountOrAnIncompleteScheduleProjectsNothing()
    {
        var projector = new ScheduleProjector(Eastern);
        Assert.Empty(projector.NextOccurrences(S(K.Daily, "2026-01-01 00:00", 60), T("2026-01-01 00:00"), 0));
        Assert.Empty(projector.NextOccurrences(S(K.Weekly, "2026-01-01 00:00", 60), T("2026-01-01 00:00"), 3));
        Assert.Empty(projector.NextOccurrences(S(K.Daily, "2026-01-01 00:00", 60, interval: 0), T("2026-01-01 00:00"), 3));
        Assert.Empty(projector.NextOccurrences(S(K.Daily, "2026-01-01 00:00", 0), T("2026-01-01 00:00"), 3));
    }

    [Fact]
    public void AUtcFromIsConvertedToTheProjectorZone()
    {
        var projector = new ScheduleProjector(Eastern);
        var from = DateTime.SpecifyKind(T("2026-10-19 05:37"), DateTimeKind.Utc);
        Assert.Equal(T("2026-10-19 04:00"), projector.NextOccurrences(S(K.Daily, "2026-08-13 04:00", 360), from, 1).Single());
    }

    public static TheoryData<MaintenanceSchedule, string> LabTexts => new()
    {
        { S(K.OneTime, "2026-11-03 22:30", 180), "Occurs on 11/3/2026 10:30 PM" },
        { S(K.Daily, "2026-11-03 22:30", 180, 2), "Occurs every 2 days effective 11/3/2026 10:30 PM" },
        { S(K.Daily, "2026-08-13 04:00", 360), "Occurs every 1 days effective 8/13/2026 4:00 AM" },
        { S(K.Weekly, "2026-11-03 22:30", 180, 3, DayOfWeek.Saturday), "Occurs every 3 weeks on Saturday effective 11/3/2026 10:30 PM" },
        { S(K.Weekly, "2026-11-03 22:30", 60, day: DayOfWeek.Monday), "Occurs every 1 weeks on Monday effective 11/3/2026 10:30 PM" },
        { S(K.Weekly, "2026-11-03 22:30", 180, day: DayOfWeek.Sunday, utc: true), "Occurs every 1 weeks on Sunday effective 11/3/2026 10:30 PM" },
        { S(K.MonthlyByDate, "2026-11-03 22:30", 180, dayOfMonth: 31), "Occurs day 31 of every 1 months effective 11/3/2026 10:30 PM" },
        { S(K.MonthlyByDate, "2026-11-03 22:30", 180, 2, dayOfMonth: 0), "Occurs the last day of every 2 months effective 11/3/2026 10:30 PM" },
        { S(K.MonthlyByWeekday, "2026-11-03 22:30", 180, day: DayOfWeek.Friday, week: 5), "Occurs the Last Friday of every 1 months effective 11/3/2026 10:30 PM" },
        { S(K.PatchTuesday, "2026-11-03 22:30", 180, offset: 3), "Occurs 3 day(s) after the Second Tuesday of every 1 months effective 11/3/2026 10:30 PM" },
        { S(K.PatchTuesday, "2026-11-03 22:30", 180, offset: 7), "Occurs 7 day(s) after the Second Tuesday of every 1 months effective 11/3/2026 10:30 PM" },
        { S(K.PatchTuesday, "2026-11-03 22:30", 180), "Occurs the Second Tuesday of every 1 months effective 11/3/2026 10:30 PM" },
        { S(K.PatchTuesday, "2026-11-03 22:30", 180, offset: -3), "Occurs 4 day(s) after the First Tuesday of every 1 months effective 11/3/2026 10:30 PM" },
    };

    [Theory]
    [MemberData(nameof(LabTexts))]
    public void DescribeUsesTheWordingConfigMgrStoresInTheWindowDescription(MaintenanceSchedule schedule, string expected)
    {
        Assert.Equal(expected, new ScheduleProjector(Eastern).Describe(schedule));
    }

    public static TheoryData<MaintenanceSchedule, string?> Validations => new()
    {
        { S(K.Daily, "2026-01-01 00:00", 1440), null },
        { S(K.Daily, "2026-01-01 00:00", 1), null },
        { S(K.Daily, "2026-01-01 00:00", 0), "at least 1 minute" },
        { S(K.Daily, "2026-01-01 00:00", 1441), "24 hours or less" },
        { new MaintenanceSchedule(K.Daily, T("2026-01-01 00:00"), TimeSpan.FromSeconds(90), false, 1, null, null, null, 0, null), "whole number of minutes" },
        { S(K.OneTime, "2034-01-01 00:00", 60), "years 1970 to 2033" },
        { S(K.OneTime, "2033-12-31 23:00", 60), null },
        { S(K.Daily, "2026-01-01 00:00", 60, end: "2026-02-01 00:00"), "no end date" },
        { S(K.Daily, "2026-01-01 00:00", 60, 32), "1 to 31 days" },
        { S(K.Daily, "2026-01-01 00:00", 60, 31), null },
        { S(K.Weekly, "2026-01-01 00:00", 60, 5, DayOfWeek.Monday), "1 to 4 weeks" },
        { S(K.Weekly, "2026-01-01 00:00", 60), "day of the week" },
        { S(K.MonthlyByDate, "2026-01-01 00:00", 60, 13, dayOfMonth: 1), "1 to 12 months" },
        { S(K.MonthlyByDate, "2026-01-01 00:00", 60, dayOfMonth: 32), "day of the month" },
        { S(K.MonthlyByDate, "2026-01-01 00:00", 60, dayOfMonth: 0), null },
        { S(K.MonthlyByWeekday, "2026-01-01 00:00", 60, day: DayOfWeek.Monday, week: 6), "first, second" },
        { S(K.MonthlyByWeekday, "2026-01-01 00:00", 60, day: DayOfWeek.Monday, week: 5, offset: 8), "0 to 7 days" },
        { S(K.PatchTuesday, "2026-01-01 00:00", 60, offset: 22), "21 days after" },
        { S(K.PatchTuesday, "2026-01-01 00:00", 60, offset: -8), "7 days before" },
        { S(K.PatchTuesday, "2026-01-01 00:00", 60, offset: -7), null },
        { S(K.PatchTuesday, "2026-01-01 00:00", 60, offset: 21), null },
    };

    [Theory]
    [MemberData(nameof(Validations))]
    public void ValidateNamesWhatConfigMgrCannotStore(MaintenanceSchedule schedule, string? part)
    {
        var reason = new ScheduleProjector(Eastern).Validate(schedule);
        if (part is null) Assert.Null(reason);
        else Assert.Contains(part, reason);
    }

    [Fact]
    public void EffectiveOnKeepsTheStartTime()
    {
        var schedule = S(K.Weekly, "2000-01-01 02:15", 60, day: DayOfWeek.Sunday).EffectiveOn(new DateTime(2026, 10, 19, 13, 0, 0, DateTimeKind.Local));
        Assert.Equal(T("2026-10-19 02:15"), schedule.Start);
        Assert.Equal(DateTimeKind.Unspecified, schedule.Start.Kind);
    }
}
