using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

public sealed class RingPlanBuilderTests
{
    private static List<DeploymentCollection> Collections(int n) =>
        [.. Enumerable.Range(1, n).Select(i => new DeploymentCollection("MCM1" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture), $"Wave {i:00} 10%", @"Rings", i))];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(13)]
    [InlineData(100)]
    public void RingsFromCollectionsMakeAValidPlanOfAnySize(int count)
    {
        var rings = RingPlanBuilder.FromCollections(Collections(count), "Required", 1, 2, 3);
        Assert.Equal(count, rings.Count);
        Assert.Equal("Wave 01 10%", rings[0].Name);
        Assert.Equal(("MCM10001", 1, 4), (rings[0].CollectionId, rings[0].AvailableOffsetDays, rings[0].DeadlineOffsetDays!.Value));
        Assert.Equal((1 + (count - 1) * 2, 4 + (count - 1) * 2), (rings[^1].AvailableOffsetDays, rings[^1].DeadlineOffsetDays!.Value));
        Assert.Empty(RingPlanRules.Validate(new RingPlan("Waves", "", false, "LocalTime", rings, false)));
    }

    [Fact]
    public void MoreThanTheCapIsRefusedAndBuiltInOrRepeatedCollectionsAreLeftOut()
    {
        Assert.Contains("at most 100 rings", Assert.Throws<DeploymentServiceException>(() => RingPlanBuilder.FromCollections(Collections(101), "Available", 0, 1, 1)).Message);
        var mixed = new List<DeploymentCollection>
        {
            new("MCM10002", "Zeta", "", 0), new("SMS00001", "All Systems", "", 9), new("mcm10002", "Zeta again", "", 0), new("MCM10001", "Alpha", "", 0)
        };
        Assert.Equal(["Zeta", "Alpha"], RingPlanBuilder.FromCollections(mixed, "Available", 0, 1, 1).Select(r => r.Name));
        Assert.Equal(["Alpha", "Zeta"], RingPlanBuilder.FromCollections(mixed, "Available", 0, 1, 1, sortByName: true).Select(r => r.Name));
        Assert.All(RingPlanBuilder.FromCollections(mixed, "Available", 0, 1, 1), r => Assert.Null(r.DeadlineOffsetDays));
    }

    [Theory]
    [InlineData(-1, 1, 1, "first available offset")]
    [InlineData(0, -1, 1, "days between rings")]
    [InlineData(0, 1, 0, "to its deadline must be 1 or more")]
    [InlineData(0, 0, 1, "days between rings must be 1 or more")]
    [InlineData(3000, 100, 1, "at most 3650 days")]
    public void FillOffsetsRefusesValuesThatBreakThePlanRules(int first, int between, int deadline, string text)
    {
        var rings = RingPlanBuilder.FromCollections(Collections(13), "Available", 0, 1, 1).Select(r => r with { Purpose = "Required" }).ToList();
        Assert.Contains(text, Assert.Throws<DeploymentServiceException>(() => RingPlanBuilder.FillOffsets(rings, first, between, deadline)).Message);
    }

    [Fact]
    public void ApplyToAllSetsOnlyTheGivenSettings()
    {
        var rings = RingPlanBuilder.FromCollections(Collections(13), "Required", 0, 1, 2);
        var applied = RingPlanBuilder.ApplyToAll(rings, new RingBulkSettings(UserNotification: "HideAll", OverrideServiceWindow: true, RerunBehavior: "AlwaysRerunProgram"));
        Assert.All(applied, r => Assert.Equal(("HideAll", true, "AlwaysRerunProgram", "Required"), (r.UserNotification, r.OverrideServiceWindow, r.RerunBehavior, r.Purpose)));
        Assert.Equal(rings.Select(r => r.DeadlineOffsetDays), applied.Select(r => r.DeadlineOffsetDays));
        var available = RingPlanBuilder.ApplyToAll(applied, new RingBulkSettings(Purpose: "Available", UserNotification: "DisplayAll"));
        Assert.All(available, r => Assert.Null(r.DeadlineOffsetDays));
        Assert.Empty(RingPlanRules.Validate(new RingPlan("Waves", "", false, "LocalTime", available, false)));
        Assert.Throws<DeploymentServiceException>(() => RingPlanBuilder.ApplyToAll(rings, new RingBulkSettings(Purpose: "Optional")));
    }
}
