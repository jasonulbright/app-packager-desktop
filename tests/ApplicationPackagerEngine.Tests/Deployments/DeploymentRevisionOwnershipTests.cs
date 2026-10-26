using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Deployments.DeploymentHarness;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The real Deployments script, the real audit log and the stub site with application revisions:
/// an application deployment holds the CI_ID of the latest revision of its model, as on CM01, so
/// a History Undo record must name the revision that was the latest when the deployment was
/// created. The plan names the object ConfigMgr holds, never the one the record names.
/// </summary>
public sealed class DeploymentRevisionOwnershipTests
{
    private const string EarlierRevision = "16779500";
    private const string LaterRevision = "16779700";
    private const string RenamedName = "7-Zip renamed in the console";
    private static readonly DateTime CreatedUtc = new(2026, 10, 20, 12, 0, 30, DateTimeKind.Utc);

    private static string ListOf(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "AppAssignments",
        DeploymentObjectType.SoftwareUpdateGroup => "UgAssignments",
        _ => "Adverts"
    };

    /// <summary>The wall-clock text the provider returns for a UTC time, as the stub stores CreationTime.</summary>
    private static string SiteText(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A create on the site by another program as the same account, with its own audit log.</summary>
    private static async Task<(DeploymentHarness Site, DeploymentAuditRecord Record)> MadeElsewhere(DeploymentObjectType type)
    {
        var script = new DeploymentHarness();
        script.Site.SetOption("CreatedUtc", "2026-10-20T12:00:30");
        var created = await script.PrepareAndApplyAsync(Create(Of(type), Options(type), required: false));
        Assert.True(created.Succeeded, created.Message);
        return (script, script.Audit.Read(10).Single());
    }

    /// <summary>
    /// Revision 12 made before the create and revision 14 made an hour after it (a rename in the
    /// console), so the application holds revisions 12, 13 (deployed) and 14 (latest).
    /// </summary>
    private static void AddRevisions(StubDeploymentSite site)
    {
        var latest = site.List("Applications")[0]!.AsObject();
        var deployed = JsonNode.Parse(latest.ToJsonString())!.AsObject();
        var earlier = JsonNode.Parse(latest.ToJsonString())!.AsObject();
        earlier["CI_ID"] = long.Parse(EarlierRevision, CultureInfo.InvariantCulture);
        earlier["CIVersion"] = 12;
        earlier["DateLastModified"] = SiteText(CreatedUtc.AddDays(-25));
        deployed["DateLastModified"] = SiteText(CreatedUtc.AddDays(-19));
        site.List("Revisions").Add(earlier);
        site.List("Revisions").Add(deployed);
        latest["CI_ID"] = long.Parse(LaterRevision, CultureInfo.InvariantCulture);
        latest["CIVersion"] = 14;
        latest["LocalizedDisplayName"] = RenamedName;
        latest["DateLastModified"] = SiteText(CreatedUtc.AddHours(1));
    }

    private static async Task<DeploymentChange> HistoryUndo(DeploymentHarness h)
    {
        var record = h.Audit.Read(10).Last(r => r.Event == "Create");
        Assert.True(record.Verified);
        return await h.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(record)!);
    }

    private static PreflightCheck CheckOf(DeploymentChange prepared, string name) => prepared.Checks.Single(c => c.Name == name);

