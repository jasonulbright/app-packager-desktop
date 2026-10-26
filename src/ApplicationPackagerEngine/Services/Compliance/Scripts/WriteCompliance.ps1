param(
    [ValidateSet('SetRemediation', 'CreateDeployment', 'RemoveDeployment', 'Redeploy', 'Reassign', 'AddSetting', 'EditSetting', 'RemoveSetting', 'Tag', 'RemoveObjects')][string]$Operation,
    [ValidateRange(0, 2147483647)][long]$AssignmentId = 0,
    [ValidateRange(0, 2147483647)][long]$BaselineId = 0,
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$CollectionId = '',
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$ToCollectionId = '',
    [bool]$ExpectedEnforcement = $false,
    [ValidatePattern('^[0-9A-Fa-f]{0,16}$')][string]$ExpectedSchedule = '',
    [bool]$Enforcement = $false,
    [ValidatePattern('^[0-9A-Fa-f]{0,16}$')][string]$ScheduleToken = '',
    [bool]$OverrideServiceWindow = $false,
    [bool]$GenerateAlert = $false,
    [ValidateRange(0, 100)][int]$AlertThreshold = 0,
    [bool]$MonitoredByScom = $false,
    [ValidatePattern('^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)?$')][string]$AlertAfter = '',
    [bool]$ExpectedOverride = $false,
    [bool]$ExpectedAlert = $false,
    [ValidateRange(0, 100)][int]$ExpectedThreshold = 0,
    [bool]$ExpectedScom = $false,
    [ValidatePattern('^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z)?$')][string]$ExpectedAlertAfter = '',
    [ValidateRange(0, 2147483647)][long]$CiId = 0,
    [ValidateRange(0, 2147483647)][int]$ExpectedRevision = 0,
    [ValidatePattern('^[A-Za-z0-9_\-]{0,128}$')][string]$SettingId = '',
    [ValidateSet('', 'InPlace', 'Replace')][string]$EditMode = '',
    [string]$OldName = '',
    [string]$TempName = '',
    [ValidateSet('', 'Registry', 'Script')][string]$SettingKind = '',
    [string]$Name = '',
    [string]$Description = '',
    [ValidateSet('', 'String', 'Integer', 'DateTime', 'FloatingPoint', 'Version', 'StringArray', 'Boolean')][string]$DataType = '',
    [ValidateSet('', 'LocalMachine', 'CurrentUser', 'ClassesRoot', 'CurrentConfig', 'Users')][string]$Hive = '',
    [string]$KeyName = '',
    [string]$ValueName = '',
    [bool]$Is64Bit = $true,
    [bool]$RemediateDword = $false,
    [ValidateSet('', 'PowerShell', 'VBScript', 'JScript')][string]$DiscoveryLanguage = '',
    [string]$DiscoveryScript = '',
    [ValidateSet('', 'PowerShell', 'VBScript', 'JScript')][string]$RemediationLanguage = '',
    [string]$RemediationScript = '',
    [string]$RuleName = '',
    [ValidateSet('', 'IsEquals', 'NotEquals', 'GreaterThan', 'LessThan', 'Between', 'GreaterEquals', 'LessEquals', 'BeginsWith', 'NotBeginsWith', 'EndsWith', 'NotEndsWith', 'Contains', 'NotContains', 'AllOf', 'OneOf', 'NoneOf')][string]$Operator = '',
    [string[]]$ExpectedValues = @(),
    [bool]$Remediate = $false,
    [bool]$Report = $true,
    [ValidateSet('', 'None', 'Informational', 'Warning', 'Critical', 'CriticalWithEvent')][string]$Severity = '',
    [string]$ExpectedDescription = '',
    [string]$NewDescription = '',
    [string[]]$DeploymentIds = @(),
    [string[]]$BaselineIds = @(),
    [string[]]$ItemIds = @()
)

# Only the validated numbers and IDs above reach WQL text. Every write is preceded by a read of
# the object it changes and followed by a read-back; a cmdlet result is never trusted as the
# stored state (New-CMBaselineDeployment can return another deployment than the one it made).

$CMPSSuppressFastNotUsedCheck = $true
$steps = New-Object System.Collections.Generic.List[string]

function Get-Prop {
    param([object]$Object, [string]$Property)
    if ($null -eq $Object) { return $null }
    $found = $Object.PSObject.Properties[$Property]
    if ($null -eq $found) { return $null }
    return $found.Value
}

