using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// The Live sources as CM01 returned them on 2026-10-25 (SH2-QUALIFICATION: SMS_DeploymentSummary 9 rows,
/// the distribution summarizer 9 pairs all installed, SMS_DistributionDPStatus 9 messages, the root
/// summarizer names, one DP, 10 site system rows, component rows), as provider classes of the stub site.
/// </summary>
internal static class LiveFixtures
{
    public const string Nal = """["Display=\\CM01.contoso.com\"]MSWNET:["SMS_SITE=MCM"]\\CM01.contoso.com\""";
    public const string NalUpper = """["Display=\\CM01.CONTOSO.COM\"]MSWNET:["SMS_SITE=MCM"]\\CM01.CONTOSO.COM\""";

    private static JsonObject Row(params (string Name, JsonNode? Value)[] cells)
    {
        var o = new JsonObject();
        foreach (var (name, value) in cells) o[name] = value;
        return o;
    }

    private static JsonObject Deployment(string id, string name, string collection, int feature, int intent, int targeted, int success, int errors, int progress, int unknown) =>
        Row(("DeploymentID", id), ("SoftwareName", name), ("ApplicationName", feature == 1 ? name : ""), ("CollectionName", "Collection " + collection), ("CollectionID", collection),
            ("FeatureType", feature), ("DeploymentIntent", intent), ("NumberTargeted", targeted), ("NumberSuccess", success), ("NumberErrors", errors),
            ("NumberInProgress", progress), ("NumberUnknown", unknown), ("NumberOther", 0), ("SummarizationTime", "2026-10-25T14:27:42"));

    public static JsonArray Deployments() =>
    [
        Deployment("{EBD95D0B-A855-4AC6-9B7E-02211FA6C0E5}", "CMCO-CANARY-App", "MCM0001F", 1, 2, 1, 0, 0, 0, 0),
        Deployment("{F58A48B4-B663-4A89-90E2-503AA372D3F1}", "CMCO-CANARY-Baseline", "MCM0001F", 6, 1, 1, 1, 0, 0, 0),
        Deployment("{CD46EDF2-4D58-4F27-89FA-A4756688A97F}", "CMCO-CANARY-Remediate-Baseline", "MCM0001F", 6, 1, 1, 1, 0, 0, 0),
        Deployment("{4A690236-399B-49ED-A705-220342F2E195}", "ConfigMgr Client Health", "MCM0002D", 6, 1, 1, 1, 0, 0, 0),
        Deployment("MCM20002", "CMCO-CANARY-Package (CMCO-CANARY-Run)", "MCM0001F", 2, 1, 1, 1, 0, 0, 0),
        Deployment("MCM20003", "CMCO-CANARY-Package (CMCO-CANARY-RunAvailable)", "MCM0001F", 2, 2, 1, 0, 0, 0, 0),
        Deployment("MCM2000E", "ConfigMgr Client Health (Deploy)", "MCM0002D", 2, 1, 1, 1, 0, 0, 0),
        Deployment("{1C340617-806D-42C7-9680-46D6026D3F0C}", "CMCO-CANARY-SWU-Group", "MCM00025", 5, 1, 1, 0, 0, 1, 0),
        Deployment("{7098E515-2E10-4392-9FDD-0BB0D131E887}", "202609-W11-25H2", "SMSDM003", 5, 2, 4, 2, 0, 2, 0)
    ];

    private static readonly (string Id, int Type)[] Pairs =
        [("MCM00004", 0), ("MCM00005", 0), ("MCM00007", 257), ("MCM0001B", 0), ("MCM0001C", 8), ("MCM00020", 5), ("MCM00021", 5), ("MCM00035", 8), ("MCM00047", 0)];

    private static readonly (string Id, string Name)[] Names =
    [
        ("MCM00001", "User State Migration Tool for Windows"), ("MCM00004", "Configuration Manager Client Package"), ("MCM00005", "Configuration Manager Client Upgrade Package"),
        ("MCM00007", "Windows 11 Enterprise Evaluation (build 10.0.26200.6584)"), ("MCM0001B", "CMCO-CANARY-Package"), ("MCM0001C", "CMCO-CANARY-App"),
        ("MCM00020", "CMCO-CANARY-SWU-Package"), ("MCM00021", "updates"), ("MCM00035", "7-Zip (x64 edition) - 26.03"), ("MCM00047", "ConfigMgr Client Health")
    ];

    private static readonly (string Role, string Server, int ObjectType)[] SiteSystems =
    [
        ("SMS Component Server", "CM01.CONTOSO.COM", 0), ("SMS Distribution Point", "CM01.contoso.com", 0), ("SMS Dmp Connector", "CM01.contoso.com", 0),
        ("SMS Fallback Status Point", "CM01.contoso.com", 0), ("SMS Management Point", "CM01.contoso.com", 0), ("SMS Site Server", "CM01.CONTOSO.COM", 0),
        ("SMS Software Update Point", "CM01.contoso.com", 0), ("SMS SQL Server", "CM01.contoso.com", 1), ("SMS SQL Server", "CM01.contoso.com", 2),
        ("SMS SRS Reporting Point", "CM01.contoso.com", 0)
    ];

    public static string SiteSystemOf(string server) => $"""["Display=\\{server}\"]MSWNET:["SMS_SITE=MCM"]\\{server}\""";

    private static readonly string[] Components =
        ["CONFIGURATION_MANAGER_UPDATE", "SMS_AD_FOREST_DISCOVERY_MANAGER", "SMS_AD_SYSTEM_DISCOVERY_AGENT", "SMS_DISTRIBUTION_MANAGER", "SMS_EXECUTIVE", "SMS_SITE_COMPONENT_MANAGER"];

    public static JsonObject State()
    {
        var classes = new JsonObject
        {
            ["SMS_DeploymentSummary"] = Deployments(),
            ["SMS_PackageStatusDistPointsSummarizer"] = new JsonArray(Pairs.Select(p => (JsonNode)Row(("PackageID", p.Id), ("ServerNALPath", Nal), ("State", 0), ("PackageType", p.Type), ("SourceVersion", 1), ("SiteCode", "MCM"))).ToArray()),
            ["SMS_DistributionDPStatus"] = new JsonArray(Pairs.Select(p => (JsonNode)Row(("PackageID", p.Id), ("NalPath", Nal), ("MessageState", 1), ("MessageID", 2330))).ToArray()),
            ["SMS_PackageStatusRootSummarizer"] = new JsonArray(Names.Select(n => (JsonNode)Row(("PackageID", n.Id), ("Name", n.Name))).ToArray()),
            ["SMS_DistributionPointInfo"] = new JsonArray(Row(("NALPath", Nal), ("ServerName", "CM01.CONTOSO.COM"), ("SiteCode", "MCM"), ("IsPullDP", false), ("IsPXE", false), ("ResourceType", "Windows NT Server"))),
            ["SMS_SiteSystemSummarizer"] = new JsonArray(SiteSystems.Select(s => (JsonNode)Row(("SiteSystem", SiteSystemOf(s.Server)), ("SiteCode", "MCM"), ("Role", s.Role), ("Status", 0), ("AvailabilityState", 0), ("ObjectType", s.ObjectType))).ToArray()),
            ["SMS_ComponentSummarizer"] = new JsonArray(Components.SelectMany(c => new[] { "0001128000100008", "0001128000080008" }.Select(t =>
                (JsonNode)Row(("ComponentName", c), ("MachineName", "CM01.CONTOSO.COM"), ("SiteCode", "MCM"), ("Status", 0), ("State", c == "CONFIGURATION_MANAGER_UPDATE" ? 0 : 1),
                    ("LastStarted", "2026-10-25T10:25:00"), ("TallyInterval", t)))).ToArray())
        };
        return new JsonObject { ["Options"] = new JsonObject(), ["Now"] = "2026-10-25T14:29:06", ["Provider"] = "CM01", ["Classes"] = classes };
    }

    /// <summary>The client health rows of CM01 as the SQL script writes them: ResourceID, Name0, health, active, state text, six UTC times, version, OS.</summary>
    public static readonly string[][] Clients =
    [
        ["16777219", "CM01", "1", "1", "Active/Pass", "2026-10-25T14:07:34", "2026-10-22T10:18:44", "2026-10-19T08:20:53", "2026-10-25T14:07:34", "2026-10-19T09:27:10", "2026-10-23T17:27:05", "5.00.9146.1009", "Microsoft Windows NT Server 10.0"],
        ["16777220", "DC01", "1", "1", "Active/Pass", "2026-10-25T12:46:12", "2026-10-16T18:27:02", "2026-10-19T08:14:44", "2026-10-25T12:46:12", "2026-10-19T08:33:38", "2026-10-24T05:26:56", "5.00.9141.1011", "Microsoft Windows NT Advanced Server 10.0"],
        ["16777221", "CLIENT01", "1", "1", "Active/Pass", "2026-10-25T13:44:43", "2026-10-22T23:45:05", "2026-10-22T10:22:23", "2026-10-25T13:44:43", "2026-10-23T16:26:39", "2026-10-24T03:26:31", "5.00.9146.1009", "Microsoft Windows NT Workstation 10.0"],
        ["16777222", "CLIENT02", "1", "0", "Inactive/Pass", "2026-10-13T12:55:25", "2026-10-12T04:40:06", "2026-10-12T08:25:53", "2026-10-13T12:55:25", "2026-10-12T10:43:47", "2026-10-01T06:11:51", "5.00.9141.1011", "Microsoft Windows NT Workstation 10.0"]
    ];
}

/// <summary>
/// Runs the real Live SQL script in Windows PowerShell with a stand-in System.Data.SqlClient: New-Object
/// returns a fake connection for SqlConnection, whose command hands back the given result sets. The fake
/// writes "#probe" lines with the connection string, the command text hash and the parameters, which the
/// parser ignores. No test opens a SQL connection. A script without "$LiveRun" (the site definition read)
/// answers with <see cref="SiteLine"/>.
/// </summary>
internal sealed class FakeSqlHost
{
    public List<string> Scripts { get; } = [];
    public List<string> Outputs { get; } = [];
    public string SiteLine { get; set; } = """{"t":"site","found":true,"server":"CM01.contoso.com","database":"CM_MCM","via":"Dcom"}""";

    /// <summary>PowerShell expressions of the two result sets, each an array of row arrays.</summary>
    public string ClientsSet { get; set; } = "@()";
    public string InactiveSet { get; set; } = "@()";

    /// <summary>A PowerShell statement that Open() runs first; a throw there is a connection failure.</summary>
    public string OnOpen { get; set; } = "";

    public int SiteLookups { get; private set; }

    /// <summary>The site definition read never answers; it ends only on its token.</summary>
    public bool SiteHangs { get; set; }

    public DiscoveryService.ScriptRunner Runner => RunAsync;

    private const string Prelude = """
        function global:New-FakeReader {
            param([object[]]$Sets)
            $reader = Microsoft.PowerShell.Utility\New-Object psobject -Property @{ Sets = $Sets; SetIndex = 0; RowIndex = -1 }
            $reader | Add-Member ScriptMethod Read { $this.RowIndex++; return $this.RowIndex -lt @($this.Sets[$this.SetIndex]).Count }
            $reader | Add-Member ScriptMethod NextResult { $this.SetIndex++; $this.RowIndex = -1; return $this.SetIndex -lt $this.Sets.Count }
            $reader | Add-Member ScriptProperty FieldCount { @(@($this.Sets[$this.SetIndex])[$this.RowIndex]).Count }
            $reader | Add-Member ScriptMethod IsDBNull { param($i) $v = @(@($this.Sets[$this.SetIndex])[$this.RowIndex])[$i]; return ($null -eq $v -or $v -is [System.DBNull]) }
            $reader | Add-Member ScriptMethod GetValue { param($i) return @(@($this.Sets[$this.SetIndex])[$this.RowIndex])[$i] }
            $reader | Add-Member ScriptMethod Dispose { }
            return $reader
        }
        function global:New-Object {
            param([Parameter(Position = 0)][string]$TypeName, [Parameter(Position = 1)][object[]]$ArgumentList, [hashtable]$Property)
            if ($TypeName -eq 'System.Data.SqlClient.SqlConnection') {
                [Console]::Out.WriteLine('#probe cs ' + [string]$ArgumentList[0])
                $c = Microsoft.PowerShell.Utility\New-Object psobject
                $c | Add-Member ScriptMethod Open { & ([scriptblock]::Create($global:FakeOnOpen)) }
                $c | Add-Member ScriptMethod CreateCommand {
                    $cmd = Microsoft.PowerShell.Utility\New-Object psobject -Property @{ CommandText = ''; CommandType = $null; CommandTimeout = 0 }
                    $ps = Microsoft.PowerShell.Utility\New-Object psobject
                    $ps | Add-Member ScriptMethod Add { param($n, $t) $p = Microsoft.PowerShell.Utility\New-Object psobject -Property @{ Name = $n; Type = [string]$t; Value = $null }; $global:FakeParams.Add($p); return $p }
                    $cmd | Add-Member NoteProperty Parameters $ps
                    $cmd | Add-Member ScriptMethod ExecuteReader { [Console]::Out.WriteLine('#probe timeout ' + $this.CommandTimeout); foreach ($p in $global:FakeParams) { [Console]::Out.WriteLine('#probe param ' + $p.Name + ' ' + $p.Type + ' ' + $p.Value + ' ' + $p.Value.GetType().Name) }; [Console]::Out.WriteLine('#probe text ' + $this.CommandText.Length); return (New-FakeReader -Sets @(,$global:FakeClients + ,$global:FakeInactive)) }
                    $cmd | Add-Member ScriptMethod ExecuteScalar { [Console]::Out.WriteLine('#probe scalar ' + $this.CommandText); return 4 }
                    $cmd | Add-Member ScriptMethod Dispose { }
                    return $cmd
                }
                $c | Add-Member ScriptMethod Dispose { [Console]::Out.WriteLine('#probe disposed') }
                return $c
            }
            $forward = @{ TypeName = $TypeName }
            if ($PSBoundParameters.ContainsKey('ArgumentList')) { $forward.ArgumentList = $ArgumentList }
            if ($PSBoundParameters.ContainsKey('Property')) { $forward.Property = $Property }
            return , (Microsoft.PowerShell.Utility\New-Object @forward)
        }
        $global:FakeParams = [System.Collections.Generic.List[object]]::new()
        """;

    private async Task<(int, string, string)> RunAsync(string script, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Scripts.Add(script);
        if (!script.Contains("$LiveRun = $true", StringComparison.Ordinal))
        {
            SiteLookups++;
            if (SiteHangs) await Task.Delay(Timeout.Infinite, ct);
            return (0, SiteLine + "\r\n{\"t\":\"end\"}\r\n", "");
        }
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shsql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "run.ps1");
            var text = Prelude + "\r\n$global:FakeClients = " + ClientsSet + "\r\n$global:FakeInactive = " + InactiveSet + "\r\n$global:FakeOnOpen = " +
                PowerShellLiteral.SingleQuoted(OnOpen) + "\r\n" + script;
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), ct);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync(ct);
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            Outputs.Add(output);
            return (process.ExitCode, output, await error);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>A session whose every call throws the given failure, or blocks until cancelled.</summary>
internal sealed class ThrowingSession(ConfigMgrFailure? failure) : IConfigMgrSession
{
    public int Calls { get; private set; }

    public async Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        Calls++;
        if (failure is { } f) throw new ConfigMgrSessionException(f, "The ConfigMgr command failed: SECRET-DETAIL-1234");
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
