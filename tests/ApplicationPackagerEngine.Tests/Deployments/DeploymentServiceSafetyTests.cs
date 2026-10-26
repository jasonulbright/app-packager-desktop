using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using static ApplicationPackagerEngine.Tests.Deployments.DeploymentHarness;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The real Deployments script through the stub site: ownership proof after a failed create,
/// stale plans, the signing rule by the site's name, the target lock, concurrent deployments,
/// audit failures, sub-second times, cut lists and the Undo built from an audit record.
/// </summary>
public sealed class DeploymentServiceSafetyTests
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

    private static DeploymentService Service(DeploymentHarness h, IDeploymentAuditLog? audit = null, IAppLog? log = null, int maxRows = 1000, IDeploymentSigningCheck? signing = null) =>
        new(h.Site, h.Collections, audit ?? h.Audit, signing, log, () => UtcNow, Local) { MaxListRows = maxRows };

    private static async Task<DeploymentOutcome> Apply(DeploymentService service, DeploymentChange change) =>
        (await service.ApplyAsync([change], new Progress<DeploymentOutcome>(), CancellationToken.None)).Single();

    // ---- a create that threw is claimed only with a proven creation time and account ------

    [Theory]
    [MemberData(nameof(Types))]
    public async Task AnErrorAfterTheCreateKeepsTheUndoWhenTheCreationTimeAndAccountProveTheDeployment(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.Fail("after-create:" + type);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        Assert.False(outcome.Succeeded);
        Assert.Contains("then reported an error", outcome.Message);
        Assert.NotNull(outcome.UndoChange);
        Assert.Equal("Partial", h.Audit.Read(10).Single().Result);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task AnErrorAfterTheCreateGivesNoUndoWhenAnotherAccountMadeTheNewDeployment(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("CreatedBy", @"CONTOSO\OtherAdmin");
        h.Site.Fail("after-create:" + type);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.StartsWith("The result of the create", outcome.Message);
        Assert.Contains("cannot be shown to be made by this account during this run", outcome.Message);
        Assert.Equal("Unknown", h.Audit.Read(10).Single().Result);
        Assert.Single(h.Site.List(ListOf(type)));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task AnErrorAfterTheCreateGivesNoUndoWhenTheDeploymentIsOlderThanTheRun(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("CreatedUtc", "2026-10-20T11:50:00");
        h.Site.Fail("after-create:" + type);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        Assert.Null(outcome.UndoChange);
        Assert.StartsWith("The result of the create", outcome.Message);
    }

    [Theory]
    [InlineData(DeploymentObjectType.Package)]
    [InlineData(DeploymentObjectType.TaskSequence)]
    public async Task AnErrorAfterTheCreateOfAnAdvertisementGivesNoUndoWithoutItsStatusMessage(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("NoStatus", true);
        h.Site.Fail("after-create:" + type);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        Assert.Null(outcome.UndoChange);
        Assert.StartsWith("The result of the create", outcome.Message);
        Assert.Equal(14, h.Site.Log.Count(l => l == "Sleep"));
    }

    [Theory]
    [InlineData(DeploymentObjectType.Package, true)]
    [InlineData(DeploymentObjectType.Package, false)]
    [InlineData(DeploymentObjectType.TaskSequence, true)]
    [InlineData(DeploymentObjectType.Application, true)]
    [InlineData(DeploymentObjectType.Application, false)]
    public async Task ASessionFailureDuringTheCreateClaimsTheDeploymentOnlyWithTheProof(DeploymentObjectType type, bool proven)
    {
        using var h = new DeploymentHarness();
        if (!proven) h.Site.SetOption(type == DeploymentObjectType.Application ? "CreatedBy" : "NoStatus", type == DeploymentObjectType.Application ? @"CONTOSO\OtherAdmin" : true);
        var prepared = await h.PrepareAsync(Create(Of(type), Options(type)));
        h.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "timeout") : null;

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.False(outcome.Succeeded);
        Assert.Equal(proven, outcome.UndoChange is not null);
        Assert.Equal(proven ? "Partial" : "Unknown", h.Audit.Read(10).Single().Result);
    }

    // ---- times finer than a second ---------------------------------------------------------

    [Fact]
    public async Task PlannedTimesWithMillisecondsAreCutToTheSecondAndStillEqualTheStoredOnes()
    {
        using var h = new DeploymentHarness();
        var options = Options(DeploymentObjectType.Application, "Required") with
        {
            AvailableTime = new DateTime(2026, 10, 21, 9, 0, 0).AddMilliseconds(731),
            Deadline = new DateTime(2026, 10, 23, 17, 30, 0).AddMilliseconds(250)
        };
        var prepared = await h.PrepareAsync(Create(App, options, required: true));
        Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0), prepared.Options!.AvailableTime);

        var outcome = (await h.ApplyAsync(prepared with { Options = options })).Single();

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(new DateTime(2026, 10, 23, 17, 30, 0), outcome.UndoChange!.Existing!.Deadline);
        Assert.True((await h.ApplyAsync(outcome.UndoChange)).Single().Succeeded);
    }

    [Fact]
    public async Task AReconcileWithMillisecondsInThePlanClaimsTheDeployment()
    {
        using var h = new DeploymentHarness();
        var options = Options(DeploymentObjectType.SoftwareUpdateGroup) with { AvailableTime = new DateTime(2026, 10, 21, 9, 0, 0).AddMilliseconds(500) };
        var prepared = await h.PrepareAsync(Create(UpdateGroup, options));
        h.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "timeout") : null;

        var outcome = (await h.ApplyAsync(prepared with { Options = options })).Single();

        Assert.NotNull(outcome.UndoChange);
    }

    // ---- create now while the content distributes ------------------------------------------

    [Fact]
    public async Task CreateNowIsRefusedWithoutADistributionInThePlan()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));

        var noGroups = (await h.ApplyAsync(prepared with { CreateNowContentDistributing = true })).Single();
        var notInProgress = (await h.ApplyAsync(prepared with { CreateNowContentDistributing = true, DpGroups = ["All DPs"] })).Single();

        Assert.Equal(DeploymentService.SkippedPrefix + DeploymentService.CreateNowWithoutDistributionMessage, noGroups.Message);
        Assert.Equal(DeploymentService.SkippedPrefix + DeploymentService.CreateNowWithoutDistributionMessage, notInProgress.Message);
        Assert.Equal(0, h.CreateCalls);
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Start-CMContentDistribution", StringComparison.Ordinal));
    }

    // ---- audit failures -------------------------------------------------------------

    [Fact]
    public async Task AnAuditAppendThatThrowsKeepsTheDeploymentIdAndTheUndo()
    {
        using var h = new DeploymentHarness();
        var service = Service(h, new ScriptedAudit(h.Audit, _ => new InvalidOperationException("broken")));
        var prepared = await service.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)), CancellationToken.None);

        var outcome = await Apply(service, prepared);

        Assert.True(outcome.Succeeded);
        var id = (string)h.Stored("Adverts")["AdvertisementID"]!;
        Assert.Equal(id, outcome.UndoChange!.CreatedId);
        Assert.Contains("The audit record could not be written", outcome.Message);
        Assert.Contains($"Write down the deployment ID {id}", outcome.Message);
        Assert.DoesNotContain("broken", outcome.Message);
    }

    [Fact]
    public async Task AFailureWhileTheResultIsProcessedKeepsTheUndo()
    {
        using var h = new DeploymentHarness();
        h.Site.Fail("after-create:Package");
        var service = Service(h, log: new ThrowingLog("did not finish after ConfigMgr made a change"));
        var prepared = await service.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)), CancellationToken.None);

        var outcome = await Apply(service, prepared);

        Assert.False(outcome.Succeeded);
        Assert.Contains("its result could not be processed", outcome.Message);
        Assert.Equal((string)h.Stored("Adverts")["AdvertisementID"]!, outcome.UndoChange!.CreatedId);
        Assert.Equal("Partial", h.Audit.Read(10).Single().Result);
    }

    [Fact]
    public async Task ADistributionThatCannotBeLoggedStopsBeforeTheCreate()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]![0]!["Content"] = JsonNode.Parse("""{ "Targeted": 1, "Success": 0, "InProgress": 1 }""");
        using var h = new DeploymentHarness(state);
        var service = Service(h, new ScriptedAudit(h.Audit, n => n == 0 ? new IOException("full") : null));
        var prepared = await service.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package), groups: ["All DPs"], createNow: true), CancellationToken.None);

        var outcome = await Apply(service, prepared);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.UndoChange);
        Assert.StartsWith("The content distribution may have been requested but could not be logged", outcome.Message);
        Assert.Contains("Start-CMContentDistribution:All DPs", h.Site.Log);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public void TheAuditLogReportsAWriteFailureOfAnyKind()
    {
        using var h = new DeploymentHarness();
        var record = DeploymentAuditLogTests.Record() with { Message = new string('x', 10) };
        using var reservation = h.Audit.Reserve();
        Assert.True(reservation.Probe.Writable);
        reservation.Dispose();

        var write = reservation.Append(record);

        Assert.False(write.Succeeded);
        Assert.StartsWith("The audit record could not be written", write.Message);
    }

    // ---- a stale plan ----------------------------------------------------------------------

    [Fact]
    public async Task ANewApplicationRevisionMakesThePlanStaleAndKeepsThePlannedId()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)));
        Assert.Equal("16779607", prepared.Prepared!.ObjectId);
        h.Site.List("Applications")[0]!["CI_ID"] = 16779700;

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.StartsWith("Skipped: the plan is stale, refresh the plan: ", outcome.Message);
        Assert.Contains("16779700", outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task AChangedRevisionOrContentMakesThePlanStale()
    {
        using var h = new DeploymentHarness();
        var package = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));
        var ts = await h.PrepareAsync(Create(TaskSequence, Options(DeploymentObjectType.TaskSequence)));
        var sug = await h.PrepareAsync(Create(UpdateGroup, Options(DeploymentObjectType.SoftwareUpdateGroup)));
        h.Site.List("Packages")[0]!["SourceVersion"] = 2;
        h.Site.List("TaskSequences")[0]!["References"]!.AsArray().Add(JsonNode.Parse("""{ "RefPackageID": "MCM00035", "ObjectName": "Other", "Targeted": 1, "Success": 1 }"""));
        h.Site.List("Groups")[0]!["CIVersion"] = 3;

        var outcomes = await h.ApplyAsync(package, ts, sug);

        Assert.All(outcomes, o => Assert.StartsWith("Skipped: the plan is stale, refresh the plan: ", o.Message));
        Assert.Contains("revision none is now 2", outcomes[0].Message);
        Assert.Contains("MCM0001B is now MCM0001B, MCM00035", outcomes[1].Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Theory]
    [InlineData("Required", 5, false)]
    [InlineData("Required", 0, true)]
    [InlineData("Available", 5, true)]
    public async Task MoreMembersMakeARequiredPlanStaleOnly(string purpose, int membersAtApply, bool created)
    {
        var state = StubDeploymentSite.Lab();
        state["Collections"]![0]!["MemberCount"] = 2;
        using var h = new DeploymentHarness(state);
        var prepared = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package, purpose), required: purpose == "Required"));
        Assert.Equal(2, prepared.Prepared!.MemberCount);
        h.Site.List("Collections")[0]!["MemberCount"] = membersAtApply;

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.Equal(created, outcome.Succeeded);
        if (!created) Assert.StartsWith("Skipped: the plan is stale, refresh the plan: the collection has 5 members now and had 2", outcome.Message);
    }

    [Fact]
    public async Task ACreateWithoutThePreparedStateIsRefused()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)));

        var outcome = (await h.ApplyAsync(prepared with { Prepared = null })).Single();

        Assert.Equal(DeploymentService.SkippedPrefix + DeploymentService.NotPreparedMessage, outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    // ---- the signing rule by the name the site holds ---------------------------------------

    [Fact]
    public async Task TheSigningRuleUsesTheNameTheSiteHoldsAtApply()
    {
        var signing = new NamedSigning(App.Name);
        using var h = new DeploymentHarness(signing: signing);
        h.Site.List("Applications")[0]!["LocalizedDisplayName"] = "Renamed before";
        var prepared = await h.PrepareAsync(Create(App with { Name = "Renamed before" }, Options(DeploymentObjectType.Application, "Required"), required: true));
        Assert.Equal("Pass", prepared.Checks.Single(c => c.Name == "Signing").Result);
        h.Site.List("Applications")[0]!["LocalizedDisplayName"] = App.Name;

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.Equal("Skipped: refused " + App.Name, outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task TheSigningRuleStillAppliesWhenTheApplicationWasRenamedAfterThePlan()
    {
        using var h = new DeploymentHarness(signing: new NamedSigning(App.Name, allowAtPrepare: true));
        var prepared = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application, "Required"), required: true));
        Assert.Equal("Pass", prepared.Checks.Single(c => c.Name == "Signing").Result);
        h.Site.List("Applications")[0]!["LocalizedDisplayName"] = "Renamed in the console";

        var outcome = (await h.ApplyAsync(prepared)).Single();

        Assert.Equal("Skipped: refused " + App.Name, outcome.Message);
        Assert.Equal(0, h.CreateCalls);
    }

    [Fact]
    public async Task ARequiredDeploymentOfAnApplicationWhoseNameIsNotUniqueIsRefused()
    {
        var state = StubDeploymentSite.Lab();
        state["Applications"]![1]!["LocalizedDisplayName"] = App.Name;
        using var h = new DeploymentHarness(state, signing: new NamedSigning("nothing packaged"));

        var prepared = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application, "Required"), required: true));
        var check = prepared.Checks.Single(c => c.Name == "Signing");
        Assert.Equal("Fail", check.Result);
        Assert.StartsWith("2 applications on the site are named", check.Message);
        var forged = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };

        Assert.StartsWith("Skipped: 2 applications on the site are named", (await h.ApplyAsync(forged)).Single().Message);
        Assert.Equal("Pass", (await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)))).Checks.Single(c => c.Name == "Signing").Result);
        Assert.Equal(0, h.CreateCalls);
    }

    // ---- concurrent creates ----------------------------------------------------------------

    [Fact]
    public async Task ACreateIsRefusedWhileAnotherSessionHoldsTheTargetLock()
    {
        using var h = new DeploymentHarness();
        var prepared = await h.PrepareAsync(Create(App, Options(DeploymentObjectType.Application)));
        var path = DeploymentTargetLock.LockPath(h.AuditPath, "M16778866", "MCM00098");

        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            var refused = (await h.ApplyAsync(prepared)).Single();
            Assert.Equal(DeploymentService.SkippedPrefix + DeploymentService.TargetBusyMessage, refused.Message);
        }
        Assert.Equal(0, h.CreateCalls);

        Assert.True((await h.ApplyAsync(prepared)).Single().Succeeded);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(DeploymentObjectType.Package)]
    [InlineData(DeploymentObjectType.TaskSequence)]
    [InlineData(DeploymentObjectType.SoftwareUpdateGroup)]
    public async Task ADeploymentAnotherAdminMadeAtTheSameTimeIsReportedAndUndoRemovesOnlyOurs(DeploymentObjectType type)
    {
        using var h = new DeploymentHarness();
        h.Site.SetOption("ForeignOnReadBack:" + type, true);

        var outcome = await h.PrepareAndApplyAsync(Create(Of(type), Options(type)));

        Assert.False(outcome.Succeeded);
        var ours = outcome.UndoChange!.CreatedId!;
        var key = type == DeploymentObjectType.SoftwareUpdateGroup ? "AssignmentUniqueID" : "AdvertisementID";
        var foreign = h.Site.List(ListOf(type)).Select(d => (string)d![key]!).Single(id => id != ours);
        Assert.Contains("A second deployment of the same object to this collection exists (made by someone else at the same time): " + foreign, outcome.Message);
        Assert.Contains($"The one made here is {ours}", outcome.Message);
        Assert.Equal("Partial", h.Audit.Read(10).Single().Result);

        Assert.True((await h.ApplyAsync(await h.PrepareAsync(outcome.UndoChange))).Single().Succeeded);
        Assert.Equal(foreign, (string)h.Site.List(ListOf(type)).Single()![key]!);
    }

    // ---- cut lists -------------------------------------------------------------------------

    [Fact]
    public async Task ListsCutAtTheRowLimitSaySoAndTheReconcileReadsEveryRow()
    {
        var state = StubDeploymentSite.Lab();
        state["Packages"]!.AsArray().Add(JsonNode.Parse("""{ "PackageID": "MCM0001C", "Name": "Second", "Version": "1", "Programs": [ "Run" ], "Content": { "Targeted": 1, "Success": 1 } }"""));
        using var h = new DeploymentHarness(state);
        var service = Service(h, maxRows: 1);

        var objects = Assert.IsType<DeploymentList<DeployableObject>>(await service.ListObjectsAsync(DeploymentObjectType.Package, CancellationToken.None));
        Assert.True(objects.Truncated);
        Assert.Single(objects);
        Assert.Equal("The package list shows the first 1 of 2 rows. Use the console for the rest.", objects.Note);

        Assert.True((await Apply(service, await service.PrepareAsync(Create(Package with { Program = "CMCO-CANARY-RunAvailable" }, Options(DeploymentObjectType.Package)), CancellationToken.None))).Succeeded);
        var prepared = await service.PrepareAsync(Create(Package, Options(DeploymentObjectType.Package)), CancellationToken.None);
        h.Site.AfterRun = p => (string?)p["Operation"] == "Create" ? new ConfigMgrSessionException(ConfigMgrFailure.Timeout, "timeout") : null;
        var claimed = await Apply(service, prepared);
        h.Site.AfterRun = null;
        Assert.NotNull(claimed.UndoChange);

        var deployments = Assert.IsType<DeploymentList<DeploymentRecord>>(await service.ListDeploymentsAsync(Package, CancellationToken.None));
        Assert.True(deployments.Truncated);
        Assert.False(Assert.IsType<DeploymentList<DeployableObject>>(await h.Service.ListObjectsAsync(DeploymentObjectType.Package, CancellationToken.None)).Truncated);
    }

    // ---- Undo built from the audit record --------------------------------------------------

    public static TheoryData<DeploymentObjectType, string, bool> AuditCases => new()
    {
        { DeploymentObjectType.Application, "Required", false },
        { DeploymentObjectType.Package, "Available", false },
        { DeploymentObjectType.TaskSequence, "Required", false },
        { DeploymentObjectType.SoftwareUpdateGroup, "Available", false },
        { DeploymentObjectType.SoftwareUpdateGroup, "Required", true },
        { DeploymentObjectType.Application, "Available", true }
    };

    [Theory]
    [MemberData(nameof(AuditCases))]
    public async Task TheAuditRecordOfACreateHoldsEverythingItsUndoNeedsInANewSession(DeploymentObjectType type, string purpose, bool utc)
    {
        using var h = new DeploymentHarness();
        var created = await h.PrepareAndApplyAsync(Create(Of(type), Options(type, purpose, utc), required: purpose == "Required"));
        Assert.True(created.Succeeded, created.Message);
        var record = h.Audit.Read(10).Single();
        Assert.Equal(created.UndoChange!.CreatedId, record.DeploymentUniqueID);
        Assert.Equal(utc ? "Utc" : "LocalTime", record.TimeBasedOn);
        Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0), record.AvailableDateTime);
        Assert.Equal(type == DeploymentObjectType.Application ? "16778866" : "", record.ModelId);

        var undo = DeploymentChangeBuilder.RemoveFromAudit(record)!;
        Assert.Equal(created.UndoChange.Existing! with { CollectionName = "DH-Test-1", Created = undo.Existing!.Created, CreatedBy = undo.Existing.CreatedBy, ObjectName = undo.Existing.ObjectName }, undo.Existing);
        var later = Service(h);
        var prepared = await later.PrepareAsync(undo, CancellationToken.None);
        Assert.False(prepared.Blocked, string.Join(" | ", prepared.Checks.Select(c => $"{c.Name}:{c.Result}:{c.Message}")));

        var removed = await Apply(later, prepared);

        Assert.True(removed.Succeeded, removed.Message);
        Assert.Empty(h.Site.List(ListOf(type)));
    }

    [Fact]
    public async Task AnUndoFromTheAuditRecordOfOneProgramKeepsTheOtherProgram()
    {
        using var h = new DeploymentHarness();
        Assert.True((await h.PrepareAndApplyAsync(Create(Package, Options(DeploymentObjectType.Package)))).Succeeded);
        Assert.True((await h.PrepareAndApplyAsync(Create(Package with { Program = "CMCO-CANARY-RunAvailable" }, Options(DeploymentObjectType.Package)))).Succeeded);
        var record = h.Audit.Read(10)[0];
        Assert.Equal("CMCO-CANARY-Run", record.ProgramName);

        var removed = await Apply(Service(h), await Service(h).PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(record)!, CancellationToken.None));

        Assert.True(removed.Succeeded, removed.Message);
        Assert.Equal("CMCO-CANARY-RunAvailable", (string)Assert.Single(h.Site.List("Adverts"))!["ProgramName"]!);
    }

    [Fact]
    public void OnlyACreateOfThisProductThatMadeADeploymentGivesAnUndo()
    {
        var create = DeploymentAuditLogTests.Record() with
        {
            Event = "Create", Result = "Success", DeploymentType = "Package", ObjectId = "MCM0001B", CollectionID = "MCM00098", DeploymentUniqueID = "MCM20020",
            DeployPurpose = "Available", AvailableDateTime = new DateTime(2026, 10, 21, 9, 0, 0), TimeBasedOn = "LocalTime", ProgramName = "Run"
        };
        Assert.NotNull(DeploymentChangeBuilder.RemoveFromAudit(create));
        Assert.NotNull(DeploymentChangeBuilder.RemoveFromAudit(create with { Result = "Partial" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { Event = "Undo" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { Result = "Failed" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { Result = "Unknown" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { DeploymentUniqueID = "" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { AvailableDateTime = null }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { ProgramName = "" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { TimeBasedOn = "" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { DeployPurpose = "Required" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { DeploymentType = "Application", DeploymentUniqueID = "16777301" }));
        Assert.Null(DeploymentChangeBuilder.RemoveFromAudit(create with { CollectionID = "MCM0009" }));
    }

    // ---- Fakes ---------------------------------------------------------------------------------

    /// <summary>Refuses a Required deployment of the named application only.</summary>
    private sealed class NamedSigning(string packaged, bool allowAtPrepare = false) : IDeploymentSigningCheck
    {
        private int _calls;
        public IReadOnlySet<string> PackagedApplications() => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { packaged };
        public string? RequiredDeploymentRefusal(string applicationName)
        {
            var first = _calls++ == 0;
            if (allowAtPrepare && first) return null;
            return string.Equals(applicationName, packaged, StringComparison.OrdinalIgnoreCase) ? "refused " + applicationName : null;
        }
    }

    /// <summary>The real log with the appends a function picks failing with an exception.</summary>
    private sealed class ScriptedAudit(DeploymentAuditLog inner, Func<int, Exception?> fail) : IDeploymentAuditLog
    {
        private int _appends;
        private Exception? Next() => fail(_appends++);
        public AuditProbe Probe() => inner.Probe();
        public AuditWrite Append(DeploymentAuditRecord r) => inner.Append(r);
        public IReadOnlyList<DeploymentAuditRecord> Read(int max) => inner.Read(max);
        public string Path => inner.Path;
        public IDeploymentAuditReservation Reserve() => new Reservation(this, inner.Reserve());

        private sealed class Reservation(ScriptedAudit owner, IDeploymentAuditReservation held) : IDeploymentAuditReservation
        {
            public AuditProbe Probe => held.Probe;
            public AuditWrite Append(DeploymentAuditRecord r) => owner.Next() is { } ex ? throw ex : held.Append(r);
            public void Dispose() => held.Dispose();
        }
    }

    /// <summary>A log whose warning with the given text throws.</summary>
    private sealed class ThrowingLog(string text) : IAppLog
    {
        public string? SessionFile => null;
        public AppLogLevel MinimumLevel { get; set; }
        public event EventHandler<AppLogEntry>? Written { add { } remove { } }
        public void Write(AppLogLevel level, string area, string message)
        {
            if (message.Contains(text, StringComparison.Ordinal)) throw new InvalidOperationException("log failed");
        }
        public void RegisterSecret(string value) { }
        public IReadOnlyList<AppLogEntry> Tail() => [];
    }
}
