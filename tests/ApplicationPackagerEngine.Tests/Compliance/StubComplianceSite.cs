using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Tests.Compliance;

/// <summary>
/// A ConfigMgr site in a child Windows PowerShell: stub cmdlets over a JSON state that behave as
/// the lab site does (a setting change gives the CI a new CI_ID and Get-CMConfigurationItem -Id
/// resolves an older ID to it; setting names need not be unique and Remove-CMComplianceSetting
/// removes every setting with the name; a DateTime setting with remediation is not stored and no
/// error is reported; a script setting refuses StringArray; ConfigMgr holds two deployments of one
/// baseline to one collection; New-CMBaselineDeployment returns another deployment than the one it
/// made; names are matched exactly). Each listed failure ("create:BaselineId", "remove:AssignmentId",
/// "set:AssignmentId", "add:SettingName", "removesetting:SettingName", "rename:SettingName",
/// "tag:BaselineId", "removebaseline:Id", "removeitem:Id", "query") happens once, also across runs.
/// It runs the embedded Compliance scripts under strict mode as the session host does.
/// </summary>
internal sealed class StubComplianceSite : IConfigMgrSession
{
    private const string Harness = """
        param([string]$StatePath, [string]$ScriptPath, [string]$ParametersPath, [string]$OutPath)
        $ErrorActionPreference = 'Stop'
        $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        function global:V([object]$Object, [string]$Name, [object]$Default) { if ($null -eq $Object) { return $Default }; $p = $Object.PSObject.Properties[$Name]; if ($null -eq $p -or $null -eq $p.Value) { return $Default }; return $p.Value }
        $global:Log = [System.Collections.Generic.List[string]]::new()
        $global:Opt = @{}
        foreach ($p in $state.Options.PSObject.Properties) { $global:Opt[$p.Name] = $p.Value }
        $global:Next = [long](V $state 'Next' 16790000)
        $global:NextAssignment = [long](V $state 'NextAssignment' 16777300)
        $global:FailLeft = [System.Collections.Generic.List[string]]::new()
        foreach ($f in @(V $state.Options 'Fail' @())) { if ($f) { $global:FailLeft.Add([string]$f) } }
        function global:Test-Fail([string]$Key) { if ($global:FailLeft.Remove($Key)) { throw 'The SMS Provider reported an error: SECRET-DETAIL-1234.' } }
        function global:L([object]$List) { $r = [System.Collections.Generic.List[object]]::new(); foreach ($x in @($List)) { if ($null -ne $x) { $r.Add($x) } }; return ,$r }
        $global:Items = L $state.Items
        foreach ($i in $global:Items) { $i.Settings = L (V $i 'Settings' @()) }
        $global:Baselines = L $state.Baselines
        foreach ($b in $global:Baselines) { $b.Items = @(V $b 'Items' @()) }
        $global:Assignments = L $state.Assignments
        $global:Collections = L $state.Collections
        $global:Aliases = @{}
        foreach ($a in @(V $state 'Aliases' @())) { if ($null -ne $a) { $global:Aliases[[string]$a.From] = [string]$a.To } }
        function global:Resolve-Ci([string]$Id) { $guard = 0; while ($global:Aliases.ContainsKey($Id) -and $guard -lt 100) { $Id = $global:Aliases[$Id]; $guard++ }; return $Id }
        function global:Get-Ci([string]$Id) { $r = Resolve-Ci $Id; $global:Items | Where-Object { [string]$_.Id -eq $r } | Select-Object -First 1 }
        function global:Bump-Ci([object]$Ci) {
            $old = [string]$Ci.Id; $new = [string]$global:Next; $global:Next++
            $global:Aliases[$old] = $new; $Ci.Id = $new; $Ci.Version = [int]$Ci.Version + 1
            foreach ($b in $global:Baselines) { $b.Items = @($b.Items | ForEach-Object { if ([string]$_ -eq $old) { $new } else { $_ } }) }
        }
        function global:Sdm-Type([string]$Type) { switch ($Type) { 'Integer' { 'Int64' } 'FloatingPoint' { 'Double' } default { $Type } } }
        function global:Hive-Name([string]$Hive) { switch ($Hive) { 'LocalMachine' { 'HKEY_LOCAL_MACHINE' } 'CurrentUser' { 'HKEY_CURRENT_USER' } 'ClassesRoot' { 'HKEY_CLASSES_ROOT' } 'CurrentConfig' { 'HKEY_CURRENT_CONFIG' } 'Users' { 'HKEY_USERS' } default { $Hive } } }
        function global:New-Xml([object]$Ci) {
            $d = 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/07/10/DesiredConfiguration'
            $r = 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/06/14/Rules'
            $x = New-Object System.Xml.XmlDocument
            $root = $x.AppendChild($x.CreateElement('DesiredConfigurationDigest', $d))
            $os = $root.AppendChild($x.CreateElement('OperatingSystem', $d))
            $settings = $os.AppendChild($x.CreateElement('Settings', $d)).AppendChild($x.CreateElement('RootComplexSetting', $d))
            $rules = $os.AppendChild($x.CreateElement('Rules', $d))
            function Ann([object]$Parent, [string]$Name, [string]$Description) {
                $a = $Parent.AppendChild($x.CreateElement('Annotation', $r))
                $n = $a.AppendChild($x.CreateElement('DisplayName', $r)); $n.SetAttribute('Text', $Name)
                $t = $a.AppendChild($x.CreateElement('Description', $r)); $t.SetAttribute('Text', $Description)
            }
            foreach ($s in $Ci.Settings) {
                $e = $settings.AppendChild($x.CreateElement('SimpleSetting', $d)); $e.SetAttribute('LogicalName', [string]$s.SettingId); $e.SetAttribute('DataType', (Sdm-Type ([string]$s.DataType)))
                Ann $e ([string]$s.Name) ([string](V $s 'Description' ''))
                if ($s.Kind -eq 'Registry') {
                    $src = $e.AppendChild($x.CreateElement('RegistryDiscoverySource', $d)); $src.SetAttribute('Hive', (Hive-Name ([string]$s.Hive))); $src.SetAttribute('Is64Bit', ([string][bool]$s.Is64Bit).ToLowerInvariant()); $src.SetAttribute('RemediateDword', ([string][bool](V $s 'RemediateDword' $false)).ToLowerInvariant())
                    $k = $src.AppendChild($x.CreateElement('Key', $d)); $k.InnerText = [string]$s.KeyName
                    $v = $src.AppendChild($x.CreateElement('ValueName', $d)); $v.InnerText = [string](V $s 'ValueName' '')
                } else {
                    $src = $e.AppendChild($x.CreateElement('ScriptDiscoverySource', $d)); $src.SetAttribute('Is64Bit', ([string][bool]$s.Is64Bit).ToLowerInvariant())
                    $b = $src.AppendChild($x.CreateElement('DiscoveryScriptBody', $d)); $b.SetAttribute('ScriptType', [string]$s.DiscoveryLanguage); $b.InnerText = [string]$s.DiscoveryScript
                    if ([string](V $s 'RemediationScript' '')) { $m = $src.AppendChild($x.CreateElement('RemediationScriptBody', $d)); $m.SetAttribute('ScriptType', [string]$s.RemediationLanguage); $m.InnerText = [string]$s.RemediationScript }
                }
                $rule = $rules.AppendChild($x.CreateElement('Rule', $r)); $rule.SetAttribute('id', 'Rule_' + [string]$s.SettingId); $rule.SetAttribute('Severity', [string]$s.Severity); $rule.SetAttribute('NonCompliantWhenSettingIsNotFound', ([string][bool]$s.Report).ToLowerInvariant())
                $ra = $rule.AppendChild($x.CreateElement('Annotation', $r)); $rn = $ra.AppendChild($x.CreateElement('DisplayName', $r)); $rn.SetAttribute('Text', [string]$s.RuleName)
                $ex = $rule.AppendChild($x.CreateElement('Expression', $r)); $op = $ex.AppendChild($x.CreateElement('Operator', $r)); $op.InnerText = [string]$s.Operator
                $ops = $ex.AppendChild($x.CreateElement('Operands', $r))
                $ref = $ops.AppendChild($x.CreateElement('SettingReference', $r)); $ref.SetAttribute('SettingLogicalName', [string]$s.SettingId); $ref.SetAttribute('Method', 'Value'); $ref.SetAttribute('Changeable', ([string][bool]$s.Remediate).ToLowerInvariant())
                if ($s.DataType -eq 'StringArray') { $list = $ops.AppendChild($x.CreateElement('ConstantValueList', $r)); foreach ($val in @($s.Values)) { $c = $list.AppendChild($x.CreateElement('ConstantValue', $r)); $c.SetAttribute('Value', [string]$val) } }
                else { $c = $ops.AppendChild($x.CreateElement('ConstantValue', $r)); $c.SetAttribute('Value', [string](@($s.Values)[0])) }
            }
            return $x.OuterXml
        }
        function global:Ci-Object([object]$Ci, [switch]$Lazy) {
            $o = [pscustomobject]@{ CI_ID = [long]$Ci.Id; LocalizedDisplayName = [string]$Ci.Name; LocalizedDescription = [string](V $Ci 'Description' ''); CIVersion = [int]$Ci.Version; DateLastModified = [datetime]'2026-10-01T10:00:00Z' }
            if ($Lazy) { $o | Add-Member -NotePropertyName SDMPackageXML -NotePropertyValue (New-Xml $Ci) }
            return $o
        }
        function global:Get-CMConfigurationItem {
            [CmdletBinding()] param([int]$Id, [string]$Name, [switch]$Fast)
            $global:Log.Add('Get-CMConfigurationItem')
            if ($PSBoundParameters.ContainsKey('Id')) { $ci = Get-Ci ([string]$Id); if ($null -eq $ci) { return }; return (Ci-Object $ci -Lazy:(-not $Fast)) }
            foreach ($ci in $global:Items) { if ([string]$ci.Name -like $Name) { Ci-Object $ci -Lazy:(-not $Fast) } }
        }
        function global:Get-CMBaseline {
            [CmdletBinding()] param([int]$Id, [string]$Name, [switch]$Fast)
            foreach ($b in $global:Baselines) {
                if (($PSBoundParameters.ContainsKey('Id') -and [string]$b.Id -eq [string]$Id) -or ($PSBoundParameters.ContainsKey('Name') -and [string]$b.Name -like $Name)) {
                    [pscustomobject]@{ CI_ID = [long]$b.Id; LocalizedDisplayName = [string]$b.Name; LocalizedDescription = [string](V $b 'Description' ''); CIVersion = [int](V $b 'Version' 1) }
                }
            }
        }
        function global:Get-CMCollection { [CmdletBinding()] param([string]$Name) foreach ($c in $global:Collections) { if ([string]$c.Name -like $Name) { [pscustomobject]@{ CollectionID = [string]$c.CollectionID; Name = [string]$c.Name; CollectionType = [int](V $c 'CollectionType' 2); MemberCount = [int](V $c 'Members' 0) } } } }
        function global:Convert-CMSchedule { [CmdletBinding()] param([string]$ScheduleString) [pscustomobject]@{ Token = $ScheduleString } }
        function global:Assignment-Object([object]$A) { [pscustomobject]@{ AssignmentID = [long]$A.AssignmentID; AssignmentUniqueID = [string]$A.UniqueId; ObjectClass = 'SMS_BaselineAssignment' } }
        function global:Get-CMBaselineDeployment { [CmdletBinding()] param([string]$DeploymentId, [switch]$Fast) foreach ($a in $global:Assignments) { if ([string]$a.UniqueId -eq $DeploymentId) { Assignment-Object $a } } }
        function global:Set-CMBaselineDeployment {
            [CmdletBinding()] param([object]$InputObject, [bool]$EnableEnforcement)
            $global:Log.Add('Set-CMBaselineDeployment')
            $a = $global:Assignments | Where-Object { [long]$_.AssignmentID -eq [long]$InputObject.AssignmentID } | Select-Object -First 1
            Test-Fail ('set:' + [string]$a.AssignmentID)
            if (-not $global:Opt.ContainsKey('IgnoreEnforcement')) { $a.Enforcement = $EnableEnforcement }
        }
        function global:Remove-CMBaselineDeployment {
            [CmdletBinding()] param([object]$InputObject, [switch]$Force)
            $global:Log.Add('Remove-CMBaselineDeployment:' + [string]$InputObject.AssignmentID)
            Test-Fail ('remove:' + [string]$InputObject.AssignmentID)
            $a = $global:Assignments | Where-Object { [long]$_.AssignmentID -eq [long]$InputObject.AssignmentID } | Select-Object -First 1
            [void]$global:Assignments.Remove($a)
            Test-Fail ('after-remove:' + [string]$InputObject.AssignmentID)
        }
        function global:New-CMBaselineDeployment {
            [CmdletBinding()] param([int]$Id, [string]$CollectionId, [bool]$EnableEnforcement, [bool]$OverrideServiceWindow, [bool]$GenerateAlert, [int]$ParameterValue, [bool]$MonitoredByScom, [object]$Schedule)
            $global:Log.Add('New-CMBaselineDeployment:' + $CollectionId)
            Test-Fail ('create:' + [string]$Id)
            $n = $global:NextAssignment; $global:NextAssignment++
            $global:Assignments.Add([pscustomobject]@{ AssignmentID = $n; UniqueId = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'; BaselineId = [string]$Id; CollectionID = $CollectionId
                Enforcement = $EnableEnforcement; Schedule = $(if ($null -ne $Schedule) { [string]$Schedule.Token } else { '0001200000100038' }); Override = ($EnableEnforcement -and $OverrideServiceWindow)
                Mom = $false; Alert = $GenerateAlert; AlertThreshold = $(if ($GenerateAlert) { $ParameterValue } else { 0 }); Summary = $null })
            Test-Fail ('after-create:' + [string]$Id)
            if ($global:Assignments.Count -gt 0) { Assignment-Object $global:Assignments[0] }
        }
        function global:Set-CMBaseline {
            [CmdletBinding()] param([int]$Id, [string]$Description)
            Test-Fail ('tag:' + [string]$Id)
            $b = $global:Baselines | Where-Object { [string]$_.Id -eq [string]$Id } | Select-Object -First 1
            $b.Description = $Description; $b.Version = [int](V $b 'Version' 1) + 1
            $global:Log.Add('Set-CMBaseline:' + [string]$Id)
        }
        function global:Remove-CMBaseline {
            [CmdletBinding()] param([int]$Id, [switch]$Force)
            $global:Log.Add('Remove-CMBaseline:' + [string]$Id)
            Test-Fail ('removebaseline:' + [string]$Id)
            if (@($global:Assignments | Where-Object { [string]$_.BaselineId -eq [string]$Id }).Count -gt 0) { throw 'The configuration item cannot be deleted because there are dependent items in use.' }
            [void]$global:Baselines.Remove(($global:Baselines | Where-Object { [string]$_.Id -eq [string]$Id } | Select-Object -First 1))
        }
        function global:Remove-CMConfigurationItem {
            [CmdletBinding()] param([int]$Id, [switch]$Force)
            $global:Log.Add('Remove-CMConfigurationItem:' + [string]$Id)
            Test-Fail ('removeitem:' + [string]$Id)
            if (@($global:Baselines | Where-Object { @($_.Items | ForEach-Object { [string]$_ }) -contains [string]$Id }).Count -gt 0) { throw 'The configuration item cannot be deleted because there are dependent items in use.' }
            [void]$global:Items.Remove((Get-Ci ([string]$Id)))
        }
        function global:Stub-AddSetting([object]$InputObject, [string]$Kind, [hashtable]$P) {
            $ci = Get-Ci ([string]$InputObject.CI_ID)
            Test-Fail ('add:' + [string]$P['Name'])
            $global:Log.Add('Add-Setting:' + $Kind + ':' + [string]$P['Name'])
            $dataType = [string]$P['DataType']
            if ($dataType -eq 'DateTime' -and $P.ContainsKey('Remediate')) { return }
            if ($global:Opt.ContainsKey('DropAdds')) { return }
            $operator = [string]$P['ExpressionOperator']
            if ($dataType -eq 'StringArray') { $operator = 'AllOf' } elseif ($operator -eq 'IsEquals') { $operator = 'Equals' }
            if ($dataType -ne 'StringArray' -and @($P['ExpectedValue']).Count -gt 1 -and $operator -eq 'Equals') { throw 'ExpectedValue must be a single element when using ExpressionOperator of type IsEquals.' }
            $id = $(if ($Kind -eq 'Registry') { 'RegSetting_' } else { 'ScriptSetting_' }) + [guid]::NewGuid().ToString()
            $ci.Settings.Add([pscustomobject]@{ SettingId = $id; Name = [string]$P['Name']; Description = [string]$P['Description']; Kind = $Kind; DataType = $dataType
                Hive = [string]$P['Hive']; KeyName = [string]$P['KeyName']; ValueName = [string]$P['ValueName']; Is64Bit = [bool]$P['Is64Bit']; RemediateDword = [bool]$P['RemediateDword']
                DiscoveryLanguage = [string]$P['DiscoveryScriptLanguage']; DiscoveryScript = [string]$P['DiscoveryScriptText']; RemediationLanguage = [string]$P['RemediationScriptLanguage']; RemediationScript = [string]$P['RemediationScriptText']
                RuleName = [string]$P['RuleName']; Operator = $operator; Values = @($P['ExpectedValue']); Severity = [string]$P['NoncomplianceSeverity']; Remediate = [bool]$P['Remediate']; Report = [bool]$P['ReportNoncompliance'] })
            Bump-Ci $ci
            Test-Fail ('after-add:' + [string]$P['Name'])
        }
        function global:Add-CMComplianceSettingRegistryKeyValue {
            [CmdletBinding()] param([object]$InputObject, [string]$Name, [ValidateSet('String','Integer','DateTime','FloatingPoint','Version','StringArray')][string]$DataType, [string]$Description, [switch]$ValueRule, [string]$RuleName, [string[]]$ExpectedValue, [string]$NoncomplianceSeverity, [string]$ExpressionOperator, [switch]$Remediate, [switch]$ReportNoncompliance, [switch]$Is64Bit, [string]$Hive, [string]$KeyName, [bool]$RemediateDword, [string]$ValueName)
            Stub-AddSetting $InputObject 'Registry' $PSBoundParameters
        }
        function global:Add-CMComplianceSettingScript {
            [CmdletBinding()] param([object]$InputObject, [string]$Name, [ValidateSet('String','DateTime','Integer','FloatingPoint','Version','Boolean')][string]$DataType, [string]$Description, [switch]$ValueRule, [string]$RuleName, [string[]]$ExpectedValue, [string]$NoncomplianceSeverity, [string]$ExpressionOperator, [switch]$Remediate, [switch]$ReportNoncompliance, [switch]$Is64Bit, [string]$DiscoveryScriptLanguage, [string]$DiscoveryScriptText, [string]$RemediationScriptLanguage, [string]$RemediationScriptText)
            Stub-AddSetting $InputObject 'Script' $PSBoundParameters
        }
        function global:Stub-SetSetting([object]$InputObject, [hashtable]$P) {
            $ci = Get-Ci ([string]$InputObject.CI_ID)
            $matches = @($ci.Settings | Where-Object { [string]$_.Name -eq [string]$P['SettingName'] })
            if ($matches.Count -eq 0) { throw 'No object corresponds to the specified parameters.' }
            if ($P.ContainsKey('NewSettingName')) { Test-Fail ('rename:' + [string]$P['SettingName']) } else { Test-Fail ('set:' + [string]$P['SettingName']) }
            foreach ($s in $matches) {
                if ($P.ContainsKey('NewSettingName')) { $s.Name = [string]$P['NewSettingName'] }
                foreach ($k in 'Description', 'Hive', 'KeyName', 'ValueName', 'Is64Bit', 'RemediateDword') { if ($P.ContainsKey($k)) { $s.$k = $P[$k] } }
                if ($P.ContainsKey('DiscoveryScriptText')) { $s.DiscoveryScript = [string]$P['DiscoveryScriptText']; $s.DiscoveryLanguage = [string]$P['DiscoveryScriptLanguage'] }
                if ($P.ContainsKey('RemediationScriptText')) { $s.RemediationScript = [string]$P['RemediationScriptText']; $s.RemediationLanguage = [string]$P['RemediationScriptLanguage'] }
                if ($P.ContainsKey('RemoveRemediationScript')) { $s.RemediationScript = ''; $s.RemediationLanguage = '' }
            }
            Bump-Ci $ci
        }
        function global:Set-CMComplianceSettingRegistryKeyValue { [CmdletBinding()] param([object]$InputObject, [string]$SettingName, [string]$NewSettingName, [string]$Description, [string]$Hive, [string]$KeyName, [string]$ValueName, [bool]$Is64Bit, [bool]$RemediateDword) $global:Log.Add('Set-Registry'); Stub-SetSetting $InputObject $PSBoundParameters }
        function global:Set-CMComplianceSettingScript { [CmdletBinding()] param([object]$InputObject, [string]$SettingName, [string]$NewSettingName, [string]$Description, [bool]$Is64Bit, [string]$DiscoveryScriptLanguage, [string]$DiscoveryScriptText, [string]$RemediationScriptLanguage, [string]$RemediationScriptText, [switch]$RemoveRemediationScript) $global:Log.Add('Set-Script'); Stub-SetSetting $InputObject $PSBoundParameters }
        function global:Remove-CMComplianceSetting {
            [CmdletBinding()] param([object]$InputObject, [string]$SettingName, [switch]$Force)
            $global:Log.Add('Remove-CMComplianceSetting:' + $SettingName)
            Test-Fail ('removesetting:' + $SettingName)
            $ci = Get-Ci ([string]$InputObject.CI_ID)
            foreach ($s in @($ci.Settings | Where-Object { [string]$_.Name -eq $SettingName })) { [void]$ci.Settings.Remove($s) }
            Bump-Ci $ci
        }
        function global:Invoke-CMWmiQuery {
            [CmdletBinding()] param([string]$Query, [string]$Option)
            $global:Log.Add('Query')
            Test-Fail 'query'
            if ($Query -match 'FROM SMS_ConfigurationBaselineInfo WHERE CI_ID = (\d+)') { return @($global:Baselines | Where-Object { [string]$_.Id -eq $Matches[1] } | ForEach-Object { [pscustomobject]@{ CI_ID = [long]$_.Id } }) }
            if ($Query -match 'FROM SMS_ConfigurationBaselineInfo') {
                return @($global:Baselines | ForEach-Object { [pscustomobject]@{ CI_ID = [long]$_.Id; CI_UniqueID = 'B' + $_.Id; LocalizedDisplayName = [string]$_.Name; LocalizedDescription = [string](V $_ 'Description' ''); CIVersion = [int](V $_ 'Version' 1); DateLastModified = [datetime]'2026-10-01T10:00:00Z'; ComplianceCount = [int](V $_ 'Compliant' 0); NonComplianceCount = [int](V $_ 'NonCompliant' 0); FailureCount = [int](V $_ 'Failed' 0) } })
            }
            if ($Query -match 'FROM SMS_CIRelation WHERE (FromCIID|ToCIID) IN \(([\d,]+)\)') {
                $side = $Matches[1]; $ids = $Matches[2] -split ','
                return @(foreach ($b in $global:Baselines) { foreach ($i in @($b.Items)) {
                    $rel = [pscustomobject]@{ FromCIID = [long]$b.Id; ToCIID = [long]$i; RelationType = 1 }
                    if (($side -eq 'FromCIID' -and $ids -contains [string]$b.Id) -or ($side -eq 'ToCIID' -and $ids -contains [string]$i)) { $rel } } })
            }
            if ($Query -match 'FROM SMS_ConfigurationItemLatest WHERE CIType_ID = 3') { return @($global:Items | ForEach-Object { [pscustomobject]@{ CI_ID = [long]$_.Id; LocalizedDisplayName = [string]$_.Name; LocalizedDescription = [string](V $_ 'Description' ''); CIVersion = [int]$_.Version; DateLastModified = [datetime]'2026-10-01T10:00:00Z'; CIType_ID = 3; IsUserDefined = $true } }) }
            if ($Query -match 'FROM SMS_BaselineAssignment') {
                $rows = @($global:Assignments | ForEach-Object { [pscustomobject]@{ AssignmentID = [long]$_.AssignmentID; AssignmentUniqueID = [string]$_.UniqueId; AssignmentName = 'A'; AssignedCIs = @([long]$_.BaselineId); TargetCollectionID = [string]$_.CollectionID; EnforcementEnabled = [bool]$_.Enforcement; EvaluationSchedule = [string]$_.Schedule; OverrideServiceWindows = [bool]$_.Override; RaiseMomAlertsOnFailure = [bool]$_.Mom; LastModificationTime = [datetime]'2026-10-01T10:00:00Z' } })
                if ($Query -match 'WHERE AssignmentID = (\d+)') { $id = $Matches[1]; return @($rows | Where-Object { [string]$_.AssignmentID -eq $id }) }
                if ($Query -match "WHERE TargetCollectionID = '(\w+)'") { $c = $Matches[1]; return @($rows | Where-Object { $_.TargetCollectionID -eq $c }) }
                return $rows
            }
            if ($Query -match 'FROM SMS_DeploymentSummary WHERE FeatureType = 6') {
                return @($global:Assignments | Where-Object { $null -ne (V $_ 'Summary' $null) } | ForEach-Object { $s = $_.Summary
                    [pscustomobject]@{ AssignmentID = [long]$_.AssignmentID; CollectionName = ''; NumberSuccess = [int](V $s 'Success' 0); NumberErrors = [int](V $s 'Errors' 0); NumberOther = [int](V $s 'Other' 0); NumberInProgress = [int](V $s 'InProgress' 0); NumberUnknown = [int](V $s 'Unknown' 0); NumberTargeted = [int](V $s 'Targeted' 0); SummarizationTime = [datetime](V $s 'Time' '2026-10-01T10:00:00Z'); ModificationTime = [datetime]'2026-10-01T10:00:00Z' } })
            }
            if ($Query -match 'FROM SMS_Collection WHERE CollectionID IN \((.+)\)') { $ids = @([regex]::Matches($Matches[1], "'(\w+)'") | ForEach-Object { $_.Groups[1].Value }); return @($global:Collections | Where-Object { $ids -contains [string]$_.CollectionID } | ForEach-Object { [pscustomobject]@{ CollectionID = [string]$_.CollectionID; Name = [string]$_.Name } }) }
            if ($Query -match "FROM SMS_Collection WHERE CollectionID = '(\w+)'") { $id = $Matches[1]; return @($global:Collections | Where-Object { [string]$_.CollectionID -eq $id } | ForEach-Object { [pscustomobject]@{ CollectionID = [string]$_.CollectionID; Name = [string]$_.Name; CollectionType = [int](V $_ 'CollectionType' 2); MemberCount = [int](V $_ 'Members' 0) } }) }
            if ($Query -match 'FROM SMS_Alert WHERE TypeID = 14') { return @($global:Assignments | Where-Object { [bool]$_.Alert } | ForEach-Object { [pscustomobject]@{ TypeInstanceID = [string]$_.UniqueId; ParameterValues = '<Parameters><Parameter index="3" isUserParameter="1" type="int">' + [string]$_.AlertThreshold + '</Parameter></Parameters>' } }) }
            throw ('Unexpected query: ' + $Query)
        }

        $out = @()
        $thrown = ''
        Set-StrictMode -Version 2.0
        $parameters = @{}
        foreach ($p in (Get-Content -LiteralPath $ParametersPath -Raw | ConvertFrom-Json).PSObject.Properties) {
            $value = $p.Value
            if ($value -is [System.Array]) { $value = [string[]]@($value | ForEach-Object { [string]$_ }) }
            $parameters[$p.Name] = $value
        }
        $block = [scriptblock]::Create((Get-Content -LiteralPath $ScriptPath -Raw))
        try { $out = @(& $block @parameters 3>$null) } catch { $thrown = [string]$_.Exception.Message }
        Set-StrictMode -Off
        $state.Options | Add-Member -NotePropertyName Fail -NotePropertyValue @($global:FailLeft) -Force
        $aliases = @($global:Aliases.Keys | ForEach-Object { [pscustomobject]@{ From = $_; To = $global:Aliases[$_] } })
        $document = [pscustomobject]@{ Output = $out; Thrown = $thrown; Log = @($global:Log)
            State = [pscustomobject]@{ Items = @($global:Items); Baselines = @($global:Baselines); Assignments = @($global:Assignments); Collections = @($global:Collections); Aliases = $aliases; Options = $state.Options; Next = $global:Next; NextAssignment = $global:NextAssignment } }
        [IO.File]::WriteAllText($OutPath, (ConvertTo-Json -InputObject $document -Depth 10 -Compress), (New-Object System.Text.UTF8Encoding $false))
        """;

