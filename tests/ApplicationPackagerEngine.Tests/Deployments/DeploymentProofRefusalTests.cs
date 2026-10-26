using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using static ApplicationPackagerEngine.Tests.Deployments.DeploymentHarness;

namespace ApplicationPackagerEngine.Tests.Deployments;

/// <summary>
/// The real Deployments script, the real audit log and the stub site: each reason that refuses
/// the History Undo of a package or task sequence deployment has its own text, so a refusal never
/// says that the status message 30006 is missing when the site holds it.
/// </summary>
public sealed class DeploymentProofRefusalTests
{
    public static TheoryData<DeploymentObjectType, string> Reasons()
    {
        var data = new TheoryData<DeploymentObjectType, string>();
        foreach (var type in new[] { DeploymentObjectType.Package, DeploymentObjectType.TaskSequence })
            foreach (var reason in new[] { "message-missing", "message-without-account", "message-without-time", "account-differs", "time-outside-tolerance", "values-differ", "not-verified" })
                data.Add(type, reason);
        return data;
    }

    private static string Expected(string reason, string id, DateTime createdUtc) => reason switch
    {
        "message-missing" => $"ConfigMgr holds no status message 30006 for deployment {id}. This message shows who created the deployment and when. The site writes it some seconds after a create and deletes old status messages. It is not removed. Try again in one minute",
        "message-without-account" => $"The status message 30006 of deployment {id} names no account, so it cannot be shown that this product created it.",
        "message-without-time" => $"The status message 30006 of deployment {id} holds no time that can be read, so it cannot be shown that this product created it.",
        "account-differs" => $@"ConfigMgr shows that CONTOSO\OtherAdmin created deployment {id} (status message 30006), and the audit record names",
        "time-outside-tolerance" => $"ConfigMgr shows that deployment {id} was created at 2026-10-20T12:00:30 UTC (status message 30006), and the audit record says the create started at {createdUtc.AddHours(-4.5):yyyy-MM-dd'T'HH:mm:ss} UTC, more than 5 minutes apart",
        "values-differ" => $"Deployment {id} holds other values than the audit record (available time)",
        _ => $"The audit record of the create of deployment {id} cannot be verified"
    };

    private static readonly string[] Others = ["holds no status message 30006", "names no account", "holds no time that can be read", "created deployment", "more than 5 minutes apart", "holds other values", "cannot be verified"];

    [Theory]
    [MemberData(nameof(Reasons))]
    public async Task EachRefusalReasonOfTheStatusMessageProofHasItsOwnText(DeploymentObjectType type, string reason)
    {
        using var first = new DeploymentHarness();
        first.Site.SetOption("CreatedUtc", "2026-10-20T12:00:30");
        Assert.True((await first.PrepareAndApplyAsync(Create(Of(type), Options(type)))).Succeeded);
        var made = first.Audit.Read(10).Single();
        using var later = new DeploymentHarness(first.Site.State);
        var message = later.Site.List("StatusMessages").Single()!.AsObject();
        var record = made;
        switch (reason)
        {
            case "message-missing": later.Site.List("StatusMessages").Clear(); break;
            case "message-without-account": message["User"] = ""; break;
            case "message-without-time": message["Time"] = ""; break;
            case "account-differs": message["User"] = @"CONTOSO\OtherAdmin"; break;
            case "time-outside-tolerance": record = made with { CreatedUtc = made.CreatedUtc!.Value.AddHours(-4.5) }; break;
            case "values-differ": record = made with { AvailableDateTime = made.AvailableDateTime!.Value.AddDays(1) }; break;
        }
        Assert.True(later.Audit.Append(record).Succeeded);
        if (reason == "not-verified")
            File.WriteAllText(later.AuditPath, File.ReadAllText(later.AuditPath).Replace("\"MemberCount\":0", "\"MemberCount\":1", StringComparison.Ordinal));
        var read = later.Audit.Read(10).Single();

        var prepared = await later.PrepareAsync(DeploymentChangeBuilder.RemoveFromAudit(read with { Verified = reason == "not-verified" ? null : read.Verified })!);

        var check = prepared.Checks.Single(c => c.Name == "CreatedHere");
        Assert.Equal("Fail", check.Result);
        var expected = Expected(reason, made.DeploymentUniqueID, made.CreatedUtc!.Value);
        Assert.StartsWith(expected, check.Message);
        var own = Others.Single(o => expected.Contains(o, StringComparison.Ordinal) && (o != "created deployment" || reason == "account-differs"));
        Assert.All(Others.Where(o => o != own), o => Assert.DoesNotContain(o, check.Message));
        Assert.Single(later.Site.List("Adverts"));
    }
}
