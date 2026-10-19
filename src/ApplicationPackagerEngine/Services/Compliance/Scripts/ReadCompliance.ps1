param(
    [ValidateSet('Items', 'Baselines', 'Deployments', 'Settings', 'Snapshot', 'Collection')][string]$Operation,
    [ValidateRange(0, 2147483647)][long]$BaselineId = 0,
    [ValidateRange(0, 2147483647)][long]$AssignmentId = 0,
    [ValidateRange(0, 2147483647)][long]$CiId = 0,
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$CollectionId = '',
    [string[]]$ItemNames = @(),
    [string[]]$BaselineNames = @(),
    [string[]]$CollectionNames = @(),
    [switch]$WithIds
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

# The provider returns a time with a known offset as local time, and a time without an offset
# (DMTF '+***', for example SMS_DeploymentSummary.SummarizationTime) as the stored UTC value
# with an unspecified kind: -Utc reads such a value as UTC instead of converting it again.
function Get-Time {
    param([object]$Value, [switch]$Utc)
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) {
        if ($Utc -and $Value.Kind -ne [System.DateTimeKind]::Local) { return [datetime]::SpecifyKind($Value, [System.DateTimeKind]::Utc).ToString('o') }
        return $Value.ToUniversalTime().ToString('o')
    }
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
    param([string]$Prefix, [string]$Property, [long[]]$Values, [switch]$And)
    $all = @($Values | Sort-Object -Unique)
    $joint = if ($And) { 'AND' } else { 'WHERE' }
    for ($i = 0; $i -lt $all.Count; $i += 200) {
        $chunk = $all[$i..([Math]::Min($i + 199, $all.Count - 1))]
        Invoke-Query ("{0} {1} {2} IN ({3})" -f $Prefix, $joint, $Property, (($chunk | ForEach-Object { [string][long]$_ }) -join ','))
    }
}

$BaselineColumns = 'CI_ID, CI_UniqueID, LocalizedDisplayName, LocalizedDescription, CIVersion, DateLastModified, ComplianceCount, NonComplianceCount, FailureCount'

function Get-Baselines {
    param([long[]]$Ids)
    if ($PSBoundParameters.ContainsKey('Ids')) {
        if (@($Ids).Count -eq 0) { return @() }
        return Invoke-InQuery "SELECT $BaselineColumns FROM SMS_ConfigurationBaselineInfo" 'CI_ID' $Ids
    }
    Invoke-Query "SELECT $BaselineColumns FROM SMS_ConfigurationBaselineInfo"
}

# A count property the provider does not return is an unknown count (null), never zero.
function Get-Count {
    param([object]$Object, [string[]]$Properties)
    if ($null -eq $Object) { return $null }
    $total = 0
    foreach ($p in $Properties) {
        $value = Get-Prop $Object $p
        if ($null -eq $value) { return $null }
        $total += [int]$value
    }
    return $total
}

