using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.Deployments.DeploymentHarness;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>The real Deployments script through the stub site, for every change kind and object type.</summary>
public sealed class DeploymentServiceScriptTests
{
    public static TheoryData<DeploymentObjectType> Types => new()
    {
        DeploymentObjectType.Application, DeploymentObjectType.Package, DeploymentObjectType.TaskSequence, DeploymentObjectType.SoftwareUpdateGroup
    };

    private static string ListOf(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "AppAssignments",
        DeploymentObjectType.SoftwareUpdateGroup => "UgAssignments",
        _ => "Adverts"
    };

    [Theory]
    [MemberData(nameof(Types))]
    public async Task AnAvailableCreateOfEachTypeStoresThePlanReadsItBackAndUndoRemovesIt(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(Of(type), Options(type)));
        Assert.False(prepared.Blocked, string.Join(" | ", prepared.Checks.Select(c => $"{c.Name}:{c.Result}:{c.Message}")));
        Assert.Contains(prepared.Impact, l => l == DeploymentService.UndoNote);

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.True(outcome.Succeeded, outcome.Message);
        var undo = Assert.IsType<DeploymentChange>(outcome.UndoChange);
        Assert.Equal(DeploymentChangeKind.RemoveDeployment, undo.Kind);
        var stored = h.Stored(ListOf(type));
        var id = type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup ? (string)stored["AssignmentUniqueID"]! : (string)stored["AdvertisementID"]!;
        Assert.Equal(id, undo.CreatedId);
        Assert.Equal(id, undo.Existing!.DeploymentId);
        Assert.Matches(type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup ? @"^\{[0-9A-F-]{36}\}$" : "^MCM2[0-9A-F]{4}$", id);
        Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0), undo.Existing.AvailableTime);
        Assert.Null(undo.Existing.Deadline);
        var record = Assert.Single(h.Audit.Read(10));
        Assert.Equal(("Create", "Success", id, "MCM00098"), (record.Event, record.Result, record.DeploymentUniqueID, record.CollectionID));

        var removed = (await h.ApplyAsync(await h.PrepareAsync(undo))).Single();

        Assert.True(removed.Succeeded, removed.Message);
        Assert.Contains("stays installed", removed.Message);
        Assert.Empty(h.Site.List(ListOf(type)));
        var undoRecord = h.Audit.Read(10)[1];
        Assert.Equal(("Undo", "Success", id), (undoRecord.Event, undoRecord.Result, undoRecord.Undoes));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task ARequiredCreateWithoutTheSecondConfirmationIsRefusedInTheService(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(Of(type), Options(type, "Required")));
        Assert.True(prepared.NeedsRequiredConfirmation);

        var refused = (await h.ApplyAsync(prepared)).Single();

        Assert.False(refused.Succeeded);
        Assert.StartsWith("Skipped: A Required deployment installs on every device", refused.Message);
        Assert.Equal(0, h.CreateCalls);
        Assert.Empty(h.Audit.Read(10));

        var created = (await h.ApplyAsync(prepared with { RequiredConfirmed = true })).Single();
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal("Required", created.UndoChange!.Existing!.Purpose);
        Assert.Equal(new DateTime(2026, 10, 23, 17, 30, 0), created.UndoChange.Existing.Deadline);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task ARequiredUtcCreateStoresTheDeadlineAsGiven(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type, "Required", utc: true), required: true));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(outcome.UndoChange!.Existing!.UtcBasis);
        Assert.Equal(new DateTime(2026, 10, 23, 17, 30, 0), outcome.UndoChange.Existing.Deadline);
        if (type is DeploymentObjectType.Package or DeploymentObjectType.TaskSequence)
        {
            var advert = h.Stored("Adverts");
            Assert.False((bool)advert["ExpirationTimeEnabled"]!);
            Assert.True((bool)advert["PresentTimeIsGMT"]!);
        }
    }

    [Fact]
    public async Task TheStoredFlagsOfEachTypeEqualTheOptions()
    {
        using var h = new DeploymentHarness();
        foreach (var type in new[] { DeploymentObjectType.Package, DeploymentObjectType.TaskSequence, DeploymentObjectType.SoftwareUpdateGroup, DeploymentObjectType.Application })
            Assert.True((await h.PrepareAndApplyAsync(Create(Of(type), Options(type, "Required"), required: true))).Succeeded);

        var package = h.Site.List("Adverts").Single(a => (string)a!["PackageID"]! == "MCM0001B")!;
        Assert.Equal(0x2110000L, (long)package["AdvertFlags"]!);
        Assert.Equal(0x4A048L, (long)package["RemoteClientFlags"]!);
        var ts = h.Site.List("Adverts").Single(a => (string)a!["PackageID"]! == "MCM00030")!;
        Assert.Equal(0x2290000L, (long)ts["AdvertFlags"]!);
        Assert.Equal(0x49050L, (long)ts["RemoteClientFlags"]!);
        var sug = h.Stored("UgAssignments");
        Assert.Equal((3, 0xE0050L, true, false), ((int)sug["SuppressReboot"]!, (long)sug["DPLocality"]!, (bool)sug["UserUIExperience"]!, (bool)sug["NotifyUser"]!));
        var app = h.Stored("AppAssignments");
        Assert.Equal((false, false, 0x80050L), ((bool)app["UserUIExperience"]!, (bool)app["NotifyUser"]!, (long)app["DPLocality"]!));
    }

    [Fact]
    public async Task ATaskSequenceOptionConfigMgrIgnoresFailsTheOutcomeWithItsUndo()
    {
        using var h = new DeploymentHarness();
        var options = Options(DeploymentObjectType.TaskSequence) with { NetworkBehavior = "RunFromDistributionPoint" };

        var outcome = await h.PrepareAndApplyAsync(Create(TaskSequence, options));

        Assert.False(outcome.Succeeded);
        Assert.Contains("stored other values than the plan: network or content option", outcome.Message);
        Assert.NotNull(outcome.UndoChange);
        Assert.Equal("Partial", h.Audit.Read(10).Single().Result);
    }

    [Fact]
    public async Task ADuplicateMadeAfterThePlanIsFoundRightBeforeTheCreate()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));
        Assert.Equal("Pass", prepared.Checks.Single(c => c.Name == "NoDuplicate").Result);
        h.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] == "State" && site.List("Adverts").Count == 0)
                site.List("Adverts").Add(JsonNode.Parse("""{ "AdvertisementID": "MCM20099", "PackageID": "MCM0001B", "ProgramName": "CMCO-CANARY-Run", "CollectionID": "MCM00098", "OfferType": 2, "PresentTime": "2026-10-21T09:00:00", "PresentTimeIsGMT": false, "ExpirationTimeEnabled": false, "AssignedScheduleEnabled": false, "AssignedSchedule": [], "AdvertFlags": 65536, "RemoteClientFlags": 34864 }"""));
        };

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.StartsWith("Skipped: ", outcome.Message);
        Assert.Contains("MCM20099", outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task ADuplicateInsideTheCreateScriptIsRefusedThere()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(UpdateGroup, Options(DeploymentObjectType.SoftwareUpdateGroup)));
        h.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] == "Create")
                site.List("UgAssignments").Add(JsonNode.Parse("""{ "AssignmentID": 1, "AssignmentUniqueID": "{11111111-2222-3333-4444-555555555555}", "AssignedUpdateGroup": 16778893, "TargetCollectionID": "MCM00098", "StartTime": "2026-10-21T09:00:00", "UseGMTTimes": false, "UserUIExperience": true, "NotifyUser": true, "DPLocality": 16, "SuppressReboot": 0, "RequirePostRebootFullScan": true }"""));
        };

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.StartsWith("Skipped: ", outcome.Message);
        Assert.Contains("{11111111-2222-3333-4444-555555555555}", outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task AFailedDuplicateCheckBlocksThePlanAndTheCreate()
    {
        using var h = new DeploymentHarness();
        h.Site.Fail("query:SMS_ApplicationAssignment");
        var prepared = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)));
        Assert.Equal("Unknown", prepared.Checks.Single(c => c.Name == "NoDuplicate").Result);
        Assert.True(prepared.Blocked);
        Assert.StartsWith("Skipped: ", (await h.ApplyAsync(prepared)).Single().Message);

        var passed = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)));
        Assert.False(passed.Blocked);
        h.Site.BeforeRun = (p, site) => { if ((string?)p["Operation"] == "Create") site.Fail("query:SMS_ApplicationAssignment"); };
        var outcome = (await h.ApplyAsync(passed)).Single();

        Assert.StartsWith("Skipped: the checks right before the create could not read ConfigMgr", outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task EachOfTheFiveChecksFailsForItsOwnReason()
    {
        using var h = new DeploymentHarness();
        string Result(DeploymentChange c, string name) => c.Checks.Single(x => x.Name == name).Result;

        var missing = await h.PrepareAsync(Create(App with { Id = "16799999", ModelId = "16799998" }, Options(DeploymentObjectType.Application)));
        Assert.Equal("Fail", Result(missing, "TargetExists"));

        var noContent = await h.PrepareAsync(Create(AppWithoutContent, Options(DeploymentObjectType.Application)));
        Assert.Equal("Fail", Result(noContent, "ContentDistributed"));
        Assert.Contains("0 of 0 distribution points", noContent.Checks.Single(c => c.Name == "ContentDistributed").Message);

        var userCollection = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package), new DeploymentCollection("MCM00099", "Users", "", 3)));
        Assert.Equal("Fail", Result(userCollection, "CollectionValid"));

        var builtIn = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package), new DeploymentCollection("SMS00001", "All Systems", "", 8)));
        Assert.Equal("Fail", Result(builtIn, "CollectionSafe"));

        var gone = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package), new DeploymentCollection("MCM000FF", "Gone", "", 0)));
        Assert.Equal("Fail", Result(gone, "CollectionValid"));

        Assert.True((await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)))).Succeeded);
        var duplicate = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));
        Assert.Equal("Fail", Result(duplicate, "NoDuplicate"));
        var otherProgram = await h.PrepareAsync(Create(Package with { Program = "CMCO-CANARY-RunAvailable" }, Options(DeploymentObjectType.Package)));
        Assert.Equal("Pass", Result(otherProgram, "NoDuplicate"));

        foreach (var blocked in new[] { missing, noContent, userCollection, builtIn, gone, duplicate })
        {
            Assert.True(blocked.Blocked);
            Assert.StartsWith("Skipped: ", (await h.ApplyAsync(blocked)).Single().Message);
        }
        Assert.Equal(1, h.CreateCalls);
    }

    [Fact]
    public async Task AReadFailureMakesTheChecksUnknownAndBlocks()
    {
        using var h = new DeploymentHarness();
        h.Site.Fail("query:SMS_Package");

        var prepared = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));

        Assert.All(prepared.Checks.Where(c => c.Name is "TargetExists" or "ContentDistributed" or "CollectionValid" or "NoDuplicate"), c => Assert.Equal("Unknown", c.Result));
        Assert.True(prepared.Blocked);
        Assert.DoesNotContain(prepared.Checks, c => c.Message.Contains("SECRET-DETAIL"));
    }

    [Fact]
    public async Task ABuiltInCollectionIsRefusedInApplyEvenWithForgedChecks()
    {
        using var h = new DeploymentHarness();
        var forged = Create(Package, Options(DeploymentObjectType.Package), new DeploymentCollection("SMS00001", "All Systems", "", 8)) with
        {
            Checks = [new PreflightCheck("TargetExists", "Pass", "")]
        };

        var outcome = (await h.ApplyAsync(forged)).Single();

        Assert.StartsWith("Skipped: Collection SMS00001 is a built-in collection", outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task AnAuditLogThatCannotBeWrittenBlocksThePlanAndTheCreate()
    {
        using var h = new DeploymentHarness();
        h.Audit.Probe();
        DeploymentChange prepared;
        using (new FileStream(h.AuditPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            prepared = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)));
            Assert.Equal("Fail", prepared.Checks.Single(c => c.Name == "AuditLog").Result);
            var forged = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };

            var outcome = (await h.ApplyAsync(forged)).Single();

            Assert.StartsWith("Skipped: the audit log cannot be written", outcome.Message);
            Assert.Contains("another program has it open", outcome.Message);
        }
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task ABackdatedAvailableTimeNeedsItsOwnConfirmation()
    {
        using var h = new DeploymentHarness();
        var options = Options(DeploymentObjectType.Package) with { AvailableTime = LocalNow.AddHours(-1) };
        var prepared = await h.PrepareAsync(Create(Package, options));

        Assert.True(prepared.NeedsBackdatedAcknowledgement);
        Assert.Equal("Warn", prepared.Checks.Single(c => c.Name == "Schedule").Result);
        Assert.False(prepared.Blocked);
        Assert.StartsWith("Skipped: The available time is in the past", (await h.ApplyAsync(prepared)).Single().Message);
        Assert.Equal(0, h.CreateCalls);

        Assert.True((await h.ApplyAsync(prepared with { BackdatedAcknowledged = true })).Single().Succeeded);
    }

    [Fact]
    public async Task AnInvertedDeadlineAndAvailableWithHideAllAreBlocked()
    {
        using var h = new DeploymentHarness();
        var inverted = Options(DeploymentObjectType.Application, "Required") with { Deadline = new DateTime(2026, 10, 21, 8, 0, 0) };
        var hidden = Options(DeploymentObjectType.Application) with { UserNotification = "HideAll" };

        foreach (var options in new[] { inverted, hidden })
        {
            var prepared = await h.PrepareAsync(Create(App, options, required: true));
            Assert.Equal("Fail", prepared.Checks.Single(c => c.Name == "Schedule").Result);
            var forged = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };
            Assert.StartsWith("Skipped: ", (await h.ApplyAsync(forged)).Single().Message);
        }
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task UndoIsRefusedWhenTheDeploymentChangedOrWasNotCreatedHere()
    {
        using var h = new DeploymentHarness();
        var created = await h.PrepareAndApplyAsync(Create(App, Options(DeploymentObjectType.Application, "Required"), required: true));
        var undo = created.UndoChange!;
        h.Stored("AppAssignments")["EnforcementDeadline"] = "2026-10-24T17:30:00";

        var changed = await h.PrepareAsync(undo);
        Assert.Equal("Fail", changed.Checks.Single(c => c.Name == "DeploymentUnchanged").Result);
        var refused = (await h.ApplyAsync(undo)).Single();
        Assert.StartsWith("Skipped: ", refused.Message);
        Assert.Contains("changed since it was captured (deadline)", refused.Message);
        Assert.Single(h.Site.List("AppAssignments"));

        var foreign = new DeploymentHarness(h.Site.State);
        using (foreign)
        {
            var notOwned = (await foreign.ApplyAsync(undo)).Single();
            Assert.Equal($"Skipped: deployment {undo.CreatedId} was not created by this product, so it is not removed.", notOwned.Message);
            Assert.Equal("Fail", (await foreign.PrepareAsync(undo)).Checks.Single(c => c.Name == "CreatedHere").Result);
        }
    }

    [Fact]
    public async Task UndoFromTheAuditLogWorksInANewSessionAndIsRefusedWhenTheLogCannotBeRead()
    {
        using var h = new DeploymentHarness();
        var created = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)));
        var undo = created.UndoChange!;
        var later = new DeploymentService(h.Site, h.Collections, h.Audit, utcNow: () => UtcNow, local: Local);

        using (new FileStream(h.AuditPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var uncertain = (await later.ApplyAsync([undo], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single();
            Assert.StartsWith("Skipped: the audit log could not be read", uncertain.Message);
        }
        Assert.Single(h.Site.List("Adverts"));

        var removed = (await later.ApplyAsync([undo], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single();
        Assert.True(removed.Succeeded, removed.Message);
        Assert.Empty(h.Site.List("Adverts"));
    }

    [Fact]
    public async Task UndoRemovesOnlyItsOwnDeploymentAndKeepsTheOtherProgram()
    {
        using var h = new DeploymentHarness();
        var first = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)));
        var second = await h.PrepareAndApplyAsync(Create(Package with { Program = "CMCO-CANARY-RunAvailable" }, Options(DeploymentObjectType.Package)));
        Assert.True(second.Succeeded, second.Message);

        Assert.True((await h.ApplyAsync(first.UndoChange!)).Single().Succeeded);

        var left = Assert.Single(h.Site.List("Adverts"))!;
        Assert.Equal("CMCO-CANARY-RunAvailable", (string)left["ProgramName"]!);
    }

    [Fact]
    public async Task WildcardsAndQuotesInNamesNeverReachAQuery()
    {
        var state = StubDeploymentSite.Lab();
        state["Applications"]![0]!["LocalizedDisplayName"] = "Tool [x64]*' -Force";
        state["Packages"]![0]!["Programs"] = new JsonArray("Run [x64]*", "-Run 'q'");
        using var h = new DeploymentHarness(state);

        var app = await h.PrepareAndApplyAsync(Create(App with { Name = "Tool [x64]*' -Force" }, Options(DeploymentObjectType.Application)));
        var package = await h.PrepareAndApplyAsync(Create(Package with { Program = "-Run 'q'" }, Options(DeploymentObjectType.Package)));

        Assert.True(app.Succeeded, app.Message);
        Assert.True(package.Succeeded, package.Message);
        Assert.Equal("-Run 'q'", (string)h.Stored("Adverts")["ProgramName"]!);
        Assert.All(h.Site.Runs, r => Assert.True(!r.Parameters.TryGetValue("Program", out var p) || p is string));
    }

    [Fact]
    public async Task AnUncertainCreateClaimsTheOneNewDeploymentWithThePlannedValues()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(UpdateGroup, Options(DeploymentObjectType.SoftwareUpdateGroup)));
        h.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "timeout") : null;

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.False(outcome.Succeeded);
        Assert.Contains("appeared with the planned values", outcome.Message);
        Assert.Equal((string)h.Stored("UgAssignments")["AssignmentUniqueID"]!, outcome.UndoChange!.CreatedId);
        Assert.Equal("Partial", h.Audit.Read(10).Single().Result);
    }

    [Fact]
    public async Task AnUncertainCreateWithADeploymentOfAnotherAdminIsNotClaimed()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(TaskSequence, Options(DeploymentObjectType.TaskSequence)));
        h.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.ProcessDied, "died") : null;
        h.Site.BeforeRun = (p, site) =>
        {
            if ((string?)p["Operation"] == "Create")
                site.List("Adverts").Add(JsonNode.Parse("""{ "AdvertisementID": "MCM20AAA", "PackageID": "MCM00030", "ProgramName": "*", "CollectionID": "MCM00098", "OfferType": 2, "PresentTime": "2026-10-21T09:00:00", "PresentTimeIsGMT": false, "ExpirationTimeEnabled": false, "AssignedScheduleEnabled": false, "AssignedSchedule": [], "AdvertFlags": 8978432, "RemoteClientFlags": 34896 }"""));
        };

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.StartsWith("The result of the create", outcome.Message);
        Assert.Equal("Unknown", h.Audit.Read(10).Single().Result);
    }

    [Fact]
    public async Task TwoNewDeploymentsAfterOneCreateAreReportedAsUnknownWithoutUndo()
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("CreateTwice:Package", true);

        var outcome = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)));

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.Contains("2 new deployments appeared", outcome.Message);
    }

    [Fact]
    public async Task AnErrorAfterTheCreateKeepsTheUndoAndAFailedCreateHasNone()
    {
        using var h = new DeploymentHarness();
        h.Site.Fail("after-create:Package", "create:SoftwareUpdateGroup");

        var withError = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)));
        var failed = await h.PrepareAndApplyAsync(Create(UpdateGroup, Options(DeploymentObjectType.SoftwareUpdateGroup)));

        Assert.False(withError.Succeeded);
        Assert.Contains("then reported an error", withError.Message);
        Assert.NotNull(withError.UndoChange);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.UndoChange);
        Assert.Equal("ConfigMgr did not create the deployment of software update group 202609-W11-25H2 (16778893) to collection MCM00098. The log has the details.", failed.Message);
        Assert.All(new[] { withError, failed }, o => Assert.DoesNotContain("SECRET-DETAIL", o.Message));
        Assert.Equal("Failed", h.Audit.Read(10)[1].Result);
    }

    [Fact]
    public async Task CancelStopsBetweenChangesAndReportsTheRestAsSkipped()
    {
        using var h = new DeploymentHarness();
        var first = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));
        var second = await h.PrepareAsync(Create(TaskSequence, Options(DeploymentObjectType.TaskSequence)));
        using var cts = new CancellationTokenSource();
        var seen = new List<DeploymentOutcome>();
        var progress = new SyncProgress(o => { seen.Add(o); cts.Cancel(); });

        var outcomes = await h.Service.ApplyAsync([first, second], progress, cts.Token);

        Assert.True(outcomes[0].Succeeded);
        Assert.Equal("Skipped: the run was cancelled before this change.", outcomes[1].Message);
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, h.CreateCalls);
    }

    [Fact]
    public async Task APartialRunKeepsTheResultsOfTheChangesThatRan()
    {
        using var h = new DeploymentHarness();
        var good = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));
        var bad = await h.PrepareAsync(Create(AppWithoutContent, Options(DeploymentObjectType.Application)));
        var third = await h.PrepareAsync(Create(UpdateGroup, Options(DeploymentObjectType.SoftwareUpdateGroup)));

        var outcomes = await h.ApplyAsync(good, bad, third);

        Assert.Equal([true, false, true], outcomes.Select(o => o.Succeeded));
        Assert.NotNull(outcomes[0].UndoChange);
        Assert.NotNull(outcomes[2].UndoChange);
    }

    [Fact]
    public async Task DistributeRequestsEachGroupAndHasNoUndo()
    {
        using var h = new DeploymentHarness();
        var group = "{D921E77B-3F7D-4555-83D8-522461F5388F}";
        var change = new DeploymentChange(DeploymentChangeKind.Distribute, Package, null, null, ["All DPs"], false, false, false, null, [], [], null);

        var prepared = await h.PrepareAsync(change);
        Assert.Equal([group], prepared.DpGroups);
        Assert.Contains(prepared.Impact, l => l.Contains("not distributed to this group"));
        Assert.Contains(DeploymentService.NoDistributionUndo, prepared.Impact);
        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Null(outcome.UndoChange);
        Assert.Contains(outcome.Steps, s => s == "Requested distribution to DP group All DPs.");
        var record = h.Audit.Read(10).Single();
        Assert.Equal(("Distribute", "Success", "All DPs"), (record.Event, record.Result, record.DistributionPointGroups));

        var again = (await h.ApplyAsync(await h.PrepareAsync(change))).Single();
        Assert.True(again.Succeeded);
        Assert.Contains(again.Steps, s => s == "DP group All DPs already has the content.");
        await Assert.ThrowsAsync<DeploymentServiceException>(() => h.PrepareAsync(change with { Object = UpdateGroup }));
    }

    [Fact]
    public async Task ACreateWithDistributionWaitsForTheContentUnlessTheOperatorChoseToCreateNow()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]![0]!["Content"] = JsonNode.Parse("""{ "Targeted": 1, "Success": 0, "InProgress": 1 }""");
        using var h = new DeploymentHarness(state);
        var options = Options(DeploymentObjectType.Package);

        var waiting = await h.PrepareAsync(Create(Package, options, groups: ["All DPs"]));
        Assert.Equal("Warn", waiting.Checks.Single(c => c.Name == "ContentDistributed").Result);
        Assert.Contains(waiting.Impact, l => l.StartsWith("First requests content distribution"));
        var skipped = (await h.ApplyAsync(waiting)).Single();
        Assert.StartsWith("Skipped: the content of package", skipped.Message);
        Assert.Contains(skipped.Steps, s => s.StartsWith("Requested distribution"));
        Assert.Equal(0, h.CreateCalls);

        var now = await h.PrepareAsync(Create(Package, options, groups: ["All DPs"], createNow: true));
        var created = (await h.ApplyAsync(now)).Single();
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(["Distribute", "Create", "Distribute", "Create"], h.Audit.Read(10).Select(r => r.Event));
        Assert.Equal(["Success", "Failed", "Success", "Success"], h.Audit.Read(10).Select(r => r.Result));
    }

    [Fact]
    public async Task ASigningRefusalBlocksARequiredDeploymentOfAPackagedApplication()
    {
        var signing = new FakeSigning(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { App.Name }, "This product packaged it and the build is not signed.");
        using var h = new DeploymentHarness(signing: signing);

        var required = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application, "Required"), required: true));
        Assert.Equal("Fail", required.Checks.Single(c => c.Name == "Signing").Result);
        Assert.True(required.Object!.PackagedByProduct);
        var forged = required with { Checks = required.Checks.Select(c => c with { Result = "Pass" }).ToList() };
        Assert.Equal("Skipped: This product packaged it and the build is not signed.", (await h.ApplyAsync(forged)).Single().Message);

        var available = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)));
        Assert.Equal("Pass", available.Checks.Single(c => c.Name == "Signing").Result);
        Assert.Equal(0, h.CreateCalls);

        using var unknown = new DeploymentHarness(signing: new FakeSigning(new HashSet<string>(), fail: true));
        Assert.Equal("Unknown", (await unknown.PrepareAsync(Create(App, Options(DeploymentObjectType.Application, "Required"), required: true))).Checks.Single(c => c.Name == "Signing").Result);
    }

    [Fact]
    public async Task TheListsReadObjectsProgramsGroupsAndDeployments()
    {
        using var h = new DeploymentHarness(signing: new FakeSigning(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "7-Zip (x64 edition) - 26.03" }));
        Assert.True((await h.PrepareAndApplyAsync(Create(App, Options(DeploymentObjectType.Application)))).Succeeded);

        var apps = await h.Service.ListObjectsAsync(DeploymentObjectType.Application, CancellationToken.None);
        Assert.Equal(2, apps.Count);
        Assert.True(apps.Single(a => a.Id == "16779607").PackagedByProduct);
        Assert.Equal("16778866", apps.Single(a => a.Id == "16779607").ModelId);
        Assert.Equal(2, (await h.Service.ListObjectsAsync(DeploymentObjectType.SoftwareUpdateGroup, CancellationToken.None)).Single().UpdateCount);
        Assert.Equal(["CMCO-CANARY-Run", "CMCO-CANARY-RunAvailable"], await h.Service.ListProgramsAsync("mcm0001b", CancellationToken.None));
        var groups = await h.Service.ListDpGroupsAsync(App, CancellationToken.None);
        Assert.Equal(("All DPs", "Distributed", 1, 1), (groups.Single().Name, groups.Single().Status, groups.Single().Targeted, groups.Single().Success));
        var deployments = await h.Service.ListDeploymentsAsync(App, CancellationToken.None);
        Assert.Equal(("DH-Test-1", "Available", "16779607"), (deployments.Single().CollectionName, deployments.Single().Purpose, deployments.Single().ObjectId));
        var collections = await h.Service.ListCollectionsAsync(CancellationToken.None);
        Assert.Equal(["DH-Test-1", "HomeLab - Test Deployments"], collections.Select(c => c.Name));
        Assert.Empty(await h.Service.ListDpGroupsAsync(UpdateGroup, CancellationToken.None));
    }

    [Fact]
    public async Task ARemoveWhoseSessionFailsIsDoneWhenTheReadBackShowsItGone()
    {
        using var h = new DeploymentHarness();
        var created = await h.PrepareAndApplyAsync(Create(App, Options(DeploymentObjectType.Application)));
        h.Site.AfterRun = p => (string?)p["Operation"] == "Remove" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "timeout") : null;

        var removed = (await h.ApplyAsync(created.UndoChange!)).Single();

        Assert.True(removed.Succeeded, removed.Message);
        Assert.Empty(h.Site.List("AppAssignments"));
    }

    [Fact]
    public async Task NoOutcomeOrCheckCarriesRawProviderText()
    {
        using var h = new DeploymentHarness();
        h.Site.Fail("create:Application", "remove:x", "distribute:All DPs");
        var outcomes = new List<DeploymentOutcome>
        {
            await h.PrepareAndApplyAsync(Create(App, Options(DeploymentObjectType.Application))),
            await h.PrepareAndApplyAsync(new DeploymentChange(DeploymentChangeKind.Distribute, Package, null, null, ["All DPs"], false, false, false, null, [], [], null))
        };
        Assert.All(outcomes, o =>
        {
            Assert.DoesNotContain("SECRET-DETAIL", o.Message);
            Assert.All(o.Steps, s => Assert.DoesNotContain("SECRET-DETAIL", s));
        });
        Assert.DoesNotContain("SECRET-DETAIL", File.ReadAllText(h.AuditPath));
    }

    private sealed class SyncProgress(Action<DeploymentOutcome> report) : IProgress<DeploymentOutcome>
    {
        public void Report(DeploymentOutcome value) => report(value);
    }
}
