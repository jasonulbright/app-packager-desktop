using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

public class WsusAdminClientTests
{
    private static readonly WsusSettings Settings = new() { ServerName = "wsus01.contoso.com" };
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Row(string id, string title, string description, string created, bool declined = false, bool superseded = false,
        string[]? groups = null, string state = "Published") => new()
    {
        ["PackageId"] = id, ["Title"] = title, ["Description"] = description, ["Created"] = created,
        ["ApprovedGroups"] = new JsonArray((groups ?? []).Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()), ["ApprovalsRead"] = true,
        ["Declined"] = declined, ["Superseded"] = superseded, ["PublicationState"] = state
    };

    [Fact]
    public async Task ListsAppPackagerUpdatesWithTheirApprovalGroupsNewestFirst()
    {
        var line = WsusIdentity.IdentityLine("AppPackager:catalog:package-tool/default", "Update");
        var runner = new FakeAdapterRunner((op, _, _) => FakeAdapterRunner.Ok(new JsonObject
        {
            ["Rows"] = new JsonArray(
                Row("aaaaaaaa-0000-0000-0000-000000000002", "Tool 1", line, "2026-08-20T00:00:00.0000000Z", declined: true, superseded: true),
                Row(Guid.NewGuid().ToString(), "Foreign", "Published elsewhere", "2026-09-25T00:00:00.0000000Z"),
                Row("aaaaaaaa-0000-0000-0000-000000000001", "Tool 2", line, "2026-09-20T00:00:00.0000000Z", groups: ["Pilot"]))
        }));
        var client = new WsusAdminClient(runner);
        var rows = await client.ListAsync(Settings);
        Assert.Equal(["Tool 2", "Tool 1"], rows.Select(r => r.Title));
        Assert.Equal("Pilot", rows[0].ApprovedGroups);
        Assert.Equal("Update", rows[0].PackageType);
        Assert.True(rows[1].Declined);
        Assert.Equal(3, (await client.ListAsync(Settings, includeOtherPublishers: true)).Count);
        Assert.Equal("List", runner.Calls[0].Operation);
    }

