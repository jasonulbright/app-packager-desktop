param(
    [ValidateSet('Items', 'Baselines', 'Deployments', 'Settings', 'Snapshot', 'Collection')][string]$Operation,
    [ValidateRange(0, 2147483647)][long]$BaselineId = 0,
    [ValidateRange(0, 2147483647)][long]$AssignmentId = 0,
    [ValidateRange(0, 2147483647)][long]$CiId = 0,
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$CollectionId = '',
    [string[]]$ItemNames = @(),
    [string[]]$BaselineNames = @(),
    [string[]]$CollectionNames = @()
)

# Only the validated numbers and IDs above reach WQL text; names travel as cmdlet parameters
# and are compared exactly after the read, because the -Name parameters accept wildcards.

$CMPSSuppressFastNotUsedCheck = $true

function Get-Prop {
    param([object]$Object, [string]$Property)
    if ($null -eq $Object) { return $null }
    $found = $Object.PSObject.Properties[$Property]
    if ($null -eq $found) { return $null }
    return $found.Value
}

function Get-Time {
    param([object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('o') }
    $text = [string]$Value
    if ($text -match '^\d{14}\.\d{6}[+-]\d{3}$') {
        try { return [System.Management.ManagementDateTimeConverter]::ToDateTime($text).ToUniversalTime().ToString('o') } catch { return $null }
    }
    return $null
}

function Invoke-Query {
    param([string]$Query)
    @(Invoke-CMWmiQuery -Option Fast -Query $Query) | Where-Object { $null -ne $_ }
}

# An IN list of numbers, in chunks the provider accepts.
function Invoke-InQuery {
    param([string]$Prefix, [string]$Property, [long[]]$Values)
    $all = @($Values | Sort-Object -Unique)
    for ($i = 0; $i -lt $all.Count; $i += 200) {
        $chunk = $all[$i..([Math]::Min($i + 199, $all.Count - 1))]
        Invoke-Query ("{0} WHERE {1} IN ({2})" -f $Prefix, $Property, (($chunk | ForEach-Object { [string][long]$_ }) -join ','))
    }
}

function Get-Baselines {
    Invoke-Query 'SELECT CI_ID, CI_UniqueID, LocalizedDisplayName, LocalizedDescription, CIVersion, DateLastModified, ComplianceCount, NonComplianceCount, FailureCount FROM SMS_ConfigurationBaselineInfo'
}

function Get-Relations {
    param([long[]]$BaselineIds)
    if (@($BaselineIds).Count -eq 0) { return @() }
    Invoke-InQuery 'SELECT FromCIID, ToCIID, RelationType FROM SMS_CIRelation' 'FromCIID' $BaselineIds |
        Where-Object { [int](Get-Prop $_ 'RelationType') -in 1, 2, 3, 4 }
}

function Get-Assignments {
    param([long]$OnlyId = 0)
    $query = 'SELECT AssignmentID, AssignmentUniqueID, AssignmentName, AssignedCIs, TargetCollectionID, EnforcementEnabled, EvaluationSchedule, OverrideServiceWindows, RaiseMomAlertsOnFailure, LastModificationTime FROM SMS_BaselineAssignment'
    if ($OnlyId -gt 0) { $query += ' WHERE AssignmentID = ' + [string]$OnlyId }
    Invoke-Query $query
}

function ConvertTo-DeploymentRows {
    param([object[]]$Assignments)
    if (@($Assignments).Count -eq 0) { return }
    $names = @{}
    foreach ($b in Get-Baselines) { $names[[long]$b.CI_ID] = [string]$b.LocalizedDisplayName }
    $summaries = @{}
    foreach ($s in Invoke-Query 'SELECT AssignmentID, CollectionName, NumberSuccess, NumberErrors, NumberOther, NumberInProgress, NumberUnknown, NumberTargeted, SummarizationTime, ModificationTime FROM SMS_DeploymentSummary WHERE FeatureType = 6') {
        $summaries[[long]$s.AssignmentID] = $s
    }
    $collections = @{}
    $ids = @($Assignments | ForEach-Object { [string]$_.TargetCollectionID } | Where-Object { $_ -match '^[A-Za-z0-9]{1,8}$' } | Sort-Object -Unique)
    for ($i = 0; $i -lt $ids.Count; $i += 100) {
        $chunk = $ids[$i..([Math]::Min($i + 99, $ids.Count - 1))]
        foreach ($c in Invoke-Query ("SELECT CollectionID, Name FROM SMS_Collection WHERE CollectionID IN ({0})" -f (($chunk | ForEach-Object { "'" + $_ + "'" }) -join ','))) {
            $collections[[string]$c.CollectionID] = [string]$c.Name
        }
    }
    $alerts = @{}
    foreach ($a in Invoke-Query 'SELECT TypeInstanceID, ParameterValues FROM SMS_Alert WHERE TypeID = 14') {
        $threshold = 0
        try {
            $xml = [xml][string](Get-Prop $a 'ParameterValues')
            $node = @($xml.Parameters.Parameter | Where-Object { $_.index -eq '3' }) | Select-Object -First 1
            if ($null -ne $node) { [void][int]::TryParse([string]$node.InnerText, [ref]$threshold) }
        } catch { $threshold = 0 }
        $alerts[([string]$a.TypeInstanceID).ToUpperInvariant()] = $threshold
    }
    foreach ($a in $Assignments) {
        $id = [long]$a.AssignmentID
        $baseline = [long](@(Get-Prop $a 'AssignedCIs') | Select-Object -First 1)
        $summary = if ($summaries.ContainsKey($id)) { $summaries[$id] } else { $null }
        $collection = [string]$a.TargetCollectionID
        $collectionName = if ($collections.ContainsKey($collection)) { $collections[$collection] } elseif ($null -ne $summary) { [string]$summary.CollectionName } else { '' }
        $uid = ([string]$a.AssignmentUniqueID).ToUpperInvariant()
        [pscustomobject]@{
            Kind               = 'Deployment'
            AssignmentId       = [string]$id
            UniqueId           = [string]$a.AssignmentUniqueID
            AssignmentName     = [string]$a.AssignmentName
            BaselineId         = [string]$baseline
            BaselineName       = $(if ($names.ContainsKey($baseline)) { $names[$baseline] } else { '' })
            CollectionId       = $collection
            CollectionName     = $collectionName
            Enforcement        = [bool](Get-Prop $a 'EnforcementEnabled')
            Schedule           = [string](Get-Prop $a 'EvaluationSchedule')
            OverrideWindows    = [bool](Get-Prop $a 'OverrideServiceWindows')
            MonitoredByScom    = [bool](Get-Prop $a 'RaiseMomAlertsOnFailure')
            GenerateAlert      = $alerts.ContainsKey($uid)
            AlertThreshold     = $(if ($alerts.ContainsKey($uid)) { [int]$alerts[$uid] } else { 0 })
            Compliant          = $(if ($null -ne $summary) { [int](Get-Prop $summary 'NumberSuccess') } else { 0 })
            NonCompliant       = $(if ($null -ne $summary) { [int](Get-Prop $summary 'NumberOther') } else { 0 })
            Failed             = $(if ($null -ne $summary) { [int](Get-Prop $summary 'NumberErrors') } else { 0 })
            Unknown            = $(if ($null -ne $summary) { [int](Get-Prop $summary 'NumberUnknown') + [int](Get-Prop $summary 'NumberInProgress') } else { 0 })
            Targeted           = $(if ($null -ne $summary) { [int](Get-Prop $summary 'NumberTargeted') } else { 0 })
            Summarized         = $(if ($null -ne $summary) { Get-Time (Get-Prop $summary 'SummarizationTime') } else { $null })
            Modified           = Get-Time (Get-Prop $a 'LastModificationTime')
        }
    }
}

function Get-NodeText {
    param([System.Xml.XmlNode]$Context, [string]$Path, [System.Xml.XmlNamespaceManager]$Ns)
    if ($null -eq $Context) { return '' }
    $node = $Context.SelectSingleNode($Path, $Ns)
    if ($null -eq $node) { return '' }
    if ($node -is [System.Xml.XmlAttribute]) { return [string]$node.Value }
    return [string]$node.InnerText
}

function ConvertTo-SettingRows {
    param([xml]$Document)
    $ns = New-Object System.Xml.XmlNamespaceManager($Document.NameTable)
    $ns.AddNamespace('d', 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/07/10/DesiredConfiguration')
    $ns.AddNamespace('r', 'http://schemas.microsoft.com/SystemsCenterConfigurationManager/2009/06/14/Rules')
    $rules = @($Document.SelectNodes('//r:Rule', $ns))
    foreach ($s in @($Document.SelectNodes('//d:SimpleSetting', $ns))) {
        $logical = [string]$s.GetAttribute('LogicalName')
        $name = Get-NodeText $s 'r:Annotation/r:DisplayName/@Text' $ns
        $description = Get-NodeText $s 'r:Annotation/r:Description/@Text' $ns
        $registry = $s.SelectSingleNode('d:RegistryDiscoverySource', $ns)
        $script = $s.SelectSingleNode('d:ScriptDiscoverySource', $ns)
        $kind = if ($null -ne $registry) { 'Registry' } elseif ($null -ne $script) { 'Script' } else { 'Other' }
        $mine = @($rules | Where-Object {
            @($_.SelectNodes('.//r:SettingReference', $ns) | Where-Object { [string]$_.GetAttribute('SettingLogicalName') -ceq $logical }).Count -gt 0 })
        $rule = if ($mine.Count -eq 1) { $mine[0] } else { $null }
        $operator = ''; $values = @(); $ruleName = ''; $severity = ''; $remediate = $false; $report = $false; $hasValue = $false
        if ($null -ne $rule) {
            $ruleName = Get-NodeText $rule 'r:Annotation/r:DisplayName/@Text' $ns
            $severity = [string]$rule.GetAttribute('Severity')
            $report = [string]$rule.GetAttribute('NonCompliantWhenSettingIsNotFound') -eq 'true'
            $operator = Get-NodeText $rule 'r:Expression/r:Operator' $ns
            $references = @($rule.SelectNodes('.//r:SettingReference', $ns))
            $single = $rule.SelectSingleNode('r:Expression/r:Operands/r:ConstantValue', $ns)
            $list = $rule.SelectSingleNode('r:Expression/r:Operands/r:ConstantValueList', $ns)
            if ($references.Count -eq 1) {
                $remediate = [string]$references[0].GetAttribute('Changeable') -eq 'true'
                $method = [string]$references[0].GetAttribute('Method')
                if ($method -eq 'Value' -and $null -ne $single) { $values = @([string]$single.GetAttribute('Value')); $hasValue = $true }
                elseif ($method -eq 'Value' -and $null -ne $list) { $values = @($list.SelectNodes('r:ConstantValue', $ns) | ForEach-Object { [string]$_.GetAttribute('Value') }); $hasValue = $true }
            }
        }
        $row = [ordered]@{
            Kind = 'Setting'; SettingId = $logical; Name = $name; Description = $description; SettingKind = $kind
            DataType = [string]$s.GetAttribute('DataType'); RuleCount = $mine.Count; HasValueRule = $hasValue
            Operator = $operator; Values = @($values); RuleName = $ruleName; Severity = $severity; Remediate = $remediate; Report = $report
            Hive = ''; KeyName = ''; ValueName = ''; Is64Bit = $false; RemediateDword = $false
            DiscoveryLanguage = ''; DiscoveryScript = ''; RemediationLanguage = ''; RemediationScript = ''
        }
        if ($null -ne $registry) {
            $row.Hive = [string]$registry.GetAttribute('Hive')
            $row.KeyName = Get-NodeText $registry 'd:Key' $ns
            $row.ValueName = Get-NodeText $registry 'd:ValueName' $ns
            $row.Is64Bit = [string]$registry.GetAttribute('Is64Bit') -eq 'true'
            $row.RemediateDword = [string]$registry.GetAttribute('RemediateDword') -eq 'true'
        }
        if ($null -ne $script) {
            $row.Is64Bit = [string]$script.GetAttribute('Is64Bit') -eq 'true'
            $discovery = $script.SelectSingleNode('d:DiscoveryScriptBody', $ns)
            $remediation = $script.SelectSingleNode('d:RemediationScriptBody', $ns)
            if ($null -ne $discovery) { $row.DiscoveryLanguage = [string]$discovery.GetAttribute('ScriptType'); $row.DiscoveryScript = [string]$discovery.InnerText }
            if ($null -ne $remediation) { $row.RemediationLanguage = [string]$remediation.GetAttribute('ScriptType'); $row.RemediationScript = [string]$remediation.InnerText }
        }
        [pscustomobject]$row
    }
}

switch ($Operation) {
    'Items' {
        $baselineIds = @(Get-Baselines | ForEach-Object { [long]$_.CI_ID })
        $count = @{}
        foreach ($r in Get-Relations $baselineIds) {
            $to = [long]$r.ToCIID
            if ($count.ContainsKey($to)) { $count[$to]++ } else { $count[$to] = 1 }
        }
        foreach ($ci in Invoke-Query 'SELECT CI_ID, LocalizedDisplayName, LocalizedDescription, CIVersion, DateLastModified, CIType_ID, IsUserDefined FROM SMS_ConfigurationItemLatest WHERE CIType_ID = 3') {
            if (-not [bool](Get-Prop $ci 'IsUserDefined')) { continue }
            $id = [long]$ci.CI_ID
            [pscustomobject]@{
                Kind = 'Item'; CiId = [string]$id; Name = [string]$ci.LocalizedDisplayName; Description = [string](Get-Prop $ci 'LocalizedDescription')
                Revision = [int](Get-Prop $ci 'CIVersion'); BaselineCount = $(if ($count.ContainsKey($id)) { [int]$count[$id] } else { 0 })
                Modified = Get-Time (Get-Prop $ci 'DateLastModified')
            }
        }
    }
    'Baselines' {
        $baselines = @(Get-Baselines)
        $items = @{}
        foreach ($r in Get-Relations @($baselines | ForEach-Object { [long]$_.CI_ID })) {
            $from = [long]$r.FromCIID
            if ($items.ContainsKey($from)) { $items[$from]++ } else { $items[$from] = 1 }
        }
        $deployments = @{}
        foreach ($a in Invoke-Query 'SELECT AssignmentID, AssignedCIs FROM SMS_BaselineAssignment') {
            foreach ($assigned in @(Get-Prop $a 'AssignedCIs')) {
                $key = [long]$assigned
                if ($deployments.ContainsKey($key)) { $deployments[$key]++ } else { $deployments[$key] = 1 }
            }
        }
        foreach ($b in $baselines) {
            $id = [long]$b.CI_ID
            [pscustomobject]@{
                Kind = 'Baseline'; BaselineId = [string]$id; Name = [string]$b.LocalizedDisplayName; Description = [string](Get-Prop $b 'LocalizedDescription')
                Revision = [int](Get-Prop $b 'CIVersion'); ItemCount = $(if ($items.ContainsKey($id)) { [int]$items[$id] } else { 0 })
                DeploymentCount = $(if ($deployments.ContainsKey($id)) { [int]$deployments[$id] } else { 0 })
                Compliant = [int](Get-Prop $b 'ComplianceCount'); NonCompliant = [int](Get-Prop $b 'NonComplianceCount'); Failed = [int](Get-Prop $b 'FailureCount')
                Modified = Get-Time (Get-Prop $b 'DateLastModified')
            }
        }
    }
    'Deployments' {
        $assignments = @(Get-Assignments $AssignmentId)
        if ($BaselineId -gt 0) { $assignments = @($assignments | Where-Object { @(Get-Prop $_ 'AssignedCIs') -contains $BaselineId }) }
        ConvertTo-DeploymentRows $assignments
    }
    'Settings' {
        # A setting change can give the CI a new CI_ID; the cmdlet resolves an older ID to the latest one.
        $ci = Get-CMConfigurationItem -Id ([int]$CiId) -WarningAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $ci) { return }
        $latest = [long]$ci.CI_ID
        $baselineIds = @(Invoke-InQuery 'SELECT FromCIID, ToCIID, RelationType FROM SMS_CIRelation' 'ToCIID' @($latest) |
            Where-Object { [int](Get-Prop $_ 'RelationType') -in 1, 2, 3, 4 } | ForEach-Object { [long]$_.FromCIID })
        $deployed = 0
        if ($baselineIds.Count -gt 0) {
            foreach ($a in Invoke-Query 'SELECT AssignmentID, AssignedCIs FROM SMS_BaselineAssignment') {
                if (@(@(Get-Prop $a 'AssignedCIs') | Where-Object { $baselineIds -contains [long]$_ }).Count -gt 0) { $deployed++ }
            }
        }
        [pscustomobject]@{ Kind = 'Item'; CiId = [string]$latest; Name = [string]$ci.LocalizedDisplayName; Description = [string]$ci.LocalizedDescription; Revision = [int]$ci.CIVersion; BaselineCount = $baselineIds.Count; Deployments = $deployed; Modified = Get-Time $ci.DateLastModified }
        $xml = [string](Get-Prop $ci 'SDMPackageXML')
        if ($xml) { ConvertTo-SettingRows ([xml]$xml) }
    }
    'Snapshot' {
        foreach ($name in @($ItemNames | Sort-Object -Unique)) {
            foreach ($ci in @(Get-CMConfigurationItem -Name $name -Fast)) {
                if ($null -eq $ci -or [string]$ci.LocalizedDisplayName -cne $name) { continue }
                [pscustomobject]@{ Kind = 'FoundItem'; Id = [string]$ci.CI_ID; Name = $name; Revision = [int]$ci.CIVersion; Description = [string]$ci.LocalizedDescription }
            }
        }
        $baselineIds = @()
        foreach ($name in @($BaselineNames | Sort-Object -Unique)) {
            foreach ($b in @(Get-CMBaseline -Name $name -Fast)) {
                if ($null -eq $b -or [string]$b.LocalizedDisplayName -cne $name) { continue }
                $baselineIds += [long]$b.CI_ID
                [pscustomobject]@{ Kind = 'FoundBaseline'; Id = [string]$b.CI_ID; Name = $name; Revision = [int]$b.CIVersion; Description = [string]$b.LocalizedDescription }
            }
        }
        if ($baselineIds.Count -gt 0) {
            foreach ($a in Invoke-Query 'SELECT AssignmentID, AssignedCIs, TargetCollectionID FROM SMS_BaselineAssignment') {
                foreach ($assigned in @(Get-Prop $a 'AssignedCIs')) {
                    if ($baselineIds -contains [long]$assigned) {
                        [pscustomobject]@{ Kind = 'FoundDeployment'; Id = [string]$a.AssignmentID; BaselineId = [string]$assigned; CollectionId = [string]$a.TargetCollectionID }
                    }
                }
            }
        }
        foreach ($name in @($CollectionNames | Sort-Object -Unique)) {
            if ($name -match '[\*\?\[\]]') { continue }
            foreach ($c in @(Get-CMCollection -Name $name)) {
                if ($null -eq $c -or [string]$c.Name -cne $name) { continue }
                [pscustomobject]@{ Kind = 'FoundCollection'; Id = [string]$c.CollectionID; Name = $name; CollectionType = [int](Get-Prop $c 'CollectionType'); Members = [int](Get-Prop $c 'MemberCount') }
            }
        }
    }
    'Collection' {
        foreach ($c in Invoke-Query ("SELECT CollectionID, Name, CollectionType, MemberCount FROM SMS_Collection WHERE CollectionID = '{0}'" -f $CollectionId)) {
            [pscustomobject]@{ Kind = 'FoundCollection'; Id = [string]$c.CollectionID; Name = [string]$c.Name; CollectionType = [int](Get-Prop $c 'CollectionType'); Members = [int](Get-Prop $c 'MemberCount') }
        }
    }
}
