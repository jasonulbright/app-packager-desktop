using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// A site in a child Windows PowerShell: stub ConfigMgr cmdlets over a JSON state, as the lab
/// site behaves (rules are embedded objects of the collection with an ObjectClass; the query
/// rule cmdlets match a rule name literally and in any case, and a remove by name removes every
/// rule that matches; collection and query names are unique; a new collection or saved query
/// gets the next id; a query result is a copy of the object, not a live view; a failed cmdlet
/// throws; each listed failure happens once, also across runs). It runs the embedded Collections scripts under
/// strict mode as the session host does, or a function of a PowerShell module imported
/// read-only, and returns the output, the cmdlet calls and the number of query rules on each
/// collection after every rule change. A script that throws becomes a ScriptFailed session error.
/// </summary>
internal sealed class StubSitePowerShell : IConfigMgrSession
{
    private const string Harness = """
        param([string]$StatePath, [string]$Mode, [string]$ScriptPath, [string]$ParametersPath, [string]$ModulePath, [string]$Function, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        function global:V([object]$Object, [string]$Name, [object]$Default) { $p = $Object.PSObject.Properties[$Name]; if ($null -eq $p -or $null -eq $p.Value) { return $Default }; return $p.Value }
        $global:Options = $state.Options
        $global:Opt = @{}
        foreach ($p in $state.Options.PSObject.Properties) { $global:Opt[$p.Name] = $p.Value }
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:Counts = [System.Collections.Generic.List[string]]::new()
        $global:Cols = [System.Collections.Generic.List[object]]::new()
        $global:Queries = [System.Collections.Generic.List[object]]::new()
        $global:NewIds = [System.Collections.Generic.List[string]]::new()
        $global:Next = [int](V $state 'Next' 80)
        $global:NextQuery = [int](V $state 'NextQuery' 50)
        $global:Resources = @{}
        foreach ($r in @($state.Resources)) { if ($null -ne $r) { $global:Resources[[string]$r.Id] = [string]$r.Name } }
        function global:New-Schedule([string]$Token) { if (-not $Token) { return $null }; [pscustomobject]@{ Token = $Token } }
        function global:New-Rule([string]$Class, [string]$Name, [string]$Expression, [string]$Resource, [string]$Include, [string]$Exclude) {
            [pscustomobject]@{ ObjectClass = $Class; RuleName = $Name; QueryExpression = $Expression; ResourceID = $Resource; IncludeCollectionID = $Include; ExcludeCollectionID = $Exclude }
        }
        foreach ($c in @($state.Collections)) {
            if ($null -eq $c) { continue }
            $rules = [System.Collections.Generic.List[object]]::new()
            foreach ($r in @(V $c 'Rules' @())) { if ($null -ne $r) { $rules.Add((New-Rule ([string]$r.ObjectClass) ([string](V $r 'RuleName' '')) ([string](V $r 'QueryExpression' '')) ([string](V $r 'ResourceID' '')) ([string](V $r 'IncludeCollectionID' '')) ([string](V $r 'ExcludeCollectionID' '')))) } }
            $members = [System.Collections.Generic.List[object]]::new()
            foreach ($m in @(V $c 'Members' @())) { if ($null -ne $m) { $members.Add([pscustomobject]@{ ResourceID = [string]$m.ResourceID; Name = [string]$m.Name; IsClient = [bool](V $m 'IsClient' $true); IsActive = [bool](V $m 'IsActive' $true); IsDirect = [bool](V $m 'IsDirect' $false) }) } }
            $global:Cols.Add([pscustomobject]@{
                CollectionID = [string]$c.CollectionID; Name = [string]$c.Name; ObjectPath = [string](V $c 'ObjectPath' ''); LimitToCollectionID = [string](V $c 'LimitToCollectionID' '')
                LimitToCollectionName = ''; MemberCount = $members.Count; RefreshType = [int](V $c 'RefreshType' 2); RefreshSchedule = (New-Schedule ([string](V $c 'Schedule' '0001200000100038')))
                Comment = [string](V $c 'Comment' ''); ServiceWindowsCount = [int](V $c 'Windows' 0); Deployments = [int](V $c 'Deployments' 0)
                CollectionType = 2; CollectionRules = $rules; Members = $members })
        }
        foreach ($q in @(V $state 'Queries' @())) {
            if ($null -eq $q) { continue }
            $global:Queries.Add([pscustomobject]@{ QueryID = [string]$q.QueryID; Name = [string]$q.Name; Expression = [string]$q.Expression; TargetClassName = [string](V $q 'TargetClassName' 'SMS_R_System'); Comments = [string](V $q 'Comments' ''); LimitToCollectionID = [string](V $q 'LimitToCollectionID' '') })
        }
        function global:Get-Col([string]$Id) { $global:Cols | Where-Object { $_.CollectionID -eq $Id } | Select-Object -First 1 }
        function global:Snap([object]$C) {
            if ($null -eq $C) { return $null }
            $copy = $C.PSObject.Copy()
            $copy.CollectionRules = @($C.CollectionRules | ForEach-Object { $_.PSObject.Copy() })
            $limit = Get-Col $C.LimitToCollectionID
            $copy.LimitToCollectionName = if ($null -ne $limit) { $limit.Name } else { '' }
            if ($global:Opt.ContainsKey('NoObjectPath')) { $copy.PSObject.Properties.Remove('ObjectPath') }
            $copy
        }
        function global:Write-Count([string]$Id) { $c = Get-Col $Id; $global:Counts.Add($Id + '=' + @($c.CollectionRules | Where-Object { $_.ObjectClass -eq 'SMS_CollectionRuleQuery' }).Count) }
        $global:FailLeft = [System.Collections.Generic.List[string]]::new()
        foreach ($f in @(V $state.Options 'Fail' @())) { if ($f) { $global:FailLeft.Add([string]$f) } }
        function global:Test-Fail([string]$What, [string]$Name) { if ($global:FailLeft.Remove($What + ':' + $Name)) { throw 'The SMS Provider reported an error.' } }
        function global:Test-FailAfter([string]$What, [string]$Name) { if ($global:FailLeft.Remove('after-' + $What + ':' + $Name)) { throw 'The SMS Provider reported an error after the change.' } }
        function global:New-Token([string]$Unit, [int]$Count) {
            $v = ([uint64]1 -shl 48) -bor ([uint64]1 -shl 44) -bor ([uint64]56 -shl 38) -bor ([uint64]2 -shl 19)
            $shift = switch ($Unit) { 'Minutes' { 13 } 'Hours' { 8 } default { 3 } }
            '{0:X16}' -f ($v -bor ([uint64]$Count -shl $shift))
        }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            $global:Log.Add('Invoke-CMWmiQuery')
            if ($Query -match "FROM SMS_Collection WHERE CollectionType = 2 AND CollectionID = '(\w+)'") {
                $id = $Matches[1]
                if ($global:Opt.ContainsKey('HideNew') -and $global:NewIds.Contains($id)) { return @() }
                return @(Snap (Get-Col $id) | Where-Object { $null -ne $_ })
            }
            if ($Query -match 'FROM SMS_Collection WHERE CollectionType = 2$') { return @($global:Cols | ForEach-Object { Snap $_ }) }
            if ($Query -match "SMS_CollectionDependencies WHERE SourceCollectionID = '(\w+)'") {
                $id = $Matches[1]
                return @(foreach ($c in $global:Cols) {
                    if ($c.LimitToCollectionID -eq $id) { [pscustomobject]@{ DependentCollectionID = $c.CollectionID; RelationshipType = 1 } }
                    foreach ($r in $c.CollectionRules) {
                        if ($r.IncludeCollectionID -eq $id) { [pscustomobject]@{ DependentCollectionID = $c.CollectionID; RelationshipType = 2 } }
                        if ($r.ExcludeCollectionID -eq $id) { [pscustomobject]@{ DependentCollectionID = $c.CollectionID; RelationshipType = 3 } }
                    }
                })
            }
            if ($Query -match "FROM SMS_DeploymentSummary WHERE CollectionID = '(\w+)'") {
                $c = Get-Col $Matches[1]
                if ($null -eq $c) { return @() }
                return @(for ($i = 0; $i -lt $c.Deployments; $i++) { [pscustomobject]@{ CollectionID = $c.CollectionID; SoftwareName = "App $i" } })
            }
            if ($Query -match "^select count\(\*\) from SMS_FullCollectionMembership where CollectionID = '(\w+)'$") {
                $c = Get-Col $Matches[1]
                return @([pscustomobject]@{ Count = $(if ($null -eq $c) { 0 } else { $c.Members.Count }) })
            }
            if ($Query -match "FROM SMS_FullCollectionMembership WHERE CollectionID = '(\w+)'( ORDER BY Name, ResourceID)?$") {
                $c = Get-Col $Matches[1]
                if ($null -eq $c) { return @() }
                return @($c.Members | Sort-Object -Property @{ Expression = { $_.Name } }, @{ Expression = { [long]$_.ResourceID } } | ForEach-Object { $_.PSObject.Copy() })
            }
            if ($Query -match 'SELECT CollectionID, Name FROM SMS_Collection WHERE (.+)$') {
                $ids = @([regex]::Matches($Matches[1], "CollectionID = '(\w+)'") | ForEach-Object { $_.Groups[1].Value })
                return @($global:Cols | Where-Object { $ids -contains $_.CollectionID } | ForEach-Object { [pscustomobject]@{ CollectionID = $_.CollectionID; Name = $_.Name } })
            }
            if ($Query -match 'FROM SMS_ObjectContainerNode WHERE ObjectType = 5000$') { $global:Log.Add('Folders'); return @(V $global:Options 'Folders' @() | Where-Object { $null -ne $_ }) }
            if ($Query -match 'FROM SMS_ObjectContainerItem WHERE ObjectType = 5000$') { $global:Log.Add('FolderItems'); return @(V $global:Options 'FolderItems' @() | Where-Object { $null -ne $_ }) }
            if ($Query -match 'FROM SMS_R_System WHERE ResourceId = (\d+)$') { if ($global:Resources.ContainsKey($Matches[1])) { return @([pscustomobject]@{ ResourceId = $Matches[1]; Name = $global:Resources[$Matches[1]] }) } else { return @() } }
            if ($Query -match [string](V $global:Options 'Reject' 'xx') -or $Query -notmatch '^\s*select\s') { throw 'The SMS Provider reported an error.' }
            $extra = [int](V $global:Options 'ExtraRows' 0)
            $global:Log.Add('Rows')
            foreach ($key in @($global:Resources.Keys | Sort-Object)) { [pscustomobject]@{ PropertyNames = @('Name', 'ResourceID'); Name = $global:Resources[$key]; ResourceID = $key } }
            for ($i = 0; $i -lt $extra; $i++) { $global:Log.Add('Row'); [pscustomobject]@{ PropertyNames = @('Name', 'ResourceID'); Name = "PC$i"; ResourceID = [string](30000000 + $i) } }
        }
        function global:Get-CMCollection { [CmdletBinding()] param([string]$Name, [switch]$DisableWildcardHandling) $global:Cols | Where-Object { $_.Name -eq $Name } | ForEach-Object { [pscustomobject]@{ CollectionID = $_.CollectionID; Name = $_.Name; CollectionType = 2 } } }
        function global:Convert-CMSchedule { [CmdletBinding()] param([object]$InputObject, [string]$ScheduleString) if ($ScheduleString) { return (New-Schedule $ScheduleString) }; if ($null -ne $InputObject) { return [string]$InputObject.Token }; '' }
        function global:New-CMSchedule { [CmdletBinding()] param([string]$RecurInterval, [int]$RecurCount) $global:Log.Add(('New-CMSchedule:' + $RecurInterval + ':' + $RecurCount)); New-Schedule (New-Token $RecurInterval $RecurCount) }
        function global:New-CMDeviceCollection {
            [CmdletBinding()] param([string]$Name, [string]$LimitingCollectionId, [string]$RefreshType, [string]$Comment = '', [object]$RefreshSchedule)
            $global:Log.Add('New-Collection')
            Test-Fail 'create' $Name
            if ($null -ne (Get-CMCollection -Name $Name)) { throw 'The name is taken.' }
            $id = 'MCM000' + ('{0:X2}' -f $global:Next); $global:Next++
            $type = switch ($RefreshType) { 'Manual' { 1 } 'Periodic' { 2 } 'Continuous' { 4 } 'Both' { 6 } }
            $schedule = if ($null -ne $RefreshSchedule -and -not $global:Opt.ContainsKey('KeepSchedule')) { $RefreshSchedule } else { New-Schedule '0001200000100008' }
            $global:Cols.Add([pscustomobject]@{ CollectionID = $id; Name = $Name; ObjectPath = ''; LimitToCollectionID = $LimitingCollectionId; LimitToCollectionName = ''; MemberCount = 0; RefreshType = $type; RefreshSchedule = $schedule
                Comment = $Comment; ServiceWindowsCount = 0; Deployments = 0; CollectionType = 2; CollectionRules = [System.Collections.Generic.List[object]]::new(); Members = [System.Collections.Generic.List[object]]::new() })
            $global:NewIds.Add($id)
            Test-FailAfter 'create' $Name
            if ($global:Opt.ContainsKey('CreateReturnsNothing')) { return $null }
            [pscustomobject]@{ CollectionID = $id; Name = $Name }
        }
        function global:Copy-CMCollection {
            [CmdletBinding()] param([string]$Id, [string]$NewName)
            $global:Log.Add('Copy-Collection')
            Test-Fail 'copy' $NewName
            $source = Get-Col $Id
            $newId = 'MCM000' + ('{0:X2}' -f $global:Next); $global:Next++
            $copy = $source.PSObject.Copy()
            $copy.CollectionID = $newId; $copy.Name = $NewName; $copy.Members = [System.Collections.Generic.List[object]]::new(); $copy.MemberCount = 0; $copy.ServiceWindowsCount = 0; $copy.Deployments = 0
            $copy.CollectionRules = [System.Collections.Generic.List[object]]::new()
            foreach ($r in $source.CollectionRules) { $copy.CollectionRules.Add($r.PSObject.Copy()) }
            $global:Cols.Add($copy)
            $global:NewIds.Add($newId)
            Test-FailAfter 'copy' $NewName
            Snap $source
        }
        function global:Set-CMCollection {
            [CmdletBinding()] param([string]$CollectionId, [string]$Comment, [string]$NewName, [string]$LimitingCollectionId, [string]$RefreshType, [object]$RefreshSchedule)
            $global:Log.Add('Set-Collection:' + (@($PSBoundParameters.Keys | Where-Object { $_ -ne 'CollectionId' } | Sort-Object) -join ','))
            Test-Fail 'set' $CollectionId
            $c = Get-Col $CollectionId
            if ($PSBoundParameters.ContainsKey('Comment')) { $c.Comment = $Comment }
            if ($PSBoundParameters.ContainsKey('NewName')) { $c.Name = $NewName }
            if ($PSBoundParameters.ContainsKey('LimitingCollectionId')) { $c.LimitToCollectionID = $LimitingCollectionId }
            if ($PSBoundParameters.ContainsKey('RefreshType')) { $c.RefreshType = switch ($RefreshType) { 'Manual' { 1 } 'Periodic' { 2 } 'Continuous' { 4 } 'Both' { 6 } } }
            if ($PSBoundParameters.ContainsKey('RefreshSchedule') -and -not $global:Opt.ContainsKey('KeepSchedule')) { $c.RefreshSchedule = $RefreshSchedule }
            Test-FailAfter 'set' $CollectionId
        }
        function global:Remove-CMDeviceCollection {
            [CmdletBinding()] param([string]$CollectionId, [switch]$Force)
            $global:Log.Add('Remove-Collection')
            Test-Fail 'removecollection' $CollectionId
            [void]$global:Cols.Remove((Get-Col $CollectionId))
            Test-FailAfter 'removecollection' $CollectionId
        }
        function global:Invoke-CMCollectionUpdate { [CmdletBinding()] param([string]$CollectionId) $global:Log.Add('Invoke-CMCollectionUpdate') }
        function global:Get-CMDeviceCollectionQueryMembershipRule {
            [CmdletBinding()] param([string]$CollectionId, [string]$RuleName)
            $global:Log.Add('Get-Query:' + $RuleName)
            @((Get-Col $CollectionId).CollectionRules | Where-Object { $_.ObjectClass -eq 'SMS_CollectionRuleQuery' -and (-not $RuleName -or $_.RuleName -eq $RuleName) })
        }
        function global:Add-CMDeviceCollectionQueryMembershipRule {
            [CmdletBinding()] param([string]$CollectionId, [string]$RuleName, [string]$QueryExpression)
            $global:Log.Add('Add-Query:' + $RuleName)
            Test-Fail 'add' $RuleName
            if ($QueryExpression -match [string](V $global:Options 'Reject' 'xx')) { throw 'The SMS Provider reported an error.' }
            (Get-Col $CollectionId).CollectionRules.Add((New-Rule 'SMS_CollectionRuleQuery' $RuleName $QueryExpression '' '' ''))
            Write-Count $CollectionId
            Test-FailAfter 'add' $RuleName
        }
        function global:Remove-CMDeviceCollectionQueryMembershipRule {
            [CmdletBinding()] param([string]$CollectionId, [string]$RuleName, [switch]$Force)
            $global:Log.Add('Remove-Query:' + $RuleName)
            Test-Fail 'remove' $RuleName
            if ($global:FailLeft.Remove('ignore-remove:' + $RuleName)) { return }
            $c = Get-Col $CollectionId
            foreach ($r in @($c.CollectionRules | Where-Object { $_.ObjectClass -eq 'SMS_CollectionRuleQuery' -and $_.RuleName -eq $RuleName })) { [void]$c.CollectionRules.Remove($r) }
            Write-Count $CollectionId
            Test-FailAfter 'remove' $RuleName
        }
        function global:Add-CMDeviceCollectionDirectMembershipRule { [CmdletBinding()] param([string]$CollectionId, [long]$ResourceId) $global:Log.Add('Add-Direct'); Test-Fail 'add' ([string]$ResourceId); (Get-Col $CollectionId).CollectionRules.Add((New-Rule 'SMS_CollectionRuleDirect' $global:Resources[[string]$ResourceId] '' ([string]$ResourceId) '' '')); Test-FailAfter 'add' ([string]$ResourceId) }
        function global:Remove-CMDeviceCollectionDirectMembershipRule { [CmdletBinding()] param([string]$CollectionId, [long]$ResourceId, [switch]$Force) $global:Log.Add('Remove-Direct'); Test-Fail 'remove' ([string]$ResourceId); $c = Get-Col $CollectionId; foreach ($r in @($c.CollectionRules | Where-Object { $_.ResourceID -eq [string]$ResourceId })) { [void]$c.CollectionRules.Remove($r) } }
        function global:Add-CMDeviceCollectionIncludeMembershipRule { [CmdletBinding()] param([string]$CollectionId, [string]$IncludeCollectionId) $global:Log.Add('Add-Include'); Test-Fail 'add' $IncludeCollectionId; (Get-Col $CollectionId).CollectionRules.Add((New-Rule 'SMS_CollectionRuleIncludeCollection' (Get-Col $IncludeCollectionId).Name '' '' $IncludeCollectionId '')) }
        function global:Add-CMDeviceCollectionExcludeMembershipRule { [CmdletBinding()] param([string]$CollectionId, [string]$ExcludeCollectionId) $global:Log.Add('Add-Exclude'); Test-Fail 'add' $ExcludeCollectionId; (Get-Col $CollectionId).CollectionRules.Add((New-Rule 'SMS_CollectionRuleExcludeCollection' (Get-Col $ExcludeCollectionId).Name '' '' '' $ExcludeCollectionId)) }
        function global:Remove-CMDeviceCollectionIncludeMembershipRule { [CmdletBinding()] param([string]$CollectionId, [string]$IncludeCollectionId, [switch]$Force) $global:Log.Add('Remove-Include'); $c = Get-Col $CollectionId; foreach ($r in @($c.CollectionRules | Where-Object { $_.IncludeCollectionID -eq $IncludeCollectionId })) { [void]$c.CollectionRules.Remove($r) } }
        function global:Remove-CMDeviceCollectionExcludeMembershipRule { [CmdletBinding()] param([string]$CollectionId, [string]$ExcludeCollectionId, [switch]$Force) $global:Log.Add('Remove-Exclude'); $c = Get-Col $CollectionId; foreach ($r in @($c.CollectionRules | Where-Object { $_.ExcludeCollectionID -eq $ExcludeCollectionId })) { [void]$c.CollectionRules.Remove($r) } }
        function global:Get-CMQuery {
            [CmdletBinding()] param([string]$Id, [string]$Name, [switch]$DisableWildcardHandling)
            $global:Log.Add('Get-CMQuery')
            @($global:Queries | Where-Object { (-not $Id -or $_.QueryID -eq $Id) -and (-not $Name -or $_.Name -eq $Name) } | ForEach-Object { $_.PSObject.Copy() })
        }
        function global:New-CMQuery {
            [CmdletBinding()] param([string]$Name, [string]$Expression, [string]$Comment = '', [string]$TargetClassName = 'SMS_R_System', [string]$LimitToCollectionId = '')
            $global:Log.Add('New-CMQuery')
            Test-Fail 'newquery' $Name
            $id = 'MCM' + ('{0:D5}' -f $global:NextQuery); $global:NextQuery++
            $q = [pscustomobject]@{ QueryID = $id; Name = $Name; Expression = $Expression; TargetClassName = $TargetClassName; Comments = $Comment; LimitToCollectionID = $LimitToCollectionId }
            $global:Queries.Add($q)
            Test-FailAfter 'newquery' $Name
            if ($global:Opt.ContainsKey('CreateReturnsNothing')) { return $null }
            $q.PSObject.Copy()
        }
        function global:Set-CMQuery {
            [CmdletBinding()] param([string]$Id, [string]$Expression, [string]$Comment, [string]$NewName, [string]$TargetClassName, [string]$LimitToCollectionId)
            $global:Log.Add('Set-CMQuery:' + (@($PSBoundParameters.Keys | Where-Object { $_ -ne 'Id' } | Sort-Object) -join ','))
            Test-Fail 'setquery' $Id
            $q = $global:Queries | Where-Object { $_.QueryID -eq $Id } | Select-Object -First 1
            if ($PSBoundParameters.ContainsKey('Expression')) { $q.Expression = $Expression }
            if ($PSBoundParameters.ContainsKey('Comment')) { $q.Comments = $Comment }
            if ($PSBoundParameters.ContainsKey('NewName')) { $q.Name = $NewName }
            if ($PSBoundParameters.ContainsKey('TargetClassName')) { $q.TargetClassName = $TargetClassName }
            if ($PSBoundParameters.ContainsKey('LimitToCollectionId')) { $q.LimitToCollectionID = $LimitToCollectionId }
            Test-FailAfter 'setquery' $Id
        }
        function global:Remove-CMQuery {
            [CmdletBinding()] param([string]$Id, [switch]$Force)
            $global:Log.Add('Remove-CMQuery')
            Test-Fail 'removequery' $Id
            foreach ($q in @($global:Queries | Where-Object { $_.QueryID -eq $Id })) { [void]$global:Queries.Remove($q) }
            Test-FailAfter 'removequery' $Id
        }

        $out = @()
        $thrown = ''
        if ($Mode -eq 'script') {
            Set-StrictMode -Version 2.0
            $parameters = @{}
            foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $parameters[$p.Name] = $p.Value }
            $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw))
            try { $out = @(& $block @parameters 3>$null) } catch { $thrown = [string]$_.Exception.Message }
            Set-StrictMode -Off
        } else {
            Import-Module $ModulePath -Force -DisableNameChecking 3>$null 6>$null
            $arguments = @{}
            foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) { $arguments[$p.Name] = $p.Value }
            $error.Clear()
            try { $out = @(& $Function @arguments 6>$null) } catch { $out = @([pscustomobject]@{ Kind = 'Thrown'; Message = $_.Exception.Message }) }
        }
        $collections = @(foreach ($c in $global:Cols) {
            [pscustomobject]@{ CollectionID = $c.CollectionID; Name = $c.Name; ObjectPath = $c.ObjectPath; LimitToCollectionID = $c.LimitToCollectionID; Comment = $c.Comment; RefreshType = $c.RefreshType
                Schedule = $(if ($null -ne $c.RefreshSchedule) { [string]$c.RefreshSchedule.Token } else { '' }); Windows = $c.ServiceWindowsCount; Deployments = $c.Deployments
                Members = @($c.Members); Rules = @($c.CollectionRules) }
        })
        $state.Options | Add-Member -NotePropertyName Fail -NotePropertyValue @($global:FailLeft) -Force
        $document = [pscustomobject]@{ Output = $out; Thrown = $thrown; Log = @($global:Log); Counts = @($global:Counts)
            State = [pscustomobject]@{ Collections = $collections; Queries = @($global:Queries); Resources = $state.Resources; Options = $state.Options; Next = $global:Next; NextQuery = $global:NextQuery } }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 8 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    internal sealed record RunResult(IReadOnlyList<JsonElement> Output, IReadOnlyList<string> Log, IReadOnlyList<string> Counts);

    private JsonObject _state;

    public StubSitePowerShell(JsonObject state)
    {
        Assert.True(WindowsPowerShellPresent, "Windows PowerShell 5.1 is not on this computer, so the Collections scripts cannot be tested.");
        _state = state;
    }

    public List<string> Log { get; } = [];

    public List<string> Counts { get; } = [];

    /// <summary>The parameters of each script run, in order.</summary>
    public List<(string Script, Dictionary<string, object?> Parameters)> Runs { get; } = [];

    /// <summary>Called before each script run; lets a test change the site between a plan and its write.</summary>
    public Action<string, Dictionary<string, object?>>? BeforeRun { get; set; }

    /// <summary>Called after each script run; a returned exception is thrown although the run's changes stay made.</summary>
    public Func<string, Dictionary<string, object?>, Exception?>? AfterRun { get; set; }

    public static bool WindowsPowerShellPresent => File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));

    public static JsonObject State(object options, params object[] collections) => JsonSerializer.SerializeToNode(new
    {
        Collections = collections,
        Queries = Array.Empty<object>(),
        Resources = new[] { new { Id = "16777220", Name = "DC01" }, new { Id = "16777221", Name = "CLIENT01" } },
        Options = options
    })!.AsObject();

    public static object Collection(string id, string name, string limit, params object[] rules) =>
        new { CollectionID = id, Name = name, LimitToCollectionID = limit, Rules = rules };

    public static object QueryRule(string name, string expression) =>
        new { ObjectClass = "SMS_CollectionRuleQuery", RuleName = name, QueryExpression = expression, ResourceID = "", IncludeCollectionID = "", ExcludeCollectionID = "" };

    public static object DirectRule(string resourceId, string name) =>
        new { ObjectClass = "SMS_CollectionRuleDirect", RuleName = name, QueryExpression = "", ResourceID = resourceId, IncludeCollectionID = "", ExcludeCollectionID = "" };

    public static object IncludeRule(string id, string name) =>
        new { ObjectClass = "SMS_CollectionRuleIncludeCollection", RuleName = name, QueryExpression = "", ResourceID = "", IncludeCollectionID = id, ExcludeCollectionID = "" };

    /// <summary>A collection in the current state; changes to it are seen by the next script run.</summary>
    public JsonObject CollectionState(string collectionId) =>
        _state["Collections"]!.AsArray().Select(c => c!.AsObject()).Single(c => (string?)c["CollectionID"] == collectionId);

    public bool HasCollection(string collectionId) =>
        _state["Collections"]!.AsArray().Any(c => (string?)c!["CollectionID"] == collectionId);

    public void AddCollection(JsonObject collection) => _state["Collections"]!.AsArray().Add(collection);

    public IEnumerable<JsonObject> AllCollections => _state["Collections"]!.AsArray().Select(c => c!.AsObject());

    public JsonArray QueriesState => _state["Queries"] as JsonArray ?? (JsonArray)(_state["Queries"] = new JsonArray());

    public void AddQuery(string id, string name, string expression, string comment = "", string limit = "") =>
        QueriesState.Add(new JsonObject { ["QueryID"] = id, ["Name"] = name, ["Expression"] = expression, ["TargetClassName"] = "SMS_R_System", ["Comments"] = comment, ["LimitToCollectionID"] = limit });

    /// <summary>Adds a failure such as "add:Name", "after-create:Name" or "ignore-remove:Name" that happens once.</summary>
    public void Fail(params string[] failures)
    {
        var options = _state["Options"]!.AsObject();
        if (options["Fail"] is not JsonArray list) options["Fail"] = list = new JsonArray();
        foreach (var f in failures) list.Add(f);
    }

    public void SetOption(string name, JsonNode? value) => _state["Options"]!.AsObject()[name] = value;

    /// <summary>The query rules of a collection in the current state, as name and expression.</summary>
    public List<(string Name, string Expression)> QueryRules(string collectionId) =>
        _state["Collections"]!.AsArray().Where(c => (string?)c!["CollectionID"] == collectionId)
            .SelectMany(c => c!["Rules"] is JsonArray rules ? rules : [])
            .Where(r => (string?)r!["ObjectClass"] == "SMS_CollectionRuleQuery")
            .Select(r => ((string)r!["RuleName"]!, (string)r["QueryExpression"]!)).ToList();

    public RunResult RunModule(string modulePath, string function, object arguments) => Run("module", null, modulePath, function, JsonSerializer.Serialize(arguments)).Result;

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value);
        Runs.Add((script.Name, values));
        BeforeRun?.Invoke(script.Name, values);
        var (run, thrown) = Run("script", script.Text, null, null, JsonSerializer.Serialize(values));
        if (AfterRun?.Invoke(script.Name, values) is { } after) return Task.FromException<ConfigMgrCommandResult>(after);
        if (thrown.Length > 0)
            return Task.FromException<ConfigMgrCommandResult>(new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, $"The ConfigMgr command {script.Name} failed: {thrown}"));
        return Task.FromResult(new ConfigMgrCommandResult(run.Output, []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (RunResult Result, string Thrown) Run(string mode, string? scriptText, string? modulePath, string? function, string parametersJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-collstub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var harnessPath = Path.Combine(folder, "harness.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            var scriptPath = Path.Combine(folder, "script.ps1");
            var outPath = Path.Combine(folder, "out.json");
            File.WriteAllText(harnessPath, Harness, new System.Text.UTF8Encoding(true));
            File.WriteAllText(statePath, _state.ToJsonString(), new System.Text.UTF8Encoding(true));
            File.WriteAllText(parametersPath, parametersJson, new System.Text.UTF8Encoding(true));
            File.WriteAllText(scriptPath, scriptText ?? "", new System.Text.UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harnessPath, "-StatePath", statePath, "-Mode", mode,
                         "-ScriptPath", scriptPath, "-ParametersPath", parametersPath, "-ModulePath", modulePath ?? "", "-Function", function ?? "", "-OutPath", outPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The stub site did not finish.");
            Assert.True(File.Exists(outPath), "The stub site wrote no result: " + error.Result);
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = document.RootElement;
            _state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            var output = Elements(root.GetProperty("Output"));
            var log = Elements(root.GetProperty("Log")).Select(e => e.GetString() ?? "").ToList();
            var counts = Elements(root.GetProperty("Counts")).Select(e => e.GetString() ?? "").ToList();
            Log.AddRange(log);
            Counts.AddRange(counts);
            return (new RunResult(output, log, counts), root.GetProperty("Thrown").GetString() ?? "");
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