    [Fact]
    public async Task ImportsCatalogUpdatesByIdAndReportsFailuresWithoutThrowing()
    {
        var bad = Guid.Parse("00000000-0000-0000-0000-00000000dead");
        var good = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
        var runner = new FakeAdapterRunner((op, request, _) => FakeAdapterRunner.Ok(new JsonObject
        {
            ["Results"] = new JsonArray((request["UpdateIds"] as JsonArray)!.Select(n => (JsonNode)new JsonObject
            {
                ["UpdateId"] = n!.GetValue<string>(),
                ["Ok"] = n.GetValue<string>() != bad.ToString(),
                ["Message"] = n.GetValue<string>() == bad.ToString() ? "Update not found in the catalog" : ""
            }).ToArray())
        }));
        var results = await new WsusAdminClient(runner).ImportCatalogUpdatesAsync(Settings, [good, bad]);
        Assert.True(results[0].Ok);
        Assert.False(results[1].Ok);
        Assert.Contains("not found in the catalog", results[1].Message);
        Assert.DoesNotContain("SchUseStrongCrypto", results[1].Message);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void NamesTheWsusServerTls12SettingWhenTheServerCannotReachTheCatalog()
    {
        var message = WsusMessages.CatalogImportFailure("The request was aborted: Could not create SSL/TLS secure channel.");
        Assert.Contains("Could not create SSL/TLS secure channel", message);
        Assert.Contains("On the WSUS server, set SchUseStrongCrypto = 1", message);
    }

    [Fact]
    public async Task ACatalogImportWithoutAServerConnectionReportsEveryIdAsFailed()
    {
        var runner = new FakeAdapterRunner((_, _, _) => new WsusAdapterResult(false, WsusErrorKinds.Unreachable, "WSUS server wsus01.contoso.com did not answer on port 8530 within 5 seconds.", null));
        var results = await new WsusAdminClient(runner).ImportCatalogUpdatesAsync(Settings, [Guid.NewGuid(), Guid.NewGuid()]);
        Assert.All(results, r => Assert.Contains("did not answer", r.Message));
    }

    [Fact]
    public async Task ReportsAnUnreachableServerAsNotConnectedInsteadOfThrowing()
    {
        var runner = new FakeAdapterRunner((_, _, _) => FakeAdapterRunner.Ok(new JsonObject
        {
            ["Api"] = new JsonObject { ["Available"] = true }, ["Connected"] = false, ["ServerName"] = "wsus01.contoso.com",
            ["ConnectErrorKind"] = WsusErrorKinds.Unreachable, ["ConnectError"] = "WSUS server wsus01.contoso.com did not answer on port 8530 within 5 seconds."
        }));
        var status = await new WsusAdminClient(runner).GetStatusAsync(Settings);
        Assert.False(status.Connected);
        Assert.Contains("did not answer", status.Message);
    }

    [Fact]
    public async Task ReportsTheAccountRoleWithTheServerStatus()
    {
        var runner = new FakeAdapterRunner((_, _, _) => FakeAdapterRunner.Ok(new JsonObject
        {
            ["Api"] = new JsonObject { ["Available"] = true, ["Version"] = "10.0.0.0" }, ["Connected"] = true, ["ServerName"] = "wsus01.contoso.com",
            ["Version"] = "10.0.26100.1", ["Role"] = "Reporter", ["SecureConnection"] = true, ["SigningCertificate"] = null
        }));
        var status = await new WsusAdminClient(runner, () => Now).GetStatusAsync(Settings);
        Assert.True(status.Connected);
        Assert.Equal("Reporter", status.Role);
        Assert.True(status.SecureConnection);
        Assert.Contains("WSUS Administrators", status.Message);
        Assert.Null(status.SigningCertificate);
    }

    [Fact]
    public void ReadsTheCertificateAndItsExpiry()
    {
        var data = new JsonObject
        {
            ["Connected"] = true, ["ServerName"] = "wsus01", ["Role"] = "Administrator",
            ["SigningCertificate"] = new JsonObject
            {
                ["Subject"] = "CN=WSUS Publishers Self-signed", ["Issuer"] = "CN=WSUS Publishers Self-signed", ["Thumbprint"] = "AB12",
                ["NotBefore"] = "2024-01-01T00:00:00.0000000Z", ["NotAfter"] = "2026-10-01T00:00:00.0000000Z", ["KeyLength"] = 2048
            }
        };
        var status = WsusAdminClient.ReadStatus(data, Settings, Now);
        Assert.True(status.SigningCertificate!.SelfSigned);
        Assert.True(status.SigningCertificateExpired);
        Assert.Equal(2048, status.SigningCertificate.KeyLength);
        Assert.Equal("", status.Message);
    }

    [Fact]
    public void ComposesTheConnectionFailureWithTheTlsHintOnlyForAnSslFailure()
    {
        var ssl = new WsusSettings { ServerName = "wsus01", UseSsl = true, PortNumber = 8531 }.Normalize();
        Assert.Equal("Connecting to WSUS wsus01:8531 failed: Could not establish trust relationship for the SSL/TLS secure channel. " + WsusMessages.TlsHint,
            WsusMessages.ConnectFailure(ssl, "Could not establish trust relationship for the SSL/TLS secure channel"));
        Assert.EndsWith(WsusMessages.ConsoleVersionHint, WsusMessages.ConnectFailure(Settings.Normalize(), "The request failed with HTTP status 404"));
    }

    [Fact]
    public async Task DeclinesBeforeDeletingKeepsTheGivenOrderAndReportsEachUpdateOnItsOwn()
    {
        var old = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        var newer = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var missing = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
        var runner = new FakeAdapterRunner((op, request, _) => FakeAdapterRunner.Ok(new JsonObject
        {
            ["Results"] = new JsonArray(
                new JsonObject { ["PackageId"] = old.ToString(), ["Title"] = "Tool 1", ["Found"] = true, ["Ok"] = true, ["Message"] = "", ["DeclinedNow"] = false, ["FolderError"] = "" },
                new JsonObject { ["PackageId"] = newer.ToString(), ["Title"] = "Tool 2", ["Found"] = true, ["Ok"] = true, ["Message"] = "", ["DeclinedNow"] = true, ["FolderError"] = "" },
                new JsonObject { ["PackageId"] = missing.ToString(), ["Title"] = "", ["Found"] = false, ["Ok"] = false })
        }));
        var results = await new WsusAdminClient(runner).SetStateAsync(Settings, [old, newer, missing], WsusUpdateActions.Remove);
        Assert.Equal([old.ToString(), newer.ToString(), missing.ToString()],
            (runner.Calls[0].Request["PackageIds"] as JsonArray)!.Select(n => n!.GetValue<string>()));
        Assert.Equal(2, results.Count(r => r.Ok));
        Assert.Contains("no update with id", results.Single(r => r.Id == missing.ToString()).Message);
    }

    [Fact]
    public void ExplainsARemovalThatWsusRefusesBecauseAnotherUpdateReferencesIt()
    {
        var results = WsusAdminClient.ReadStateResults(new JsonArray(new JsonObject
        {
            ["PackageId"] = Guid.NewGuid().ToString(), ["Title"] = "Tool 1", ["Found"] = true, ["Ok"] = false,
            ["Message"] = "This update is still referenced by at least another update in the database.", ["DeclinedNow"] = false
        }), WsusUpdateActions.Remove);
        Assert.Matches("still referenced.*Expire this update instead, or remove the update that references it first", results[0].Message);
        Assert.DoesNotContain("now declined", results[0].Message);
    }

    [Fact]
    public void SaysSoWhenARefusedRemovalLeavesTheUpdateDeclined()
    {
        var results = WsusAdminClient.ReadStateResults(new JsonArray(new JsonObject
        {
            ["PackageId"] = Guid.NewGuid().ToString(), ["Title"] = "Tool 1", ["Found"] = true, ["Ok"] = false,
            ["Message"] = "This update is still referenced by at least another update in the database.", ["DeclinedNow"] = true
        }), WsusUpdateActions.Remove);
        Assert.EndsWith("The update is now declined.", results[0].Message);
    }

    [Fact]
    public void KeepsTheRemovalWhenThePackageFolderCannotGo()
    {
        var results = WsusAdminClient.ReadStateResults(new JsonArray(new JsonObject
        {
            ["PackageId"] = Guid.NewGuid().ToString(), ["Title"] = "Tool", ["Found"] = true, ["Ok"] = true, ["Message"] = "", ["FolderError"] = "Access is denied"
        }), WsusUpdateActions.Remove);
        Assert.True(results[0].Ok);
        Assert.Contains("package folder on the server was not deleted: Access is denied", results[0].Message);
    }

    [Fact]
    public void ExplainsASecondExpiry()
    {
        var results = WsusAdminClient.ReadStateResults(new JsonArray(new JsonObject
        {
            ["PackageId"] = Guid.NewGuid().ToString(), ["Title"] = "Tool", ["Found"] = true, ["Ok"] = false,
            ["Message"] = "Cannot revise or expire a package that has already been expired."
        }), WsusUpdateActions.Expire);
        Assert.Matches("already been expired\\. Only a locally published update that is not already expired", results[0].Message);
    }

    [Fact]
    public async Task RefusesAnApprovalWithoutAGroupBeforeAnyServerCall()
    {
        var runner = new FakeAdapterRunner((_, _, _) => throw new InvalidOperationException("no call expected"));
        var ex = await Assert.ThrowsAsync<WsusOperationException>(() =>
            new WsusAdminClient(runner).SetStateAsync(Settings, [Guid.NewGuid()], WsusUpdateActions.Approve));
        Assert.Contains("needs a computer group", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ExplainsWhyTheServerDidNotCreateASelfSignedCertificate()
    {
        var runner = new FakeAdapterRunner((_, _, _) => new WsusAdapterResult(false, WsusErrorKinds.SelfSignedRefused, "Access is denied", null));
        var ex = await Assert.ThrowsAsync<WsusOperationException>(() => new WsusAdminClient(runner).CreateSelfSignedCertificateAsync(Settings));
        Assert.StartsWith("WSUS did not create a self-signed certificate: Access is denied. ", ex.Message);
        Assert.Contains("EnableSelfSignedCertificates = 1", ex.Message);
    }

    [Fact]
    public async Task SendsOnlyServerConnectionValuesInEveryRequest()
    {
        var runner = new FakeAdapterRunner((_, _, _) => FakeAdapterRunner.Ok(new JsonObject { ["Names"] = new JsonArray("All Computers", "Pilot") }));
        var names = await new WsusAdminClient(runner).GetComputerGroupNamesAsync(new WsusSettings { ServerName = " wsus01 ", UseSsl = true, PortNumber = 0, ApprovalGroup = "Pilot" });
        Assert.Equal(["All Computers", "Pilot"], names);
        var settings = runner.Calls[0].Request["Settings"]!.AsObject();
        Assert.Equal(["ServerName", "PortNumber", "UseSsl"], settings.Select(p => p.Key));
        Assert.Equal("wsus01", settings["ServerName"]!.GetValue<string>());
        Assert.Equal(8531, settings["PortNumber"]!.GetValue<int>());
    }
}
