using System.Diagnostics;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Deployments.Rings;

public sealed class RingPlanStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-ringplans-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "Rings");
    private RingPlanStore Store() => new(() => Folder);

    public void Dispose()
    {
        try { foreach (var d in Directory.GetDirectories(_root)) if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d); Directory.Delete(_root, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static RingPlan UserPlan(string name = "Office-Rings") =>
        new(name, "Office", true, "Utc",
        [
            RingPlanRulesTests.Ring("QA", "MCM00098", threshold: 90, dpGroup: "All DPs"),
            RingPlanRulesTests.Ring("Wide", "MCM00099", purpose: "Available", available: 2) with { FastNetworkOption = "RunProgramFromDistributionPoint", AllowBoundaryFallback = false }
        ], false);

    [Fact]
    public void TheFirstListWritesTheTwoSeedsWithEmptyCollectionsOnce()
    {
        var plans = Store().List();
        Assert.Equal(["Server-Rings", "Workstation-Rings"], plans.Select(p => p.Name));
        Assert.All(plans, p => Assert.True(p.IsSeed));
        Assert.All(plans.SelectMany(p => p.Rings), r => Assert.Equal("", r.CollectionId));
        Assert.Equal([true, false], plans.Select(p => p.HoldLaterRings));
        File.Delete(Path.Combine(Folder, "Server-Rings.json"));
        Assert.Single(Store().List());
    }

    [Fact]
    public void TheSeedFilesHoldTheFieldsOfTheSuiteToolsSeeds()
    {
        Store().List();
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, "Workstation-Rings.json")))!.AsObject();
        Assert.Equal(["Name", "Description", "HoldLaterRings", "TimeBasedOn", "Rings"], file.Select(p => p.Key));
        Assert.Equal(["Name", "CollectionID", "CollectionName", "Purpose", "AvailableOffsetDays", "DeadlineOffsetDays", "UserNotification", "OverrideServiceWindow",
            "RebootOutsideServiceWindow", "AllowMeteredConnection", "DPGroup", "SuccessThresholdPercent"], file["Rings"]![0]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void AUserPlanRoundTripsWithItsOptions()
    {
        var store = Store();
        store.List();
        var saved = store.Save(UserPlan(), replace: false);
        Assert.Equal("Office-Rings.json", saved.FileName);
        var read = store.List().Single(p => p.Name == "Office-Rings");
        Assert.False(read.IsSeed);
        Assert.Equal(("Utc", true, 2), (read.TimeBasedOn, read.HoldLaterRings, read.Rings.Count));
        Assert.Equal(UserPlan().Rings[0], read.Rings[0]);
        Assert.Equal(UserPlan().Rings[1] with { DeadlineOffsetDays = null }, read.Rings[1]);
    }

    [Fact]
    public void SeedsCannotBeReplacedOrDeletedOnlyCopied()
    {
        var store = Store();
        var seed = store.List().Single(p => p.Name == "Server-Rings");
        Assert.Contains("seed plan and cannot be changed", Assert.Throws<DeploymentServiceException>(() => store.Save(seed, replace: true)).Message);
        Assert.Contains("seed plan and cannot be changed", Assert.Throws<DeploymentServiceException>(() => store.Save(seed with { Name = "server-rings" }, replace: true)).Message);
        Assert.Contains("cannot be deleted", Assert.Throws<DeploymentServiceException>(() => store.Delete("Server-Rings")).Message);
        store.Save(seed with { Name = "Server-Rings copy", IsSeed = true }, replace: false);
        Assert.False(store.List().Single(p => p.Name == "Server-Rings copy").IsSeed);
    }

    [Fact]
    public void ATakenNameIsRefusedWithoutReplaceAndADeleteRemovesTheFile()
    {
        var store = Store();
        store.List();
        store.Save(UserPlan(), false);
        Assert.Contains("exists", Assert.Throws<DeploymentServiceException>(() => store.Save(UserPlan(), false)).Message);
        store.Save(UserPlan() with { Description = "changed" }, true);
        Assert.Equal("changed", store.List().Single(p => p.Name == "Office-Rings").Description);
        store.Delete("office-rings");
        Assert.False(File.Exists(Path.Combine(Folder, "Office-Rings.json")));
        Assert.Contains("does not exist", Assert.Throws<DeploymentServiceException>(() => store.Delete("Office-Rings")).Message);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\evil")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("CON")]
    [InlineData("nul.rings")]
    [InlineData("COM1")]
    [InlineData("LPT¹")]
    [InlineData("Rings*")]
    [InlineData("Rings?")]
    [InlineData("Rings.")]
    [InlineData("a..b")]
    [InlineData("-dash")]
    [InlineData("")]
    [InlineData("C:x")]
    public void HostileNamesAreRefused(string name)
    {
        var store = Store();
        store.List();
        Assert.Throws<DeploymentServiceException>(() => store.Save(UserPlan(name), false));
        Assert.Equal(2, Directory.GetFiles(Folder).Length);
    }

    [Fact]
    public void APlanWithFiveHundredRingsIsRefusedWithAPlainLimit()
    {
        var store = Store();
        store.List();
        var big = UserPlan() with { Rings = Enumerable.Range(1, 500).Select(i => RingPlanRulesTests.Ring("R" + i, purpose: "Available", available: i)).ToList() };
        Assert.Contains("at most 50 rings", Assert.Throws<DeploymentServiceException>(() => store.Save(big, false)).Message);
        File.WriteAllText(Path.Combine(Folder, "Big.json"), new JsonObject { ["Name"] = "Big", ["Rings"] = new JsonArray(Enumerable.Range(1, 500).Select(i => (JsonNode)new JsonObject { ["Name"] = "R" + i, ["Purpose"] = "Available" }).ToArray()) }.ToJsonString());
        Assert.DoesNotContain(store.List(), p => p.Name == "Big");
        Assert.Contains("Big.json", store.Message);
        Assert.Contains("at most 50 rings", store.Message);
    }

    [Fact]
    public void UnusableFilesAreSkippedAndNamed()
    {
        var store = Store();
        store.List();
        File.WriteAllText(Path.Combine(Folder, "Corrupt.json"), "{ not json");
        File.WriteAllText(Path.Combine(Folder, "BuiltIn.json"), """{ "Name": "BuiltIn", "Rings": [ { "Name": "Everyone", "CollectionID": "SMS00001", "Purpose": "Available", "UserNotification": "DisplayAll" } ] }""");
        File.WriteAllText(Path.Combine(Folder, "Half.json"), """{ "Name": "Half", "Rings": [ { "Name": "QA", "Purpose": "Required", "AvailableOffsetDays": 0.5, "DeadlineOffsetDays": 1 } ] }""");
        File.WriteAllText(Path.Combine(Folder, "Twin.json"), """{ "Name": "Server-Rings", "Rings": [ { "Name": "QA", "Purpose": "Available" } ] }""");
        File.WriteAllText(Path.Combine(Folder, "Array.json"), "[]");
        File.WriteAllText(Path.Combine(Folder, "Huge.json"), "{\"Name\":\"Huge\",\"Pad\":\"" + new string('x', RingPlanStore.MaxFileBytes) + "\",\"Rings\":[]}");
        var plans = store.List();
        Assert.Equal(["Server-Rings", "Workstation-Rings"], plans.Select(p => p.Name));
        Assert.Contains("6 plan files are not listed", store.Message);
        Assert.Contains("Corrupt.json: the file is not valid JSON.", store.Message);
        Assert.Contains("BuiltIn.json: Ring 1 'Everyone' targets built-in collection SMS00001", store.Message);
        Assert.Contains("Half.json: Ring 1 'QA' available offset must be a whole number of days.", store.Message);
        Assert.Contains("another file already holds a plan named Server-Rings", store.Message);
        Assert.Contains("Huge.json: the file is larger than 256 KB.", store.Message);
    }

    [Fact]
    public void ASuiteToolFileWithTextNumbersAndMissingOptionsLoadsWithTheDefaults()
    {
        var store = Store();
        store.List();
        File.WriteAllText(Path.Combine(Folder, "Suite.json"), """
            { "Name": "Suite", "TimeBasedOn": "LocalTime", "HoldLaterRings": true,
              "Rings": [ { "Name": "Pilot", "CollectionID": "mcm00098", "Purpose": "Required", "AvailableOffsetDays": "2", "DeadlineOffsetDays": 5.0, "SuccessThresholdPercent": "" },
                         { "Name": "Wide", "CollectionID": "MCM00099", "Purpose": "Available", "AvailableOffsetDays": 7, "DeadlineOffsetDays": 9, "UserNotification": "DisplaySoftwareCenterOnly", "SlowNetworkOption": "DownloadContentFromDistributionPointAndLocally" } ] }
            """);
        var plan = store.List().Single(p => p.Name == "Suite");
        Assert.Equal(new RingDefinition("Pilot", "MCM00098", "", "Required", 2, 5, "DisplayAll", false, false, false, "", null, null, null, null), plan.Rings[0]);
        Assert.Null(plan.Rings[1].DeadlineOffsetDays);
        Assert.Equal("DownloadContentFromDistributionPointAndLocally", plan.Rings[1].SlowNetworkOption);
    }

    [Fact]
    public void ALinkedPlansFolderIsNeitherReadNorWritten()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(_root);
        var link = Path.Combine(_root, "Rings");
        using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) mk.WaitForExit();
        Assert.True(Directory.Exists(link));
        var store = Store();
        Assert.Empty(store.List());
        Assert.Equal(RingPlanStore.LinkMessage, store.Message);
        Assert.Throws<DeploymentServiceException>(() => store.Save(UserPlan(), false));
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void AnInvalidPlanIsNotSaved()
    {
        var store = Store();
        store.List();
        var shared = UserPlan() with { Rings = [RingPlanRulesTests.Ring("QA", "MCM00098"), RingPlanRulesTests.Ring("Pilot", "MCM00098", available: 2, deadline: 5)] };
        Assert.Contains("Rings must not share a collection", Assert.Throws<DeploymentServiceException>(() => store.Save(shared, false)).Message);
        var builtIn = UserPlan() with { Rings = [RingPlanRulesTests.Ring("QA", "SMSDM003")] };
        Assert.Contains("built-in collection SMSDM003", Assert.Throws<DeploymentServiceException>(() => store.Save(builtIn, false)).Message);
        Assert.False(File.Exists(Path.Combine(Folder, "Office-Rings.json")));
    }
}
