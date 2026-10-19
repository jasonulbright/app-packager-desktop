using ApplicationPackagerEngine.Services;
using K = ApplicationPackagerEngine.Services.MaintenanceScheduleKind;

namespace ApplicationPackagerEngine.Tests.MaintenanceWindows;

/// <summary>The tokens are values a ConfigMgr site stored for windows made with New-CMSchedule.</summary>
public sealed class ScheduleTokenTests
{
    private static readonly DateTime LabStart = new(2026, 11, 3, 22, 30, 0);

    private static MaintenanceSchedule S(K kind, DateTime start, int minutes, int interval = 1, DayOfWeek? day = null,
        int? dayOfMonth = null, int? week = null, int offset = 0, bool utc = false) =>
        new(kind, start, TimeSpan.FromMinutes(minutes), utc, interval, day, dayOfMonth, week, offset, null);

    public static TheoryData<string, MaintenanceSchedule> LabTokens => new()
    {
        { "7AC3BE0018080000", S(K.OneTime, LabStart, 180) },
        { "7AC3BE0018100010", S(K.Daily, LabStart, 180, interval: 2) },
        { "7AC3BE00181F6000", S(K.Weekly, LabStart, 180, interval: 3, day: DayOfWeek.Saturday) },
        { "7AC3BE00182FC400", S(K.MonthlyByDate, LabStart, 180, dayOfMonth: 31) },
        { "7AC3BE0018280800", S(K.MonthlyByDate, LabStart, 180, interval: 2, dayOfMonth: 0) },
        { "7AC3BE0018261000", S(K.MonthlyByWeekday, LabStart, 180, day: DayOfWeek.Friday, week: 5) },
        { "7AC3BE00183314C0", S(K.PatchTuesday, LabStart, 180, offset: 3) },
        { "7AC3BE00183315C0", S(K.PatchTuesday, LabStart, 180, offset: 7) },
        { "7AC3BE1E10100008", S(K.Daily, LabStart, 150) },
        { "7AC3BE00C0100008", S(K.Daily, LabStart, 1440) },
        { "7AC3BE0018192001", S(K.Weekly, LabStart, 180, day: DayOfWeek.Sunday, utc: true) },
        { "7AC3BE00081A2000", S(K.Weekly, LabStart, 60, day: DayOfWeek.Monday) },
        { "008D8E0030100008", S(K.Daily, new DateTime(2026, 8, 13, 4, 0, 0), 360) },
        { "00CF8E00201F2000", S(K.Weekly, new DateTime(2026, 8, 15, 6, 0, 0), 240, day: DayOfWeek.Saturday) },
        { "02FFCFC018080000", S(K.OneTime, new DateTime(2033, 12, 31, 23, 0, 0), 180) },
        { "7AC3BE0018231400", S(K.PatchTuesday, LabStart, 180) },
    };

    [Theory]
    [MemberData(nameof(LabTokens))]
    public void ALabTokenDecodesToItsScheduleAndEncodesBackToTheSameToken(string token, MaintenanceSchedule expected)
    {
        Assert.Equal(expected, ScheduleToken.Decode(token));
        Assert.Equal(token, ScheduleToken.Encode(expected));
    }

    [Fact]
    public void ADurationOfOneDayDecodesAsTwentyFourHours()
    {
        Assert.Equal(S(K.Daily, LabStart, 1440), ScheduleToken.Decode("7AC3BE0000500008"));
    }

    [Fact]
    public void AnOffsetOfZeroDaysWithTheOffsetFlagDecodesAsPatchTuesdayAndIsAnEquivalentToken()
    {
        var schedule = ScheduleToken.Decode("7AC3BE0018331400");
        Assert.Equal(S(K.PatchTuesday, LabStart, 180), schedule);
        Assert.Contains("7AC3BE0018331400", ScheduleToken.Equivalents(schedule!));
        Assert.Contains("7AC3BE0018231400", ScheduleToken.Equivalents(schedule!));
    }

    [Theory]
    [InlineData("7AC3BE0008100800")]
    [InlineData("7AC3BE0018100000")]
    [InlineData("")]
    [InlineData("7AC3BE00181000")]
    [InlineData("ZZC3BE0018100010")]
    [InlineData("7AC3BE00181000107AC3BE0018100010")]
    [InlineData("7AC3BE0018380000")]
    public void ATokenThePageCannotShowDecodesToNull(string token)
    {
        Assert.Null(ScheduleToken.Decode(token));
    }

    [Theory]
    [InlineData(-7, 1, 0)]
    [InlineData(-3, 1, 4)]
    [InlineData(-1, 1, 6)]
    [InlineData(0, 2, 0)]
    [InlineData(1, 2, 1)]
    [InlineData(7, 2, 7)]
    [InlineData(8, 3, 1)]
    [InlineData(14, 3, 7)]
    [InlineData(15, 4, 1)]
    [InlineData(21, 4, 7)]
    public void APatchTuesdayOffsetMapsToATuesdayWeekAndConfigMgrOffset(int offset, int week, int configMgrOffset)
    {
        var (weekOrder, days) = ScheduleToken.PatchTuesdayForm(offset);
        Assert.Equal((week, configMgrOffset), (weekOrder, days));
        var schedule = S(K.PatchTuesday, LabStart, 60, offset: offset);
        var decoded = ScheduleToken.Decode(ScheduleToken.Encode(schedule));
        if (offset == -7)
            Assert.Equal(S(K.MonthlyByWeekday, LabStart, 60, day: DayOfWeek.Tuesday, week: 1), decoded);
        else
            Assert.Equal(schedule, decoded);
    }

    [Fact]
    public void AMonthlyByWeekdayOffsetIsKeptForAnyWeekday()
    {
        var schedule = S(K.MonthlyByWeekday, LabStart, 60, day: DayOfWeek.Monday, week: 5, offset: 2);
        Assert.Equal(schedule, ScheduleToken.Decode(ScheduleToken.Encode(schedule)));
    }

    [Fact]
    public void TheSecondsOfTheStartAreDropped()
    {
        var schedule = S(K.OneTime, LabStart.AddSeconds(42), 180);
        Assert.Equal("7AC3BE0018080000", ScheduleToken.Encode(schedule));
    }
}