function Invoke-Query {
    param([string]$Query)
    @(Invoke-CMWmiQuery -Option Fast -Query $Query) | Where-Object { $null -ne $_ }
}

function Write-Result {
    param([string]$Status, [string]$Detail = '', [string]$CreatedId = '', [string[]]$References = @())
    $ci = ''; $revision = 0
    if ($CiId -gt 0) {
        $latest = Get-ConfigItem $CiId
        if ($null -ne $latest) { $ci = [string]$latest.CI_ID; $revision = [int]$latest.CIVersion }
    }
    [pscustomobject]@{ Kind = 'Result'; Status = $Status; Detail = $Detail; Steps = @($steps); CreatedId = $CreatedId; References = @($References); CiId = $ci; Revision = $revision }
}

function Get-Assignment {
    param([long]$Id)
    Invoke-Query ('SELECT AssignmentID, AssignmentUniqueID, AssignedCIs, TargetCollectionID, EnforcementEnabled, EvaluationSchedule, OverrideServiceWindows, RaiseMomAlertsOnFailure FROM SMS_BaselineAssignment WHERE AssignmentID = ' + [string]$Id) | Select-Object -First 1
}

function Get-AssignmentIds {
    param([long]$Baseline, [string]$Collection)
    @(Invoke-Query ("SELECT AssignmentID, AssignedCIs, TargetCollectionID FROM SMS_BaselineAssignment WHERE TargetCollectionID = '{0}'" -f $Collection) |
        Where-Object { @(Get-Prop $_ 'AssignedCIs') -contains $Baseline } | ForEach-Object { [long]$_.AssignmentID })
}

function Get-UtcText {
    param([object]$Value)
    if ($Value -isnot [datetime]) { return '' }
    return $Value.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
}

# The alert of a deployment: -MonitoredByScom is stored on the SMS_Alert (the assignment's
# RaiseMomAlertsOnFailure stays false), the threshold is parameter 3, -PostponeDateTime is SkipUntil.
function Get-AlertState {
    param([object]$Assignment)
    $uid = ([string]$Assignment.AssignmentUniqueID).ToUpperInvariant()
    if ($uid -notmatch '^\{[0-9A-F-]{36}\}$') { return $null }
    $alert = Invoke-Query ("SELECT TypeInstanceID, ParameterValues, MonitoredByScom, SkipUntil FROM SMS_Alert WHERE TypeID = 14 AND TypeInstanceID = '{0}'" -f $uid) | Select-Object -First 1
    if ($null -eq $alert) { return $null }
    $threshold = 0
    try {
        $xml = [xml][string](Get-Prop $alert 'ParameterValues')
        $node = @($xml.Parameters.Parameter | Where-Object { $_.index -eq '3' }) | Select-Object -First 1
        if ($null -ne $node) { [void][int]::TryParse([string]$node.InnerText, [ref]$threshold) }
    } catch { $threshold = 0 }
    @{ Threshold = $threshold; Scom = [bool](Get-Prop $alert 'MonitoredByScom'); After = Get-UtcText (Get-Prop $alert 'SkipUntil') }
}

# $Extra holds the further stored values to compare: Alert, Threshold, Scom and After always,
# Override when present. ConfigMgr ignores -OverrideServiceWindow on a deployment without
# remediation, so a read-back after a create compares it only for a remediating deployment.
function Test-Assignment {
    param([object]$Assignment, [long]$Baseline, [string]$Collection, [bool]$Enforced, [string]$Schedule, [hashtable]$Extra)
    if ($null -eq $Assignment) { return 'missing' }
    $assigned = @(Get-Prop $Assignment 'AssignedCIs')
    if ($assigned.Count -ne 1 -or [long]$assigned[0] -ne $Baseline) { return 'changed' }
    if ([string]$Assignment.TargetCollectionID -ne $Collection) { return 'changed' }
    if ([bool](Get-Prop $Assignment 'EnforcementEnabled') -ne $Enforced) { return 'changed' }
    if ($Schedule -and ([string](Get-Prop $Assignment 'EvaluationSchedule')).ToUpperInvariant() -ne $Schedule.ToUpperInvariant()) { return 'changed' }
    if ($null -ne $Extra) {
        if ($Extra.ContainsKey('Override') -and [bool](Get-Prop $Assignment 'OverrideServiceWindows') -ne [bool]$Extra.Override) { return 'changed' }
        $alert = Get-AlertState $Assignment
        if (($null -ne $alert) -ne [bool]$Extra.Alert) { return 'changed' }
        if ($null -ne $alert -and ($alert.Threshold -ne [int]$Extra.Threshold -or $alert.Scom -ne [bool]$Extra.Scom -or $alert.After -ne [string]$Extra.After)) { return 'changed' }
    }
    return ''
}

