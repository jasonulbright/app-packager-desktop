using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Deployments.DeploymentHarness;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The real Deployments script through the stub site: the History Undo removes a deployment only
/// with an intact audit record whose account, start time and values ConfigMgr confirms; a create
/// that races another deployment of the same object and collection keeps the Undo of its own.
/// </summary>
public sealed class DeploymentOwnershipProofTests
{
    public static TheoryData<DeploymentObjectType> Types => new()
    {
        DeploymentObjectType.Application, DeploymentObjectType.Package, DeploymentObjectType.TaskSequence, DeploymentObjectType.SoftwareUpdateGroup
    };

    public static TheoryData<DeploymentObjectType> MultiTypes => new()
    {
        DeploymentObjectType.Package, DeploymentObjectType.TaskSequence, DeploymentObjectType.SoftwareUpdateGroup
    };

    private static string ListOf(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "AppAssignments",
        DeploymentObjectType.SoftwareUpdateGroup => "UgAssignments",
        _ => "Adverts"
    };

    /// <summary>A create on the site by another program as the same account, with its own audit log and the given creation time.</summary>
    private static async Task<(DeploymentHarness Site, DeploymentAuditRecord Record)> MadeElsewhere(DeploymentObjectType type, string createdUtc)
    {
        var script = new DeploymentHarness();
        script.Site.SetOption("CreatedUtc", createdUtc);
        var created = await script.PrepareAndApplyAsync(Create(Of(type), Options(type), required: false));
        Assert.True(created.Succeeded, created.Message);
        return (script, script.Audit.Read(10).Single());
    }

