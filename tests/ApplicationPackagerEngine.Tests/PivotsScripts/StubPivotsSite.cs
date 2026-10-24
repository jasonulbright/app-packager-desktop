using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.PivotsScripts;

/// <summary>
/// A ConfigMgr site in a child Windows PowerShell for the Pivots and Scripts session script: stub WMI
/// queries, CIM calls and script cmdlets over a JSON state with the shapes CM01 (5.00.9146.1000)
/// returns. SMS_Scripts holds the text as base64 of UTF-16LE with a byte order mark (New-CMScript) and
/// a site ScriptHash; New-CMScript stores no parameter definitions; UpdateScript of the text resets
/// the approval to waiting; Invoke-CMScript refuses a script that is not approved ("Script Not
/// Approved") and a value with a double quote ("Generic failure"), drops a value for a name the
/// script does not define and a text value for an integer parameter. Time is counted in script runs:
/// a run schedules each online client's status row some runs ahead (option "Ticks", default 1);
/// an offline client and a resource without a client get no row. Options: "TwoKey" (1 makes the site
/// refuse an approval decision by the author), "User" (the author New-CMScript records), "Throw:Guid"
/// (the script stops with an error: state 2, exit code -2147467259), "Output:Guid" (the script output),
/// "Fail" ("define", "create", "query:Class", "run") fails the call once with a secret marker.
/// </summary>
internal sealed class StubPivotsSite : IConfigMgrSession
{
    public const string Lab = "CONTOSO\\LabAdmin";