function Get-Expected {
    @{ Override = $ExpectedOverride; Alert = $ExpectedAlert; Threshold = $ExpectedThreshold; Scom = $ExpectedScom; After = $ExpectedAlertAfter }
}

function Get-DeploymentObject {
    param([object]$Assignment)
    @(Get-CMBaselineDeployment -DeploymentId ([string]$Assignment.AssignmentUniqueID) -Fast) |
        Where-Object { $null -ne $_ -and [long](Get-Prop $_ 'AssignmentID') -eq [long]$Assignment.AssignmentID } | Select-Object -First 1
}

function Test-Collection {
    param([string]$Id)
    $c = Invoke-Query ("SELECT CollectionID, CollectionType FROM SMS_Collection WHERE CollectionID = '{0}'" -f $Id) | Select-Object -First 1
    if ($null -eq $c) { return 'collection-missing' }
    if ([int](Get-Prop $c 'CollectionType') -ne 2) { return 'not-device' }
    return ''
}

function Made {
    param([string]$Status, [long]$Id = 0, [string]$Detail = '')
    @{ Status = $Status; Id = $Id; Detail = $Detail }
}

function New-Deployment {
    param([long]$Baseline, [string]$Collection, [switch]$AllowExisting)
    if ($null -eq (Invoke-Query ('SELECT CI_ID FROM SMS_ConfigurationBaselineInfo WHERE CI_ID = ' + [string]$Baseline) | Select-Object -First 1)) { return (Made 'baseline-missing') }
    $problem = Test-Collection $Collection
    if ($problem) { return (Made $problem) }
    $before = @(Get-AssignmentIds $Baseline $Collection)
    if ($before.Count -gt 0 -and -not $AllowExisting) { return (Made 'deployment-exists') }
    $parameters = @{ Id = [int]$Baseline; CollectionId = $Collection; EnableEnforcement = $Enforcement; GenerateAlert = $GenerateAlert; MonitoredByScom = $MonitoredByScom; OverrideServiceWindow = $OverrideServiceWindow }
    if ($GenerateAlert) { $parameters.ParameterValue = $AlertThreshold }
    if ($GenerateAlert -and $AlertAfter) { $parameters.PostponeDateTime = [datetime]::Parse($AlertAfter, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal).ToLocalTime() }
    if ($ScheduleToken) { $parameters.Schedule = Convert-CMSchedule -ScheduleString $ScheduleToken }
    try {
        New-CMBaselineDeployment @parameters 3>$null | Out-Null
        $steps.Add('created')
    } catch {
        $made = @(Get-AssignmentIds $Baseline $Collection | Where-Object { $before -notcontains $_ })
        if ($made.Count -eq 1) { $steps.Add('created-with-error'); return (Made 'created-with-error' $made[0] $_.Exception.Message) }
        return (Made 'create-failed' 0 $_.Exception.Message)
    }
    $made = @(Get-AssignmentIds $Baseline $Collection | Where-Object { $before -notcontains $_ })
    if ($made.Count -ne 1) { return (Made 'create-unclear' 0 ('{0} new deployments' -f $made.Count)) }
    $stored = Get-Assignment $made[0]
    $schedule = if ($ScheduleToken) { $ScheduleToken } else { '' }
    $sent = @{ Alert = $GenerateAlert; Threshold = $(if ($GenerateAlert) { $AlertThreshold } else { 0 }); Scom = ($GenerateAlert -and $MonitoredByScom); After = $(if ($GenerateAlert) { $AlertAfter } else { '' }) }
    if ($Enforcement) { $sent.Override = $OverrideServiceWindow }
    if ((Test-Assignment $stored $Baseline $Collection $Enforcement $schedule $sent) -ne '') { return (Made 'stored-differs' $made[0]) }
    return (Made 'ok' $made[0])
}