    private JsonObject _state;

    public StubComplianceSite(JsonObject state)
    {
        Assert.True(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")),
            "Windows PowerShell 5.1 is not on this computer, so the Compliance scripts cannot be tested.");
        _state = state;
    }

    public List<string> Log { get; } = [];

    public List<(string Script, Dictionary<string, object?> Parameters)> Runs { get; } = [];

    /// <summary>Called before each script run; lets a test change the site between a plan and its write.</summary>
    public Action<StubComplianceSite, string, Dictionary<string, object?>>? BeforeRun { get; set; }

    public JsonObject State => _state;

    public JsonArray Items => (JsonArray)_state["Items"]!;
    public JsonArray Baselines => (JsonArray)_state["Baselines"]!;
    public JsonArray Assignments => (JsonArray)_state["Assignments"]!;
    public JsonArray Collections => (JsonArray)_state["Collections"]!;

    public JsonObject? Item(string id)
    {
        var resolved = id;
        var aliases = (_state["Aliases"] as JsonArray ?? []).Select(a => a!.AsObject()).ToDictionary(a => (string)a["From"]!, a => (string)a["To"]!);
        for (var i = 0; i < 100 && aliases.TryGetValue(resolved, out var next); i++) resolved = next;
        return Items.Select(i => i!.AsObject()).FirstOrDefault(i => (string?)i["Id"] == resolved);
    }