    private static async Task<DeploymentChange> HistoryUndo(DeploymentHarness h)
    {
        var record = h.Audit.Read(10).Last(r => r.Event == "Create");
        Assert.True(record.Verified);
        return await h.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(record)!);
    }

    private static PreflightCheck CreatedHere(DeploymentChange prepared) => prepared.Checks.Single(c => c.Name == "CreatedHere");

    // ---- the lab case: a script-made deployment and a record with an earlier time ----------

    [Theory]
    [MemberData(nameof(Types))]
    public async Task AForgedRecordOfADeploymentAScriptMadeWithTheSameAccountIsRefused(DeploymentObjectType type)
    {
        var (script, made) = await MadeElsewhere(type, "2026-10-20T13:08:00");
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            Assert.True(h.Audit.Append(made with { CreatedUtc = new DateTime(2026, 10, 20, 12, 49, 0, DateTimeKind.Utc), Timestamp = new DateTime(2026, 10, 20, 8, 49, 0) }).Succeeded);

            var prepared = await HistoryUndo(h);
            Assert.Equal("Fail", CreatedHere(prepared).Result);
            Assert.Contains("was created at 2026-10-20T13:08:00 UTC", CreatedHere(prepared).Message);
            Assert.Contains("more than 5 minutes apart", CreatedHere(prepared).Message);

            var forced = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };
            var refused = (await h.ApplyAsync(forced)).Single();
            Assert.StartsWith("Skipped: ConfigMgr shows that deployment", refused.Message);
            Assert.Single(h.Site.List(ListOf(type)));
            Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Remove:", StringComparison.Ordinal));
        }
    }

    /// <summary>The one field a forger changes in a copy of a record that matches the script-made deployment.</summary>
    public static TheoryData<DeploymentObjectType, string> OneField()
    {
        var data = new TheoryData<DeploymentObjectType, string>();
        foreach (var type in new[] { DeploymentObjectType.Application, DeploymentObjectType.Package, DeploymentObjectType.TaskSequence, DeploymentObjectType.SoftwareUpdateGroup })
        {
            foreach (var field in new[] { "CreatedUtc-earlier", "CreatedUtc-later", "Timestamp-earlier", "Timestamp-later", "User", "AvailableDateTime", "ObjectId", "CollectionID" })
                data.Add(type, field);
            if (type == DeploymentObjectType.Package) data.Add(type, "ProgramName");
        }
        return data;
    }

    private static string OtherObjectId(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "16779604",
        DeploymentObjectType.Package => "MCM0001C",
        DeploymentObjectType.TaskSequence => "MCM00031",
        _ => "16778894"
    };

    private static DeploymentAuditRecord Forged(DeploymentAuditRecord made, DeploymentObjectType type, string field)
    {
        var shift = TimeSpan.FromHours(4.5);
        return field switch
        {
            "CreatedUtc-earlier" => made with { CreatedUtc = made.CreatedUtc!.Value - shift },
            "CreatedUtc-later" => made with { CreatedUtc = made.CreatedUtc!.Value + shift },
            "Timestamp-earlier" => made with { Timestamp = made.Timestamp - shift },
            "Timestamp-later" => made with { Timestamp = made.Timestamp + shift },
            "User" => made with { User = @"CONTOSO\OtherAdmin" },
            "AvailableDateTime" => made with { AvailableDateTime = made.AvailableDateTime!.Value.AddDays(1) },
            "ObjectId" => made with { ObjectId = OtherObjectId(type) },
            "CollectionID" => made with { CollectionID = "MCM00099" },
            "ProgramName" => made with { ProgramName = "CMCO-CANARY-Repair" },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task ACopyOfTheMatchingRecordWithAValidChainPassesBecauseTheSameAccountCanForgeIt(DeploymentObjectType type)
    {
        var (script, made) = await MadeElsewhere(type, "2026-10-20T12:00:30");
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            Assert.True(h.Audit.Append(made).Succeeded);

            Assert.Equal("Pass", CreatedHere(await HistoryUndo(h)).Result);
        }
    }

    [Theory]
    [MemberData(nameof(OneField))]
    public async Task AForgedRecordWithAValidChainAndOneFieldThatDiffersFromTheSiteIsRefused(DeploymentObjectType type, string field)
    {
        var (script, made) = await MadeElsewhere(type, "2026-10-20T12:00:30");
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            Assert.True(h.Audit.Append(Forged(made, type, field)).Succeeded);

            var prepared = await HistoryUndo(h);
            Assert.Equal("Fail", CreatedHere(prepared).Result);

            var forced = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };
            var refused = (await h.ApplyAsync(forced)).Single();
            Assert.False(refused.Succeeded, refused.Message);
            Assert.StartsWith("Skipped: ", refused.Message);
            Assert.Single(h.Site.List(ListOf(type)));
            Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Remove:", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task TheLabCaseARecordTimeFourAndAHalfHoursBeforeTheCreationIsRefusedNamingBothTimes(DeploymentObjectType type)
    {
        var (script, made) = await MadeElsewhere(type, "2026-10-20T12:00:30");
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            Assert.True(h.Audit.Append(made with { Timestamp = new DateTime(2026, 10, 20, 3, 30, 0) }).Succeeded);

            var check = CreatedHere(await HistoryUndo(h));

            Assert.Equal("Fail", check.Result);
            Assert.Contains("was created at 2026-10-20T12:00:30 UTC", check.Message);
            Assert.Contains("the audit record was written at 2026-10-20T07:30:00 UTC", check.Message);
        }
    }

    [Theory]
    [InlineData("2026-10-20T12:04:30", true)]
    [InlineData("2026-10-20T11:55:30", true)]
    [InlineData("2026-10-20T12:05:30", false)]
    public async Task AGenuineRecordWithinTheToleranceAllowsTheHistoryUndo(string createdUtc, bool allowed)
    {
        using var first = new DeploymentHarness();
        first.Site.SetOption("CreatedUtc", createdUtc);
        Assert.True((await first.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)))).Succeeded);
        using var later = new DeploymentHarness(first.Site.State, auditPath: () => first.AuditPath);

        var prepared = await HistoryUndo(later);

        Assert.Equal(allowed ? "Pass" : "Fail", CreatedHere(prepared).Result);
        var outcome = (await later.ApplyAsync(prepared)).Single();
        Assert.Equal(allowed, outcome.Succeeded);
        Assert.Equal(allowed ? 0 : 1, later.Site.List("Adverts").Count);
    }

    [Fact]
    public async Task ARecordWhoseAccountConfigMgrDoesNotShowAsTheCreatorIsRefused()
    {
        using var first = new DeploymentHarness();
        Assert.True((await first.PrepareAndApplyAsync(Create(App, Options(DeploymentObjectType.Application)))).Succeeded);
        using var later = new DeploymentHarness(first.Site.State, auditPath: () => first.AuditPath);
        later.Stored("AppAssignments")["LastModifiedBy"] = @"CONTOSO\OtherAdmin";

        var check = CreatedHere(await HistoryUndo(later));

        Assert.Equal("Fail", check.Result);
        Assert.Contains(@"ConfigMgr shows that CONTOSO\OtherAdmin created deployment", check.Message);
    }

    [Fact]
    public async Task AnEditedRecordGivesNoHistoryUndoAndTheServiceRefusesItAlsoWhenThePageBuildsIt()
    {
        using var first = new DeploymentHarness();
        Assert.True((await first.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)))).Succeeded);
        File.WriteAllText(first.AuditPath, File.ReadAllText(first.AuditPath).Replace("\"MemberCount\":0", "\"MemberCount\":1", StringComparison.Ordinal));
        using var later = new DeploymentHarness(first.Site.State, auditPath: () => first.AuditPath);
        var record = later.Audit.Read(10).Single();

        Assert.False(record.Verified);
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(record));
        var prepared = await later.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(record with { Verified = null })!);
        Assert.Equal("Fail", CreatedHere(prepared).Result);
        Assert.Contains("cannot be verified", CreatedHere(prepared).Message);
        Assert.StartsWith("Skipped: the audit record of the create of deployment", (await later.ApplyAsync(prepared with { Checks = [] })).Single().Message);
        Assert.Single(later.Site.List("Adverts"));
    }

    [Fact]
    public async Task ARecordAddedByHandWithoutAChainIsRefused()
    {
        var (script, made) = await MadeElsewhere(DeploymentObjectType.Package, "2026-10-20T12:00:30");
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            Directory.CreateDirectory(Path.GetDirectoryName(h.AuditPath)!);
            var line = File.ReadAllLines(script.AuditPath).Single();
            File.WriteAllText(h.AuditPath, line[..line.LastIndexOf(",\"Chain\":", StringComparison.Ordinal)] + "}\r\n");

            var record = h.Audit.Read(10).Single();
            Assert.False(record.Verified);
            var prepared = await h.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(record with { Verified = null })!);
            Assert.Equal("Fail", CreatedHere(prepared).Result);
        }
    }

    [Theory]
    [InlineData(DeploymentObjectType.Package)]
    [InlineData(DeploymentObjectType.TaskSequence)]
    public async Task APurgedStatusMessageRefusesTheHistoryUndo(DeploymentObjectType type)
    {
        using var first = new DeploymentHarness();
        Assert.True((await first.PrepareAndApplyAsync(Create(Of(type), Options(type)))).Succeeded);
        using var later = new DeploymentHarness(first.Site.State, auditPath: () => first.AuditPath);
        later.Site.List("StatusMessages").Clear();

        var prepared = await HistoryUndo(later);

        Assert.Equal("Fail", CreatedHere(prepared).Result);
        Assert.Contains("holds no status message 30006 for deployment", CreatedHere(prepared).Message);
        Assert.Contains("Try again in one minute", CreatedHere(prepared).Message);
        Assert.Single(later.Site.List("Adverts"));
        Assert.Contains(later.Site.Runs, r => (string?)r.Parameters["Operation"] == "Proof" && (int?)r.Parameters["ProofReads"] == 1);
    }

    [Fact]
    public async Task TheInSessionUndoNeedsNoAuditRecord()
    {
        using var h = new DeploymentHarness();
        var created = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)));
        File.WriteAllText(h.AuditPath, "");

        var removed = (await h.ApplyAsync(await h.PrepareAsync(created.UndoChange!))).Single();

        Assert.True(removed.Succeeded, removed.Message);
    }

    // ---- a deployment of the same object and collection that appears during the create ------

    [Theory]
    [MemberData(nameof(MultiTypes))]
    public async Task TheObjectTheCreateCmdletReturnsNamesOurDeploymentAndUndoRemovesOnlyThatOne(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("ForeignOnCreate:" + type, true);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        var ids = h.Site.List(ListOf(type)).Select(d => (string)d![type == DeploymentObjectType.SoftwareUpdateGroup ? "AssignmentUniqueID" : "AdvertisementID"]!).ToList();
        Assert.Equal(2, ids.Count);
        Assert.False(outcome.Succeeded);
        Assert.Equal(ids[0], outcome.UndoChange!.CreatedId);
        Assert.Contains($"The one made here is {ids[0]}; Undo removes only that one.", outcome.Message);
        Assert.Equal(ids[0], h.Audit.Read(10).Single().DeploymentUniqueID);
        Assert.Equal("Partial", h.Audit.Read(10).Single().Result);

        var removed = (await h.ApplyAsync(await h.PrepareAsync(outcome.UndoChange))).Single();
        Assert.True(removed.Succeeded, removed.Message);
        Assert.Equal([ids[1]], h.Site.List(ListOf(type)).Select(d => (string)d![type == DeploymentObjectType.SoftwareUpdateGroup ? "AssignmentUniqueID" : "AdvertisementID"]!));
    }

    [Theory]
    [MemberData(nameof(MultiTypes))]
    public async Task WithoutAReturnedObjectTheCreationTimeOfTheCallNamesOurDeployment(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("ForeignOnCreate:" + type, true);
        h.Site.SetOption("NoReturn", true);
        h.Site.SetOption("CreatedNow", true);
        h.Site.SetOption("ForeignOffsetSeconds", 30);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        var ids = h.Site.List(ListOf(type)).Select(d => (string)d![type == DeploymentObjectType.SoftwareUpdateGroup ? "AssignmentUniqueID" : "AdvertisementID"]!).ToList();
        Assert.Equal(ids[0], outcome.UndoChange?.CreatedId);
        Assert.Contains("Undo removes only that one.", outcome.Message);
    }

    [Theory]
    [MemberData(nameof(MultiTypes))]
    public async Task TwoDeploymentsThatCannotBeToldApartAreNamedWithTheirCreationTimesInTheOutcomeAndTheRecord(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("ForeignOnCreate:" + type, true);
        h.Site.SetOption("NoReturn", true);
        h.Site.SetOption("CreatedNow", true);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        var ids = h.Site.List(ListOf(type)).Select(d => (string)d![type == DeploymentObjectType.SoftwareUpdateGroup ? "AssignmentUniqueID" : "AdvertisementID"]!).ToList();
        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.Contains("2 new deployments appeared: ", outcome.Message);
        foreach (var id in ids) Assert.Matches(System.Text.RegularExpressions.Regex.Escape(id) + @" \(made by .+ at \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC\)", outcome.Message);
        Assert.Contains("cannot tell which one it made", outcome.Message);
        var record = h.Audit.Read(10).Single();
        Assert.Equal("Unknown", record.Result);
        Assert.Equal(string.Join("; ", ids), record.UnclearDeploymentIds);
        Assert.Equal(ids, outcome.UnclearDeploymentIds);
        Assert.Equal("", record.DeploymentUniqueID);
        Assert.Equal(2, h.Site.List(ListOf(type)).Count);
    }

    [Fact]
    public async Task ACreateWhoseClockDoesNotMatchTheSiteLeavesTheResultUnknown()
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("CreateTwice:Package", true);
        h.Site.SetOption("NoReturn", true);

        var outcome = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)));

        Assert.Null(outcome.UndoChange);
        Assert.Contains("(made by ", outcome.Message);
    }

    // ---- a combined run, a second session and the update count ------------------------------

    [Fact]
    public async Task ACombinedRunThatSkipsTheCreateReportsTheDistributionAsAChange()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]![0]!["Content"] = JsonNode.Parse("""{ "Targeted": 1, "Success": 0, "InProgress": 1 }""");
        using var h = new DeploymentHarness(state);

        var outcome = await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package), groups: ["All DPs"]));

        Assert.StartsWith("Skipped: content distribution was requested; the deployment was not created because the content of package", outcome.Message);
        Assert.Null(outcome.UndoChange);
        Assert.Contains("Requested distribution to DP group All DPs.", outcome.Steps);
        var distribution = Assert.IsType<DeploymentOutcome>(outcome.Distribution);
        Assert.True(distribution.Succeeded);
        Assert.Equal(DeploymentChangeKind.Distribute, distribution.Change.Kind);
        Assert.StartsWith("Requested the distribution of package", distribution.Message);
        Assert.Equal([("Distribute", "Success"), ("Create", "Failed")], h.Audit.Read(10).Select(r => (r.Event, r.Result)));
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task ACreateWithoutDistributionCarriesNoDistributionOutcome()
    {
        using var h = new DeploymentHarness();
        Assert.Null((await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)))).Distribution);
    }

    [Fact]
    public async Task ASecondSessionIsToldThatAnotherSessionCreatesForTheSameTarget()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));
        using var firstSessionLock = new DeploymentTargetLock(() => h.AuditPath).TryAcquire("MCM0001B", "MCM00098");
        using var firstSessionLog = h.Audit.Reserve();
        Assert.NotNull(firstSessionLock);

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.StartsWith("Skipped: another session of this product is creating a deployment of the same object to this collection now", outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task TheUpdateCountOfAGroupReachesTheRecordsAndTheHistoryUndo()
    {
        using var first = new DeploymentHarness();
        Assert.True((await first.PrepareAndApplyAsync(Create(UpdateGroup, Options(DeploymentObjectType.SoftwareUpdateGroup)))).Succeeded);
        using var later = new DeploymentHarness(first.Site.State, auditPath: () => first.AuditPath);
        var record = later.Audit.Read(10).Single();
        Assert.Equal("(2 updates)", record.ApplicationVersion);

        var undo = DeploymentChangeBuilder.RemoveFromAudit(record)!;
        Assert.Equal(2, undo.Object!.UpdateCount);
        Assert.True((await later.ApplyAsync(await later.PrepareAsync(undo))).Single().Succeeded);

        Assert.Equal("(2 updates)", later.Audit.Read(10).Last().ApplicationVersion);
        Assert.Equal("Undo", later.Audit.Read(10).Last().Event);
    }
}
