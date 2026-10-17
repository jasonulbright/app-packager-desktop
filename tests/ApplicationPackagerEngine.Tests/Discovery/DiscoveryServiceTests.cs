using System.Text.Json;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Discovery;

public class DiscoveryServiceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Records each script and answers the site-definition script and the inventory script.</summary>
    private sealed class FakeRunner
    {
        public List<string> Scripts { get; } = [];
        public string SiteOutput { get; set; } = "{\"t\":\"site\",\"found\":true,\"server\":\"CM01.contoso.com\",\"database\":\"CM_MCM\",\"via\":\"Dcom\"}\n{\"t\":\"end\"}";
        public string InventoryOutput { get; set; } = Inventory();
        public Func<CancellationToken, Task>? Delay { get; set; }

        public async Task<(int, string, string)> Run(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            if (Delay is not null) await Delay(ct);
            var site = script.Contains("Invoke-DiscoverySiteDefinition -Provider", StringComparison.Ordinal);
            return (0, site ? SiteOutput : InventoryOutput, "raw stderr text 0x80070005");
        }
    }

    private static string Inventory(params string[] extra) => string.Join("\n", new[]
    {
        "{\"t\":\"row\",\"n\":\"7-Zip 26.03 (x64 edition)\",\"p\":\"Igor Pavlov\",\"v\":\"26.03.00.0\",\"a\":\"x64\",\"c\":[0,1]}",
        "{\"t\":\"row\",\"n\":\"7-Zip 26.03\",\"p\":\"Igor Pavlov\",\"v\":\"26.03\",\"a\":\"x86\",\"c\":[1,2]}",
        "{\"t\":\"row\",\"n\":\"Git\",\"p\":\"The Git Development Community\",\"v\":\"2.56.0\",\"a\":\"x64\",\"c\":[3]}",
        "{\"t\":\"row\",\"n\":\"grepWin x64\",\"p\":\"Stefans Tools\",\"v\":\"2.1.1434\",\"a\":\"x64\",\"c\":[3]}",
        "{\"t\":\"summary\",\"clients\":4,\"newest\":\"2026-10-15T02:55:30.000\"}",
        "{\"t\":\"app\",\"n\":\"Git 2.56.0 (x64)\"}",
        "{\"t\":\"app\",\"n\":\"CMTrace Open\"}",
    }.Concat(extra).Append("{\"t\":\"end\"}"));

    private static readonly DiscoveryPackager[] Packagers =
    [
        new("7z", "7-Zip", "7-Zip (x64)", "7-Zip", "7-Zip [0-9]*", null, null),
        new("git", "Git", "Git for Windows (x64)", "Git", "Git", null, null),
        new("grep", "Stefans Tools", "grepWin (x64)", "grepWin", "grepWin*", null, null),
        new("vlc", "VideoLAN", "VLC (x64)", "VLC media player", "VLC media player*", null, null),
    ];

    private static DiscoveryRequest Request(string site = "MCM", string provider = "CM01.contoso.com", string server = "", string database = "",
        int timeout = 600, IReadOnlyCollection<string>? hidden = null, IReadOnlyList<DiscoveryPackager>? packagers = null) =>
        new(site, provider, server, database, packagers ?? Packagers, hidden ?? ["7z", "git", "grep", "vlc"], [], timeout);

    private static (DiscoveryService Service, FakeRunner Runner) Service()
    {
        var runner = new FakeRunner();
        return (new DiscoveryService(runner.Run, new FixedTime(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero))), runner);
    }

    [Fact]
    public async Task AScanReturnsTheCandidatesTheClientTotalAndTheStatus()
    {
        var (service, runner) = Service();
        var result = await service.ScanAsync(Request(), CancellationToken.None);
        Assert.Equal(
            [new DiscoveryCandidate("7z", "7-Zip", "7-Zip", "7-Zip (x64)", 3), new DiscoveryCandidate("grep", "grepWin", "Stefans Tools", "grepWin (x64)", 1)],
            result.Candidates);
        Assert.Equal(4, result.ClientTotal);
        Assert.Equal(new DateTime(2026, 10, 15, 2, 55, 30), result.InventoryNewest);
        Assert.Equal("Inventory covers 4 clients; newest data is 2 days old. Discovered 2 apps.", result.StatusText);
        Assert.Equal(2, runner.Scripts.Count);
    }

    [Fact]
    public async Task BothOverridesSkipTheSmsProviderAndNeedNoSiteCode()
    {
        var (service, runner) = Service();
        await service.ScanAsync(Request(site: "", provider: "", server: " sql01.contoso.com ", database: " CM_ALT "), CancellationToken.None);
        var script = Assert.Single(runner.Scripts);
        Assert.StartsWith("$DiscoveryServer = 'sql01.contoso.com'" + Environment.NewLine + "$DiscoveryDatabase = 'CM_ALT'", script);
    }

    [Fact]
    public async Task TheSiteDefinitionSuppliesTheServerAndDatabase()
    {
        var (service, runner) = Service();
        var target = await service.ResolveTargetAsync(Request(site: "mcm"), CancellationToken.None);
        Assert.Equal(new DiscoveryDatabaseTarget("CM01.contoso.com", "CM_MCM"), target);
        Assert.Contains("$DiscoveryProvider = 'CM01.contoso.com'", runner.Scripts[0]);
        Assert.Contains("$DiscoverySiteCode = 'MCM'", runner.Scripts[0]);
    }

    [Fact]
    public async Task ANamedInstanceMovesFromTheDatabaseNameToTheServer()
    {
        var (service, runner) = Service();
        runner.SiteOutput = "{\"t\":\"site\",\"found\":true,\"server\":\"SQL01.contoso.com\",\"database\":\"CMINST\\\\CM_ABC\"}\n{\"t\":\"end\"}";
        Assert.Equal(new DiscoveryDatabaseTarget(@"SQL01.contoso.com\CMINST", "CM_ABC"),
            await service.ResolveTargetAsync(Request(site: "ABC"), CancellationToken.None));
    }

    [Fact]
    public async Task TheServerOverrideWinsOverTheSiteDefinition()
    {
        var (service, _) = Service();
        Assert.Equal(new DiscoveryDatabaseTarget("aglistener.contoso.com", "CM_MCM"),
            await service.ResolveTargetAsync(Request(server: "aglistener.contoso.com"), CancellationToken.None));
    }

    [Fact]
    public async Task TheDatabaseDefaultsToCMSiteCodeWhenTheLookupFailsAndAServerOverrideExists()
    {
        var (service, runner) = Service();
        runner.SiteOutput = "{\"t\":\"error\",\"stage\":\"site\"}\n{\"t\":\"end\"}";
        Assert.Equal(new DiscoveryDatabaseTarget("sql01", "CM_MCM"),
            await service.ResolveTargetAsync(Request(site: "mcm", server: "sql01"), CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"t\":\"error\",\"stage\":\"site\"}\n{\"t\":\"end\"}", "the SMS Provider CM01.contoso.com did not return the site definition")]
    [InlineData("", "the SMS Provider CM01.contoso.com did not return the site definition")]
    [InlineData("{\"t\":\"site\",\"found\":false}\n{\"t\":\"end\"}", "the SMS Provider CM01.contoso.com has no site definition for MCM")]
    [InlineData("{\"t\":\"site\",\"found\":true,\"server\":\"\",\"database\":\"CM_MCM\"}\n{\"t\":\"end\"}", "the site definition has no SQL Server name")]
    public async Task AnUnknownServerGivesAPlainMessageWithTheReason(string siteOutput, string reason)
    {
        var (service, runner) = Service();
        runner.SiteOutput = siteOutput;
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(), CancellationToken.None));
        Assert.Equal($"The SQL Server for site MCM is not known ({reason}). Enter the SQL Server name in Settings, ConfigMgr Site Settings.", ex.Message);
        Assert.DoesNotContain("0x8007", ex.Message);
    }

    [Fact]
    public async Task NoProviderAndNoOverrideGivesAPlainMessageWithoutRunningAScript()
    {
        var (service, runner) = Service();
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(provider: " "), CancellationToken.None));
        Assert.Contains("SQL Server for site MCM is not known (no SMS Provider is configured)", ex.Message);
        Assert.Empty(runner.Scripts);
    }

    [Theory]
    [InlineData("sql01", "")]
    [InlineData("", "CM_ALT")]
    [InlineData("", "")]
    public async Task NoSiteCodeAndAtMostOneOverrideGivesAPlainMessage(string server, string database)
    {
        var (service, runner) = Service();
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(site: "", server: server, database: database), CancellationToken.None));
        Assert.StartsWith("A site code or SQL overrides are required.", ex.Message);
        Assert.Empty(runner.Scripts);
    }

    [Fact]
    public async Task ASiteCodeThatIsNotThreeLettersOrDigitsIsRefusedBeforeAnyScript()
    {
        var (service, runner) = Service();
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(site: "M';--"), CancellationToken.None));
        Assert.Contains("is not three letters or digits", ex.Message);
        Assert.Empty(runner.Scripts);
    }

    [Fact]
    public async Task AMalformedPatternFailsBeforeAnyScript()
    {
        var (service, runner) = Service();
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(
            Request(packagers: [new DiscoveryPackager("odd", "V", "Odd", "Odd", "Odd [", null, null)], hidden: ["odd"]), CancellationToken.None));
        Assert.Empty(runner.Scripts);
    }

    [Fact]
    public async Task TheScanStopsAtTheTimeoutWithAPlainMessage()
    {
        var (service, runner) = Service();
        runner.Delay = ct => Task.Delay(Timeout.Infinite, ct);
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(timeout: 1), CancellationToken.None));
        Assert.Equal("The scan stopped after 1 seconds.", ex.Message);
    }

    [Fact]
    public async Task TheTimeoutIsCappedAtSixHundredSeconds()
    {
        var (service, runner) = Service();
        await service.ScanAsync(Request(server: "s", database: "d", timeout: 5000), CancellationToken.None);
        Assert.Contains("$DiscoveryCommandTimeout = 600", runner.Scripts[0]);
        Assert.Contains("$DiscoveryConnectTimeout = 30", runner.Scripts[0]);
    }

    [Fact]
    public async Task CancellingTheCallerTokenCancelsTheScan()
    {
        var (service, runner) = Service();
        using var cts = new CancellationTokenSource();
        runner.Delay = async ct => { await cts.CancelAsync(); await Task.Delay(Timeout.Infinite, ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task ARunWithoutTheEndLineGivesAPlainMessageWithoutTheStandardError()
    {
        var (service, runner) = Service();
        runner.InventoryOutput = "{\"t\":\"row\",\"n\":\"Git\",\"p\":\"\",\"v\":\"\",\"a\":\"x64\",\"c\":[0]}";
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(server: "CM01", database: "CM_MCM"), CancellationToken.None));
        Assert.Contains("CM01 did not finish", ex.Message);
        Assert.DoesNotContain("0x8007", ex.Message);
    }

    [Fact]
    public async Task ARunThatCannotStartGivesAPlainMessage()
    {
        var service = new DiscoveryService((_, _) => throw new System.ComponentModel.Win32Exception(2, "raw native text"), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<DiscoveryException>(() => service.ScanAsync(Request(server: "s", database: "d"), CancellationToken.None));
        Assert.DoesNotContain("raw native", ex.Message);
        Assert.Contains("Windows PowerShell", ex.Message);
    }

    [Fact]
    public async Task ZeroInventoryRowsReportNoInventoryData()
    {
        var (service, runner) = Service();
        runner.InventoryOutput = "{\"t\":\"summary\",\"clients\":0,\"newest\":\"\"}\n{\"t\":\"end\"}";
        var result = await service.ScanAsync(Request(), CancellationToken.None);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.ClientTotal);
        Assert.Null(result.InventoryNewest);
        Assert.StartsWith("No inventory data", result.StatusText);
    }

    [Fact]
    public void ParsedRowsKeepNamesWithEscapedCharacters()
    {
        var snapshot = DiscoveryService.ParseInventory(
            "noise line\n{\"t\":\"row\",\"n\":\"Caf\\u00e9 \\\"Pro\\\" \\\\ x\",\"p\":\"\",\"v\":\"1\",\"a\":\"x86\",\"c\":[0,0,1]}\n{\"t\":\"summary\",\"clients\":2,\"newest\":\"\"}\n{\"t\":\"end\"}",
            new DiscoveryDatabaseTarget("s", "d"), 30);
        var row = Assert.Single(snapshot.Rows);
        Assert.Equal("Café \"Pro\" \\ x", row.DisplayName);
        Assert.Equal(2, row.Clients.Count);
    }

    public static TheoryData<string, string> SqlErrors => new()
    {
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[18456],\"sspi\":false,\"anonymous\":false}", "SQL Server on CM01 denied the Windows login. The account needs the db_datareader role on CM_MCM." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[18456],\"sspi\":false,\"anonymous\":true}", "SQL Server on CM01 received an anonymous login. Windows credentials do not pass through a second remoting hop without Kerberos delegation. Run the scan from a session that holds your own logon." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[0],\"sspi\":true,\"anonymous\":false}", "Windows authentication to SQL Server CM01 failed. Run the app as a domain account on a domain-joined computer, and check the SQL Server service principal name." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[53],\"sspi\":false,\"anonymous\":false}", "Cannot reach SQL Server CM01. Check the server name, the network path and the SQL Server firewall port." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[4060,18456],\"sspi\":false,\"anonymous\":false}", "Database CM_MCM does not exist on SQL Server CM01, or the Windows login cannot open it. Check the database name." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[233],\"sspi\":false,\"anonymous\":false}", "SQL Server on CM01 closed the connection during the login. Check the database name CM_MCM, and that the account has the db_datareader role on it." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[229],\"sspi\":false,\"anonymous\":false}", "The Windows login cannot read the inventory views in CM_MCM on CM01. The account needs the db_datareader role on CM_MCM." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[-2],\"sspi\":false,\"anonymous\":false}", "SQL Server CM01 did not answer within 45 seconds." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[8152],\"sspi\":false,\"anonymous\":false}", "The site database query on CM01 failed with SQL error 8152." },
        { "{\"t\":\"error\",\"stage\":\"sql\",\"numbers\":[],\"sspi\":false,\"anonymous\":false}", "The site database query on CM01 failed." },
    };

    [Theory]
    [MemberData(nameof(SqlErrors))]
    public void ASqlErrorLineBecomesAPlainMessage(string line, string message)
    {
        var ex = Assert.Throws<DiscoveryException>(() =>
            DiscoveryService.ParseInventory(line + "\n{\"t\":\"end\"}", new DiscoveryDatabaseTarget("CM01", "CM_MCM"), 45));
        Assert.Equal(message, ex.Message);
    }

    /// <summary>
    /// Inventory script output captured from a real site (4 clients, 105 product rows, 11
    /// Applications) matched against every shipped definition with all of them hidden.
    /// </summary>
    [Fact]
    public async Task ARealSiteOutputGivesTheExpectedCandidatesForTheShippedDefinitions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var definitions = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Definitions"), "*.json")
            .Select(p => JsonSerializer.Deserialize<ApplicationPackagerEngine.Models.AppDefinition>(File.ReadAllText(p), options)!)
            .Select(DiscoveryPackagers.From).ToList();
        var (service, runner) = Service();
        runner.InventoryOutput = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Discovery", "cm01-inventory.txt"));
        var result = await service.ScanAsync(Request(server: "CM01", database: "CM_MCM", packagers: definitions, hidden: definitions.Select(d => d.Id).ToList()),
            CancellationToken.None);
        Assert.Equal(4, result.ClientTotal);
        Assert.Equal(
        [
            ".NET 10 Desktop Runtime=1", ".NET 8 Desktop Runtime=1", "Git=1", "grepWin=1", "gsudo=1", "HashTools=1",
            "Microsoft .NET 10=1", "Microsoft .NET 8=1", "Microsoft Edge=4", "Microsoft Edge WebView2 Runtime=4",
            "Microsoft ODBC Driver 18 for SQL Server=1", "Microsoft OLE DB Driver 19=1",
            "Microsoft Visual C++ v14 Redistributable (x64)=4", "Microsoft Visual C++ v14 Redistributable (x86)=4",
            "Windows ADK for Windows 11=1", "Windows PE add-on for the Windows ADK=1",
        ], result.Candidates.Select(c => $"{c.CMName}={c.ClientCount}"));
        Assert.Equal("Inventory covers 4 clients; newest data is from today. Discovered 16 apps.", result.StatusText);
    }

    [Fact]
    public void TheContractExceptionCarriesOnlyItsMessage()
    {
        var ex = new DiscoveryException("plain");
        Assert.Null(ex.InnerException);
        Assert.Equal("plain", ex.Message);
        Assert.True(typeof(DiscoveryException).IsSealed);
        Assert.Single(typeof(DiscoveryException).GetConstructors());
    }

    [Fact]
    public void TheDefaultTimeoutIsSixHundredSeconds() =>
        Assert.Equal(600, new DiscoveryRequest("MCM", "", "", "", [], [], []).TimeoutSeconds);

    [Fact]
    public void TheParsedSnapshotJsonHasNoClientKeysOutsideTheMatcher()
    {
        // The public result type is the only object that leaves the service.
        var json = JsonSerializer.Serialize(new DiscoveryResult([new DiscoveryCandidate("a", "A", "V", "A app", 2)], 4, null, "s"));
        Assert.DoesNotContain("Clients", json);
        Assert.DoesNotContain("ResourceID", json);
    }
}
