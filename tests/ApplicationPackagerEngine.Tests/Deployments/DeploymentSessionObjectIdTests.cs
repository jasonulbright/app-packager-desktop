using System.Globalization;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using static ApplicationPackagerEngine.Tests.Deployments.DeploymentHarness;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// A History Undo in the session that created the deployment: the deployment ID alone does not
/// prove the object, so an audit record with a valid Chain and another ObjectId is refused. The
/// site holds the values read from CM01 for a throwaway application: four revisions of one model
/// that share DateCreated, each with its own DateLastModified, and a deployment made on revision 3
/// whose AssignedCIs moves to revision 4 when that revision is added.
/// </summary>
public sealed class DeploymentSessionObjectIdTests
{
    private const string Model = "16779741";
    private const string ModelName = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_06d83ccb-0ad1-4311-aa2f-2ec9c6b25ce5";
    private const string AppName = "DH-Test-10201537-App";
    private const string Revision1 = "16780818";
    private const string Revision2 = "16780821";
    private const string Deployed = "16780825";
    private const string Later = "16780833";
    private const string OtherApplication = "16780828";
    private static readonly DateTime ModelCreated = Utc(19, 37, 59);
    private static readonly DateTime DeploymentCreated = Utc(19, 40, 38);
    private static readonly DateTime LaterModified = Utc(19, 41, 50);

    private static DateTime Utc(int h, int m, int s) => new(2026, 10, 20, h, m, s, DateTimeKind.Utc);

    private static string SiteText(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static JsonObject Revision(string ciId, int version, DateTime modified) => new()
    {
        ["CI_ID"] = long.Parse(ciId, CultureInfo.InvariantCulture), ["ModelID"] = long.Parse(Model, CultureInfo.InvariantCulture), ["ModelName"] = ModelName,
        ["LocalizedDisplayName"] = AppName, ["SoftwareVersion"] = "1.0", ["IsExpired"] = false, ["CIVersion"] = version,
        ["DateCreated"] = SiteText(ModelCreated), ["DateLastModified"] = SiteText(modified),
        ["ContentPackageID"] = "MCM0008B", ["Content"] = new JsonObject { ["Targeted"] = 1, ["Success"] = 1 }
    };

    private static JsonObject Cm01()
    {
        var state = StubDeploymentSite.Lab();
        var apps = state["Applications"]!.AsArray();
        apps.Insert(0, Revision(Deployed, 3, Utc(19, 38, 28)));
        var other = Revision(OtherApplication, 1, Utc(19, 38, 31));
        other["ModelID"] = 16779744L;
        other["ModelName"] = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_5b8a0f39-0f0e-4e0d-9a43-0b3c4f1a7a10";
        other["LocalizedDisplayName"] = "DH-Test-10201537-Other";
        apps.Add(other);
        state["Revisions"] = new JsonArray(Revision(Revision1, 1, ModelCreated), Revision(Revision2, 2, Utc(19, 38, 22)));
        state["Options"]!["CreatedUtc"] = "2026-10-20T19:40:38";
        return state;
    }

    private static DeployableObject Application => new(DeploymentObjectType.Application, Deployed, AppName, "1.0", "", false, Model);

    /// <summary>Revision 4 made after the create: revision 3 moves to the earlier revisions.</summary>
    private static void AddLaterRevision(StubDeploymentSite site)
    {
        var apps = site.List("Applications");
        var latest = apps.Single(a => a!["CI_ID"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture) == Deployed)!;
        apps.Remove(latest);
        site.List("Revisions").Add(latest);
        apps.Insert(0, Revision(Later, 4, LaterModified));
    }

    private static async Task<(DeploymentHarness H, DeploymentAuditRecord Created)> CreatedInSession(DeploymentObjectType type)
    {
        var h = new DeploymentHarness(Cm01());
        var obj = type == DeploymentObjectType.Application ? Application : Of(type);
        var created = await h.PrepareAndApplyAsync(Create(obj, Options(type)));
        Assert.True(created.Succeeded, created.Message);
        if (type == DeploymentObjectType.Application) AddLaterRevision(h.Site);
        return (h, h.Audit.Read(10).Single(r => r.Event == AuditEvents.Create));
    }

    private static async Task<DeploymentChange> HistoryUndo(DeploymentHarness h, DeploymentAuditRecord record)
    {
        Assert.True(h.Audit.Append(record with { Verified = null }).Succeeded);
        var read = h.Audit.Read(10).Last(r => r.Event == AuditEvents.Create);
        Assert.True(read.Verified);
        Assert.Equal(record.ObjectId, read.ObjectId);
        return await h.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(read)!);
    }

    private static PreflightCheck CheckOf(DeploymentChange prepared, string name) => prepared.Checks.Single(c => c.Name == name);

    private static string ListOf(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "AppAssignments",
        DeploymentObjectType.SoftwareUpdateGroup => "UgAssignments",
        _ => "Adverts"
    };

    public static TheoryData<DeploymentObjectType, string, string> Forged() => new()
    {
        { DeploymentObjectType.Application, OtherApplication, $"names application {OtherApplication}, which is not a revision of application {AppName} ({Later})" },
        { DeploymentObjectType.Application, "99999999", $"names application 99999999, which is not a revision of application {AppName} ({Later})" },
        { DeploymentObjectType.Application, Revision2, $"with revision {Deployed} of application {AppName} ({Later}), and the audit record names revision {Revision2}, an earlier one" },
        { DeploymentObjectType.Application, Later, $"and the audit record names revision {Later}, which was made later, at 2026-10-20T19:41:50 UTC" },
        { DeploymentObjectType.Package, "MCM0008D", "changed since it was captured (object)" },
        { DeploymentObjectType.Package, "ZZZ99999", "changed since it was captured (object)" },
        { DeploymentObjectType.TaskSequence, "MCM0008F", "changed since it was captured (object)" },
        { DeploymentObjectType.TaskSequence, "ZZZ99999", "changed since it was captured (object)" },
        { DeploymentObjectType.SoftwareUpdateGroup, "16780832", "changed since it was captured (object)" },
        { DeploymentObjectType.SoftwareUpdateGroup, "99999999", "changed since it was captured (object)" }
    };

