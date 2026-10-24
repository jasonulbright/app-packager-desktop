using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>Target resolution through the real session script and the stub site.</summary>
public sealed class PivotsTargetTests
{
    [Fact]
    public async Task ACollectionByIdResolvesToItsMembersWithTheirOnlineState()
    {
        using var h = new PivotsHarness();
        var t = await h.ResolveAsync(RunTargetKind.Collection, "mcm00015");
        Assert.Equal("MCM00015", t.CollectionId);
        Assert.Equal("All Workstations", t.CollectionName);
        Assert.False(t.IsBuiltInCollection);
        Assert.Equal(["CLIENT01", "CLIENT02"], t.Devices.Select(d => d.Name));
        Assert.Equal([true, false], t.Devices.Select(d => d.IsOnline ?? true));
        Assert.Equal(2, t.TotalMatched);
        Assert.Equal(2, h.Site.Runs.Count);
        Assert.Contains("Query:SMS_FullCollectionMembership:CollectionID = 'MCM00015'", h.Site.Log);
    }

    [Fact]
    public async Task ACollectionByNameMatchesTheWholeNameOnly()
    {
        using var h = new PivotsHarness();
        Assert.Equal("MCM00015", (await h.ResolveAsync(RunTargetKind.Collection, "all workstations")).CollectionId);
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => h.ResolveAsync(RunTargetKind.Collection, "All Work"));
        Assert.Contains("no device collection with the ID or the exact name", ex.Message);
        Assert.DoesNotContain(h.Site.Log, l => l.Contains("All Work", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABuiltInCollectionIsFlaggedAndItsNonClientsAreKeptAsNonClients()
    {
        using var h = new PivotsHarness();
        var t = await h.ResolveAsync(RunTargetKind.Collection, "SMS00001");
        Assert.True(t.IsBuiltInCollection);
        Assert.Equal(5, t.Devices.Count);
        Assert.Equal(1, t.NonClientCount);
    }

    [Fact]
    public async Task AListDropsRepeatsAndNamesTheUnknownNames()
    {
        using var h = new PivotsHarness();
        var t = await h.ResolveAsync(RunTargetKind.List, "CLIENT01\r\nclient01;CLIENT02, NOPE\n\n");
        Assert.Equal(["CLIENT01", "CLIENT02"], t.Devices.Select(d => d.Name));
        Assert.Equal(["NOPE"], t.Unmatched);
        Assert.Contains("1 not found", t.Description);
        Assert.Single(h.Site.Runs);
    }

    [Fact]
    public async Task APatternMatchesWildcardsAndEveryMatchIsInTheResult()
    {
        using var h = new PivotsHarness();
        Assert.Equal(["CLIENT01", "CLIENT02"], (await h.ResolveAsync(RunTargetKind.Pattern, "client*")).Devices.Select(d => d.Name));
        Assert.Equal(["CLIENT01", "CLIENT02"], (await h.ResolveAsync(RunTargetKind.Pattern, "CLIENT0?")).Devices.Select(d => d.Name));
        Assert.Equal(8, (await h.ResolveAsync(RunTargetKind.Pattern, "*")).Devices.Count);
    }

    [Theory]
    [InlineData("CLIENT01' OR '1'='1")]
    [InlineData("%")]
    [InlineData("CLIENT*")]
    public async Task ADeviceNameIsLiteralAndNeverReachesQueryText(string text)
    {
        using var h = new PivotsHarness();
        var t = await h.ResolveAsync(RunTargetKind.Device, text);
        Assert.Empty(t.Devices);
        Assert.DoesNotContain(h.Site.Log, l => l.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeviceNameWithTwoRecordsIsRefused()
    {
        var state = StubPivotsSite.Lab01();
        state["Devices"]!.AsArray().Add(StubPivotsSite.Device(16777230, "CLIENT01"));
        using var h = new PivotsHarness(state);
        var ex = await Assert.ThrowsAsync<PivotsServiceException>(() => h.ResolveAsync(RunTargetKind.Device, "CLIENT01"));
        Assert.Contains("2 devices have the name", ex.Message);
        var list = await h.ResolveAsync(RunTargetKind.List, "CLIENT01");
        Assert.Equal(2, list.Devices.Count);
        Assert.Contains("matched more than one device", list.Description);
    }

    [Fact]
    public async Task TwentyThousandDevicesResolveInOneSessionCallWithOneQuery()
    {
        var state = StubPivotsSite.Lab01();
        var devices = new JsonArray();
        for (var i = 0; i < 20_000; i++) devices.Add(StubPivotsSite.Device(30_000_000 + i, "WKS" + i.ToString("D5"), online: i % 7 != 0));
        state["Devices"] = devices;
        using var h = new PivotsHarness(state);
        var names = string.Join("\n", Enumerable.Range(0, 20_000).Select(i => "WKS" + i.ToString("D5")).Append("MISSING1"));
        var t = await h.ResolveAsync(RunTargetKind.List, names);
        Assert.Equal(20_000, t.Devices.Count);
        Assert.Equal(["MISSING1"], t.Unmatched);
        Assert.Single(h.Site.Runs);
        Assert.Equal(1, h.Site.Count("Query:"));
        var plan = await h.Service.PrepareAsync(PivotsHarness.Request(PivotsChangeKind.RunPivot, t, "OS"), CancellationToken.None);
        Assert.Contains(plan.Checks, c => c.Name == PivotsScriptsService.CheckNames.Cap && c.Result == DeploymentCheckResults.Fail);
        Assert.Null(plan.Fingerprint);
    }

    [Fact]
    public void MatchingIsLinearInTheNumberOfDevicesAndNames()
    {
        var all = Enumerable.Range(0, 100_000).Select(i => new ResolvedDevice(i + 1, "N" + i)).ToList();
        var names = Enumerable.Range(0, 100_000).Select(i => "n" + i).ToList();
        var t = PivotsScriptsService.Match(RunTargetKind.List, "", names, all);
        Assert.Equal(100_000, t.Devices.Count);
        Assert.False(t.Truncated);
        var over = PivotsScriptsService.Match(RunTargetKind.Pattern, "*", [], Enumerable.Range(0, PivotsScriptsService.MaxResolvedDevices + 5).Select(i => new ResolvedDevice(i + 1, "N" + i)).ToList());
        Assert.True(over.Truncated);
        Assert.Equal(PivotsScriptsService.MaxResolvedDevices, over.Devices.Count);
        Assert.Equal(PivotsScriptsService.MaxResolvedDevices + 5, over.TotalMatched);
    }
}
