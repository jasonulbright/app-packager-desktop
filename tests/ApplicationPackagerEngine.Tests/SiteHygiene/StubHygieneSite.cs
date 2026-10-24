using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.SiteHygiene;

/// <summary>
/// A ConfigMgr site in a child Windows PowerShell for the Site Hygiene script: Invoke-CMWmiQuery over a
/// JSON state of provider classes (rows with the column names CM01 returns), Get-CMCollection with rules
/// and the full schedule, Get-CMApplication with the AutoInstall setting in SDMPackageXML, the collection
/// settings instance with Get(), ServiceWindows and Put(), and the write cmdlets of the typed fixes. Each
/// write logs "Cmd:" plus the command as the plan text writes it and changes the state the way CM01 did
/// in the qualification: Remove-CMApplication refuses an application with a deployment or a dependent,
/// Remove-CMCollection refuses a collection another one includes, Remove-CMApplicationRevisionHistory
/// refuses the latest revision, Set-CMApplication makes a new revision. Times in the state are UTC text.
/// Options: "Fail" holds "query:Class" or "cmd:Cmdlet" entries that fail once with text holding a secret
/// marker; "Generate" adds generated rows at each run and keeps none of them.
/// </summary>
internal sealed class StubHygieneSite : IConfigMgrSession
{
    private const string Harness = """
        param([string]$StatePath, [string]$ScriptPath, [string]$ParametersPath, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        function global:V([object]$Object, [string]$Name, [object]$Default) { if ($null -eq $Object) { return $Default }; $p = $Object.PSObject.Properties[$Name]; if ($null -eq $p -or $null -eq $p.Value) { return $Default }; return $p.Value }
        function global:L([object]$List) { $r = [System.Collections.Generic.List[object]]::new(); foreach ($x in @($List)) { if ($null -ne $x) { $r.Add($x) } }; return ,$r }
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:FailLeft = [System.Collections.Generic.List[string]]::new()
        foreach ($f in @(V $state.Options 'Fail' @())) { if ($f) { $global:FailLeft.Add([string]$f) } }
        function global:Test-Fail([string]$Key) { if ($global:FailLeft.Remove($Key)) { if ($Key -like 'access:*') { throw [System.UnauthorizedAccessException]::new('Access is denied. SECRET-DETAIL-1234') }; throw "The SMS Provider reported an error: SECRET-DETAIL-1234 ($Key)." } }
        $global:C = @{}
        foreach ($p in $state.Classes.PSObject.Properties) { $global:C[$p.Name] = L $p.Value }
        $global:Details = @{}; foreach ($p in (V $state 'Details' ([pscustomobject]@{})).PSObject.Properties) { $global:Details[$p.Name] = $p.Value }
        $global:Windows = @{}; foreach ($p in (V $state 'Windows' ([pscustomobject]@{})).PSObject.Properties) { $global:Windows[$p.Name] = L $p.Value }
        $global:Auto = @{}; foreach ($p in (V $state 'AutoInstall' ([pscustomobject]@{})).PSObject.Properties) { $global:Auto[$p.Name] = [bool]$p.Value }
        $global:Now = [string](V $state 'Now' '2026-10-24T18:00:00')
        $global:Fmt = 'yyyy-MM-ddTHH:mm:ss'
        $global:DateColumns = @('DateCreated', 'LastActiveTime', 'LastRunTime', 'EnforcementDeadline', 'CreationTime', 'LastUpdateDate', 'ExpirationTime', 'StartTime', 'AgentTime')
        function global:Dt([string]$Text) { if (-not $Text) { return $null }; [datetime]::ParseExact($Text, $global:Fmt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]'AssumeUniversal,AdjustToUniversal') }
        function global:RowList([string]$Class) { if (-not $global:C.ContainsKey($Class)) { $global:C[$Class] = L @() }; return ,$global:C[$Class] }
        function global:Rows([string]$Class) { $list = RowList $Class; foreach ($r in $list) { $r } }

        $gen = V $state 'Generate' $null
        if ($null -ne $gen) {
            $genDevices = [int](V $gen 'Devices' 0)
            for ($i = 0; $i -lt $genDevices; $i++) { (RowList 'SMS_CM_RES_COLL_SMS00001').Add([pscustomobject]@{ ResourceID = 20000000 + $i; Name = ('GEN{0:D6}' -f $i); IsClient = $true; ClientVersion = '5.00.9146.1009'; LastActiveTime = $(if ($i % 10 -eq 0) { '2026-01-01T00:00:00' } else { '2026-10-20T00:00:00' }); SMBIOSGUID = [guid]::NewGuid().ToString() }) }
            $genApps = [int](V $gen 'Applications' 0)
            for ($i = 0; $i -lt $genApps; $i++) { (RowList 'SMS_Application').Add([pscustomobject]@{ CI_ID = 30000000 + $i; ModelID = 40000000 + $i; ModelName = ('ScopeId_GEN/Application_{0:D6}' -f $i); LocalizedDisplayName = ('Generated {0}' -f $i); IsLatest = $true; IsDeployed = $false; IsExpired = $false; IsSuperseded = $false; IsSuperseding = $false; IsEnabled = $true; HasContent = $true; Manufacturer = 'Gen'; SoftwareVersion = '1'; DateCreated = '2026-01-01T00:00:00'; NumberOfDeploymentTypes = 1; CIVersion = 1 }) }
            $genCollections = [int](V $gen 'Collections' 0)
            for ($i = 0; $i -lt $genCollections; $i++) { (RowList 'SMS_Collection').Add([pscustomobject]@{ CollectionID = ('G{0:D7}' -f $i); Name = ('Generated collection {0}' -f $i); MemberCount = 1; RefreshType = 1; LimitToCollectionID = 'SMS00001' }) }
        }

        function global:Eq([object]$Value, [string]$Wanted) { if ($Value -is [bool]) { return ([int]$Value) -eq [int]$Wanted }; return ([string]$Value) -ieq $Wanted }
        function global:Get-RowFilter([string]$Where) {
            if (-not $Where) { return { $true } }
            $expr = [regex]::Replace($Where, "(\w+) LIKE '([^']*)'", { param($m) "([string](V `$_ '$($m.Groups[1].Value)' '') -like '$($m.Groups[2].Value.Replace('%', '*'))')" })
            $expr = [regex]::Replace($expr, "(\w+) = '([^']*)'", { param($m) "(Eq (V `$_ '$($m.Groups[1].Value)' `$null) '$($m.Groups[2].Value)')" })
            $expr = [regex]::Replace($expr, "(\w+) = (\d+)", { param($m) "(Eq (V `$_ '$($m.Groups[1].Value)' `$null) '$($m.Groups[2].Value)')" })
            $expr = $expr -replace '\bAND\b', '-and' -replace '\bOR\b', '-or'
            return [scriptblock]::Create($expr)
        }
        function global:Shape([object]$Row, [string]$Columns) {
            $o = [ordered]@{}
            $names = if ($Columns -eq '*') { @($Row.PSObject.Properties | ForEach-Object Name) } else { $Columns -split ',\s*' }
            foreach ($c in $names) {
                $v = V $Row $c $null
                if ($c -in $global:DateColumns -and $null -ne $v) { $v = @($v | ForEach-Object { if ($_ -is [string]) { Dt $_ } else { $_ } }); if ($c -ne 'AgentTime') { $v = $v[0] } }
                if ($c -eq 'AgentName' -and $null -ne $v) { $v = @($v) }
                $o[$c] = $v
            }
            [pscustomobject]$o
        }
        function global:Get-Location { [pscustomobject]@{ Drive = [pscustomobject]@{ Name = 'MCM' }; Provider = [pscustomobject]@{ Name = 'CMSite' } } }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            if ($Query -notmatch '^SELECT (.+?) FROM (\w+)(?: WHERE (.+))?$') { throw "Unknown query: $Query" }
            $columns = $Matches[1]; $class = $Matches[2]; $where = $Matches[3]
            $global:Log.Add('Query:' + $class)
            Test-Fail ('query:' + $class)
            $f = Get-RowFilter $where
            $hits = @(Rows $class | Where-Object $f)
            if ($class -eq 'SMS_CollectionSettings' -and $columns -eq '*') {
                return @($hits | ForEach-Object {
                    $id = [string]$_.CollectionID
                    $o = [pscustomobject]@{ CollectionID = $id; ServiceWindows = @() }
                    $o | Add-Member -MemberType ScriptMethod -Name Get -Value { $this.ServiceWindows = @(if ($global:Windows.ContainsKey($this.CollectionID)) { $global:Windows[$this.CollectionID] | ForEach-Object { Shape $_ '*' } }) }
                    $o | Add-Member -MemberType ScriptMethod -Name Put -Value {
                        Test-Fail ('cmd:Put')
                        $keep = @($this.ServiceWindows | ForEach-Object { [string]$_.ServiceWindowID })
                        $before = @(if ($global:Windows.ContainsKey($this.CollectionID)) { $global:Windows[$this.CollectionID] })
                        $removed = @($before | Where-Object { $keep -notcontains [string]$_.ServiceWindowID } | ForEach-Object { [string]$_.ServiceWindowID })
                        $global:Log.Add('Cmd:SMS_CollectionSettings.ServiceWindows|' + $this.CollectionID + '|' + ($removed -join ','))
                        $global:Windows[$this.CollectionID] = L @($before | Where-Object { $keep -contains [string]$_.ServiceWindowID })
                    }
                    $o
                })
            }
            return @($hits | ForEach-Object { Shape $_ $columns })
        }
        function global:Q([string]$Value) { if ($Value -eq '$true' -or $Value -match '^\d{1,10}$') { return $Value }; return "'" + $Value.Replace("'", "''") + "'" }
        function global:Cmd([string]$Name, [object[]]$Pairs) {
            $text = $Name
            foreach ($p in $Pairs) { $text += ' -' + $p[0]; if ($p[0] -ne 'Force') { $text += ' ' + (Q ([string]$p[1])) } }
            $global:Log.Add('Cmd:' + $text)
            Test-Fail ('cmd:' + $Name)
        }
        function global:Remove-Rows([string]$Class, [scriptblock]$Match) { $list = RowList $Class; foreach ($r in @($list | Where-Object $Match)) { [void]$list.Remove($r) } }
        function global:Get-CMCollection { [CmdletBinding()] param([string]$Id)
            $global:Log.Add('Get-CMCollection:' + $Id)
            $row = Rows 'SMS_Collection' | Where-Object { $_.CollectionID -eq $Id } | Select-Object -First 1
            if ($null -eq $row) { return $null }
            $d = $global:Details[$Id]
            $rules = @(foreach ($r in @(V $d 'Rules' @())) { [pscustomobject]@{ ObjectClass = [string]$r } })
            $s = V $d 'Schedule' $null
            $sched = if ($null -ne $s) { [pscustomobject]@{ DaySpan = [int](V $s 'DaySpan' 0); HourSpan = [int](V $s 'HourSpan' 0); MinuteSpan = [int](V $s 'MinuteSpan' 0); StartTime = (Dt (V $s 'StartTime' '')) } } else { $null }
            [pscustomobject]@{ CollectionID = $Id; LimitToCollectionID = [string]$row.LimitToCollectionID; CollectionRules = $rules; RefreshSchedule = @($sched) }
        }
        function global:Get-CMApplication { [CmdletBinding()] param([string]$ModelName)
            $global:Log.Add('Get-CMApplication:' + $ModelName)
            Test-Fail ('cmd:Get-CMApplication')
            $row = Rows 'SMS_Application' | Where-Object { $_.ModelName -eq $ModelName -and $_.IsLatest } | Select-Object -First 1
            if ($null -eq $row) { return $null }
            $auto = if ($global:Auto.ContainsKey($ModelName) -and $global:Auto[$ModelName]) { 'true' } else { 'false' }
            [pscustomobject]@{ ModelName = $ModelName; SDMPackageXML = "<AppMgmtDigest xmlns=`"http://schemas.microsoft.com/SystemCenterConfigurationManager/2009/AppMgmtDigest`"><Application><AutoInstall>$auto</AutoInstall></Application></AppMgmtDigest>" }
        }
        function global:Remove-CMApplication { [CmdletBinding()] param([int]$Id, [switch]$Force)
            Cmd 'Remove-CMApplication' @(@('Id', $Id), @('Force', ''))
            $app = Rows 'SMS_Application' | Where-Object { [int]$_.CI_ID -eq $Id -and $_.IsLatest } | Select-Object -First 1
            if ($null -eq $app) { throw 'No object corresponds to the specified parameters.' }
            $deployed = @(Rows 'SMS_ApplicationAssignment' | Where-Object { [string]$_.AppModelID -eq [string]$app.ModelID }).Count
            $cis = @(Rows 'SMS_Application' | Where-Object { $_.ModelName -eq $app.ModelName } | ForEach-Object { [int]$_.CI_ID })
            $dependents = @(Rows 'SMS_AppDependenceRelation' | Where-Object { $cis -contains [int]$_.ToApplicationCIID }).Count
            if ($deployed -gt 0 -or $dependents -gt 0) { throw "Configuration Manager cannot delete this application because other applications or task sequences reference it or it is configured as a deployment. Number of dependent applications: $dependents Number of active deployments: $deployed" }
            Remove-Rows 'SMS_Application' { $_.ModelName -eq $app.ModelName }
        }
        function global:Remove-CMApplicationDeployment { [CmdletBinding()] param([string]$DeploymentId, [switch]$Force)
            Cmd 'Remove-CMApplicationDeployment' @(@('DeploymentId', $DeploymentId), @('Force', ''))
            $d = Rows 'SMS_ApplicationAssignment' | Where-Object { [string]$_.AssignmentUniqueID -eq $DeploymentId } | Select-Object -First 1
            if ($null -eq $d) { throw 'No object corresponds to the specified parameters.' }
            Remove-Rows 'SMS_ApplicationAssignment' { [string]$_.AssignmentUniqueID -eq $DeploymentId }
            Remove-Rows 'SMS_DeploymentSummary' { [string]$_.DeploymentID -eq $DeploymentId }
            if (@(Rows 'SMS_ApplicationAssignment' | Where-Object { [string]$_.AppModelID -eq [string]$d.AppModelID }).Count -eq 0) { foreach ($a in @(Rows 'SMS_Application' | Where-Object { [string]$_.ModelID -eq [string]$d.AppModelID })) { $a.IsDeployed = $false } }
        }
        function global:Remove-CMApplicationRevisionHistory { [CmdletBinding()] param([int]$Id, [int]$Revision, [switch]$Force)
            Cmd 'Remove-CMApplicationRevisionHistory' @(@('Id', $Id), @('Revision', $Revision), @('Force', ''))
            $app = Rows 'SMS_Application' | Where-Object { [int]$_.CI_ID -eq $Id } | Select-Object -First 1
            if ($null -eq $app) { throw 'No object corresponds to the specified parameters.' }
            $rev = Rows 'SMS_Application' | Where-Object { $_.ModelName -eq $app.ModelName -and [int]$_.CIVersion -eq $Revision } | Select-Object -First 1
            if ($null -eq $rev) { throw 'No object corresponds to the specified parameters.' }
            if ($rev.IsLatest) { throw "The application revision history $Revision can not be deleted." }
            [void](RowList 'SMS_Application').Remove($rev)
        }
        function global:Remove-ById([string]$Name, [string]$Class, [string]$Key, [string]$Param, [string]$Value) {
            Cmd $Name @(@($Param, $Value), @('Force', ''))
            $hit = @(Rows $Class | Where-Object { [string](V $_ $Key '') -eq $Value })
            if ($hit.Count -eq 0) { throw 'No object corresponds to the specified parameters.' }
            Remove-Rows $Class { [string](V $_ $Key '') -eq $Value }
        }
        function global:Remove-CMPackage { [CmdletBinding()] param([string]$Id, [switch]$Force) Remove-ById 'Remove-CMPackage' 'SMS_Package' 'PackageID' 'Id' $Id }
        function global:Remove-CMBootImage { [CmdletBinding()] param([string]$Id, [switch]$Force) Remove-ById 'Remove-CMBootImage' 'SMS_BootImagePackage' 'PackageID' 'Id' $Id }
        function global:Remove-CMDriverPackage { [CmdletBinding()] param([string]$Id, [switch]$Force) Remove-ById 'Remove-CMDriverPackage' 'SMS_DriverPackage' 'PackageID' 'Id' $Id }
        function global:Remove-CMSoftwareUpdateAutoDeploymentRule { [CmdletBinding()] param([int]$Id, [switch]$Force) Remove-ById 'Remove-CMSoftwareUpdateAutoDeploymentRule' 'SMS_AutoDeployment' 'AutoDeploymentID' 'Id' ([string]$Id) }
        function global:Remove-CMDevice { [CmdletBinding()] param([int]$ResourceId, [switch]$Force)
            Remove-ById 'Remove-CMDevice' 'SMS_CM_RES_COLL_SMS00001' 'ResourceID' 'ResourceId' ([string]$ResourceId)
            Remove-Rows 'SMS_R_System' { [string]$_.ResourceId -eq [string]$ResourceId }
        }
        function global:Remove-CMCollection { [CmdletBinding()] param([string]$Id, [switch]$Force)
            Cmd 'Remove-CMCollection' @(@('Id', $Id), @('Force', ''))
            if (@(Rows 'SMS_CollectionDependencies' | Where-Object { $_.SourceCollectionID -eq $Id }).Count -gt 0) { throw 'Failure deleting management object' }
            if (@(Rows 'SMS_Collection' | Where-Object { $_.CollectionID -eq $Id }).Count -eq 0) { throw 'No object corresponds to the specified parameters.' }
            Remove-Rows 'SMS_Collection' { $_.CollectionID -eq $Id }
            Remove-Rows 'SMS_CollectionDependencies' { $_.DependentCollectionID -eq $Id }
        }
        function global:Set-CMCollection { [CmdletBinding()] param([string]$CollectionId, [string]$RefreshType)
            Cmd 'Set-CMCollection' @(@('CollectionId', $CollectionId), @('RefreshType', $RefreshType))
            $row = Rows 'SMS_Collection' | Where-Object { $_.CollectionID -eq $CollectionId } | Select-Object -First 1
            if ($null -eq $row) { throw 'No object corresponds to the specified parameters.' }
            $row.RefreshType = switch ($RefreshType) { 'Continuous' { 4 } 'Manual' { 1 } 'Periodic' { 2 } 'Both' { 6 } }
        }
        function global:Set-CMApplication { [CmdletBinding()] param([string]$ModelName, [bool]$AutoInstall)
            Cmd 'Set-CMApplication' @(@('ModelName', $ModelName), @('AutoInstall', '$true'))
            $row = Rows 'SMS_Application' | Where-Object { $_.ModelName -eq $ModelName -and $_.IsLatest } | Select-Object -First 1
            if ($null -eq $row) { throw 'No object corresponds to the specified parameters.' }
            $old = $row.PSObject.Copy(); $old.IsLatest = $false; (RowList 'SMS_Application').Add($old)
            $row.CI_ID = [int]$row.CI_ID + 1; $row.CIVersion = [int]$row.CIVersion + 1
            $global:Auto[$ModelName] = $AutoInstall
        }
        function global:Set-CMSiteMaintenanceTask { [CmdletBinding()] param([string]$SiteCode, [string]$Name, [bool]$Enabled)
            Cmd 'Set-CMSiteMaintenanceTask' @(@('SiteCode', $SiteCode), @('Name', $Name), @('Enabled', '$true'))
            $row = Rows 'SMS_SCI_SQLTask' | Where-Object { $_.ItemName -eq $Name -and $_.SiteCode -eq $SiteCode } | Select-Object -First 1
            if ($null -eq $row) { throw 'No object corresponds to the specified parameters.' }
            $row.Enabled = $Enabled
        }
        function global:Invoke-CMSoftwareUpdateAutoDeploymentRule { [CmdletBinding()] param([int]$Id)
            Cmd 'Invoke-CMSoftwareUpdateAutoDeploymentRule' @(@('Id', $Id))
            $row = Rows 'SMS_AutoDeployment' | Where-Object { [int]$_.AutoDeploymentID -eq $Id } | Select-Object -First 1
            if ($null -eq $row) { throw 'No object corresponds to the specified parameters.' }
            if (-not [bool](V $state.Options 'AdrNoRun' $false)) { $row.LastRunTime = $global:Now }
        }

        Set-StrictMode -Version 2.0
        $parameters = @{}
        foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties) { $value = $p.Value; if ($value -is [object[]]) { $value = [string[]]@($value | ForEach-Object { [string]$_ }) }; $parameters[$p.Name] = $value }
        $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw -Encoding UTF8))
        $out = @(); $thrown = ''
        try { $out = @(& $block @parameters 3>$null) } catch { $thrown = [string]$_.Exception.Message }
        Set-StrictMode -Off
        $state.Options | Add-Member -NotePropertyName Fail -NotePropertyValue @($global:FailLeft) -Force
        $classes = [ordered]@{}
        foreach ($k in $global:C.Keys) { $classes[$k] = @($global:C[$k]) }
        $windowsOut = [ordered]@{}
        foreach ($k in $global:Windows.Keys) { $windowsOut[$k] = @($global:Windows[$k]) }
        $autoOut = [ordered]@{}
        foreach ($k in $global:Auto.Keys) { $autoOut[$k] = $global:Auto[$k] }
        $next = if ($null -ne $gen) { $state } else { [pscustomobject]@{ Options = $state.Options; Now = $global:Now; Classes = [pscustomobject]$classes; Details = (V $state 'Details' ([pscustomobject]@{})); Windows = [pscustomobject]$windowsOut; AutoInstall = [pscustomobject]$autoOut } }
        $lines = foreach ($o in $out) { ConvertTo-Json -InputObject $o -Depth 3 -Compress }
        $document = [pscustomobject]@{ Output = @($lines); Thrown = $thrown; Log = @($global:Log); State = $next }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 8 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    private JsonObject _state;

    public StubHygieneSite(JsonObject state)
    {
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
            "Windows PowerShell 5.1 is not on this computer, so the Site Hygiene script cannot be tested.");
        _state = state;
    }

    public List<string> Log { get; } = [];

    /// <summary>The JSON lines of the last run, as the session host writes them.</summary>
    public List<string> LastOutput { get; private set; } = [];

    public List<Dictionary<string, object?>> Runs { get; } = [];

    /// <summary>Called before each script run; lets a test change the site between a plan and its write.</summary>
    public Action<Dictionary<string, object?>, StubHygieneSite>? BeforeRun { get; set; }

    public JsonObject State => _state;

    public JsonArray Class(string name)
    {
        var classes = _state["Classes"]!.AsObject();
        return classes[name] as JsonArray ?? (JsonArray)(classes[name] = new JsonArray());
    }

    public void Fail(params string[] failures)
    {
        var options = _state["Options"]!.AsObject();
        if (options["Fail"] is not JsonArray list) options["Fail"] = list = new JsonArray();
        foreach (var f in failures) list.Add(f);
    }

    public IEnumerable<string> Commands => Log.Where(l => l.StartsWith("Cmd:", StringComparison.Ordinal)).Select(l => l[4..]);

    public int Queries(string? cls = null) => Log.Count(l => l.StartsWith("Query:", StringComparison.Ordinal) && (cls is null || l == "Query:" + cls));

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
        var folder = Path.Combine(Path.GetTempPath(), "apkg-shstub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var harness = Path.Combine(folder, "harness.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            var scriptPath = Path.Combine(folder, "script.ps1");
            var outPath = Path.Combine(folder, "out.json");
            var bom = new System.Text.UTF8Encoding(true);
            File.WriteAllText(harness, Harness, bom);
            File.WriteAllText(statePath, _state.ToJsonString(), bom);
            File.WriteAllText(parametersPath, parametersJson, bom);
            File.WriteAllText(scriptPath, scriptText, bom);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness, "-StatePath", statePath, "-ScriptPath", scriptPath, "-ParametersPath", parametersPath, "-OutPath", outPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(600_000), "The stub site did not finish.");
            Assert.True(File.Exists(outPath), "The stub site wrote no result: " + error.Result);
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = document.RootElement;
            _state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            Log.AddRange(Elements(root.GetProperty("Log")).Select(e => e.GetString() ?? ""));
            var lines = Elements(root.GetProperty("Output")).Select(e => e.GetString() ?? "").Where(l => l.Length > 0);
            LastOutput = lines.ToList();
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
