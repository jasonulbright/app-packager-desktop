using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

public sealed class RingPlanRulesTests
{
    internal static RingDefinition Ring(string name, string collection = "", string purpose = "Required", int available = 0, int? deadline = 1, string notification = "DisplayAll",
        int? threshold = null, string dpGroup = "", string? slow = null, string? fast = null, string? rerun = null, string availability = "Clients") =>
        new(name, collection, "", purpose, available, purpose == "Required" ? deadline : null, notification, false, false, false, dpGroup, threshold, fast, slow, rerun, TaskSequenceAvailability: availability);

    internal static RingPlan Plan(params RingDefinition[] rings) => new("Test-Rings", "", false, "LocalTime", rings, false);

    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>A plan, and the text one of its load errors holds; null when the plan loads.</summary>
    public static TheoryData<string, RingPlan, string?> PlanCases() => new()
    {
        { "seed shape with empty collections loads", Plan(Ring("QA", "", available: 0, deadline: 1), Ring("Pilot", "", available: 2, deadline: 5)), null },
        { "valid targets load", Plan(Ring("QA", "MCM00098"), Ring("Pilot", "MCM00099", available: 2, deadline: 5)), null },
        { "lower-case id loads", Plan(Ring("QA", "mcm00098")), null },
        { "built-in SMS00001", Plan(Ring("QA", "SMS00001")), "Ring 1 'QA' targets built-in collection SMS00001" },
        { "built-in SMSDM003", Plan(Ring("QA", "MCM00098"), Ring("Prod", "SMSDM003", available: 2, deadline: 5)), "Ring 2 'Prod' targets built-in collection SMSDM003" },
        { "built-in lower case", Plan(Ring("QA", "sms00004")), "built-in collection SMS00004" },
        { "short id", Plan(Ring("QA", "MCM1")), "is not an 8-character collection ID" },
        { "id with a dot", Plan(Ring("QA", "MCM0.098")), "is not an 8-character collection ID" },
        { "shared collection", Plan(Ring("QA", "MCM00098"), Ring("Pilot", "MCM00098", available: 2, deadline: 5)), "Rings must not share a collection" },
        { "shared collection case-insensitive", Plan(Ring("QA", "MCM00098"), Ring("Pilot", "mcm00098", available: 2, deadline: 5)), "Rings must not share a collection" },
        { "no rings", Plan(), "The plan has no rings." },
        { "no name", Plan(Ring("QA")) with { Name = " " }, "The plan has no name." },
        { "bad time basis", Plan(Ring("QA")) with { TimeBasedOn = "Local" }, "is not LocalTime or Utc" },
        { "utc plan loads", Plan(Ring("QA")) with { TimeBasedOn = "Utc" }, null },
        { "ring without name", Plan(Ring("")), "Ring 1 has no name." },
        { "bad purpose", Plan(Ring("QA", purpose: "Optional")), "is not Available or Required" },
        { "negative available offset", Plan(Ring("QA", available: -1, deadline: 2)), "available offset must be 0 to 3650 days" },
        { "huge available offset", Plan(Ring("QA", purpose: "Available", available: 4000)), "available offset must be 0 to 3650 days" },
        { "required without deadline", Plan(Ring("QA", deadline: null)), "is Required and needs a deadline offset" },
        { "deadline equals available", Plan(Ring("QA", available: 2, deadline: 2)), "deadline offset (2) must be later than its available offset (2)" },
        { "deadline before available", Plan(Ring("QA", available: 3, deadline: 1)), "must be later than its available offset" },
        { "deadline not after previous", Plan(Ring("QA", "MCM00001", available: 0, deadline: 5), Ring("Pilot", "MCM00002", available: 1, deadline: 5)), "must be later than the Ring 1 'QA' deadline offset (5)" },
        { "available ring skipped in ordering", Plan(Ring("QA", available: 0, deadline: 5), Ring("Wide", purpose: "Available", available: 1), Ring("Prod", available: 6, deadline: 7)), null },
        { "ordering against nearest required", Plan(Ring("QA", available: 0, deadline: 5), Ring("Wide", purpose: "Available", available: 1), Ring("Prod", available: 2, deadline: 4)), "must be later than the Ring 1 'QA' deadline offset (5)" },
        { "available with hide all", Plan(Ring("QA", purpose: "Available", notification: "HideAll")), "is Available with HideAll" },
        { "required with hide all loads", Plan(Ring("QA", notification: "HideAll")), null },
        { "bad notification", Plan(Ring("QA", notification: "Loud")), "user notification 'Loud' is not valid" },
        { "threshold above 100", Plan(Ring("QA", threshold: 101)), "success threshold must be empty or 0 to 100" },
        { "threshold below 0", Plan(Ring("QA", threshold: -1)), "success threshold must be empty or 0 to 100" },
        { "threshold 0 and 100 load", Plan(Ring("QA", threshold: 0), Ring("Prod", available: 1, deadline: 2, threshold: 100)), null },
        { "slow option refused by the site", Plan(Ring("QA", slow: "RunProgramFromDistributionPoint")), "is refused by ConfigMgr" },
        { "bad fast option", Plan(Ring("QA", fast: "Teleport")), "fast network option 'Teleport' is not valid" },
        { "bad rerun", Plan(Ring("QA", rerun: "Sometimes")), "rerun behavior 'Sometimes' is not valid" },
        { "bad task sequence availability", Plan(Ring("QA", availability: "Everywhere")), "task sequence availability 'Everywhere' is not valid" },
        { "wildcard dp group", Plan(Ring("QA", dpGroup: "All [DPs]")), "has a wildcard or control character" },
        { "too many rings", Plan(Enumerable.Range(1, 500).Select(i => Ring("R" + i, purpose: "Available", available: i % 3000)).ToArray()), "at most 100 rings" },
        { "101 rings name the cap", Plan(Enumerable.Range(1, 101).Select(i => Ring("R" + i, purpose: "Available", available: i)).ToArray()), "The plan has 101 rings. A plan can have at most 100 rings." },
        { "one ring loads", Plan(Ring("Only", "MCM00098")), null },
        { "thirteen required rings in order load", Plan(Enumerable.Range(1, 13).Select(i => Ring("R" + i, "MCM0" + (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), available: i * 2, deadline: i * 2 + 1)).ToArray()), null },
        { "a hundred rings load", Plan(Enumerable.Range(1, 100).Select(i => Ring("R" + i, purpose: "Available", available: i)).ToArray()), null },
        { "long ring name", Plan(Ring(new string('x', 65))), "needs a name of at most 64 characters" }
    };

