using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.DpContent;

/// <summary>
/// A ConfigMgr site in a child Windows PowerShell for the DP content script: stub WMI queries and
/// cmdlets over a JSON state that keep the shapes CM01 (5.2603) returns. SMS_DistributionPoint can
/// hold a NAL path in other letter case than the summarizer (DISPLAY=\\CM01.CONTOSO.COM\) and
/// SMS_DistributionDPStatus holds the last message of each pair (MessageState 1 success, 2 in
/// progress, 4 error). Time is counted in script runs: each run moves the clock one tick, and a
/// write schedules its later states some ticks ahead (option "Ticks", default 2), as CM01 shows:
/// a refresh sets Status 1 and RefreshNow at once and Status 0, a new LastRefreshTime and a new
/// SummaryDate later; a validation sets Status 4 at once and message 2384 later; a delete removes
/// the summarizer row, the instance and the message at once; a distribution adds State 7, Status 2
/// and message 2397 at once and State 0, Status 0 and message 2330 later. Options:
/// "RefreshError:Id" and "DistributeError:Id" end in message 2306 with MessageState 4 and State 7;
/// "VerifyError:Id" ends in a message 2385 with MessageState 4; "RemovePending:Id" keeps State 4
/// and Status 3 until the later tick; "RemoveFails:Id" ends in State 6; "DistributeIgnored:Id" makes
/// the distribution return without a change, as CM01 does while it still removes the same content;
/// "NoChange:Id" makes a refresh or a validation accepted without any later state. Each failure in
/// "Fail" ("put:Id", "delete:Id", "verify:Id", "distribute:Id", "access:Id", "query:Class") happens
/// once and throws text with a secret marker. "Generate" builds DPs, packages and pairs in the harness.
/// </summary>
internal sealed class StubDpContentSite : IConfigMgrSession
{
    public const string Nal = "[\"Display=\\\\CM01.contoso.com\\\"]MSWNET:[\"SMS_SITE=MCM\"]\\\\CM01.contoso.com\\";
    public const string NalUpper = "[\"DISPLAY=\\\\CM01.CONTOSO.COM\\\"]MSWNET:[\"SMS_SITE=MCM\"]\\\\CM01.CONTOSO.COM\\";

    public static string NalOf(string server, string site = "MCM") => $"[\"Display=\\\\{server}\\\"]MSWNET:[\"SMS_SITE={site}\"]\\\\{server}\\";

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
        function global:Test-Fail([string]$Key) {
            if ($global:FailLeft.Remove($Key)) {
                if ($Key -like 'access:*') { throw [System.UnauthorizedAccessException]::new('Access is denied. SECRET-DETAIL-1234 (0x80041003)') }
                throw "The SMS Provider reported an error: SECRET-DETAIL-1234 ($Key)."
            }
        }
        $global:S = @{}
        foreach ($k in 'Dps','Roles','Packages','Summaries','Instances','Messages','Applications','Adverts','TsRefs','AppAssignments','Pending') { $global:S[$k] = L (V $state $k @()) }
        $global:Tick = [int](V $state 'Tick' 0) + 1
        $global:Ticks = [int](V $state.Options 'Ticks' 2)
        $global:Fmt = 'yyyy-MM-ddTHH:mm:ss'
        function global:Dt([string]$Text) { if (-not $Text) { return $null }; [datetime]::ParseExact($Text, $global:Fmt, [Globalization.CultureInfo]::InvariantCulture) }
        function global:Now([int]$At) { (Dt '2026-10-23T23:00:00').AddSeconds(10 * $At).ToString($global:Fmt, [Globalization.CultureInfo]::InvariantCulture) }