    public JsonObject? Baseline(string id) => Baselines.Select(b => b!.AsObject()).FirstOrDefault(b => (string?)b["Id"] == id);

    public JsonObject? Assignment(long id) => Assignments.Select(a => a!.AsObject()).FirstOrDefault(a => (long?)a["AssignmentID"] == id);

    public void Fail(params string[] failures)
    {
        var options = _state["Options"]!.AsObject();
        if (options["Fail"] is not JsonArray list) options["Fail"] = list = new JsonArray();
        foreach (var f in failures) list.Add(f);
    }

    public void SetOption(string name, JsonNode? value) => _state["Options"]!.AsObject()[name] = value;

    public static JsonObject NewState(object[]? items = null, object[]? baselines = null, object[]? assignments = null, object[]? collections = null) =>
        JsonSerializer.SerializeToNode(new
        {
            Items = items ?? [],
            Baselines = baselines ?? [],
            Assignments = assignments ?? [],
            Collections = collections ?? new object[]
            {
                new { CollectionID = "SMS00001", Name = "All Systems", CollectionType = 2, Members = 8 },
                new { CollectionID = "MCM00080", Name = "CCM Test 1", CollectionType = 2, Members = 1 },
                new { CollectionID = "MCM00081", Name = "CCM Test 2", CollectionType = 2, Members = 3 },
                new { CollectionID = "MCM00082", Name = "Users A", CollectionType = 1, Members = 2 }
            },
            Aliases = Array.Empty<object>(),
            Options = new { },
            Next = 16790000,
            NextAssignment = 16777300
        })!.AsObject();