    [Theory]
    [MemberData(nameof(PlanCases))]
    public void PlanValidationGivesThePlainReason(string name, RingPlan plan, string? expected)
    {
        var errors = RingPlanRules.Validate(plan);
        if (expected is null) Assert.True(errors.Count == 0, $"{name}: {string.Join(" | ", errors)}");
        else Assert.True(errors.Any(e => e.Contains(expected, StringComparison.Ordinal)), $"{name}: expected '{expected}', got: {string.Join(" | ", errors)}");
    }

    public static TheoryData<string, string, int, int?, string, string, string?> ExpandCases() => new()
    {
        // name, basis, available offset, deadline offset, start, expected available, expected deadline
        { "same day", "LocalTime", 0, 1, "2026-10-21T09:00:00", "2026-10-21T09:00:00", "2026-10-22T09:00:00" },
        { "week later", "LocalTime", 7, 10, "2026-10-21T09:00:00", "2026-10-28T09:00:00", "2026-10-31T09:00:00" },
        { "month end", "LocalTime", 10, 12, "2026-10-25T23:30:00", "2026-11-04T23:30:00", "2026-11-06T23:30:00" },
        { "leap day", "Utc", 1, 2, "2028-02-28T06:00:00", "2028-02-29T06:00:00", "2028-03-01T06:00:00" },
        { "year end", "Utc", 3, 4, "2026-12-30T12:00:00", "2027-01-02T12:00:00", "2027-01-03T12:00:00" },
        { "utc across eastern spring gap keeps 02:30", "Utc", 1, 2, "2027-03-13T02:30:00", "2027-03-14T02:30:00", "2027-03-15T02:30:00" },
        { "local across eastern spring gap moves to 03:30", "LocalTime", 1, 2, "2027-03-13T02:30:00", "2027-03-14T03:30:00", "2027-03-15T02:30:00" },
        { "local fall overlap keeps 01:30", "LocalTime", 1, 3, "2026-10-31T01:30:00", "2026-11-01T01:30:00", "2026-11-03T01:30:00" },
        { "local across fall change keeps wall clock", "LocalTime", 0, 2, "2026-10-31T09:00:00", "2026-10-31T09:00:00", "2026-11-02T09:00:00" },
        { "seconds cut", "LocalTime", 0, 1, "2026-10-21T09:00:59.900", "2026-10-21T09:00:59", "2026-10-22T09:00:59" },
        { "available ring has no deadline", "LocalTime", 2, null, "2026-10-21T09:00:00", "2026-10-23T09:00:00", null },
        { "zero offsets", "Utc", 0, 0, "2026-10-21T00:00:00", "2026-10-21T00:00:00", "2026-10-21T00:00:00" },
        { "far offsets", "Utc", 365, 366, "2026-10-21T08:15:00", "2027-10-21T08:15:00", "2027-10-22T08:15:00" }
    };