        $gen = V $state 'Generate' $null
        if ($null -ne $gen) {
            $dpCount = [int]$gen.Dps; $pkgCount = [int]$gen.Packages; $pairCount = [int]$gen.Pairs
            for ($d = 0; $d -lt $dpCount; $d++) { $name = 'DP{0:D3}.contoso.com' -f $d; $global:S.Dps.Add([pscustomobject]@{ NALPath = ('["Display=\\{0}\"]MSWNET:["SMS_SITE=MCM"]\\{0}\' -f $name); ServerName = $name.ToUpperInvariant(); SiteCode = 'MCM'; IsPullDP = $false; ResourceType = 'Windows NT Server' }) }
            for ($c = 0; $c -lt $pkgCount; $c++) { $global:S.Packages.Add([pscustomobject]@{ Class = 'SMS_Package'; PackageID = ('G{0:D7}' -f $c); Name = ('Generated {0}' -f $c); PackageSize = 1024; PackageType = 0 }) }
            for ($i = 0; $i -lt $pairCount; $i++) {
                $dp = $global:S.Dps[$i % $dpCount]; $pkg = ('G{0:D7}' -f ([int][Math]::Floor($i / $dpCount) % $pkgCount))
                $st = if ($i % 97 -eq 0) { 3 } elseif ($i % 89 -eq 0) { 7 } else { 0 }
                $global:S.Summaries.Add([pscustomobject]@{ PackageID = $pkg; ServerNALPath = $dp.NALPath; State = $st; SourceVersion = 1; SummaryDate = '2026-10-01T10:00:00'; PackageType = 0; SiteCode = 'MCM' })
            }
        }

        $due = @($global:S.Pending | Where-Object { [int]$_.At -le $global:Tick })
        foreach ($p in $due) { [void]$global:S.Pending.Remove($p); $global:Log.Add('Due:' + $p.Action + ':' + $p.PackageID) }
        function global:Key([string]$Id, [string]$Nal) { $Id.ToUpperInvariant() + '|' + $Nal.ToUpperInvariant() }
        function global:Find([string]$List, [string]$Id, [string]$Nal, [string]$NalName) { $global:S[$List] | Where-Object { (Key ([string]$_.PackageID) ([string]$_.$NalName)) -eq (Key $Id $Nal) } | Select-Object -First 1 }
        function global:Set-Message([string]$Id, [string]$Nal, [int]$State, [int]$MessageId, [int]$At) {
            $m = Find 'Messages' $Id $Nal 'NalPath'
            if ($null -eq $m) { $m = [pscustomobject]@{ PackageID = $Id; NalPath = $Nal; MessageState = 0; MessageID = 0; LastUpdateDate = '' }; $global:S.Messages.Add($m) }
            $m.MessageState = $State; $m.MessageID = $MessageId; $m.LastUpdateDate = (Now $At)
        }
        function global:Remove-Pair([string]$Id, [string]$Nal) {
            foreach ($list in @(@('Summaries', 'ServerNALPath'), @('Instances', 'ServerNALPath'), @('Messages', 'NalPath'))) { $row = Find $list[0] $Id $Nal $list[1]; if ($null -ne $row) { [void]$global:S[$list[0]].Remove($row) } }
        }
        foreach ($p in $due) {
            $id = [string]$p.PackageID; $nal = [string]$p.Nal
            $sum = Find 'Summaries' $id $nal 'ServerNALPath'; $inst = Find 'Instances' $id $nal 'ServerNALPath'
            switch ([string]$p.Action) {
                'RefreshDone' {
                    if ($global:Opt.ContainsKey('RefreshError:' + $id)) { Set-Message $id $nal 4 2306 $global:Tick; if ($null -ne $sum) { $sum.State = 7 }; if ($null -ne $inst) { $inst.Status = 2 } }
                    else { if ($null -ne $inst) { $inst.Status = 0; $inst.RefreshNow = $false; $inst.LastRefreshTime = (Now $global:Tick) }; if ($null -ne $sum) { $sum.SummaryDate = (Now $global:Tick); $sum.State = 0 }; Set-Message $id $nal 1 2330 $global:Tick }
                }
                'VerifyDone' {
                    if ($null -ne $inst) { $inst.Status = 0 }
                    if ($global:Opt.ContainsKey('VerifyError:' + $id)) { Set-Message $id $nal 4 2385 $global:Tick } else { Set-Message $id $nal 1 2384 $global:Tick; if ($null -ne $sum) { $sum.SummaryDate = (Now $global:Tick) } }
                }
                'RemoveDone' { if ($global:Opt.ContainsKey('RemoveFails:' + $id)) { if ($null -ne $sum) { $sum.State = 6 } } else { Remove-Pair $id $nal } }
                'DistributeDone' {
                    if ($global:Opt.ContainsKey('DistributeError:' + $id)) { Set-Message $id $nal 4 2306 $global:Tick }
                    else { if ($null -ne $sum) { $sum.State = 0; $sum.SummaryDate = (Now $global:Tick) }; if ($null -ne $inst) { $inst.Status = 0; $inst.LastRefreshTime = (Now $global:Tick) }; Set-Message $id $nal 1 2330 $global:Tick }
                }
            }
        }
        function global:Later([string]$Action, [string]$Id, [string]$Nal) { $global:S.Pending.Add([pscustomobject]@{ At = $global:Tick + $global:Ticks; Action = $Action; PackageID = $Id; Nal = $Nal }) }

        function global:Ids([string]$Where, [string]$Name) { @([regex]::Matches($Where, "$Name = '([^']+)'") | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() }) }
        function global:Nums([string]$Where, [string]$Name) { @([regex]::Matches($Where, "$Name = (\d+)") | ForEach-Object { $_.Groups[1].Value }) }
        function global:Pick([object]$Row, [string]$Columns) { if ($Columns -eq '*') { return $Row.PSObject.Copy() }; $o = [ordered]@{}; foreach ($c in ($Columns -split ',\s*')) { $o[$c] = V $Row $c $null }; [pscustomobject]$o }
        function global:Wmi([object]$Row, [string]$Columns, [bool]$Writable) {
            $o = Pick $Row $Columns
            foreach ($n in 'SummaryDate','LastRefreshTime','LastUpdateDate') { $prop = $o.PSObject.Properties[$n]; if ($null -ne $prop -and $prop.Value -is [string] -and $prop.Value) { $prop.Value = Dt $prop.Value } }
            if ($null -ne $o.PSObject.Properties['ServerNALPath'] -and $null -ne $o.PSObject.Properties['RefreshNow'] -and -not $Writable) {
                $o | Add-Member -MemberType ScriptMethod -Name Put -Value { throw 'A Put of an instance read with the Fast option writes its lazy properties back empty.' }
                $o | Add-Member -MemberType ScriptMethod -Name Delete -Value { throw 'The stub refuses a write through an instance read with the Fast option.' }
            }
            if ($Writable) {
                $o | Add-Member -NotePropertyName __Row -NotePropertyValue $Row
                $o | Add-Member -MemberType ScriptMethod -Name Put -Value {
                    $r = $this.__Row; $id = [string]$r.PackageID; $global:Log.Add('Put:' + $id); Test-Fail ('put:' + $id); Test-Fail ('access:' + $id)
                    if ($this.RefreshNow) { $r.RefreshNow = $true; $r.Status = 1; if (-not $global:Opt.ContainsKey('NoChange:' + $id)) { Later 'RefreshDone' $id ([string]$r.ServerNALPath) } }
                }
                $o | Add-Member -MemberType ScriptMethod -Name Delete -Value {
                    $r = $this.__Row; $id = [string]$r.PackageID; $nal = [string]$r.ServerNALPath; $global:Log.Add('Delete:' + $id); Test-Fail ('delete:' + $id); Test-Fail ('access:' + $id)
                    if ($global:Opt.ContainsKey('RemovePending:' + $id) -or $global:Opt.ContainsKey('RemoveFails:' + $id)) { $sum = Find 'Summaries' $id $nal 'ServerNALPath'; if ($null -ne $sum) { $sum.State = 4 }; $r.Status = 3; Later 'RemoveDone' $id $nal }
                    else { Remove-Pair $id $nal }
                }
                $manager = [pscustomobject]@{ Row = $Row }
                $manager | Add-Member -MemberType ScriptMethod -Name ExecuteMethod -Value {
                    param([string]$Class, [string]$Method, [object]$Arguments)
                    $id = [string]$Arguments['PackageId']; $nal = [string]$Arguments['NALPath']; $global:Log.Add('Verify:' + $id); Test-Fail ('verify:' + $id); Test-Fail ('access:' + $id)
                    if ($Class -ne 'SMS_DistributionPoint' -or $Method -ne 'VerifyPackage') { throw 'Unknown method.' }
                    $inst = Find 'Instances' $id $nal 'ServerNALPath'
                    if ($null -eq $inst) { throw 'No object corresponds to the specified parameters.' }
                    $inst.Status = 4
                    if (-not $global:Opt.ContainsKey('NoChange:' + $id)) { Later 'VerifyDone' $id ([string]$inst.ServerNALPath) }
                    @{ ReturnValue = [pscustomobject]@{ IntegerValue = 0 } }
                }
                $o | Add-Member -NotePropertyName ConnectionManager -NotePropertyValue $manager
            }
            return $o
        }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            if ($Query -notmatch '^SELECT (.+?) FROM (\w+)(?: WHERE (.+))?$') { throw "Unknown query: $Query" }
            $columns = $Matches[1]; $class = $Matches[2]; $where = $Matches[3]
            $global:Log.Add('Query:' + $class)
            Test-Fail ('query:' + $class)
            $writable = $Option -ne 'Fast'
            switch ($class) {
                'SMS_DistributionPointInfo' { return @($global:S.Dps | ForEach-Object { Wmi $_ $columns $false }) }
                'SMS_SCI_SysResUse' { return @($global:S.Roles | ForEach-Object { $_.PSObject.Copy() }) }
                'SMS_Application' {
                    $apps = @($global:S.Applications)
                    if ($where -match "ModelName = '") { $names = @([regex]::Matches($where, "ModelName = '([^']+)'") | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() }); $apps = @($apps | Where-Object { $names -contains ([string]$_.ModelName).ToUpperInvariant() }) }
                    return @($apps | ForEach-Object { Wmi $_ $columns $false })
                }
                'SMS_ApplicationAssignment' { $models = Nums $where 'AppModelID'; return @($global:S.AppAssignments | Where-Object { $models -contains [string]$_.AppModelID } | ForEach-Object { Wmi $_ $columns $false }) }
                'SMS_Advertisement' { $ids = Ids $where 'PackageID'; return @($global:S.Adverts | Where-Object { $ids -contains ([string]$_.PackageID).ToUpperInvariant() } | ForEach-Object { Wmi $_ $columns $false }) }
                'SMS_TaskSequencePackageReference' { $ids = Ids $where 'RefPackageID'; return @($global:S.TsRefs | Where-Object { $ids -contains ([string]$_.RefPackageID).ToUpperInvariant() } | ForEach-Object { Wmi $_ $columns $false }) }
                'SMS_TaskSequencePackage' {
                    $rows = @($global:S.Packages | Where-Object { $_.Class -eq 'SMS_TaskSequencePackage' })
                    if ($where) { $ids = Ids $where 'BootImageID'; $rows = @($rows | Where-Object { $ids -contains ([string](V $_ 'BootImageID' '')).ToUpperInvariant() }) }
                    return @($rows | ForEach-Object { Wmi $_ $columns $false })
                }
                'SMS_PackageStatusDistPointsSummarizer' { $rows = @($global:S.Summaries); if ($where) { $ids = Ids $where 'PackageID'; $rows = @($rows | Where-Object { $ids -contains ([string]$_.PackageID).ToUpperInvariant() }) }; return @($rows | ForEach-Object { Wmi $_ $columns $false }) }
                'SMS_DistributionDPStatus' { $rows = @($global:S.Messages); if ($where) { $ids = Ids $where 'PackageID'; $rows = @($rows | Where-Object { $ids -contains ([string]$_.PackageID).ToUpperInvariant() }) }; return @($rows | ForEach-Object { Wmi $_ $columns $false }) }
                'SMS_DistributionPoint' { $ids = Ids $where 'PackageID'; return @($global:S.Instances | Where-Object { $ids -contains ([string]$_.PackageID).ToUpperInvariant() } | ForEach-Object { Wmi $_ $columns $writable }) }
                default {
                    $rows = @($global:S.Packages | Where-Object { $_.Class -eq $class })
                    if ($where) { $ids = Ids $where 'PackageID'; $rows = @($rows | Where-Object { $ids -contains ([string]$_.PackageID).ToUpperInvariant() }) }
                    return @($rows | ForEach-Object { Wmi $_ $columns $false })
                }
            }
        }
        function global:Start-CMContentDistribution {
            [CmdletBinding()] param([string]$PackageId, [string]$DeploymentPackageId, [string]$BootImageId, [string]$OperatingSystemImageId, [string]$OperatingSystemInstallerId, [string]$DriverPackageId,
                [string]$ApplicationId, [switch]$DisableContentDependencyDetection, [string]$DistributionPointName)
            $id = @($PackageId, $DeploymentPackageId, $BootImageId, $OperatingSystemImageId, $OperatingSystemInstallerId, $DriverPackageId) | Where-Object { $_ } | Select-Object -First 1
            $class = if ($PackageId) { 'SMS_Package' } elseif ($DeploymentPackageId) { 'SMS_SoftwareUpdatesPackage' } elseif ($BootImageId) { 'SMS_BootImagePackage' } elseif ($OperatingSystemImageId) { 'SMS_ImagePackage' } elseif ($OperatingSystemInstallerId) { 'SMS_OperatingSystemInstallPackage' } elseif ($DriverPackageId) { 'SMS_DriverPackage' } else { 'SMS_ContentPackage' }
            if ($ApplicationId) {
                if (-not $DisableContentDependencyDetection) { $global:Log.Add('DependenciesIncluded') }
                $app = $global:S.Applications | Where-Object { [string]$_.CI_ID -eq $ApplicationId } | Select-Object -First 1
                $content = $global:S.Packages | Where-Object { $_.Class -eq 'SMS_ContentPackage' -and ([string]$_.SecurityKey).ToUpperInvariant() -eq ([string](V $app 'ModelName' '')).ToUpperInvariant() } | Select-Object -First 1
                if ($null -eq $content) { throw 'No object corresponds to the specified parameters.' }
                $id = [string]$content.PackageID
            }
            $global:Log.Add('Start-CMContentDistribution:' + $class + ':' + $id + ':' + $DistributionPointName)
            Test-Fail ('distribute:' + $id); Test-Fail ('access:' + $id)
            $pkg = $global:S.Packages | Where-Object { $_.Class -eq $class -and ([string]$_.PackageID).ToUpperInvariant() -eq $id.ToUpperInvariant() } | Select-Object -First 1
            if ($null -eq $pkg) { throw 'No object corresponds to the specified parameters.' }
            $dp = $global:S.Dps | Where-Object { ([string]$_.ServerName) -ieq $DistributionPointName } | Select-Object -First 1
            if ($null -eq $dp) { throw 'No content destination was found. This can happen when an invalid collection, distribution point, or distribution point is specified or if the content has already been distributed to the specified destination.' }
            if ($global:Opt.ContainsKey('DistributeIgnored:' + $id)) { return }
            $nal = [string]$dp.NALPath
            if ($null -ne (Find 'Instances' $id $nal 'ServerNALPath')) { return }
            $type = switch ($class) { 'SMS_Package' { 0 } 'SMS_SoftwareUpdatesPackage' { 5 } 'SMS_BootImagePackage' { 258 } 'SMS_ImagePackage' { 257 } 'SMS_OperatingSystemInstallPackage' { 259 } 'SMS_DriverPackage' { 3 } default { 8 } }
            $global:S.Summaries.Add([pscustomobject]@{ PackageID = $id.ToUpperInvariant(); ServerNALPath = $nal; State = 7; SourceVersion = 1; SummaryDate = (Now $global:Tick); PackageType = $type; SiteCode = 'MCM' })
            $global:S.Instances.Add([pscustomobject]@{ PackageID = $id.ToUpperInvariant(); ServerNALPath = $nal.ToUpperInvariant(); Status = 2; RefreshNow = $false; LastRefreshTime = (Now $global:Tick); SiteCode = 'MCM' })
            Set-Message $id.ToUpperInvariant() $nal 2 2397 $global:Tick
            Later 'DistributeDone' $id.ToUpperInvariant() $nal
        }

        Set-StrictMode -Version 2.0
        $parameters = @{}
        foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $value = $p.Value; if ($value -is [object[]]) { $value = [string[]]@($value | ForEach-Object { [string]$_ }) }; $parameters[$p.Name] = $value }
        $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw))
        $out = @(); $thrown = ''
        try { $out = @(& $block @parameters 3>$null) } catch { $thrown = [string]$_.Exception.Message }
        Set-StrictMode -Off
        $state.Options | Add-Member -NotePropertyName Fail -NotePropertyValue @($global:FailLeft) -Force
        $next = [pscustomobject]@{ Options = $state.Options; Tick = $global:Tick }
        if ($null -ne $gen) { $next = $state } else { foreach ($k in $global:S.Keys) { $next | Add-Member -NotePropertyName $k -NotePropertyValue @($global:S[$k]) } }
        $lines = foreach ($o in $out) { ConvertTo-Json -InputObject $o -Depth 3 -Compress }
        $document = [pscustomobject]@{ Output = @($lines); Thrown = $thrown; Log = @($global:Log); State = $next }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 6 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    private JsonObject _state;

    public StubDpContentSite(JsonObject state)
    {
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
            "Windows PowerShell 5.1 is not on this computer, so the DP content script cannot be tested.");
        _state = state;
    }

    public List<string> Log { get; } = [];

    public List<(string Script, Dictionary<string, object?> Parameters, ConfigMgrParameters Raw)> Runs { get; } = [];

    /// <summary>Called before each script run; lets a test change the site between a plan and its write.</summary>
    public Action<Dictionary<string, object?>, StubDpContentSite>? BeforeRun { get; set; }

    /// <summary>Called after each script run; a returned exception is thrown although the run's changes stay made.</summary>
    public Func<Dictionary<string, object?>, Exception?>? AfterRun { get; set; }

    public JsonObject State => _state;

    public JsonArray List(string name) => _state[name] as JsonArray ?? (JsonArray)(_state[name] = new JsonArray());

    public void Fail(params string[] failures)
    {
        var options = _state["Options"]!.AsObject();
        if (options["Fail"] is not JsonArray list) options["Fail"] = list = new JsonArray();
        foreach (var f in failures) list.Add(f);
    }

    public void SetOption(string name, JsonNode? value) => _state["Options"]!.AsObject()[name] = value;

    public JsonObject? Summary(string packageId, string nal) =>
        List("Summaries").OfType<JsonObject>().FirstOrDefault(r => string.Equals((string?)r["PackageID"], packageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string?)r["ServerNALPath"], nal, StringComparison.OrdinalIgnoreCase));

    public int Queries(string? cls = null) => Log.Count(l => l.StartsWith("Query:", StringComparison.Ordinal) && (cls is null || l == "Query:" + cls));

    /// <summary>
    /// The CM01 shapes: one DP (CM01) and a second DP whose name contains the first
    /// (CM01-B.contoso.com holds "CM01" and the source's LIKE '%CM01.contoso.com%' does not tell
    /// them apart from a DP named "CM01.contoso.com.backup"); package MCM000A6 installed on CM01 and
    /// on CM01.contoso.com.backup, package MCM000A7 failing with State 7 and message 2306, application
    /// content MCM000A8 of application 16780955, package MCM0001B with two deployments and a task
    /// sequence MCM00008 that references it, an orphan status row MCM00005, and a boot image MCM00006.
    /// SMS_DistributionPoint holds the CM01 NAL path in upper case for MCM000A8, as CM01 does for some content.
    /// </summary>
    public static JsonObject Lab()
    {
        var nal = Nal;
        var backup = NalOf("CM01.contoso.com.backup");
        var node = new JsonObject
        {
            ["Options"] = new JsonObject(),
            ["Dps"] = new JsonArray(
                Dp(nal, "CM01.CONTOSO.COM"),
                Dp(backup, "CM01.CONTOSO.COM.BACKUP")),
            ["Roles"] = new JsonArray(),
            ["Packages"] = new JsonArray(
                Pkg("SMS_Package", "MCM000A6", "DPCM-Test-202610231921-1", 1, 0),
                Pkg("SMS_Package", "MCM000A7", "DPCM-Test-202610231921-2", 0, 0),
                Pkg("SMS_Package", "MCM0001B", "CMCO-CANARY-Package", 1, 0),
                Pkg("SMS_BootImagePackage", "MCM00006", "WinPE x64", 391286, 258),
                new JsonObject { ["Class"] = "SMS_ContentPackage", ["PackageID"] = "MCM000A8", ["Name"] = "DPCM-Test-202610231921-3", ["PackageSize"] = 1, ["SecurityKey"] = AppModel },
                new JsonObject { ["Class"] = "SMS_TaskSequencePackage", ["PackageID"] = "MCM00008", ["Name"] = "Build Win11", ["BootImageID"] = "MCM00006" }),
            ["Applications"] = new JsonArray(new JsonObject { ["CI_ID"] = 16780955, ["ModelID"] = 16780950, ["ModelName"] = AppModel, ["IsLatest"] = true }),
            ["Summaries"] = new JsonArray(
                Sum("MCM000A6", nal, 0, 0), Sum("MCM000A6", backup, 0, 0),
                Sum("MCM000A7", nal, 7, 0),
                Sum("MCM000A8", nal, 0, 8),
                Sum("MCM0001B", nal, 0, 0),
                Sum("MCM00005", nal, 0, 0),
                Sum("MCM00006", nal, 0, 258)),
            ["Instances"] = new JsonArray(
                Inst("MCM000A6", nal, 0, "2026-10-23T23:29:07"), Inst("MCM000A6", backup, 0, "2026-10-23T23:29:07"),
                Inst("MCM000A7", nal, 2, "1970-04-10T06:35:00", true),
                Inst("MCM000A8", NalUpper, 0, "2026-10-23T23:39:18"),
                Inst("MCM0001B", nal, 0, "2026-10-20T09:13:44"),
                Inst("MCM00005", nal, 0, "2026-09-08T16:26:47"),
                Inst("MCM00006", NalUpper, 0, "2026-09-21T22:15:14")),
            ["Messages"] = new JsonArray(
                Msg("MCM000A6", nal, 1, 2330, "2026-10-23T19:29:07"), Msg("MCM000A6", backup, 1, 2330, "2026-10-23T19:29:07"),
                Msg("MCM000A7", nal, 4, 2306, "2026-10-23T19:41:07"),
                Msg("MCM000A8", nal, 1, 2384, "2026-10-23T19:39:57"),
                Msg("MCM0001B", nal, 1, 2330, "2026-10-20T05:13:44")),
            ["Adverts"] = new JsonArray(
                new JsonObject { ["AdvertisementID"] = "MCM20002", ["PackageID"] = "MCM0001B" },
                new JsonObject { ["AdvertisementID"] = "MCM20003", ["PackageID"] = "MCM0001B" },
                new JsonObject { ["AdvertisementID"] = "MCM20010", ["PackageID"] = "MCM00008" }),
            ["TsRefs"] = new JsonArray(new JsonObject { ["PackageID"] = "MCM00008", ["RefPackageID"] = "MCM0001B" }),
            ["AppAssignments"] = new JsonArray(new JsonObject { ["AssignmentID"] = 16777300, ["AppModelID"] = 16780950 }),
            ["Pending"] = new JsonArray()
        };
        return node;
    }

    public const string AppModel = "ScopeId_89DF871C-6222-4BDF-8BB7-75CBCCF9821E/Application_6aa690cc-bc19-4f9a-9546-3ab58b5dba74";

    public static JsonObject Dp(string nal, string name, bool pull = false, string resourceType = "Windows NT Server") =>
        new() { ["NALPath"] = nal, ["ServerName"] = name, ["SiteCode"] = "MCM", ["IsPullDP"] = pull, ["ResourceType"] = resourceType };

    public static JsonObject Pkg(string cls, string id, string name, long sizeKb, int type) =>
        new() { ["Class"] = cls, ["PackageID"] = id, ["Name"] = name, ["PackageSize"] = sizeKb, ["PackageType"] = type };

    public static JsonObject Sum(string id, string nal, int state, int type) =>
        new() { ["PackageID"] = id, ["ServerNALPath"] = nal, ["State"] = state, ["SourceVersion"] = 1, ["SummaryDate"] = "2026-10-23T23:29:07", ["PackageType"] = type, ["SiteCode"] = "MCM" };

    public static JsonObject Inst(string id, string nal, int status, string refreshed, bool refreshNow = false) =>
        new() { ["PackageID"] = id, ["ServerNALPath"] = nal, ["Status"] = status, ["RefreshNow"] = refreshNow, ["LastRefreshTime"] = refreshed, ["SiteCode"] = "MCM" };

    public static JsonObject Msg(string id, string nal, int state, int message, string time) =>
        new() { ["PackageID"] = id, ["NalPath"] = nal, ["MessageState"] = state, ["MessageID"] = message, ["LastUpdateDate"] = time };

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var raw = parameters ?? new ConfigMgrParameters();
        var values = raw.ToDictionary(p => p.Key, p => p.Value);
        Runs.Add((script.Name, values, raw));
        BeforeRun?.Invoke(values, this);
        var (output, thrown) = Run(script.Text, JsonSerializer.Serialize(values));
        if (AfterRun?.Invoke(values) is { } after) return Task.FromException<ConfigMgrCommandResult>(after);
        if (thrown.Length > 0)
            return Task.FromException<ConfigMgrCommandResult>(new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, $"The ConfigMgr command {script.Name} failed: {thrown}"));
        return Task.FromResult(new ConfigMgrCommandResult(output, []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (List<JsonElement> Output, string Thrown) Run(string scriptText, string parametersJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dpcstub-" + Guid.NewGuid().ToString("N"));
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
            var state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            _state = state;
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