# Every CI_ID of every revision with its ModelID: a baseline keeps referencing the CI_ID of the
# revision it was made with, so counts follow the lineage (ModelID), not the latest CI_ID.
function Get-Lineage {
    $model = @{}
    foreach ($r in Invoke-Query 'SELECT CI_ID, ModelID FROM SMS_ConfigurationItem WHERE CIType_ID = 3') {
        $id = [long]$r.CI_ID
        $m = Get-Prop $r 'ModelID'
        $model[$id] = $(if ($null -eq $m -or [long]$m -eq 0) { $id } else { [long]$m })
    }
    return $model
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
    param([object[]]$Assignments, [switch]$Scoped)
    if (@($Assignments).Count -eq 0) { return }
    $names = @{}
    $assignedIds = @($Assignments | ForEach-Object { @(Get-Prop $_ 'AssignedCIs') } | Where-Object { $null -ne $_ } | ForEach-Object { [long]$_ })
    $baselineRows = if ($Scoped) { Get-Baselines -Ids $assignedIds } else { Get-Baselines }
    foreach ($b in $baselineRows) { $names[[long]$b.CI_ID] = [string]$b.LocalizedDisplayName }
    $summaries = @{}
    $summaryQuery = 'SELECT AssignmentID, CollectionName, NumberSuccess, NumberErrors, NumberOther, NumberInProgress, NumberUnknown, NumberTargeted, SummarizationTime, ModificationTime FROM SMS_DeploymentSummary WHERE FeatureType = 6'
    $summaryRows = if ($Scoped) { Invoke-InQuery $summaryQuery 'AssignmentID' @($Assignments | ForEach-Object { [long]$_.AssignmentID }) -And } else { Invoke-Query $summaryQuery }
    foreach ($s in $summaryRows) {
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
    $alertQuery = 'SELECT TypeInstanceID, ParameterValues, MonitoredByScom, SkipUntil FROM SMS_Alert WHERE TypeID = 14'
    $alertRows = if ($Scoped) {
        $uids = @($Assignments | ForEach-Object { ([string]$_.AssignmentUniqueID).ToUpperInvariant() } | Where-Object { $_ -match '^\{[0-9A-F-]{36}\}$' } | Sort-Object -Unique)
        if ($uids.Count -gt 0) { Invoke-Query ("{0} AND TypeInstanceID IN ({1})" -f $alertQuery, (($uids | ForEach-Object { "'" + $_ + "'" }) -join ',')) }
    } else { Invoke-Query $alertQuery }
    foreach ($a in $alertRows) {
        $alerts[([string]$a.TypeInstanceID).ToUpperInvariant()] = ConvertFrom-Alert $a
    }
    foreach ($a in $Assignments) {
        $id = [long]$a.AssignmentID
        $baseline = [long](@(Get-Prop $a 'AssignedCIs') | Select-Object -First 1)
        $summary = if ($summaries.ContainsKey($id)) { $summaries[$id] } else { $null }
        $collection = [string]$a.TargetCollectionID
        $collectionName = if ($collections.ContainsKey($collection)) { $collections[$collection] } elseif ($null -ne $summary) { [string]$summary.CollectionName } else { '' }
        $uid = ([string]$a.AssignmentUniqueID).ToUpperInvariant()
        $alert = if ($alerts.ContainsKey($uid)) { $alerts[$uid] } else { $null }
        # Verified on a site: NumberSuccess counts compliant devices, NumberOther non-compliant
        # ones, NumberErrors devices with an evaluation error.
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
            MonitoredByScom    = $(if ($null -ne $alert) { $alert.Scom } else { $false })
            GenerateAlert      = $null -ne $alert
            AlertThreshold     = $(if ($null -ne $alert) { $alert.Threshold } else { 0 })
            AlertAfter         = $(if ($null -ne $alert) { $alert.After } else { $null })
            Compliant          = Get-Count $summary 'NumberSuccess'
            NonCompliant       = Get-Count $summary 'NumberOther'
            Failed             = Get-Count $summary 'NumberErrors'
            Unknown            = Get-Count $summary 'NumberUnknown', 'NumberInProgress'
            Targeted           = Get-Count $summary 'NumberTargeted'
            Summarized         = $(if ($null -ne $summary) { Get-Time (Get-Prop $summary 'SummarizationTime') -Utc } else { $null })
            Modified           = Get-Time (Get-Prop $a 'LastModificationTime')
        }
    }
}