    private const string Harness = """
        param([string]$StatePath, [string]$ScriptPath, [string]$ParametersPath, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        function global:V([object]$Object, [string]$Name, [object]$Default) { if ($null -eq $Object) { return $Default }; $p = $Object.PSObject.Properties[$Name]; if ($null -eq $p -or $null -eq $p.Value) { return $Default }; return $p.Value }
        function global:L([object]$List) { $r = [System.Collections.Generic.List[object]]::new(); foreach ($x in @($List)) { if ($null -ne $x) { $r.Add($x) } }; return ,$r }
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:Opt = @{}
        foreach ($p in $state.Options.PSObject.Properties) { $global:Opt[$p.Name] = $p.Value }
        $global:FailLeft = [System.Collections.Generic.List[string]]::new()
        foreach ($f in @(V $state.Options 'Fail' @())) { if ($f) { $global:FailLeft.Add([string]$f) } }
        function global:Test-Fail([string]$Key) { if ($global:FailLeft.Remove($Key)) { throw "The SMS Provider reported an error. SECRET-DETAIL-1234 ($Key)" } }
        $global:S = @{}
        foreach ($k in 'Devices','Collections','Members','Scripts','Status','Pending') { $global:S[$k] = L (V $state $k @()) }
        $global:Tick = [int](V $state 'Tick' 0) + 1
        $global:Ticks = [int](V $state.Options 'Ticks' 1)
        $global:NextOp = [long](V $state 'NextOp' 16784600)
        $global:NextGuid = [int](V $state 'NextGuid' 1)
        $due = @($global:S.Pending | Where-Object { [int]$_.At -le $global:Tick })
        foreach ($p in $due) { [void]$global:S.Pending.Remove($p); $global:S.Status.Add(($p | Select-Object * -ExcludeProperty At)) }

        function global:Hash([string]$Text) { $sha = [System.Security.Cryptography.SHA256]::Create(); try { ([BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::Unicode.GetBytes($Text)))).Replace('-', '') } finally { $sha.Dispose() } }
        function global:Get-Location { [pscustomobject]@{ Drive = [pscustomobject]@{ Name = 'MCM'; Root = 'cm01.contoso.com' }; Path = 'MCM:\' } }
        function global:Get-CMSite { param([string]$SiteCode) $global:Log.Add('Get-CMSite'); [pscustomobject]@{ SiteCode = 'MCM'; Version = '5.00.9146.1000' } }
        function global:Find-Script([string]$Guid) { $global:S.Scripts | Where-Object { ([string]$_.ScriptGuid).ToUpperInvariant() -eq $Guid.ToUpperInvariant() } | Select-Object -First 1 }
        function global:Script-Row([object]$Row, [bool]$Lazy) {
            $o = $Row.PSObject.Copy()
            if (-not $Lazy) { $o.Script = ''; $o.ParamsDefinition = '' }
            $o | Add-Member -NotePropertyName __Row -NotePropertyValue $Row
            $o | Add-Member -MemberType ScriptMethod -Name Get -Value { $r = $this.__Row; $this.Script = $r.Script; $this.ParamsDefinition = $r.ParamsDefinition }
            $o
        }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            if ($Query -notmatch '^SELECT (.+?) FROM (\w+)(?: WHERE (.+))?$') { throw "Unknown query: $Query" }
            $class = $Matches[2]; $where = $Matches[3]
            $global:Log.Add('Query:' + $class + $(if ($where) { ':' + $where } else { '' }))
            Test-Fail ('query:' + $class)
            switch ($class) {
                'SMS_CombinedDeviceResources' {
                    $rows = @($global:S.Devices)
                    if ($where) { if ($where -notmatch '^ResourceID IN \(([\d,]+)\)$') { throw "Bad where: $where" }; $ids = $Matches[1] -split ','; $rows = @($rows | Where-Object { $ids -contains [string]$_.ResourceID }) }
                    return @($rows | ForEach-Object { $_.PSObject.Copy() })
                }
                'SMS_Collection' { if ($where -ne 'CollectionType = 2') { throw "Bad where: $where" }; return @($global:S.Collections | ForEach-Object { $_.PSObject.Copy() }) }
                'SMS_FullCollectionMembership' {
                    if ($where -notmatch "^CollectionID = '([A-Z0-9]{8})'$") { throw "Bad where: $where" }
                    $id = $Matches[1]
                    return @($global:S.Members | Where-Object { [string]$_.CollectionID -eq $id } | ForEach-Object { $_.PSObject.Copy() })
                }
                'SMS_Scripts' {
                    if ($where -eq 'Feature = 0') { return @($global:S.Scripts | Where-Object { [int]$_.Feature -eq 0 } | ForEach-Object { Script-Row $_ $false }) }
                    if ($where -notmatch "^ScriptGuid = '([0-9A-F-]{36})'$") { throw "Bad where: $where" }
                    $r = Find-Script $Matches[1]
                    if ($null -eq $r) { return @() }
                    return @(Script-Row $r $false)
                }
                'SMS_ScriptsExecutionStatus' {
                    if ($where -notmatch '^ClientOperationId = (\d+)(?: AND ResourceId IN \(([\d,]+)\))?$') { throw "Bad where: $where" }
                    $op = $Matches[1]
                    $only = if ($Matches[2]) { @($Matches[2] -split ',') } else { $null }
                    return @($global:S.Status | Where-Object { [string]$_.ClientOperationId -eq $op -and ($null -eq $only -or $only -contains [string]$_.ResourceId) } | ForEach-Object { $_.PSObject.Copy() })
                }
                'SMS_SCI_SiteDefinition' { return @([pscustomobject]@{ SiteCode = 'MCM'; Props = @([pscustomobject]@{ PropertyName = 'TwoKeyApproval'; Value = [int](V $state.Options 'TwoKey' 0) }) }) }
                default { throw "Unknown class $class" }
            }
        }
        function global:New-CMScript {
            [CmdletBinding()] param([string]$ScriptName, [string]$ScriptText, [switch]$Fast)
            $global:Log.Add('New-CMScript:' + $ScriptName); Test-Fail 'create'
            $guid = ('00000000-0000-4000-8000-{0:D12}' -f $global:NextGuid); $global:NextGuid++
            $bytes = [byte[]](0xFF, 0xFE) + [System.Text.Encoding]::Unicode.GetBytes($ScriptText)
            $row = [pscustomobject]@{ ScriptGuid = $guid; ScriptName = $ScriptName; Author = [string](V $state.Options 'User' 'CONTOSO\LabAdmin'); Approver = ''; ApprovalState = 0; ScriptVersion = '1'; ScriptType = 0; Feature = 0; Comment = ''
                ScriptHash = (Hash $ScriptText); LastUpdateTime = [datetime]'2026-10-24T08:40:00'; Script = [Convert]::ToBase64String($bytes); ParamsDefinition = ''; ScriptDescription = ''; Timeout = 1800 }
            $global:S.Scripts.Add($row)
            $row.PSObject.Copy()
        }
        function global:New-CimSessionOption { param([string]$Protocol) [pscustomobject]@{ Protocol = $Protocol } }
        function global:New-CimSession { param([string]$ComputerName, [object]$SessionOption, $ErrorAction) $global:Log.Add('New-CimSession:' + $ComputerName + ':' + $SessionOption.Protocol); [pscustomobject]@{ Computer = $ComputerName } }
        function global:Remove-CimSession { param([object]$CimSession, $ErrorAction) $global:Log.Add('Remove-CimSession') }
        function global:Get-CimInstance {
            [CmdletBinding()] param([object]$CimSession, [string]$Namespace, [string]$ClassName, [string]$Filter, [Parameter(ValueFromPipeline = $true)][object]$InputObject)
            process {
                if ($null -ne $InputObject) { $r = $InputObject.__Row; $c = $r.PSObject.Copy(); $c | Add-Member -NotePropertyName __Row -NotePropertyValue $r; return $c }
                if ($Filter -notmatch "^ScriptGuid = '([0-9A-F-]{36})'$") { throw "Bad filter: $Filter" }
                $r = Find-Script $Matches[1]
                if ($null -eq $r) { return }
                $c = $r.PSObject.Copy(); $c.Script = ''; $c.ParamsDefinition = ''; $c | Add-Member -NotePropertyName __Row -NotePropertyValue $r; $c
            }
        }
        function global:Invoke-CimMethod {
            [CmdletBinding()] param([object]$InputObject, [string]$MethodName, [hashtable]$Arguments)
            $global:Log.Add('Invoke-CimMethod:' + $MethodName); Test-Fail 'define'
            if ($MethodName -ne 'UpdateScript') { throw 'Unknown method' }
            $r = $InputObject.__Row
            if ([string]$Arguments.Script -ne [string]$r.Script) { $r.Script = [string]$Arguments.Script; $r.ApprovalState = 0; $r.ScriptHash = 'CHANGED' + $global:Tick }
            $r.ParamsDefinition = [string]$Arguments.ParamsDefinition
            [pscustomobject]@{ ReturnValue = 0 }
        }
        function global:Get-CMScript { [CmdletBinding()] param([string]$ScriptGuid, [switch]$Fast) $r = Find-Script $ScriptGuid; if ($null -ne $r) { $r.PSObject.Copy() } }
        function global:Remove-CMScript {
            [CmdletBinding()] param([object]$InputObject, [switch]$Force)
            $r = Find-Script ([string]$InputObject.ScriptGuid); $global:Log.Add('Remove-CMScript:' + $InputObject.ScriptGuid)
            if ($null -ne $r) { [void]$global:S.Scripts.Remove($r); foreach ($s in @($global:S.Status | Where-Object { [string]$_.ScriptGuid -eq [string]$r.ScriptGuid })) { [void]$global:S.Status.Remove($s) } }
        }
        function global:Set-Decision([string]$Guid, [int]$Decision, [string]$Comment, [string]$Verb) {
            $global:Log.Add($Verb + ':' + $Guid)
            $r = Find-Script $Guid
            if ($null -eq $r) { throw 'No object corresponds to the specified parameters.' }
            $me = [string](V $state.Options 'Caller' 'CONTOSO\LabAdmin')
            if ([int](V $state.Options 'TwoKey' 0) -eq 1 -and [string]$r.Author -eq $me) { throw 'The SMS Provider reported an error.' }
            $r.ApprovalState = $Decision; $r.Approver = $me; $r.Comment = $Comment
        }
        function global:Approve-CMScript { [CmdletBinding()] param([string]$ScriptGuid, [string]$Comment) Set-Decision $ScriptGuid 3 $Comment 'Approve' }
        function global:Deny-CMScript { [CmdletBinding()] param([string]$ScriptGuid, [string]$Comment) Set-Decision $ScriptGuid 1 $Comment 'Deny' }
        function global:Invoke-CMScript {
            [CmdletBinding()] param([string]$ScriptGuid, [object[]]$Device, [string]$CollectionId, [hashtable]$ScriptParameter, [switch]$PassThru)
            Test-Fail 'run'
            $r = Find-Script $ScriptGuid
            if ($null -eq $r -or [int]$r.ApprovalState -ne 3) { throw [System.InvalidOperationException]::new('Script Not Approved') }
            foreach ($v in @($ScriptParameter.Values)) { if ([string]$v -match '"') { throw 'Generic failure ' } }
            $op = $global:NextOp; $global:NextOp++
            $targets = if ($CollectionId) { @($global:S.Members | Where-Object { [string]$_.CollectionID -eq $CollectionId } | ForEach-Object { $id = $_.ResourceID; $global:S.Devices | Where-Object { $_.ResourceID -eq $id } }) } else { @($Device) }
            $global:Log.Add('Invoke-CMScript:' + $ScriptGuid + ':' + $(if ($CollectionId) { 'collection ' + $CollectionId } else { 'devices ' + (@($targets | ForEach-Object { $_.ResourceID }) -join ',') }) + ':' + (@($ScriptParameter.Keys | Sort-Object | ForEach-Object { $_ + '=' + $ScriptParameter[$_] + '(' + $ScriptParameter[$_].GetType().Name + ')' }) -join ';'))
            $throw = $global:Opt.ContainsKey('Throw:' + $ScriptGuid)
            $output = [string](V $state.Options ('Output:' + $ScriptGuid) '{\"Label\":\"none\"}')
            foreach ($t in @($targets)) {
                if (-not $t.IsClient -or $t.CNIsOnline -eq $false) { continue }
                $global:S.Pending.Add([pscustomobject]@{ At = $global:Tick + $global:Ticks; ClientOperationId = $op; ResourceId = $t.ResourceID; DeviceName = $t.Name; ScriptGuid = $r.ScriptGuid
                    ScriptExecutionState = $(if ($throw) { 2 } else { 1 }); ScriptExitCode = $(if ($throw) { -2147467259 } else { 0 }); ScriptOutput = $(if ($throw) { '' } else { $output }); LastUpdateTime = [datetime]'2026-10-24T08:51:11' })
            }
            if ($PassThru) { [pscustomobject]@{ OperationID = $op; ReturnValue = 0 } }
        }

        Set-StrictMode -Version 2.0
        $parameters = @{}
        foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $value = $p.Value; if ($value -is [object[]]) { $value = [string[]]@($value | ForEach-Object { [string]$_ }) }; $parameters[$p.Name] = $value }
        $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw))
        $out = @(); $thrown = ''
        try { $out = @(& $block @parameters 3>$null) } catch { $thrown = [string]$_.Exception.Message }
        Set-StrictMode -Off
        $state.Options | Add-Member -NotePropertyName Fail -NotePropertyValue @($global:FailLeft) -Force
        $next = [pscustomobject]@{ Options = $state.Options; Tick = $global:Tick; NextOp = $global:NextOp; NextGuid = $global:NextGuid }
        foreach ($k in $global:S.Keys) { $rows = @($global:S[$k] | ForEach-Object { $_ | Select-Object * -ExcludeProperty __Row }); $next | Add-Member -NotePropertyName $k -NotePropertyValue $rows }
        $lines = foreach ($o in $out) { ConvertTo-Json -InputObject $o -Depth 5 -Compress }
        $document = [pscustomobject]@{ Output = @($lines); Thrown = $thrown; Log = @($global:Log); State = $next }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 8 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    private JsonObject _state;

    public StubPivotsSite(JsonObject state)
    {
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
            "Windows PowerShell 5.1 is not on this computer, so the Pivots and Scripts script cannot be tested.");
        _state = state;
    }

    public List<string> Log { get; } = [];

    public List<Dictionary<string, object?>> Runs { get; } = [];

    /// <summary>Called before each script run; lets a test change the site between a plan and its write.</summary>
    public Action<Dictionary<string, object?>, StubPivotsSite>? BeforeRun { get; set; }

    public JsonObject State => _state;

    public JsonArray List(string name) => _state[name] as JsonArray ?? (JsonArray)(_state[name] = new JsonArray());

    public void Fail(params string[] failures)
    {
        var options = _state["Options"]!.AsObject();
        if (options["Fail"] is not JsonArray list) options["Fail"] = list = new JsonArray();
        foreach (var f in failures) list.Add(f);
    }

    public void SetOption(string name, JsonNode? value) => _state["Options"]!.AsObject()[name] = value;

    public JsonObject? Script(string guid) => List("Scripts").OfType<JsonObject>().FirstOrDefault(s => string.Equals((string?)s["ScriptGuid"], guid, StringComparison.OrdinalIgnoreCase));

    public int Count(string prefix) => Log.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));

    public const string ScriptGuid = "39AC2988-335F-43B2-9CBD-D48ABCEA21F7";
    public const string ScriptText = "param([string]$Label = 'none', [int]$Count = 1)\n[pscustomobject]@{ Label = $Label; Count = $Count; Type = $Count.GetType().Name } | ConvertTo-Json -Compress";

    /// <summary>The ParamsDefinition CM01 stored for <see cref="ScriptText"/> (base64 of the XML the session script writes).</summary>
    public static string Definitions(params (string Name, string Type, string Default)[] parameters)
    {
        var xml = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"utf-16\"?><ScriptParameters SchemaVersion=\"1\">");
        foreach (var (name, type, def) in parameters)
            xml.Append($"<ScriptParameter Name=\"{name}\" FriendlyName=\"{name}\" Type=\"{type}\" Description=\"\" IsRequired=\"false\" IsHidden=\"false\" DefaultValue=\"{def}\"><Validators /></ScriptParameter>");
        xml.Append("</ScriptParameters>");
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml.ToString()));
    }

    public static string Encoded(string text) => Convert.ToBase64String([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes(text)]);

    public static JsonObject ScriptRow(string guid, string name, string text, int approval, string definitions, string author = Lab, int feature = 0, string hash = "D08DF3F2F20F5C3A822B0FEDA476096F857C66ADC4CF60218C6118E85C7B0B16") =>
        new()
        {
            ["ScriptGuid"] = guid, ["ScriptName"] = name, ["Author"] = author, ["Approver"] = approval == 3 ? Lab : "", ["ApprovalState"] = approval, ["ScriptVersion"] = "1",
            ["ScriptType"] = 0, ["Feature"] = feature, ["Comment"] = "", ["ScriptHash"] = hash, ["LastUpdateTime"] = "2026-10-24T08:51:01", ["Script"] = Encoded(text),
            ["ParamsDefinition"] = definitions, ["ScriptDescription"] = "", ["Timeout"] = 1800
        };

    public static JsonObject Device(int id, string name, bool client = true, bool? online = true) =>
        new() { ["ResourceID"] = id, ["Name"] = name, ["IsClient"] = client, ["CNIsOnline"] = online is null ? null : JsonValue.Create(online.Value) };

    public static JsonObject Member(string collection, int id, string name, bool client = true) =>
        new() { ["CollectionID"] = collection, ["ResourceID"] = id, ["Name"] = name, ["IsClient"] = client };

    /// <summary>
    /// The CM01 shapes: CM01, DC01, CLIENT01 online, CLIENT02 offline, four resources without a
    /// client; collections All Systems (SMS00001), All Workstations (MCM00015: CLIENT01 and CLIENT02),
    /// HomeLab - Test Deployments (MCM00014: CLIENT01); the built-in CMPivot script (Feature 1) and an
    /// approved throwaway script with the parameters Label (string) and Count (integer).
    /// </summary>
    public static JsonObject Lab01()
    {
        return new JsonObject
        {
            ["Options"] = new JsonObject(),
            ["Devices"] = new JsonArray(
                Device(16777219, "CM01"), Device(16777220, "DC01"), Device(16777221, "CLIENT01"), Device(16777222, "CLIENT02", online: false),
                Device(2046820352, "x86 Unknown Computer (x86 Unknown Computer)", false, null), Device(2046820353, "x64 Unknown Computer (x64 Unknown Computer)", false, null),
                Device(2046820354, "arm64 Unknown Computer (arm64 Unknown Computer)", false, null), Device(2030043136, "Provisioning Device (Provisioning Device)", false, null)),
            ["Collections"] = new JsonArray(
                new JsonObject { ["CollectionID"] = "SMS00001", ["Name"] = "All Systems", ["MemberCount"] = 8 },
                new JsonObject { ["CollectionID"] = "MCM00014", ["Name"] = "HomeLab - Test Deployments", ["MemberCount"] = 1 },
                new JsonObject { ["CollectionID"] = "MCM00015", ["Name"] = "All Workstations", ["MemberCount"] = 2 }),
            ["Members"] = new JsonArray(
                Member("SMS00001", 16777219, "CM01"), Member("SMS00001", 16777220, "DC01"), Member("SMS00001", 16777221, "CLIENT01"), Member("SMS00001", 16777222, "CLIENT02"),
                Member("SMS00001", 2046820353, "x64 Unknown Computer (x64 Unknown Computer)", false),
                Member("MCM00014", 16777221, "CLIENT01"),
                Member("MCM00015", 16777221, "CLIENT01"), Member("MCM00015", 16777222, "CLIENT02")),
            ["Scripts"] = new JsonArray(
                ScriptRow("7DC6B6F1-E7F6-43C1-96E0-E1D16BC25C14", "CMPivot", "param([string] $kustoquery)", 3, "", "CM", feature: 1, hash: "5f077643c10df4deee1f51f24e98d0aecdb07bfd19d4b458fb8d4ba18177b2e3"),
                ScriptRow(ScriptGuid, "PSQ-Throwaway-Real", ScriptText, 3, Definitions(("Label", "System.String", "none"), ("Count", "System.Int32", "1")))),
            ["Status"] = new JsonArray(),
            ["Pending"] = new JsonArray()
        };
    }

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value);
        Runs.Add(values);
        BeforeRun?.Invoke(values, this);
        var (output, thrown) = Run(script.Text, JsonSerializer.Serialize(values));
        if (thrown.Length > 0)
            return Task.FromException<ConfigMgrCommandResult>(new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, $"The ConfigMgr command {script.Name} failed: {thrown}"));
        return Task.FromResult(new ConfigMgrCommandResult(output, []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (List<JsonElement> Output, string Thrown) Run(string scriptText, string parametersJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-psqstub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var harness = Path.Combine(folder, "harness.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            var scriptPath = Path.Combine(folder, "script.ps1");
            var outPath = Path.Combine(folder, "out.json");
            File.WriteAllText(harness, Harness, new System.Text.UTF8Encoding(true));
            File.WriteAllText(statePath, _state.ToJsonString(), new System.Text.UTF8Encoding(true));
            File.WriteAllText(parametersPath, parametersJson, new System.Text.UTF8Encoding(true));
            File.WriteAllText(scriptPath, scriptText, new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-StatePath", statePath, "-ScriptPath", scriptPath, "-ParametersPath", parametersPath, "-OutPath", outPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(300_000), "The stub site did not finish.");
            Assert.True(File.Exists(outPath), "The stub site wrote no result: " + error.Result);
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = document.RootElement;
            _state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            Log.AddRange(Elements(root.GetProperty("Log")).Select(e => e.GetString() ?? ""));
            var lines = Elements(root.GetProperty("Output")).Select(e => e.GetString() ?? "").Where(l => l.Length > 0);
            return (lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList(), root.GetProperty("Thrown").GetString() ?? "");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static List<JsonElement> Elements(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Select(e => e.Clone()).ToList(),
        JsonValueKind.Null or JsonValueKind.Undefined => [],
        _ => [value.Clone()]
    };
}