    public static object RegistrySetting(string id, string name, string type = "Integer", string value = "1", bool remediate = false) => new
    {
        SettingId = id, Name = name, Description = "", Kind = "Registry", DataType = type, Hive = "LocalMachine", KeyName = @"SOFTWARE\Contoso", ValueName = name,
        Is64Bit = true, RemediateDword = type == "Integer", DiscoveryLanguage = "", DiscoveryScript = "", RemediationLanguage = "", RemediationScript = "",
        RuleName = name + " rule", Operator = type == "StringArray" ? "AllOf" : "Equals", Values = value.Split('\n'), Severity = "Warning", Remediate = remediate, Report = true
    };

    public static object ScriptSetting(string id, string name, string type = "Boolean", string value = "True") => new
    {
        SettingId = id, Name = name, Description = "", Kind = "Script", DataType = type, Hive = "", KeyName = "", ValueName = "", Is64Bit = true, RemediateDword = false,
        DiscoveryLanguage = "PowerShell", DiscoveryScript = "Write-Output $true", RemediationLanguage = "", RemediationScript = "",
        RuleName = name + " rule", Operator = "Equals", Values = new[] { value }, Severity = "Critical", Remediate = false, Report = true
    };

    public static object Ci(string id, string name, int version, params object[] settings) => new { Id = id, Name = name, Description = "", Version = version, Settings = settings };