# The SMS_Alert of a baseline deployment: -MonitoredByScom is stored here (not in
# SMS_BaselineAssignment.RaiseMomAlertsOnFailure), the threshold is parameter 3 and
# -PostponeDateTime is SkipUntil.
function ConvertFrom-Alert {
    param([object]$Alert)
    $threshold = 0
    try {
        $xml = [xml][string](Get-Prop $Alert 'ParameterValues')
        $node = @($xml.Parameters.Parameter | Where-Object { $_.index -eq '3' }) | Select-Object -First 1
        if ($null -ne $node) { [void][int]::TryParse([string]$node.InnerText, [ref]$threshold) }
    } catch { $threshold = 0 }
    @{ Threshold = $threshold; Scom = [bool](Get-Prop $Alert 'MonitoredByScom'); After = Get-Time (Get-Prop $Alert 'SkipUntil') }
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
        $lineage = Get-Lineage
        $holders = @{}
        foreach ($r in Get-Relations $baselineIds) {
            $to = [long]$r.ToCIID
            $model = if ($lineage.ContainsKey($to)) { $lineage[$to] } else { $to }
            if (-not $holders.ContainsKey($model)) { $holders[$model] = New-Object System.Collections.Generic.HashSet[long] }
            [void]$holders[$model].Add([long]$r.FromCIID)
        }
        foreach ($ci in Invoke-Query 'SELECT CI_ID, LocalizedDisplayName, LocalizedDescription, CIVersion, DateLastModified, CIType_ID, IsUserDefined FROM SMS_ConfigurationItemLatest WHERE CIType_ID = 3') {
            if (-not [bool](Get-Prop $ci 'IsUserDefined')) { continue }
            $id = [long]$ci.CI_ID
            $model = if ($lineage.ContainsKey($id)) { $lineage[$id] } else { $id }
            [pscustomobject]@{
                Kind = 'Item'; CiId = [string]$id; Name = [string]$ci.LocalizedDisplayName; Description = [string](Get-Prop $ci 'LocalizedDescription')
                Revision = [int](Get-Prop $ci 'CIVersion'); BaselineCount = $(if ($holders.ContainsKey($model)) { [int]$holders[$model].Count } else { 0 })
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
                Compliant = Get-Count $b 'ComplianceCount'; NonCompliant = Get-Count $b 'NonComplianceCount'; Failed = Get-Count $b 'FailureCount'
                Modified = Get-Time (Get-Prop $b 'DateLastModified')
            }
        }
    }
    'Deployments' {
        $assignments = @(Get-Assignments $AssignmentId)
        if ($BaselineId -gt 0) { $assignments = @($assignments | Where-Object { @(Get-Prop $_ 'AssignedCIs') -contains $BaselineId }) }
        ConvertTo-DeploymentRows $assignments -Scoped:($AssignmentId -gt 0 -or $BaselineId -gt 0)
    }
    'Settings' {
        # A setting change can give the CI a new CI_ID; the cmdlet resolves an older ID to the latest
        # one, and baselines keep referencing the CI_ID of the revision they were made with.
        $ci = Get-CMConfigurationItem -Id ([int]$CiId) -WarningAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $ci) { return }
        $latest = [long]$ci.CI_ID
        $lineageIds = @($latest)
        $model = Get-Prop $ci 'ModelID'
        if ($null -ne $model -and [long]$model -gt 0) {
            $lineageIds += @(Invoke-Query ('SELECT CI_ID, ModelID FROM SMS_ConfigurationItem WHERE ModelID = ' + [string][long]$model) | ForEach-Object { [long]$_.CI_ID })
        }
        $baselineIds = @(Invoke-InQuery 'SELECT FromCIID, ToCIID, RelationType FROM SMS_CIRelation' 'ToCIID' $lineageIds |
            Where-Object { [int](Get-Prop $_ 'RelationType') -in 1, 2, 3, 4 } | ForEach-Object { [long]$_.FromCIID } | Sort-Object -Unique)
        $held = @()
        if ($baselineIds.Count -gt 0) {
            $held = @(Invoke-Query 'SELECT AssignmentID, AssignedCIs, TargetCollectionID, EnforcementEnabled FROM SMS_BaselineAssignment' |
                Where-Object { @(@(Get-Prop $_ 'AssignedCIs') | Where-Object { $baselineIds -contains [long]$_ }).Count -gt 0 })
        }
        [pscustomobject]@{ Kind = 'Item'; CiId = [string]$latest; Name = [string]$ci.LocalizedDisplayName; Description = [string]$ci.LocalizedDescription; Revision = [int]$ci.CIVersion; BaselineCount = $baselineIds.Count; Deployments = $held.Count; Modified = Get-Time $ci.DateLastModified }
        $remediating = @($held | Where-Object { [bool](Get-Prop $_ 'EnforcementEnabled') })
        if ($remediating.Count -gt 0) {
            $names = @{}
            foreach ($b in Get-Baselines -Ids @($remediating | ForEach-Object { @(Get-Prop $_ 'AssignedCIs') } | ForEach-Object { [long]$_ })) { $names[[long]$b.CI_ID] = [string]$b.LocalizedDisplayName }
            $collections = @{}
            $ids = @($remediating | ForEach-Object { [string]$_.TargetCollectionID } | Where-Object { $_ -match '^[A-Za-z0-9]{1,8}$' } | Sort-Object -Unique)
            for ($i = 0; $i -lt $ids.Count; $i += 100) {
                $chunk = $ids[$i..([Math]::Min($i + 99, $ids.Count - 1))]
                foreach ($c in Invoke-Query ("SELECT CollectionID, Name, MemberCount FROM SMS_Collection WHERE CollectionID IN ({0})" -f (($chunk | ForEach-Object { "'" + $_ + "'" }) -join ','))) {
                    $collections[[string]$c.CollectionID] = $c
                }
            }
            foreach ($a in $remediating) {
                $baseline = [long](@(@(Get-Prop $a 'AssignedCIs') | Where-Object { $baselineIds -contains [long]$_ }) | Select-Object -First 1)
                $collection = [string]$a.TargetCollectionID
                $c = if ($collections.ContainsKey($collection)) { $collections[$collection] } else { $null }
                $members = Get-Prop $c 'MemberCount'
                [pscustomobject]@{
                    Kind = 'ItemDeployment'; AssignmentId = [string]$a.AssignmentID; BaselineId = [string]$baseline
                    BaselineName = $(if ($names.ContainsKey($baseline)) { $names[$baseline] } else { '' })
                    CollectionId = $collection; CollectionName = $(if ($null -ne $c) { [string]$c.Name } else { '' })
                    Members = $(if ($null -ne $members) { [int]$members } else { $null })
                }
            }
        }
        $xml = [string](Get-Prop $ci 'SDMPackageXML')
        if ($xml) { ConvertTo-SettingRows ([xml]$xml) }
    }
    'Snapshot' {
        # ConfigMgr compares object names without case, so a name that differs only in case is the same name.
        if ($WithIds) {
            [pscustomobject]@{
                Kind = 'SnapshotInfo'; Now = (Get-Date).ToUniversalTime().ToString('o'); User = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
                ItemIds = @(Invoke-Query 'SELECT CI_ID FROM SMS_ConfigurationItem WHERE CIType_ID = 3' | ForEach-Object { [string]$_.CI_ID })
                BaselineIds = @(Invoke-Query 'SELECT CI_ID FROM SMS_ConfigurationBaselineInfo' | ForEach-Object { [string]$_.CI_ID })
                DeploymentIds = @(Invoke-Query 'SELECT AssignmentID FROM SMS_BaselineAssignment' | ForEach-Object { [string]$_.AssignmentID })
            }
        }
        foreach ($name in @($ItemNames | Sort-Object -Unique)) {
            foreach ($ci in @(Get-CMConfigurationItem -Name $name -Fast)) {
                if ($null -eq $ci -or [string]$ci.LocalizedDisplayName -ne $name) { continue }
                [pscustomobject]@{ Kind = 'FoundItem'; Id = [string]$ci.CI_ID; Name = [string]$ci.LocalizedDisplayName; Revision = [int]$ci.CIVersion; Description = [string]$ci.LocalizedDescription
                    Created = Get-Time (Get-Prop $ci 'DateCreated'); CreatedBy = [string](Get-Prop $ci 'CreatedBy') }
            }
        }
        $baselineIds = @()
        foreach ($name in @($BaselineNames | Sort-Object -Unique)) {
            foreach ($b in @(Get-CMBaseline -Name $name -Fast)) {
                if ($null -eq $b -or [string]$b.LocalizedDisplayName -ne $name) { continue }
                $baselineIds += [long]$b.CI_ID
                [pscustomobject]@{ Kind = 'FoundBaseline'; Id = [string]$b.CI_ID; Name = [string]$b.LocalizedDisplayName; Revision = [int]$b.CIVersion; Description = [string]$b.LocalizedDescription
                    Created = Get-Time (Get-Prop $b 'DateCreated'); CreatedBy = [string](Get-Prop $b 'CreatedBy') }
            }
        }
        if ($baselineIds.Count -gt 0) {
            foreach ($a in Invoke-Query 'SELECT AssignmentID, AssignedCIs, TargetCollectionID, CreationTime, LastModifiedBy FROM SMS_BaselineAssignment') {
                foreach ($assigned in @(Get-Prop $a 'AssignedCIs')) {
                    if ($baselineIds -contains [long]$assigned) {
                        [pscustomobject]@{ Kind = 'FoundDeployment'; Id = [string]$a.AssignmentID; BaselineId = [string]$assigned; CollectionId = [string]$a.TargetCollectionID
                            Created = Get-Time (Get-Prop $a 'CreationTime'); CreatedBy = [string](Get-Prop $a 'LastModifiedBy') }
                    }
                }
            }
        }
        foreach ($name in @($CollectionNames | Sort-Object -Unique)) {
            if ($name -match '[\*\?\[\]]') { continue }
            foreach ($c in @(Get-CMCollection -Name $name)) {
                if ($null -eq $c -or [string]$c.Name -ne $name) { continue }
                [pscustomobject]@{ Kind = 'FoundCollection'; Id = [string]$c.CollectionID; Name = [string]$c.Name; CollectionType = [int](Get-Prop $c 'CollectionType'); Members = [int](Get-Prop $c 'MemberCount') }
            }
        }
    }
    'Collection' {
        foreach ($c in Invoke-Query ("SELECT CollectionID, Name, CollectionType, MemberCount FROM SMS_Collection WHERE CollectionID = '{0}'" -f $CollectionId)) {
            [pscustomobject]@{ Kind = 'FoundCollection'; Id = [string]$c.CollectionID; Name = [string]$c.Name; CollectionType = [int](Get-Prop $c 'CollectionType'); Members = [int](Get-Prop $c 'MemberCount') }
        }
    }
}