    [Theory]
    [MemberData(nameof(Forged))]
    public async Task ARecordWithAnotherObjectIdIsRefusedInTheSessionThatCreatedTheDeployment(DeploymentObjectType type, string forgedId, string reason)
    {
        var (h, made) = await CreatedInSession(type);
        using (h)
        {
            var prepared = await HistoryUndo(h, made with { ObjectId = forgedId });

            Assert.True(prepared.Blocked);
            Assert.Equal("Fail", CheckOf(prepared, "DeploymentUnchanged").Result);
            var createdHere = CheckOf(prepared, "CreatedHere");
            Assert.Equal("Fail", createdHere.Result);
            Assert.Contains(reason, createdHere.Message);
            Assert.EndsWith("so it is not removed.", createdHere.Message);
            var site = prepared.Site!;
            Assert.Equal(type == DeploymentObjectType.Application ? Later : made.ObjectId, site.ObjectId);
            Assert.Contains($"({site.ObjectId})", prepared.Impact[0]);
            if (forgedId != site.ObjectId) Assert.DoesNotContain(forgedId, prepared.Impact[0]);

            var forced = prepared with { Checks = prepared.Checks.Select(c => c with { Result = "Pass" }).ToList() };
            var refused = (await h.ApplyAsync(forced)).Single();
            Assert.False(refused.Succeeded, refused.Message);
            Assert.StartsWith("Skipped: ", refused.Message);
            Assert.Contains(reason, refused.Message);
            Assert.Single(h.Site.List(ListOf(type)));
            Assert.DoesNotContain(h.Site.Log, l => l.StartsWith("Remove:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheGenuineRecordAfterANewRevisionIsAcceptedInTheSessionAndThePlanNamesTheSiteObject()
    {
        var (h, made) = await CreatedInSession(DeploymentObjectType.Application);
        using (h)
        {
            Assert.Equal(Deployed, made.ObjectId);
            var prepared = await HistoryUndo(h, made);

            Assert.False(prepared.Blocked, string.Join(" ", prepared.Checks.Select(c => c.Message)));
            Assert.Equal("this product created the deployment in this session.", CheckOf(prepared, "CreatedHere").Message.ToLowerInvariant());
            Assert.Equal(Later, prepared.Site!.ObjectId);
            Assert.Contains($"of application {AppName} ({Later}) from collection DH-Test-1 (MCM00098).", prepared.Impact[0]);

            var removed = (await h.ApplyAsync(prepared)).Single();
            Assert.True(removed.Succeeded, removed.Message);
            Assert.Contains($"of application {AppName} ({Later})", removed.Message);
            Assert.Empty(h.Site.List("AppAssignments"));
        }
    }

    [Theory]
    [InlineData(OtherApplication)]
    [InlineData("99999999")]
    [InlineData(Later)]
    public async Task TheRemoveScriptRefusesARevisionThatIsNotOfTheModelOrIsLaterThanTheDeployment(string objectId)
    {
        var (h, made) = await CreatedInSession(DeploymentObjectType.Application);
        using (h)
        {
            var output = await h.Site.InvokeAsync(ConfigMgrScript.FromResource(DeploymentService.ScriptResource), new ConfigMgrParameters
            {
                { "Operation", "Remove" }, { "Type", "Application" }, { "DeploymentId", made.DeploymentUniqueID }, { "ObjectId", objectId }, { "ModelId", Model },
                { "CollectionId", "MCM00098" }, { "Purpose", "Available" }, { "Available", "2026-10-21T09:00:00" }, { "Deadline", "" }, { "Utc", false }
            });

            var result = output.Output.Single(e => e.TryGetProperty("Kind", out var k) && k.GetString() == "Result");
            Assert.Equal("changed", result.GetProperty("Status").GetString());
            Assert.Contains("Object", result.GetProperty("Differences").EnumerateArray().Select(d => d.GetString()));
            Assert.Single(h.Site.List("AppAssignments"));
        }
    }

    [Fact]
    public async Task TheRemoveScriptRemovesTheDeploymentForTheRevisionItWasMadeWith()
    {
        var (h, made) = await CreatedInSession(DeploymentObjectType.Application);
        using (h)
        {
            var output = await h.Site.InvokeAsync(ConfigMgrScript.FromResource(DeploymentService.ScriptResource), new ConfigMgrParameters
            {
                { "Operation", "Remove" }, { "Type", "Application" }, { "DeploymentId", made.DeploymentUniqueID }, { "ObjectId", Deployed }, { "ModelId", Model },
                { "CollectionId", "MCM00098" }, { "Purpose", "Available" }, { "Available", "2026-10-21T09:00:00" }, { "Deadline", "" }, { "Utc", false }
            });

            var result = output.Output.Single(e => e.TryGetProperty("Kind", out var k) && k.GetString() == "Result");
            Assert.Equal("ok", result.GetProperty("Status").GetString());
            Assert.Empty(h.Site.List("AppAssignments"));
        }
    }
}