function Remove-Deployment {
    param([object]$Assignment)
    $object = Get-DeploymentObject $Assignment
    if ($null -eq $object) { return 'missing' }
    try {
        Remove-CMBaselineDeployment -InputObject $object -Force
        $steps.Add('removed:Deployment:' + [string]$Assignment.AssignmentID)
    } catch {
        if ($null -eq (Get-Assignment ([long]$Assignment.AssignmentID))) { $steps.Add('removed:Deployment:' + [string]$Assignment.AssignmentID); return 'ok' }
        return 'remove-failed'
    }
    if ($null -ne (Get-Assignment ([long]$Assignment.AssignmentID))) { return 'remove-failed' }
    return 'ok'
}

# A setting change can give the CI a new CI_ID; Get-CMConfigurationItem -Id resolves an older ID
# of the same CI to the latest one, a query on the ID does not.
function Get-ConfigItem {
    param([long]$Id, [switch]$Lazy)
    if ($Lazy) { return (Get-CMConfigurationItem -Id ([int]$Id) -WarningAction SilentlyContinue | Select-Object -First 1) }
    Get-CMConfigurationItem -Id ([int]$Id) -Fast -WarningAction SilentlyContinue | Select-Object -First 1
}

function Get-SettingNames {
    param([object]$Item)
    $xml = [xml][string](Get-Prop $Item 'SDMPackageXML')
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('d', 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/07/10/DesiredConfiguration')
    $ns.AddNamespace('r', 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/06/14/Rules')
    foreach ($s in @($xml.SelectNodes('//d:SimpleSetting', $ns))) {
        $display = $s.SelectSingleNode('r:Annotation/r:DisplayName/@Text', $ns)
        [pscustomobject]@{ SettingId = [string]$s.GetAttribute('LogicalName'); Name = $(if ($null -ne $display) { [string]$display.Value } else { '' }) }
    }
}

function Add-Setting {
    param([object]$Item, [string]$SettingName)
    # A string array rule is stored as All of whatever operator is given, but the cmdlet asks for
    # an operator and refuses more than one value with IsEquals.
    $operatorSent = if ($DataType -eq 'StringArray') { 'AllOf' } else { $Operator }
    $common = @{ InputObject = $Item; Name = $SettingName; DataType = $DataType; Description = $Description; ValueRule = $true; RuleName = $RuleName; ExpectedValue = @($ExpectedValues); NoncomplianceSeverity = $Severity; ExpressionOperator = $operatorSent }
    if ($Remediate) { $common.Remediate = $true }
    if ($Report) { $common.ReportNoncompliance = $true }
    if ($Is64Bit) { $common.Is64Bit = $true }
    if ($SettingKind -eq 'Registry') {
        $common.Hive = $Hive; $common.KeyName = $KeyName; $common.RemediateDword = $RemediateDword
        if ($ValueName) { $common.ValueName = $ValueName }
        Add-CMComplianceSettingRegistryKeyValue @common | Out-Null
    } else {
        $common.DiscoveryScriptLanguage = $DiscoveryLanguage; $common.DiscoveryScriptText = $DiscoveryScript
        if ($RemediationScript) { $common.RemediationScriptLanguage = $RemediationLanguage; $common.RemediationScriptText = $RemediationScript }
        Add-CMComplianceSettingScript @common | Out-Null
    }
}

function Find-Settings {
    param([long]$Id, [string]$SettingName)
    $item = Get-ConfigItem $Id -Lazy
    if ($null -eq $item) { return @() }
    @(Get-SettingNames $item | Where-Object { $_.Name -eq $SettingName })
}

# The stored form of one setting and its rules. A new revision rewrites the Version attribute of
# every SettingReference and nothing else of an unchanged setting, so that attribute is left out.
function Get-SettingFragment {
    param([long]$Id, [string]$LogicalName)
    $item = Get-ConfigItem $Id -Lazy
    if ($null -eq $item) { return '' }
    $xml = [xml][string](Get-Prop $item 'SDMPackageXML')
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace('d', 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/07/10/DesiredConfiguration')
    $ns.AddNamespace('r', 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/06/14/Rules')
    $setting = @($xml.SelectNodes('//d:SimpleSetting', $ns) | Where-Object { [string]$_.GetAttribute('LogicalName') -ceq $LogicalName })
    if ($setting.Count -ne 1) { return '' }
    $rules = @($xml.SelectNodes('//r:Rule', $ns) | Where-Object {
        @($_.SelectNodes('.//r:SettingReference', $ns) | Where-Object { [string]$_.GetAttribute('SettingLogicalName') -ceq $LogicalName }).Count -gt 0 })
    foreach ($reference in @($rules | ForEach-Object { $_.SelectNodes('.//r:SettingReference', $ns) })) { $reference.RemoveAttribute('Version') }
    return $setting[0].OuterXml + (($rules | ForEach-Object { $_.OuterXml }) -join '')
}

function Test-Item {
    $item = Get-ConfigItem $CiId
    if ($null -eq $item) { return 'item-missing' }
    if ($ExpectedRevision -gt 0 -and [int](Get-Prop $item 'CIVersion') -ne $ExpectedRevision) { return 'changed' }
    return ''
}

switch ($Operation) {
    'SetRemediation' {
        $a = Get-Assignment $AssignmentId
        $state = Test-Assignment $a $BaselineId $CollectionId $ExpectedEnforcement $ExpectedSchedule (Get-Expected)
        if ($state) { Write-Result $state; return }
        $object = Get-DeploymentObject $a
        if ($null -eq $object) { Write-Result 'missing'; return }
        try { Set-CMBaselineDeployment -InputObject $object -EnableEnforcement $Enforcement; $steps.Add('set') }
        catch { $steps.Add('set-error'); $detail = $_.Exception.Message; $now = Get-Assignment $AssignmentId
            if ($null -ne $now -and [bool](Get-Prop $now 'EnforcementEnabled') -eq $Enforcement) { Write-Result 'partial' $detail } else { Write-Result 'failed' $detail }; return }
        $now = Get-Assignment $AssignmentId
        if ($null -eq $now -or [bool](Get-Prop $now 'EnforcementEnabled') -ne $Enforcement) { Write-Result 'stored-differs'; return }
        Write-Result 'ok'
    }
    'CreateDeployment' {
        $made = New-Deployment $BaselineId $CollectionId
        Write-Result $made.Status ([string]$made.Detail) $(if ($made.Id -gt 0) { [string]$made.Id } else { '' })
    }
    'RemoveDeployment' {
        $a = Get-Assignment $AssignmentId
        $state = Test-Assignment $a $BaselineId $CollectionId $ExpectedEnforcement $ExpectedSchedule (Get-Expected)
        if ($state) { Write-Result $state; return }
        Write-Result (Remove-Deployment $a)
    }
    'Redeploy' {
        # The new deployment is made before the old one goes: ConfigMgr holds two deployments of
        # one baseline to one collection, so the collection is never without the deployment.
        $a = Get-Assignment $AssignmentId
        $state = Test-Assignment $a $BaselineId $CollectionId $ExpectedEnforcement $ExpectedSchedule (Get-Expected)
        if ($state) { Write-Result $state; return }
        $made = New-Deployment $BaselineId $CollectionId -AllowExisting
        if ($made.Status -notin 'ok', 'created-with-error', 'stored-differs') { Write-Result $made.Status ([string]$made.Detail); return }
        # The old deployment goes only when the new one holds the captured values.
        if ($made.Status -ne 'ok') { Write-Result 'replacement-differs' ([string]$made.Detail) ([string]$made.Id); return }
        $removed = Remove-Deployment $a
        if ($removed -ne 'ok') { Write-Result 'old-remains' '' ([string]$made.Id); return }
        Write-Result 'ok' '' ([string]$made.Id)
    }
    'Reassign' {
        $a = Get-Assignment $AssignmentId
        $state = Test-Assignment $a $BaselineId $CollectionId $ExpectedEnforcement $ExpectedSchedule (Get-Expected)
        if ($state) { Write-Result $state; return }
        $made = New-Deployment $BaselineId $ToCollectionId
        if ($made.Status -notin 'ok', 'created-with-error', 'stored-differs') { Write-Result $made.Status ([string]$made.Detail); return }
        # The old deployment goes only when the new one holds the captured values.
        if ($made.Status -ne 'ok') { Write-Result 'replacement-differs' ([string]$made.Detail) ([string]$made.Id); return }
        $removed = Remove-Deployment $a
        if ($removed -ne 'ok') { Write-Result 'old-remains' '' ([string]$made.Id); return }
        Write-Result 'ok' '' ([string]$made.Id)
    }
    'AddSetting' {
        $problem = Test-Item
        if ($problem) { Write-Result $problem; return }
        if (@(Find-Settings $CiId $Name).Count -gt 0) { Write-Result 'setting-name-taken'; return }
        $item = Get-ConfigItem $CiId
        try { Add-Setting $item $Name; $steps.Add('added') } catch { $detail = $_.Exception.Message
            $found = @(Find-Settings $CiId $Name)
            if ($found.Count -eq 1) { Write-Result 'partial' $detail $found[0].SettingId } else { Write-Result 'failed' $detail }; return }
        $found = @(Find-Settings $CiId $Name)
        if ($found.Count -ne 1) { Write-Result 'not-stored'; return }
        Write-Result 'ok' '' $found[0].SettingId
    }
    'RemoveSetting' {
        $problem = Test-Item
        if ($problem) { Write-Result $problem; return }
        $found = @(Find-Settings $CiId $OldName)
        if ($found.Count -eq 0 -or @($found | Where-Object { $_.SettingId -ceq $SettingId }).Count -eq 0) { Write-Result 'setting-missing'; return }
        if ($found.Count -gt 1) { Write-Result 'setting-duplicate'; return }
        $item = Get-ConfigItem $CiId
        try { Remove-CMComplianceSetting -InputObject $item -SettingName $OldName -Force; $steps.Add('removed') } catch { $detail = $_.Exception.Message
            if (@(Find-Settings $CiId $OldName).Count -eq 0) { Write-Result 'partial' $detail } else { Write-Result 'failed' $detail }; return }
        if (@(Find-Settings $CiId $OldName).Count -ne 0) { Write-Result 'not-stored'; return }
        Write-Result 'ok'
    }
    'EditSetting' {
        $problem = Test-Item
        if ($problem) { Write-Result $problem; return }
        $found = @(Find-Settings $CiId $OldName)
        if ($found.Count -eq 0 -or @($found | Where-Object { $_.SettingId -ceq $SettingId }).Count -eq 0) { Write-Result 'setting-missing'; return }
        if ($found.Count -gt 1) { Write-Result 'setting-duplicate'; return }
        if (@(Find-Settings $CiId $Name | Where-Object { $_.SettingId -cne $SettingId }).Count -gt 0) { Write-Result 'setting-name-taken'; return }
        $captured = Get-SettingFragment $CiId $SettingId
        $item = Get-ConfigItem $CiId
        if ($EditMode -eq 'InPlace') {
            $parameters = @{ InputObject = $item; SettingName = $OldName; Description = $Description; Is64Bit = $Is64Bit }
            if ($Name -cne $OldName) { $parameters.NewSettingName = $Name }
            try {
                if ($SettingKind -eq 'Registry') {
                    $parameters.Hive = $Hive; $parameters.KeyName = $KeyName; $parameters.ValueName = $ValueName; $parameters.RemediateDword = $RemediateDword
                    Set-CMComplianceSettingRegistryKeyValue @parameters | Out-Null
                } else {
                    $parameters.DiscoveryScriptLanguage = $DiscoveryLanguage; $parameters.DiscoveryScriptText = $DiscoveryScript
                    if ($RemediationScript) { $parameters.RemediationScriptLanguage = $RemediationLanguage; $parameters.RemediationScriptText = $RemediationScript }
                    else { $parameters.RemoveRemediationScript = $true }
                    Set-CMComplianceSettingScript @parameters | Out-Null
                }
                $steps.Add('set')
            } catch {
                # The cmdlet can commit the new revision and then fail: an unchanged revision
                # proves nothing was stored; otherwise the setting found by its ID is reported.
                $detail = $_.Exception.Message
                $now = Get-ConfigItem $CiId
                if ($null -ne $now -and $ExpectedRevision -gt 0 -and [int](Get-Prop $now 'CIVersion') -eq $ExpectedRevision) { Write-Result 'failed' $detail; return }
                if (@(Find-Settings $CiId $Name | Where-Object { $_.SettingId -ceq $SettingId }).Count -eq 1) { Write-Result 'set-uncertain' $detail $SettingId } else { Write-Result 'set-unknown' $detail }
                return
            }
            $now = @(Find-Settings $CiId $Name | Where-Object { $_.SettingId -ceq $SettingId })
            if ($now.Count -ne 1) { Write-Result 'not-stored' '' $SettingId; return }
            Write-Result 'ok' '' $SettingId
            return
        }
        # Replace: the new setting is added under a temporary name first, so the CI never lacks
        # the setting; the old one goes next and the new one then takes the final name.
        if (@(Find-Settings $CiId $TempName).Count -gt 0) { Write-Result 'setting-name-taken'; return }
        try { Add-Setting $item $TempName; $steps.Add('added') } catch { $detail = $_.Exception.Message
            $made = @(Find-Settings $CiId $TempName)
            if ($made.Count -eq 1) { $steps.Add('added-with-error'); Write-Result 'replace-added' $detail $made[0].SettingId } else { Write-Result 'failed' $detail }; return }
        $made = @(Find-Settings $CiId $TempName)
        if ($made.Count -ne 1) { Write-Result 'not-stored'; return }
        $newId = $made[0].SettingId
        # The old setting is removed only while it is as captured and no other revision came in
        # since the add; otherwise another writer changed the CI and both settings stay.
        $item = Get-ConfigItem $CiId
        $revisionNow = [int](Get-Prop $item 'CIVersion')
        if (($ExpectedRevision -gt 0 -and $revisionNow -ne $ExpectedRevision + 1) -or -not $captured -or (Get-SettingFragment $CiId $SettingId) -cne $captured) {
            Write-Result 'replace-conflict' '' $newId; return
        }
        try { Remove-CMComplianceSetting -InputObject $item -SettingName $OldName -Force; $steps.Add('removed') } catch { }
        if (@(Find-Settings $CiId $OldName | Where-Object { $_.SettingId -ceq $SettingId }).Count -gt 0) { Write-Result 'replace-added' '' $newId; return }
        if ($TempName -cne $Name) {
            $item = Get-ConfigItem $CiId
            try {
                if ($SettingKind -eq 'Registry') { Set-CMComplianceSettingRegistryKeyValue -InputObject $item -SettingName $TempName -NewSettingName $Name | Out-Null }
                else { Set-CMComplianceSettingScript -InputObject $item -SettingName $TempName -NewSettingName $Name | Out-Null }
                $steps.Add('renamed')
            } catch { }
            if (@(Find-Settings $CiId $Name | Where-Object { $_.SettingId -ceq $newId }).Count -ne 1) { Write-Result 'replace-unnamed' '' $newId; return }
        }
        Write-Result 'ok' '' $newId
    }
    'Tag' {
        $b = Get-CMBaseline -Id ([int]$BaselineId) -Fast
        if ($null -eq $b) { Write-Result 'baseline-missing'; return }
        if ([string]$b.LocalizedDescription -cne $ExpectedDescription) { Write-Result 'changed'; return }
        try { Set-CMBaseline -Id ([int]$BaselineId) -Description $NewDescription; $steps.Add('tagged') } catch { Write-Result 'failed' $_.Exception.Message; return }
        $now = Get-CMBaseline -Id ([int]$BaselineId) -Fast
        if ($null -eq $now -or [string]$now.LocalizedDescription -cne $NewDescription) { Write-Result 'stored-differs'; return }
        Write-Result 'ok'
    }
    'RemoveObjects' {
        # Deployments first, then baselines, then CIs. Remove-CMBaseline -Force removes a baseline
        # that still has deployments (seen on a site), so the deployments and relations of each
        # baseline and CI are read again right before its removal: any that remains keeps it.
        $failed = New-Object System.Collections.Generic.List[string]
        $kept = New-Object System.Collections.Generic.List[object]
        function Add-Kept([string]$Reference, [string]$Name, [string[]]$UsedBy) {
            $kept.Add([pscustomobject]@{ Ref = $Reference; Name = $Name; UsedBy = @($UsedBy) })
            $steps.Add('kept:' + $Reference)
        }
        function Get-Holders([long[]]$Ids) {
            @(Invoke-Query ('SELECT FromCIID, ToCIID, RelationType FROM SMS_CIRelation WHERE ToCIID IN ({0})' -f ((@($Ids) | ForEach-Object { [string][long]$_ }) -join ',')) |
                Where-Object { [int](Get-Prop $_ 'RelationType') -in 1, 2, 3, 4 } | ForEach-Object { [long]$_.FromCIID } | Sort-Object -Unique)
        }
        function Get-HolderText([long]$Id) {
            $b = Invoke-Query ('SELECT CI_ID, LocalizedDisplayName FROM SMS_ConfigurationBaselineInfo WHERE CI_ID = ' + [string]$Id) | Select-Object -First 1
            if ($null -ne $b) { return ('baseline {0} ({1})' -f [string](Get-Prop $b 'LocalizedDisplayName'), [string]$Id) }
            return ('configuration item ' + [string]$Id)
        }
        $owned = @($DeploymentIds | Where-Object { $_ -match '^\d{1,10}$' } | ForEach-Object { [long]$_ })
        foreach ($id in $DeploymentIds) {
            if ($id -notmatch '^\d{1,10}$') { $failed.Add('Deployment:' + $id); continue }
            $a = Get-Assignment ([long]$id)
            if ($null -eq $a) { $steps.Add('gone:Deployment:' + $id); continue }
            if ((Remove-Deployment $a) -ne 'ok') { $failed.Add('Deployment:' + $id) }
        }
        foreach ($id in $BaselineIds) {
            if ($id -notmatch '^\d{1,10}$') { $failed.Add('Baseline:' + $id); continue }
            $b = Get-CMBaseline -Id ([int]$id) -Fast
            if ($null -eq $b) { $steps.Add('gone:Baseline:' + $id); continue }
            $deployed = @(Invoke-Query 'SELECT AssignmentID, AssignedCIs, TargetCollectionID FROM SMS_BaselineAssignment' |
                Where-Object { @(Get-Prop $_ 'AssignedCIs') -contains [long]$id })
            $foreign = @($deployed | Where-Object { $owned -notcontains [long]$_.AssignmentID })
            $holders = @(Get-Holders @([long]$id))
            if ($foreign.Count + $holders.Count -gt 0) {
                Add-Kept ('Baseline:' + $id) ([string]$b.LocalizedDisplayName) (@($foreign | ForEach-Object { 'deployment {0} to collection {1}' -f [string]$_.AssignmentID, [string]$_.TargetCollectionID }) + @($holders | ForEach-Object { Get-HolderText $_ }))
                continue
            }
            if ($deployed.Count -gt 0) { $failed.Add('Baseline:' + $id); continue }
            try { Remove-CMBaseline -Id ([int]$id) -Force } catch { }
            if ($null -eq (Get-CMBaseline -Id ([int]$id) -Fast)) { $steps.Add('removed:Baseline:' + $id) } else { $failed.Add('Baseline:' + $id) }
        }
        $ownedBaselines = @($BaselineIds | Where-Object { $_ -match '^\d{1,10}$' } | ForEach-Object { [long]$_ })
        foreach ($id in $ItemIds) {
            if ($id -notmatch '^\d{1,10}$') { $failed.Add('ConfigurationItem:' + $id); continue }
            $ci = Get-ConfigItem ([long]$id)
            if ($null -eq $ci) { $steps.Add('gone:ConfigurationItem:' + $id); continue }
            $lineage = @([long]$id, [long]$ci.CI_ID)
            $model = Get-Prop $ci 'ModelID'
            if ($null -ne $model -and [long]$model -gt 0) {
                $lineage += @(Invoke-Query ('SELECT CI_ID, ModelID FROM SMS_ConfigurationItem WHERE ModelID = ' + [string][long]$model) | ForEach-Object { [long]$_.CI_ID })
            }
            $holders = @(Get-Holders ($lineage | Sort-Object -Unique))
            $keptRefs = @($kept | ForEach-Object { [string]$_.Ref })
            $foreign = @($holders | Where-Object { $ownedBaselines -notcontains $_ -or $keptRefs -contains ('Baseline:' + [string]$_) })
            if ($foreign.Count -gt 0) {
                Add-Kept ('ConfigurationItem:' + $id) ([string]$ci.LocalizedDisplayName) @($foreign | ForEach-Object { Get-HolderText $_ })
                continue
            }
            if ($holders.Count -gt 0) { $failed.Add('ConfigurationItem:' + $id); continue }
            try { Remove-CMConfigurationItem -Id ([int]$ci.CI_ID) -Force } catch { }
            if ($null -eq (Get-ConfigItem ([long]$id))) { $steps.Add('removed:ConfigurationItem:' + $id) } else { $failed.Add('ConfigurationItem:' + $id) }
        }
        $status = if ($failed.Count -gt 0) { 'some-remain' } elseif ($kept.Count -gt 0) { 'kept' } else { 'ok' }
        $result = Write-Result $status '' '' @($failed)
        $result | Add-Member -NotePropertyName Kept -NotePropertyValue ([object[]]$kept.ToArray())
        $result
    }
}