    [Theory]
    [MemberData(nameof(ExpandCases))]
    public void ExpansionKeepsTheStartTimeOfDay(string name, string basis, int available, int? deadline, string start, string expectedAvailable, string? expectedDeadline)
    {
        var ring = Ring("R", purpose: deadline is null ? "Available" : "Required", available: available, deadline: deadline);
        var plan = Plan(ring) with { TimeBasedOn = basis };
        var row = RingPlanRules.Expand(plan, DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture), Eastern).Single();
        Assert.True(row.AvailableTime == DateTime.Parse(expectedAvailable, System.Globalization.CultureInfo.InvariantCulture), $"{name}: available {row.AvailableTime:o}");
        Assert.True(row.Deadline == (expectedDeadline is null ? null : DateTime.Parse(expectedDeadline, System.Globalization.CultureInfo.InvariantCulture)), $"{name}: deadline {row.Deadline:o}");
        Assert.Equal(DateTimeKind.Unspecified, row.AvailableTime.Kind);
        Assert.Same(ring, row.Definition);
    }

    [Fact]
    public void ASkippedAndARepeatedLocalTimeCarryAWarning()
    {
        var gap = RingPlanRules.Expand(Plan(Ring("R", available: 1, deadline: 2)), new DateTime(2027, 3, 13, 2, 30, 0), Eastern).Single();
        Assert.Contains(gap.Checks, c => c.Result == "Warn" && c.Message.Contains("does not exist", StringComparison.Ordinal) && c.Message.Contains("2027-03-14 03:30", StringComparison.Ordinal));
        var overlap = RingPlanRules.Expand(Plan(Ring("R", available: 1, deadline: 3)), new DateTime(2026, 10, 31, 1, 30, 0), Eastern).Single();
        Assert.Contains(overlap.Checks, c => c.Result == "Warn" && c.Message.Contains("occurs twice", StringComparison.Ordinal));
        var utc = RingPlanRules.Expand(Plan(Ring("R", available: 1, deadline: 2)) with { TimeBasedOn = "Utc" }, new DateTime(2027, 3, 13, 2, 30, 0), Eastern).Single();
        Assert.Empty(utc.Checks);
    }

    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0);

    private static RingExpansionRow Row(int index, string collection, string purpose, double availableHours, double? deadlineHours, string notification = "DisplayAll") =>
        new(index, "R" + index, collection, "", 0, purpose, Now.AddHours(availableHours), deadlineHours is { } d ? Now.AddHours(d) : null, "", null, [])
        { Definition = Ring("R" + index, collection, purpose, notification: notification) };

    /// <summary>Rows, and the text of an error (or of a warning with a leading "warn:"); null when the rows pass without a warning.</summary>
    public static TheoryData<string, RingExpansionRow[], string?> RowCases() => new()
    {
        { "valid", [Row(1, "MCM00001", "Required", 1, 24), Row(2, "MCM00002", "Required", 48, 72)], null },
        { "no rows", [], "There are no rings to run." },
        { "empty collection", [Row(1, "", "Required", 1, 24)], "Ring 1 'R1' has no target collection." },
        { "built-in edited in", [Row(1, "SMS00001", "Required", 1, 24)], "targets built-in collection SMS00001" },
        { "shared after edit", [Row(1, "MCM00001", "Required", 1, 24), Row(2, "mcm00001", "Required", 48, 72)], "Rings must not share a collection" },
        { "required without deadline", [Row(1, "MCM00001", "Required", 1, null)], "is Required and has no deadline" },
        { "deadline equals available", [Row(1, "MCM00001", "Required", 5, 5)], "must be later than its available time" },
        { "deadline in the past", [Row(1, "MCM00001", "Required", -48, -1)], "is in the past; the deployment would enforce at once" },
        { "deadline exactly now", [Row(1, "MCM00001", "Required", -48, 0)], "is in the past" },
        { "deadline one minute ahead", [Row(1, "MCM00001", "Required", -48, 1.0 / 60)], "warn:available time 2026-10-18 12:00 is in the past" },
        { "deadline not after previous", [Row(1, "MCM00001", "Required", 1, 24), Row(2, "MCM00002", "Required", 2, 24)], "must be later than the Ring 1 'R1' deadline" },
        { "available ring skipped in ordering", [Row(1, "MCM00001", "Required", 1, 24), Row(2, "MCM00002", "Available", 2, null), Row(3, "MCM00003", "Required", 3, 30)], null },
        { "available with hide all", [Row(1, "MCM00001", "Available", 1, null, "HideAll")], "is Available with HideAll" },
        { "available four minutes back passes", [Row(1, "MCM00001", "Available", -4.0 / 60, null)], null },
        { "available six minutes back warns", [Row(1, "MCM00001", "Available", -0.1, null)], "warn:is in the past; clients see it at once" },
        { "bad purpose", [Row(1, "MCM00001", "Optional", 1, null)], "is not Available or Required" }
    };

    [Theory]
    [MemberData(nameof(RowCases))]
    public void RowRulesGiveThePlainReason(string name, RingExpansionRow[] rows, string? expected)
    {
        var result = RingPlanRules.CheckRows(rows, Now);
        if (expected is null)
        {
            Assert.True(result.Errors.Count == 0 && result.Warnings.Count == 0, $"{name}: {string.Join(" | ", result.Errors.Concat(result.Warnings).Select(e => e.Message))}");
            return;
        }
        if (expected.StartsWith("warn:", StringComparison.Ordinal))
        {
            Assert.False(result.Blocked, $"{name}: {string.Join(" | ", result.Errors.Select(e => e.Message))}");
            Assert.Contains(result.Warnings, w => w.Message.Contains(expected[5..], StringComparison.Ordinal));
        }
        else Assert.True(result.Errors.Any(e => e.Message.Contains(expected, StringComparison.Ordinal)), $"{name}: got {string.Join(" | ", result.Errors.Select(e => e.Message))}");
    }

    public static TheoryData<string, string, string?, string, int> ShiftCases() => new()
    {
        // available, deadline, now, expected minutes
        { "2026-10-20T08:00:00", "2026-10-22T08:00:00", "2026-10-20T12:00:00", "deadline ahead", 0 },
        { "2026-10-18T08:00:00", null, "2026-10-20T12:00:00", "no deadline", 0 },
        { "2026-10-18T08:00:00", "2026-10-20T12:00:00", "2026-10-20T12:00:00", "deadline exactly now", 3121 },
        { "2026-10-18T08:00:00", "2026-10-19T08:00:00", "2026-10-20T12:00:00", "two days back", 3121 },
        { "2026-10-18T08:00:00", "2026-10-19T08:00:00", "2026-10-20T12:00:30", "seconds of now are cut", 3121 },
        { "2026-10-18T08:00:30", "2026-10-19T08:00:00", "2026-10-20T12:00:00", "partial minute rounds up", 3121 },
        { "2026-10-20T11:00:00", "2026-10-20T11:30:00", "2026-10-20T12:00:00", "one hour", 61 },
        { "2026-10-20T11:59:00", "2026-10-20T11:59:30", "2026-10-20T12:00:00", "two minutes", 2 }
    };

    [Theory]
    [MemberData(nameof(ShiftCases))]
    public void ThePromoteShiftStartsAtTheNextMinute(string available, string? deadline, string now, string name, int expected)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.True(expected == RingPlanRules.PromoteShift(DateTime.Parse(available, c), deadline is null ? null : DateTime.Parse(deadline, c), DateTime.Parse(now, c)), name);
    }

    public static TheoryData<string, RingLiveCounts?, int?, bool, double?, string> ThresholdCases() => new()
    {
        { "no threshold", null, null, true, null, "" },
        { "no counts", null, 90, false, null, "No live counts; the 90% threshold cannot be checked." },
        { "error state", Counts(RingLiveStates.Error, 10, 10, false), 90, false, null, "No live counts" },
        { "not found", Counts(RingLiveStates.NotFound, 0, 0, false), 90, false, null, "No live counts" },
        { "found without summary", Counts(RingLiveStates.Found, 0, 0, false), 90, false, null, "has not summarized this ring yet" },
        { "no targeted", Counts(RingLiveStates.Summarized, 0, 0, true), 90, false, 0, "No clients targeted yet; the threshold is 90%." },
        { "below", Counts(RingLiveStates.Summarized, 10, 8, true), 90, false, 80, "Success 80% is below the 90% threshold." },
        { "equal", Counts(RingLiveStates.Summarized, 10, 9, true), 90, true, 90, "" },
        { "above", Counts(RingLiveStates.Summarized, 3, 3, true), 95, true, 100, "" },
        { "rounded to one decimal", Counts(RingLiveStates.Summarized, 3, 2, true), 66, true, 66.7, "" },
        { "rounded below", Counts(RingLiveStates.Summarized, 7, 6, true), 86, false, 85.7, "Success 85.7% is below the 86% threshold." },
        { "zero threshold with targets", Counts(RingLiveStates.Summarized, 5, 0, true), 0, true, 0, "" }
    };

    private static RingLiveCounts Counts(string state, int targeted, int success, bool known) =>
        new(1, "{00000000-0000-0000-0000-000000000001}", state, targeted, success, 0, 0, 0, known, "");

    [Theory]
    [MemberData(nameof(ThresholdCases))]
    public void TheThresholdGateFollowsTheLiveCounts(string name, RingLiveCounts? counts, int? threshold, bool met, double? percent, string reason)
    {
        var result = RingPlanRules.Threshold(counts, threshold);
        Assert.True(met == result.Met, name);
        Assert.Equal(percent, result.Percent);
        Assert.True(reason.Length == 0 ? result.Reason.Length == 0 : result.Reason.Contains(reason, StringComparison.Ordinal), $"{name}: {result.Reason}");
    }

    [Fact]
    public void TheRingOptionsFollowThePlanDefaultsOfEachType()
    {
        var ring = Ring("R");
        var available = new DateTime(2026, 10, 21, 9, 0, 0);
        var package = RingPlanRules.Options(DeploymentObjectType.Package, ring, available, available.AddDays(1), false);
        Assert.Equal(("DownloadContentFromDistributionPointAndRunLocally", "DoNotRunProgram", "NeverRerunDeployedProgram", false), (package.NetworkBehavior, package.SlowNetworkBehavior, package.RerunBehavior, package.AllowFallback));
        var ts = RingPlanRules.Options(DeploymentObjectType.TaskSequence, ring, available, available.AddDays(1), true);
        Assert.Equal(("DownloadAllContentLocallyBeforeStartingTaskSequence", "Clients", true, true), (ts.NetworkBehavior, ts.TaskSequenceAvailability, ts.ShowProgress, ts.UtcBasis));
        var pxe = RingPlanRules.Options(DeploymentObjectType.TaskSequence, Ring("R", availability: "ClientsMediaAndPxe"), available, available.AddDays(1), false);
        Assert.Equal("DownloadContentLocallyWhenNeededByRunningTaskSequence", pxe.NetworkBehavior);
        var sug = RingPlanRules.Options(DeploymentObjectType.SoftwareUpdateGroup, ring, available, available.AddDays(1), false);
        Assert.Equal((true, true, false), (sug.FallbackToDp, sug.PostRebootScan, sug.AllowMicrosoftUpdate));
        var app = RingPlanRules.Options(DeploymentObjectType.Application, Ring("R", purpose: "Available"), available, available.AddDays(1), false);
        Assert.Null(app.Deadline);
        Assert.Null(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.Package, package));
        Assert.Null(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.TaskSequence, ts));
        Assert.Null(DeploymentScheduleRules.OptionProblem(DeploymentObjectType.TaskSequence, pxe));
    }
}
