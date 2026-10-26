using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments;

public sealed class DeploymentScheduleRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0);

    internal static DeploymentOptions Options(string purpose = "Available", DateTime? available = null, DateTime? deadline = null, string notification = "DisplayAll", bool utc = false) =>
        new(purpose, available ?? Now.AddHours(1), deadline, utc, notification, false, false, false,
            "DownloadContentFromDistributionPointAndRunLocally", "NeverRerunDeployedProgram", true, false, true, true, false);

    [Theory]
    [InlineData("Required", 2, 1, "later than the available time")]
    [InlineData("Required", 2, 2, "later than the available time")]
    [InlineData("Required", -3, -2, "in the past")]
    public void ADeadlineThatIsNotLaterThanAvailableOrIsPastBlocks(string purpose, int availableHours, int deadlineHours, string expected)
    {
        var verdict = DeploymentScheduleRules.Check(DeploymentObjectType.Application, Options(purpose, Now.AddHours(availableHours), Now.AddHours(deadlineHours)), Now);

        Assert.True(verdict.Blocked);
        Assert.Contains(expected, verdict.Error);
    }

    [Fact]
    public void ARequiredDeploymentWithoutADeadlineBlocks()
    {
        var verdict = DeploymentScheduleRules.Check(DeploymentObjectType.Package, Options("Required"), Now);
        Assert.True(verdict.Blocked);
        Assert.Equal("A Required deployment needs a deadline.", verdict.Error);
    }

    [Theory]
    [InlineData(DeploymentObjectType.Application, true)]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, true)]
    [InlineData(DeploymentObjectType.Package, false)]
    [InlineData(DeploymentObjectType.TaskSequence, false)]
    public void AvailableWithHideAllBlocksForTheTypesWithANotification(DeploymentObjectType type, bool blocked)
    {
        var verdict = DeploymentScheduleRules.Check(type, Options(notification: "HideAll"), Now);
        Assert.Equal(blocked, verdict.Blocked);
        if (blocked) Assert.Contains("Hide all", verdict.Error);
    }

    [Fact]
    public void RequiredWithHideAllIsAllowed()
    {
        var verdict = DeploymentScheduleRules.Check(DeploymentObjectType.Application, Options("Required", Now.AddHours(1), Now.AddHours(5), "HideAll"), Now);
        Assert.False(verdict.Blocked);
    }

    [Theory]
    [InlineData(-6, true)]
    [InlineData(-5, false)]
    [InlineData(-4, false)]
    [InlineData(0, false)]
    public void AnAvailableTimeMoreThanFiveMinutesBackIsBackdated(int minutes, bool backdated)
    {
        var verdict = DeploymentScheduleRules.Check(DeploymentObjectType.Package, Options(available: Now.AddMinutes(minutes)), Now);
        Assert.False(verdict.Blocked);
        Assert.Equal(backdated, verdict.Backdated);
        if (backdated) Assert.Contains(verdict.Warnings, w => w.Contains("is in the past"));
    }

    [Fact]
    public void AnAvailableTimeMoreThanFiveYearsAheadWarnsWithoutBlocking()
    {
        var verdict = DeploymentScheduleRules.Check(DeploymentObjectType.Package, Options(available: Now.AddYears(5).AddDays(1)), Now);
        Assert.False(verdict.Blocked);
        Assert.Contains(verdict.Warnings, w => w.Contains("five years"));
    }

    [Fact]
    public void TheUtcBasisComparesWithTheUtcClock()
    {
        var utcNow = new DateTime(2026, 10, 20, 16, 0, 0, DateTimeKind.Utc);
        var eastern = TimeZoneInfo.CreateCustomTimeZone("UTC-4", TimeSpan.FromHours(-4), "UTC-4", "UTC-4");

        Assert.Equal(new DateTime(2026, 10, 20, 16, 0, 0), DeploymentScheduleRules.NowIn(true, utcNow, eastern));
        Assert.Equal(new DateTime(2026, 10, 20, 12, 0, 0), DeploymentScheduleRules.NowIn(false, utcNow, eastern));
        Assert.Equal(DateTimeKind.Unspecified, DeploymentScheduleRules.NowIn(true, utcNow, eastern).Kind);

        // A UTC deadline 10 minutes past is still 3 h 50 min ahead on the local clock of a UTC-4 machine.
        var options = Options("Required", new DateTime(2026, 10, 20, 15, 0, 0), new DateTime(2026, 10, 20, 15, 50, 0), utc: true);
        Assert.True(DeploymentScheduleRules.Check(DeploymentObjectType.Application, options, DeploymentScheduleRules.NowIn(true, utcNow, eastern)).Blocked);
        Assert.False(DeploymentScheduleRules.Check(DeploymentObjectType.Application, options with { UtcBasis = false }, DeploymentScheduleRules.NowIn(false, utcNow, eastern)).Blocked);
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("")]
    public void AnUnknownPurposeBlocks(string purpose)
    {
        Assert.True(DeploymentScheduleRules.Check(DeploymentObjectType.Application, Options(purpose), Now).Blocked);
    }

    [Theory]
    [InlineData(DeploymentObjectType.Application, "ShowAll")]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup, "")]
    public void AnUnknownNotificationBlocks(DeploymentObjectType type, string notification)
    {
        Assert.NotNull(DeploymentScheduleRules.OptionProblem(type, Options(notification: notification)));
    }

    [Fact]
    public void TheOptionsOfEachTypeAreCheckedAgainstTheValuesConfigMgrAccepts()
    {
        var o = Options();
        Assert.Null(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.Package, o));
        Assert.NotNull(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.Package, o with { SlowNetworkBehavior = "RunProgramFromDistributionPoint" }));
        Assert.NotNull(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.Package, o with { NetworkBehavior = "Teleport" }));
        Assert.NotNull(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.Package, o with { RerunBehavior = "Sometimes" }));
        var ts = o with { NetworkBehavior = "DownloadContentLocallyWhenNeededByRunningTaskSequence" };
        Assert.Null(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.TaskSequence, ts));
        Assert.NotNull(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.TaskSequence, ts with { TaskSequenceAvailability = "Everyone" }));
        Assert.NotNull(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.TaskSequence,
            ts with { NetworkBehavior = "DownloadAllContentLocallyBeforeStartingTaskSequence", TaskSequenceAvailability = "ClientsMediaAndPxe" }));
        Assert.Null(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.TaskSequence,
            ts with { NetworkBehavior = "DownloadAllContentLocallyBeforeStartingTaskSequence", TaskSequenceAvailability = "Clients" }));
    }
}