    public static object CiBaseline(string id, string name, string description, params string[] items) => new { Id = id, Name = name, Description = description, Version = 2, Items = items };

    public static object Deployment(long id, string baseline, string collection, bool enforcement = false, string schedule = "0001200000100008", object? summary = null) => new
    {
        AssignmentID = id, UniqueId = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}", BaselineId = baseline, CollectionID = collection, Enforcement = enforcement,
        Schedule = schedule, Override = false, Mom = false, Alert = false, AlertThreshold = 0, Summary = summary
    };

    public Task<ConfigMgrCommandResult> InvokeAsync(ConfigMgrScript script, ConfigMgrParameters? parameters = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var values = (parameters ?? new ConfigMgrParameters()).ToDictionary(p => p.Key, p => p.Value);
        Runs.Add((script.Name, values));
        BeforeRun?.Invoke(this, script.Name, values);
        var (output, thrown) = Run(script.Text, JsonSerializer.Serialize(values));
        if (thrown.Length > 0)
            return Task.FromException<ConfigMgrCommandResult>(new ConfigMgrSessionException(ConfigMgrFailure.ScriptFailed, $"The ConfigMgr command {script.Name} failed: {thrown}"));
        return Task.FromResult(new ConfigMgrCommandResult(output, []));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private (IReadOnlyList<JsonElement> Output, string Thrown) Run(string scriptText, string parametersJson)
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-compstub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var harnessPath = Path.Combine(folder, "harness.ps1");
            var statePath = Path.Combine(folder, "state.json");
            var parametersPath = Path.Combine(folder, "parameters.json");
            var scriptPath = Path.Combine(folder, "script.ps1");
            var outPath = Path.Combine(folder, "out.json");
            var bom = new System.Text.UTF8Encoding(true);
            File.WriteAllText(harnessPath, Harness, bom);
            File.WriteAllText(statePath, _state.ToJsonString(), bom);
            File.WriteAllText(parametersPath, parametersJson, bom);
            File.WriteAllText(scriptPath, scriptText, bom);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harnessPath, "-StatePath", statePath, "-ScriptPath", scriptPath, "-ParametersPath", parametersPath, "-OutPath", outPath })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "The stub site did not finish.");
            Assert.True(File.Exists(outPath), "The stub site wrote no result: " + error.Result);
            using var document = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = document.RootElement;
            _state = JsonNode.Parse(root.GetProperty("State").GetRawText())!.AsObject();
            foreach (var key in new[] { "Items", "Baselines", "Assignments", "Collections", "Aliases" })
                if (_state[key] is not JsonArray) _state[key] = _state[key] is null ? new JsonArray() : new JsonArray(_state[key]!.DeepClone());
            Log.AddRange(Elements(root.GetProperty("Log")).Select(e => e.GetString() ?? ""));
            return (Elements(root.GetProperty("Output")), root.GetProperty("Thrown").GetString() ?? "");
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
