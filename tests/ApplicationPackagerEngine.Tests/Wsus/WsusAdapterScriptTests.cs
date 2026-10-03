using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Wsus;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>
/// The shipped adapter script under Windows PowerShell 5.1: it parses, it
/// takes every value from its request file, it refuses on a computer without
/// the WSUS console with the console install text, and its operations produce
/// the result shape the client reads when a stand-in replaces the server.
/// </summary>
public class WsusAdapterScriptTests : IDisposable
{
    private const string ApiMissing = "The WSUS administration API is not installed on this computer. Install the WSUS console: on Windows 10 or 11, Settings > System > Optional features > RSAT: Windows Server Update Services Tools (Add-WindowsCapability -Online -Name Rsat.WSUS.Tools~~~~0.0.1.0); on Windows Server, Install-WindowsFeature UpdateServices-UI.";

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static bool WsusApiInstalled() =>
        File.Exists(Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Update Services\Api\Microsoft.UpdateServices.Administration.dll"))
        || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\assembly\GAC_MSIL\Microsoft.UpdateServices.Administration"));

    [Fact]
    public async Task ParsesUnderTheWindowsPowerShell51ParserWithoutErrors()
    {
        var adapter = Path.Combine(_temp.Path, "WsusAdapter.ps1");
        File.WriteAllText(adapter, WsusAdapterRunner.ScriptText(), new UTF8Encoding(true));
        const string script = """
            param([string]$Path)
            $tokens = $null; $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
            Write-Output ('PSVersion ' + $PSVersionTable.PSVersion.Major + '.' + $PSVersionTable.PSVersion.Minor)
            Write-Output ('Errors ' + @($errors).Count)
            foreach ($e in @($errors)) { Write-Output ($e.Extent.StartLineNumber.ToString() + ': ' + $e.Message) }
            """;
        var run = await WindowsPowerShellHost.RunAsync(script, null, default, "ap-wsus-test", ["-Path", adapter]);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("PSVersion 5.1", run.StdOut);
        Assert.Contains("Errors 0", run.StdOut);
    }

    [Fact]
    public void CarriesNoStringBuiltCommandsAndReadsEveryValueFromItsRequest()
    {
        var text = WsusAdapterRunner.ScriptText();
        Assert.DoesNotContain("Invoke-Expression", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?i)\biex\b", text);
        Assert.DoesNotMatch(@"(?i)\[scriptblock\]::Create|NewScriptBlock|Invoke-Command|Add-Type|Start-Process|\.InvokeScript\(", text);
        Assert.DoesNotMatch(@"&\s*\$", text);
        // The only parameter is the request path; every other value is a property of the request.
        Assert.Matches(@"(?m)^param\(\[string\]\$InputPath = ''\)\r?$", text);
        Assert.Single(Regex.Matches(text, @"(?m)^param\("));
        Assert.Contains("[System.IO.File]::ReadAllText($InputPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json", text);
    }

    [Fact]
    public async Task RefusesOnAComputerWithoutTheWsusConsoleWithTheConsoleInstallText()
    {
        var runner = new WsusAdapterRunner();
        if (WsusApiInstalled())
        {
            Assert.True((await new WsusAdminClient(runner).GetStatusAsync(new WsusSettings { ServerName = "wsus01.contoso.com" })).Api.Available);
            return;
        }
        var groups = await runner.RunAsync("Groups", WsusAdminClient.Request(new WsusSettings { ServerName = "wsus01.contoso.com" }.Normalize()));
        Assert.False(groups.Ok);
        Assert.Equal(WsusErrorKinds.ApiMissing, groups.ErrorKind);
        Assert.Equal(ApiMissing, groups.Error);

        var status = await new WsusAdminClient(runner).GetStatusAsync(new WsusSettings { ServerName = "wsus01.contoso.com" });
        Assert.False(status.Connected);
        Assert.False(status.Api.Available);
        Assert.Equal(ApiMissing, status.Api.Reason);
        Assert.Equal(ApiMissing, status.Message);
    }

    [Fact]
    public async Task RefusesARequestWithoutAServerAndAnUnknownOperation()
    {
        var runner = new WsusAdapterRunner();
        var noServer = await runner.RunAsync("Groups", new JsonObject { ["Settings"] = new JsonObject { ["ServerName"] = "" } });
        Assert.Equal(WsusErrorKinds.Input, noServer.ErrorKind);
        Assert.Equal("No WSUS server is configured.", noServer.Error);
        var unknown = await runner.RunAsync("Format-Disk", new JsonObject());
        Assert.Equal(WsusErrorKinds.Input, unknown.ErrorKind);
        Assert.Equal("Operation 'Format-Disk' is not known.", unknown.Error);
    }

    [Fact]
    public async Task TakesAServerNameWithQuotesAndSemicolonsAsDataOnly()
    {
        var marker = Path.Combine(_temp.Path, "injected.txt");
        var hostile = $"x'; Set-Content -Path '{marker}' -Value 1; '\"; $(Set-Content -Path '{marker}' -Value 2)";
        JsonObject Hostile() => new() { ["Settings"] = new JsonObject { ["ServerName"] = hostile, ["PortNumber"] = 8530, ["UseSsl"] = false } };
        var (result, _) = await StandIn("Default", "Groups", Hostile());
        Assert.True(result.Ok, result.Error);
        var (refused, _) = await StandIn("Reporter", "SetState",
            AddTo(Hostile(), "Action", "Decline", "PackageIds", new JsonArray("bbbbbbbb-0000-0000-0000-000000000002")));
        Assert.EndsWith($"on WSUS {hostile}. Add it to the WSUS Administrators group on the server.", refused.Error);
        Assert.False(File.Exists(marker));
    }

    private async Task<(WsusAdapterResult Result, IReadOnlyList<string> Log)> StandIn(string scenario, string operation, JsonObject request, string? stdin = null)
    {
        var adapter = Path.Combine(_temp.Path, "WsusAdapter.ps1");
        File.WriteAllText(adapter, WsusAdapterRunner.ScriptText(), new UTF8Encoding(true));
        var requestPath = Path.Combine(_temp.Path, $"request-{Guid.NewGuid():N}.json");
        var resultPath = Path.Combine(_temp.Path, $"result-{Guid.NewGuid():N}.json");
        var document = (JsonObject)request.DeepClone();
        document["Operation"] = operation;
        document["ResultPath"] = resultPath;
        File.WriteAllText(requestPath, document.ToJsonString(), new UTF8Encoding(false));
        var harness = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WsusAdapter", "Invoke-AdapterWithStandIns.ps1"));
        var run = await WindowsPowerShellHost.RunAsync(harness, null, default, "ap-wsus-test",
            ["-AdapterPath", adapter, "-RequestPath", requestPath, "-Scenario", scenario], stdin ?? "");
        var result = WsusAdapterRunner.ReadResult(resultPath, run.ExitCode, run.StdErr);
        var log = File.Exists(resultPath)
            ? WsusAdminClient.Strings(JsonNode.Parse(File.ReadAllText(resultPath))!["StandInLog"])
            : [];
        return (result, log);
    }

    private static JsonObject Request(string server = "wsus01.contoso.com") =>
        WsusAdminClient.Request(new WsusSettings { ServerName = server }.Normalize());

    [Fact]
    public async Task ReturnsGroupNamesSortedOnceEachAsAnArrayEvenForOneGroup()
    {
        var (many, _) = await StandIn("Default", "Groups", Request());
        Assert.True(many.Ok);
        Assert.Equal(["All Computers", "Pilot"], WsusAdminClient.Strings(many.Data!["Names"]));
        var (one, _) = await StandIn("OneGroup", "Groups", Request());
        Assert.IsType<JsonArray>(one.Data!["Names"]);
        Assert.Equal(["Pilot"], WsusAdminClient.Strings(one.Data["Names"]));
    }

    [Fact]
    public async Task ReadsThePfxPasswordFromStandardInputAndRefusesAnInsecureConnection()
    {
        var pfx = Path.Combine(_temp.Path, "signing.pfx");
        File.WriteAllText(pfx, "stand-in");
        var request = Request();
        request["PfxPath"] = pfx;
        var (result, log) = await StandIn("Default", "ImportPfx", request, "pa'ss; word");
        Assert.True(result.Ok, result.Error);
        Assert.Equal([$"pfx {pfx} password pa'ss; word", "save"], log);

        var (insecure, insecureLog) = await StandIn("Insecure", "ImportPfx", request, "pa'ss; word");
        Assert.Equal(WsusErrorKinds.InsecureConnection, insecure.ErrorKind);
        Assert.StartsWith("Importing a PFX sends its private key and password to the server inside the API call, and this connection is not secure.", insecure.Error);
        Assert.Empty(insecureLog);
    }

    [Fact]
    public async Task NamesTheAccountRoleWhenTheAccountIsNotAWsusAdministrator()
    {
        var (result, _) = await StandIn("Reporter", "SetState", AddTo(Request(), "Action", "Decline", "PackageIds", new JsonArray("bbbbbbbb-0000-0000-0000-000000000002")));
        Assert.Equal(WsusErrorKinds.NotAdministrator, result.ErrorKind);
        Assert.Matches(@"^The account .*\\.* has the role Reporter on WSUS wsus01\.contoso\.com\. Add it to the WSUS Administrators group on the server\.$", result.Error);
    }

    [Fact]
    public async Task ReportsAConnectionFailureByKindSoTheClientAddsTheHint()
    {
        var (result, _) = await StandIn("ConnectFails", "Groups", Request());
        Assert.Equal(WsusErrorKinds.Connect, result.ErrorKind);
        Assert.Equal("The request failed with HTTP status 404: Not Found.", result.Error);
        Assert.Equal("Connecting to WSUS wsus01.contoso.com:8530 failed: The request failed with HTTP status 404: Not Found.. " + WsusMessages.ConsoleVersionHint,
            WsusMessages.Compose(result, new WsusSettings { ServerName = "wsus01.contoso.com" }.Normalize()));
    }

    [Fact]
    public async Task RemovesEachUpdateOnItsOwnDecliningBeforeDeleting()
    {
        var request = AddTo(Request(), "Action", "Remove", "PackageIds", new JsonArray(
            "bbbbbbbb-0000-0000-0000-000000000001", "bbbbbbbb-0000-0000-0000-000000000002",
            "bbbbbbbb-0000-0000-0000-000000000003", "bbbbbbbb-0000-0000-0000-000000000004"));
        var (result, log) = await StandIn("Default", "SetState", request);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(["delete bbbbbbbb-0000-0000-0000-000000000001", "decline Tool 2", "delete bbbbbbbb-0000-0000-0000-000000000002", "decline Tool 4"], log);
        var rows = WsusAdminClient.ReadStateResults(result.Data!["Results"] as JsonArray, WsusUpdateActions.Remove);
        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].Ok);
        Assert.Contains("package folder on the server was not deleted: Access is denied", rows[0].Message);
        Assert.Contains("no update with id", rows[2].Message);
        Assert.False(rows[3].Ok);
        Assert.Matches("still referenced.*Expire this update instead.*The update is now declined\\.$", rows[3].Message);
    }

    [Fact]
    public async Task AppliesTheApprovalAndTheDeclinesToAnExistingUpdateAndReportsEachOutcome()
    {
        var request = Request();
        request["ExistingPackageId"] = "bbbbbbbb-0000-0000-0000-000000000002";
        request["ApprovalGroup"] = "Pilto";
        request["DeclinePackageIds"] = new JsonArray("bbbbbbbb-0000-0000-0000-000000000004");
        var (result, log) = await StandIn("Default", "Publish", request);
        Assert.True(result.Ok, result.Error);
        Assert.False(WsusAdminClient.Bool(result.Data, "Published"));
        Assert.True(WsusAdminClient.Bool(result.Data, "UpdateVisible"));
        var approval = result.Data!["Approval"]!.AsObject();
        Assert.False(WsusAdminClient.Bool(approval, "Ok"));
        Assert.Equal("WSUS has no computer group named 'Pilto'.", WsusAdminClient.Str(approval, "Message"));
        var decline = Assert.Single(result.Data["Declines"]!.AsArray())!.AsObject();
        Assert.True(WsusAdminClient.Bool(decline, "Ok"));
        Assert.Equal(["decline Tool 4"], log);
    }

    [Fact]
    public async Task ListsUpdatesAsAnArrayWithTheDescriptionTheClientParses()
    {
        var (result, _) = await StandIn("Default", "List", Request());
        Assert.True(result.Ok, result.Error);
        Assert.IsType<JsonArray>(result.Data!["Rows"]);
        var rows = WsusAdminClient.ReadPublishedUpdates(result.Data["Rows"] as JsonArray, false);
        var row = Assert.Single(rows);
        Assert.Equal("AppPackager:catalog:package-tool/default", row.IdentityTag);
        Assert.Equal("2", row.Version);
        Assert.NotNull(row.Created);
    }

    [Fact]
    public async Task ImportsCatalogUpdatesOneByOne()
    {
        var request = Request();
        request["UpdateIds"] = new JsonArray("12345678-90ab-cdef-1234-567890abcdef", "00000000-0000-0000-0000-00000000dead");
        var (result, _) = await StandIn("Default", "CatalogImport", request);
        var rows = result.Data!["Results"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.True(WsusAdminClient.Bool(rows[0], "Ok"));
        Assert.Equal("Update not found in the catalog", WsusAdminClient.Str(rows[1], "Message"));
    }

    [Fact]
    public async Task RefusesAPayloadPathOutsideTheContentFolderBeforeBuildingAPackage()
    {
        var request = Request();
        request["ContentFolder"] = _temp.Path;
        request["PayloadFiles"] = new JsonArray(@"..\outside.txt");
        request["Plan"] = new JsonObject { ["PackageId"] = Guid.NewGuid().ToString() };
        var (result, _) = await StandIn("Default", "Publish", request);
        Assert.Equal(WsusErrorKinds.Input, result.ErrorKind);
        Assert.Equal(@"The staged file path '..\outside.txt' points outside the content folder.", result.Error);
    }

    private static JsonObject AddTo(JsonObject request, string key1, string value1, string key2, JsonNode value2)
    {
        request[key1] = value1;
        request[key2] = value2;
        return request;
    }
}