    private static async Task AssertRefusedAndKept(DeploymentHarness h, DeploymentChange prepared, DeploymentObjectType type)
    {
        Assert.Equal("Fail", CheckOf(prepared, "CreatedHere").Result);
        Assert.True(prepared.Blocked);
        var forced = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };
        var refused = (await h.ApplyAsync(forced)).Single();
        Assert.False(refused.Succeeded, refused.Message);
        Assert.StartsWith("Skipped: ", refused.Message);
        Assert.Single(h.Site.List(ListOf(type)));
        Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Remove:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLabCaseARecordThatNamesTheRevisionMadeAfterTheCreateIsRefused()
    {
        var (script, made) = await MadeElsewhere(DeploymentObjectType.Application);
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            AddRevisions(h.Site);
            Assert.True(h.Audit.Append(made with { ObjectId = LaterRevision }).Succeeded);

            var prepared = await HistoryUndo(h);

            Assert.Equal("Fail", CheckOf(prepared, "DeploymentUnchanged").Result);
            Assert.Contains($"with revision 16779607 of application {RenamedName} ({LaterRevision}), and the audit record names revision {LaterRevision}, which was made later",
                CheckOf(prepared, "DeploymentUnchanged").Message);
            Assert.Contains($"names revision {LaterRevision}, which was made later", CheckOf(prepared, "CreatedHere").Message);
            Assert.Equal(LaterRevision, prepared.Site!.ObjectId);
            Assert.Equal(RenamedName, prepared.Site.ObjectName);
            Assert.Contains($"of application {RenamedName} ({LaterRevision}) from collection DH-Test-1 (MCM00098).", prepared.Impact[0]);
            await AssertRefusedAndKept(h, prepared, DeploymentObjectType.Application);
        }
    }

    [Fact]
    public async Task TheGenuineRecordAfterANewRevisionIsAcceptedAndThePlanNamesTheObjectTheSiteHolds()
    {
        var (script, made) = await MadeElsewhere(DeploymentObjectType.Application);
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            AddRevisions(h.Site);
            Assert.True(h.Audit.Append(made).Succeeded);

            var prepared = await HistoryUndo(h);

            Assert.Equal("Pass", CheckOf(prepared, "DeploymentUnchanged").Result);
            Assert.Equal("Pass", CheckOf(prepared, "CreatedHere").Result);
            Assert.Equal("7-Zip (x64 edition) - 26.03", made.ApplicationName);
            Assert.Equal(RenamedName, prepared.Site!.ObjectName);
            Assert.DoesNotContain(prepared.Impact, l => l.Contains(made.ApplicationName, StringComparison.Ordinal));
            var (plan, problem) = ApplicationPackagerUI.ViewModels.Pages.DeploymentText.BuildRemovePlan(prepared);
            Assert.Null(problem);
            var item = plan!.Plan.Items.Single();
            Assert.Equal($"{RenamedName} from DH-Test-1", item.Target);
            Assert.Contains($"Object: Application {RenamedName} ({LaterRevision}).", item.Detail);
            Assert.DoesNotContain(made.ApplicationName, item.Detail);
            var removed = (await h.ApplyAsync(prepared)).Single();
            Assert.True(removed.Succeeded, removed.Message);
            Assert.Contains($"of application {RenamedName} ({LaterRevision})", removed.Message);
            Assert.Empty(h.Site.List("AppAssignments"));
        }
    }

    public static TheoryData<DeploymentObjectType, string> ForgedObjectIds()
    {
        var data = new TheoryData<DeploymentObjectType, string>
        {
            { DeploymentObjectType.Application, "another-application" },
            { DeploymentObjectType.Application, "earlier-revision" },
            { DeploymentObjectType.Application, "later-revision" },
            { DeploymentObjectType.Application, "missing" }
        };
        foreach (var type in new[] { DeploymentObjectType.Package, DeploymentObjectType.TaskSequence, DeploymentObjectType.SoftwareUpdateGroup })
        {
            data.Add(type, "another-object");
            data.Add(type, "missing");
        }
        return data;
    }

    private static string ForgedId(DeploymentObjectType type, string kind) => (type, kind) switch
    {
        (DeploymentObjectType.Application, "another-application") => "16779604",
        (DeploymentObjectType.Application, "earlier-revision") => EarlierRevision,
        (DeploymentObjectType.Application, "later-revision") => LaterRevision,
        (DeploymentObjectType.Application, _) => "16799999",
        (DeploymentObjectType.Package, "another-object") => "MCM0001C",
        (DeploymentObjectType.Package, _) => "MCM0FFFF",
        (DeploymentObjectType.TaskSequence, "another-object") => "MCM00031",
        (DeploymentObjectType.TaskSequence, _) => "MCM0FFFE",
        (_, "another-object") => "16778894",
        _ => "16799998"
    };

    [Theory]
    [MemberData(nameof(ForgedObjectIds))]
    public async Task ARecordWithAValidChainAndAnotherObjectIdIsRefusedAndThePlanNamesTheSiteObject(DeploymentObjectType type, string kind)
    {
        var (script, made) = await MadeElsewhere(type);
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            if (type == DeploymentObjectType.Application) AddRevisions(h.Site);
            var forgedId = ForgedId(type, kind);
            Assert.True(h.Audit.Append(made with { ObjectId = forgedId, ApplicationName = "Forged name" }).Succeeded);

            var prepared = await HistoryUndo(h);

            Assert.Equal("Fail", CheckOf(prepared, "DeploymentUnchanged").Result);
            Assert.Equal(kind == "later-revision", forgedId == prepared.Site!.ObjectId);
            Assert.DoesNotContain(prepared.Impact, l => l.Contains("Forged name", StringComparison.Ordinal) || (kind != "later-revision" && l.Contains(forgedId, StringComparison.Ordinal)));
            if (type == DeploymentObjectType.Application && kind != "later-revision")
                Assert.Contains(kind == "earlier-revision" ? "names revision 16779500, an earlier one" : $"names application {forgedId}, which is not a revision of application",
                    CheckOf(prepared, "DeploymentUnchanged").Message);
            await AssertRefusedAndKept(h, prepared, type);
        }
    }

    [Fact]
    public async Task ARecordOfTheDeployedRevisionWithoutANewerOneIsAccepted()
    {
        var (script, made) = await MadeElsewhere(DeploymentObjectType.Application);
        using (script)
        {
            using var h = new DeploymentHarness(script.Site.State);
            Assert.True(h.Audit.Append(made).Succeeded);

            var prepared = await HistoryUndo(h);

            Assert.Equal("Pass", CheckOf(prepared, "DeploymentUnchanged").Result);
            Assert.Equal("Pass", CheckOf(prepared, "CreatedHere").Result);
            Assert.Equal("16779607", prepared.Site!.ObjectId);
        }
    }
}
