param(
    [ValidateSet('Read', 'State', 'Apply')][string]$Operation,
    [string[]]$Sets = @(),
    [ValidateRange(1, 10000000)][int]$MaxRows = 200000,
    [ValidateRange(0, 60000)][int]$PacingMs = 0,
    [string[]]$Keys = @(),
    [ValidateRange(1, 100000)][int]$MaxObjects = 2000,
    [string]$Kind = '',
    [string]$Target = ''
)

# Values from the caller reach WQL text only after a pattern check (IDs, model names, GUIDs,
# numbers). Names never reach WQL text: an object is found by its identifier and compared in
# PowerShell. Every value leaves the session as text in a fixed column order. A time leaves as the
# clock value the provider returns, unconverted: the source tool compares the same values with the
# local time, and the provider marks values of both bases as local.

$CMPSSuppressFastNotUsedCheck = $true
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$IdPattern = '^[A-Za-z0-9]{8}$'
$ModelPattern = '^ScopeId_[A-Za-z0-9-]+/[A-Za-z]+_[A-Za-z0-9-]+$'
$GuidPattern = '^\{?[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12}\}?$'
$NumberPattern = '^[0-9]{1,10}$'
$BlockSize = 1000
$script:Queries = 0
$script:Called = $false

function Wait-Pace {
    if ($script:Called -and $PacingMs -gt 0) { Start-Sleep -Milliseconds $PacingMs }
    $script:Called = $true
}

function Invoke-Query {
    param([string]$Query)
    Wait-Pace
    $script:Queries++
    @(Invoke-CMWmiQuery -Option Fast -Query $Query -ErrorAction Stop) | Where-Object { $null -ne $_ }
}

function Get-Prop {
    param([object]$Object, [string]$Property)
    if ($null -eq $Object) { return $null }
    $found = $Object.PSObject.Properties[$Property]
    if ($null -eq $found) { return $null }
    return $found.Value
}

function ConvertTo-Text {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [datetime]) {
        return $Value.ToString('yyyy-MM-ddTHH:mm:ss', $Invariant)
    }
    if ($Value -is [bool]) { if ($Value) { return 'True' } else { return 'False' } }
    if ($Value -is [System.Array]) { return (@($Value | ForEach-Object { ConvertTo-Text $_ }) -join '|') }
    return [string]::Format($Invariant, '{0}', $Value)
}

function ConvertTo-Row {
    param([object]$Object, [string[]]$Columns)
    , [string[]]@(foreach ($c in $Columns) { ConvertTo-Text (Get-Prop $Object $c) })
}

# One set: rows in blocks, then the status line with the total the site holds.
function Write-Set {
    param([string]$Name, [string[]]$Columns, [object[]]$Rows, [int]$Total)
    $block = New-Object System.Collections.Generic.List[object]
    foreach ($r in $Rows) {
        $block.Add($r)
        if ($block.Count -ge $BlockSize) { [pscustomobject]@{ Kind = 'Rows'; Set = $Name; Rows = $block.ToArray() }; $block.Clear() }
    }
    if ($block.Count -gt 0) { [pscustomobject]@{ Kind = 'Rows'; Set = $Name; Rows = $block.ToArray() } }
    [pscustomobject]@{ Kind = 'Set'; Set = $Name; Columns = $Columns; Shown = @($Rows).Count; Total = $Total; State = $(if ($Total -gt @($Rows).Count) { 'Cut' } else { 'Read' }) }
}

function Read-Simple {
    param([string]$Name, [string]$Class, [string[]]$Columns, [string]$Where = '')
    $query = 'SELECT ' + ($Columns -join ', ') + ' FROM ' + $Class
    if ($Where) { $query += ' WHERE ' + $Where }
    $rows = New-Object System.Collections.Generic.List[object]
    $total = 0
    foreach ($o in Invoke-Query $query) {
        $total++
        if ($rows.Count -lt $MaxRows) { $rows.Add((ConvertTo-Row $o $Columns)) }
    }
    Write-Set $Name $Columns $rows.ToArray() $total
}

# A failure is reported by its kind only; the provider's error text never leaves the session.
function Get-FailureKind {
    param([System.Management.Automation.ErrorRecord]$Record)
    $e = $Record.Exception
    while ($null -ne $e.InnerException) { $e = $e.InnerException }
    $text = [string]$Record.Exception.Message
    if ($e -is [System.UnauthorizedAccessException] -or $text -match 'denied|0x80041003|0x80070005') { return 'access' }
    if ($text -match 'No object corresponds|not found|Invalid class|Invalid query|Not found') { return 'not-found' }
    if ($text -match 'cannot delete|reference|dependent|Failure deleting') { return 'referenced' }
    return 'error'
}

# ---- Per-object sets ------------------------------------------------------------------------

# Rule counts and the full-update schedule are lazy properties: one read per collection that
# has a full schedule (RefreshType 2 or 6), the only ones COL-05, COL-06 and COL-09 judge.
function Read-CollectionDetails {
    $columns = 'CollectionID', 'DirectRules', 'QueryRules', 'DaySpan', 'HourSpan', 'MinuteSpan', 'StartTime'
    $targets = @(Invoke-Query 'SELECT CollectionID, RefreshType FROM SMS_Collection' | Where-Object { ([string](Get-Prop $_ 'CollectionID')) -notlike 'SMS*' -and (Get-Prop $_ 'RefreshType') -in 2, 6 })
    $rows = New-Object System.Collections.Generic.List[object]
    $done = 0
    foreach ($t in $targets) {
        if ($done -ge $MaxObjects) { break }
        $done++
        $id = [string](Get-Prop $t 'CollectionID')
        if ($id -notmatch $IdPattern) { continue }
        Wait-Pace
        $script:Queries++
        $c = Get-CMCollection -Id $id -ErrorAction Stop
        if ($null -eq $c) { continue }
        $direct = 0; $query = 0
        foreach ($rule in @($c.CollectionRules)) {
            if ($null -eq $rule) { continue }
            $class = [string](Get-Prop $rule 'ObjectClass')
            if (-not $class) { $class = [string](Get-Prop $rule 'SmsProviderObjectPath') }
            if ($class -match 'Direct') { $direct++ } elseif ($class -match 'Query') { $query++ }
        }
        $s = @($c.RefreshSchedule)[0]
        $rows.Add([string[]]@($id, [string]$direct, [string]$query, (ConvertTo-Text (Get-Prop $s 'DaySpan')), (ConvertTo-Text (Get-Prop $s 'HourSpan')), (ConvertTo-Text (Get-Prop $s 'MinuteSpan')), (ConvertTo-Text (Get-Prop $s 'StartTime'))))
    }
    [pscustomobject]@{ Kind = 'Coverage'; Set = 'CollectionDetails'; Done = $done; Total = $targets.Count }
    Write-Set 'CollectionDetails' $columns $rows.ToArray() $rows.Count
}

# Maintenance windows are a lazy array of the collection settings: one read per collection
# that has settings.
function Get-Windows {
    param([string]$CollectionId)
    $settings = @(Invoke-CMWmiQuery -Query "SELECT * FROM SMS_CollectionSettings WHERE CollectionID = '$CollectionId'" -ErrorAction Stop) | Select-Object -First 1
    if ($null -eq $settings) { return @() }
    $settings.Get()
    @($settings.ServiceWindows) | Where-Object { $null -ne $_ }
}

$WindowColumns = 'CollectionID', 'ServiceWindowID', 'Name', 'RecurrenceType', 'StartTime', 'Duration', 'IsEnabled', 'ServiceWindowType'

function ConvertTo-WindowRow {
    param([string]$CollectionId, [object]$Window)
    , [string[]]@($CollectionId, [string](Get-Prop $Window 'ServiceWindowID'), [string](Get-Prop $Window 'Name'), (ConvertTo-Text (Get-Prop $Window 'RecurrenceType')),
        (ConvertTo-Text (Get-Prop $Window 'StartTime')), (ConvertTo-Text (Get-Prop $Window 'Duration')), (ConvertTo-Text (Get-Prop $Window 'IsEnabled')), (ConvertTo-Text (Get-Prop $Window 'ServiceWindowType')))
}

function Read-MaintenanceWindows {
    $ids = @(Invoke-Query 'SELECT CollectionID FROM SMS_CollectionSettings' | ForEach-Object { [string](Get-Prop $_ 'CollectionID') } | Where-Object { $_ -match $IdPattern } | Sort-Object -Unique)
    $rows = New-Object System.Collections.Generic.List[object]
    $done = 0
    foreach ($id in $ids) {
        if ($done -ge $MaxObjects) { break }
        $done++
        Wait-Pace
        $script:Queries++
        foreach ($w in Get-Windows $id) { $rows.Add((ConvertTo-WindowRow $id $w)) }
    }
    # The total counts collections, not windows: a cut leaves whole collections out.
    [pscustomobject]@{ Kind = 'Coverage'; Set = 'MaintenanceWindows'; Done = $done; Total = $ids.Count }
    Write-Set 'MaintenanceWindows' $WindowColumns $rows.ToArray() $rows.Count
}

function Get-AutoInstall {
    param([string]$Model)
    Wait-Pace
    $script:Queries++
    $app = Get-CMApplication -ModelName $Model -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $app) { return $null }
    [xml]$xml = [string]$app.SDMPackageXML
    $nsm = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $nsm.AddNamespace('d', 'http://schemas.microsoft.com/SystemCenterConfigurationManager/2009/AppMgmtDigest')
    $node = $xml.SelectSingleNode('/d:AppMgmtDigest/d:Application/d:AutoInstall', $nsm)
    return ($null -ne $node -and ([string]$node.InnerText).Trim() -eq 'true')
}

# The task sequence install setting exists only in the application definition: one read per
# application that a task sequence references.
function Read-TsAppDefinitions {
    $models = @(Invoke-Query 'SELECT PackageID, ObjectID FROM SMS_TaskSequencePackageReference_All' | ForEach-Object { [string](Get-Prop $_ 'ObjectID') } | Where-Object { $_ -match $ModelPattern -and $_ -like '*/Application_*' } | Sort-Object -Unique)
    $rows = New-Object System.Collections.Generic.List[object]
    $done = 0
    foreach ($m in $models) {
        if ($done -ge $MaxObjects) { break }
        $done++
        $auto = Get-AutoInstall $m
        if ($null -eq $auto) { continue }
        $rows.Add([string[]]@($m, (ConvertTo-Text $auto)))
    }
    [pscustomobject]@{ Kind = 'Coverage'; Set = 'TsAppDefinitions'; Done = $done; Total = $models.Count }
    Write-Set 'TsAppDefinitions' @('ModelName', 'AutoInstall') $rows.ToArray() $rows.Count
}

# ---- Set table ------------------------------------------------------------------------------

$Simple = [ordered]@{
    Applications = @('SMS_Application', @('CI_ID', 'ModelID', 'ModelName', 'LocalizedDisplayName', 'IsDeployed', 'IsExpired', 'IsSuperseded', 'IsSuperseding', 'IsEnabled', 'HasContent', 'Manufacturer', 'SoftwareVersion', 'DateCreated', 'NumberOfDeploymentTypes', 'CIVersion'), 'IsLatest = 1')
    AppRevisions = @('SMS_Application', @('CI_ID', 'ModelName', 'CIVersion'), 'IsLatest = 0')
    Packages = @('SMS_Package', @('PackageID', 'Name', 'PackageType'), '')
    Programs = @('SMS_Program', @('PackageID', 'ProgramName', 'ProgramFlags'), '')
    TaskSequences = @('SMS_TaskSequencePackage', @('PackageID', 'Name', 'BootImageID', 'ProgramFlags'), '')
    TsReferences = @('SMS_TaskSequencePackageReference_All', @('PackageID', 'ObjectID'), '')
    Devices = @('SMS_CM_RES_COLL_SMS00001', @('ResourceID', 'Name', 'IsClient', 'ClientVersion', 'LastActiveTime', 'SMBIOSGUID'), '')
    DeviceAgents = @('SMS_R_System', @('ResourceId', 'AgentName', 'AgentTime'), '')
    Boundaries = @('SMS_Boundary', @('BoundaryID', 'DisplayName', 'Value', 'BoundaryType', 'GroupCount'), '')
    BoundaryGroups = @('SMS_BoundaryGroup', @('GroupID', 'Name', 'SiteSystemCount'), '')
    BootImages = @('SMS_BootImagePackage', @('PackageID', 'Name'), '')
    OsImages = @('SMS_ImagePackage', @('PackageID', 'Name'), '')
    OsUpgradePackages = @('SMS_OperatingSystemInstallPackage', @('PackageID', 'Name'), '')
    DriverPackages = @('SMS_DriverPackage', @('PackageID', 'Name'), '')
    UpdatePackages = @('SMS_SoftwareUpdatesPackage', @('PackageID', 'Name'), '')
    UpdateGroups = @('SMS_AuthorizationList', @('CI_ID', 'LocalizedDisplayName', 'NumberOfUpdates', 'NumberOfExpiredUpdates', 'ContainsSupersededUpdates'), '')
    AutoDeploymentRules = @('SMS_AutoDeployment', @('AutoDeploymentID', 'Name', 'AutoDeploymentEnabled', 'LastRunTime', 'LastErrorCode'), '')
    MaintenanceTasks = @('SMS_SCI_SQLTask', @('ItemName', 'SiteCode', 'Enabled'), '')
    CollectionSettings = @('SMS_CollectionSettings', @('CollectionID'), '')
    AppDependencies = @('SMS_AppDependenceRelation', @('FromApplicationCIID', 'FromDeploymentTypeCIID', 'ToApplicationCIID', 'ToDeploymentTypeCIID', 'TypeFlag'), '')
    AppRelations = @('SMS_AppRelation_Flat', @('FromApplicationCIID', 'FromDeploymentTypeCIID', 'ToApplicationCIID', 'ToDeploymentTypeCIID', 'RelationType'), 'Level = 1')
    DeploymentTypes = @('SMS_DeploymentType', @('CI_ID', 'ModelName', 'AppModelName', 'LocalizedDisplayName', 'ContentId', 'IsLatest'), '')
    AppContent = @('SMS_Content', @('ContentUniqueID', 'ContentSource'), "ContentUniqueID LIKE 'Content%'")
    CollectionDependencies = @('SMS_CollectionDependencies', @('DependentCollectionID', 'SourceCollectionID', 'RelationshipType'), '')
    Collections = @('SMS_Collection', @('CollectionID', 'Name', 'MemberCount', 'RefreshType', 'LimitToCollectionID'), '')
    Deployments = @('SMS_DeploymentSummary', @('DeploymentID', 'SoftwareName', 'ProgramName', 'PackageID', 'ModelName', 'CollectionID', 'CollectionName', 'DeploymentIntent', 'FeatureType', 'NumberTargeted', 'NumberSuccess', 'NumberInProgress', 'NumberErrors', 'EnforcementDeadline', 'CreationTime'), '')
    CollectionEvalFull = @('SMS_CollectionEvaluationFull', @('CollectionID', 'CollectionName', 'Length', 'MemberChanges'), '')
    CollectionEvalIncremental = @('SMS_CollectionEvaluationIncremental', @('CollectionID', 'CollectionName', 'Length', 'MemberChanges'), '')
    ContentStatus = @('SMS_ObjectContentInfo', @('ObjectID', 'PackageID', 'SoftwareName', 'ObjectType', 'Targeted', 'NumberSuccess', 'NumberInProgress', 'NumberErrors', 'NumberUnknown', 'SourceSize', 'LastUpdateDate'), '')
    DistributionPoints = @('SMS_DistributionPointInfo', @('NALPath', 'Name'), '')
    BoundaryGroupSiteSystems = @('SMS_BoundaryGroupSiteSystems', @('ServerNALPath'), '')
    DistributionPointGroups = @('SMS_DPGroupInfo', @('GroupID', 'Name', 'MembersCount', 'AssignedContentCount'), '')
    Baselines = @('SMS_ConfigurationBaselineInfo', @('CI_ID', 'ModelName', 'LocalizedDisplayName', 'IsAssigned', 'InUse', 'IsUserDefined'), '')
    ConfigurationItems = @('SMS_ConfigurationItemLatest', @('CI_ID', 'ModelName', 'LocalizedDisplayName', 'InUse', 'IsUserDefined'), 'CIType_ID = 3')
    ClientSettings = @('SMS_ClientSettings', @('SettingsID', 'Name', 'AssignmentCount'), '')
    Drivers = @('SMS_Driver', @('CI_ID', 'LocalizedDisplayName'), '')
    DriverContainers = @('SMS_DriverContainer', @('CI_ID'), '')
    AdminUsers = @('SMS_Admin', @('AdminID', 'LogonName', 'IsDeleted', 'RoleNames'), '')
    ExpiredUpdates = @('SMS_SoftwareUpdate', @('CI_ID'), 'IsExpired = 1')
    UpdateContentMap = @('SMS_CIToContent', @('CI_ID', 'ContentID'), 'ContentDownloaded = 1')
    UpdatePackageContent = @('SMS_PackageToContent', @('PackageID', 'ContentID'), 'PackageType = 5')
    AppDeployments = @('SMS_ApplicationAssignment', @('AssignmentID', 'AssignmentUniqueID', 'ApplicationName', 'AppModelID', 'CollectionName', 'TargetCollectionID', 'ExpirationTime'), '')
}

function Invoke-Read {
    [pscustomobject]@{ Kind = 'Site'; Code = [string](Get-Location).Drive.Name }
    foreach ($name in $Sets) {
        try {
            $ErrorActionPreference = 'Stop'
            switch ($name) {
                'CollectionDetails' { Read-CollectionDetails }
                'MaintenanceWindows' { Read-MaintenanceWindows }
                'TsAppDefinitions' { Read-TsAppDefinitions }
                default {
                    if (-not $Simple.Contains($name)) { [pscustomobject]@{ Kind = 'Set'; Set = $name; State = 'Unknown' }; continue }
                    $d = $Simple[$name]
                    Read-Simple $name $d[0] $d[1] $d[2]
                }
            }
        }
        catch { [pscustomobject]@{ Kind = 'Set'; Set = $name; State = 'Failed'; Failure = (Get-FailureKind $_) } }
        finally { $ErrorActionPreference = 'Continue' }
    }
    [pscustomobject]@{ Kind = 'Queries'; Count = $script:Queries }
}

# ---- State: the facts that decide whether a finding still holds ------------------------------

function Get-OrList {
    param([string]$Property, [string[]]$Values, [switch]$Number)
    ($Values | ForEach-Object { if ($Number) { "$Property = $_" } else { "$Property = '$_'" } }) -join ' OR '
}

function Write-Facts {
    param([string]$Name, [string]$Class, [string[]]$Columns, [string]$Where)
    $query = 'SELECT ' + ($Columns -join ', ') + ' FROM ' + $Class + ' WHERE ' + $Where
    $rows = @(foreach ($o in Invoke-Query $query) { ConvertTo-Row $o $Columns })
    Write-Set $Name $Columns $rows $rows.Count
}

function Invoke-State {
    $ErrorActionPreference = 'Stop'
    [pscustomobject]@{ Kind = 'Site'; Code = [string](Get-Location).Drive.Name }
    $ids = @($Keys | Where-Object { $_ -match $IdPattern } | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    $models = @($Keys | Where-Object { $_ -match $ModelPattern } | Sort-Object -Unique)
    $numbers = @($Keys | Where-Object { $_ -match $NumberPattern } | Sort-Object -Unique)
    $guids = @($Keys | Where-Object { $_ -match $GuidPattern } | ForEach-Object { '{' + $_.Trim('{', '}').ToUpperInvariant() + '}' } | Sort-Object -Unique)
    try {
        switch ($Kind) {
            { $_ -in 'RemoveApplication', 'RemoveApplicationDeployment', 'RemoveApplicationRevisionHistory', 'SetApplication', 'ResumeApplication' } {
                if ($models.Count -gt 0) {
                    $where = Get-OrList 'ModelName' $models
                    Write-Facts 'Applications' 'SMS_Application' $Simple.Applications[1] "IsLatest = 1 AND ($where)"
                    Write-Facts 'AppRevisions' 'SMS_Application' @('CI_ID', 'ModelName', 'CIVersion', 'IsLatest') $where
                    $all = @(Invoke-Query "SELECT CI_ID, ModelID FROM SMS_Application WHERE $where")
                    $ci = @($all | ForEach-Object { [string](Get-Prop $_ 'CI_ID') } | Where-Object { $_ -match $NumberPattern } | Sort-Object -Unique)
                    $modelIds = @($all | ForEach-Object { [string](Get-Prop $_ 'ModelID') } | Where-Object { $_ -match $NumberPattern } | Sort-Object -Unique)
                    if ($modelIds.Count -gt 0) { Write-Facts 'AppDeployments' 'SMS_ApplicationAssignment' $Simple.AppDeployments[1] (Get-OrList 'AppModelID' $modelIds -Number) }
                    else { Write-Set 'AppDeployments' $Simple.AppDeployments[1] @() 0 }
                    Write-Facts 'TsReferences' 'SMS_TaskSequencePackageReference_All' $Simple.TsReferences[1] (Get-OrList 'ObjectID' $models)
                    if ($ci.Count -gt 0) {
                        Write-Facts 'AppDependencies' 'SMS_AppDependenceRelation' $Simple.AppDependencies[1] ((Get-OrList 'ToApplicationCIID' $ci -Number) + ' OR ' + (Get-OrList 'FromApplicationCIID' $ci -Number))
                        Write-Facts 'AppRelations' 'SMS_AppRelation_Flat' $Simple.AppRelations[1] ('Level = 1 AND (' + (Get-OrList 'ToApplicationCIID' $ci -Number) + ' OR ' + (Get-OrList 'FromApplicationCIID' $ci -Number) + ')')
                    }
                    else { Write-Set 'AppDependencies' $Simple.AppDependencies[1] @() 0; Write-Set 'AppRelations' $Simple.AppRelations[1] @() 0 }
                    if ($Kind -eq 'SetApplication') {
                        $rows = @(foreach ($m in $models) { $a = Get-AutoInstall $m; if ($null -ne $a) { , [string[]]@($m, (ConvertTo-Text $a)) } })
                        Write-Set 'TsAppDefinitions' @('ModelName', 'AutoInstall') $rows $rows.Count
                    }
                }
                if ($guids.Count -gt 0) { Write-Facts 'AppDeploymentsById' 'SMS_ApplicationAssignment' $Simple.AppDeployments[1] (Get-OrList 'AssignmentUniqueID' $guids) }
            }
            { $_ -in 'RemovePackage', 'RemoveBootImage', 'RemoveDriverPackage' } {
                if ($ids.Count -gt 0) {
                    $where = Get-OrList 'PackageID' $ids
                    $class = switch ($Kind) { 'RemovePackage' { 'SMS_Package' } 'RemoveBootImage' { 'SMS_BootImagePackage' } default { 'SMS_DriverPackage' } }
                    $columns = if ($Kind -eq 'RemovePackage') { @('PackageID', 'Name', 'PackageType') } else { @('PackageID', 'Name') }
                    Write-Facts 'Objects' $class $columns $where
                    if ($Kind -eq 'RemovePackage') {
                        Write-Facts 'Programs' 'SMS_Program' $Simple.Programs[1] $where
                        Write-Facts 'Deployments' 'SMS_DeploymentSummary' $Simple.Deployments[1] $where
                    }
                    Write-Facts 'TsReferences' 'SMS_TaskSequencePackageReference_All' $Simple.TsReferences[1] (Get-OrList 'ObjectID' $ids)
                    Write-Facts 'TaskSequences' 'SMS_TaskSequencePackage' $Simple.TaskSequences[1] (Get-OrList 'BootImageID' $ids)
                }
            }
            { $_ -in 'RemoveCollection', 'SetCollectionRefreshType' } {
                if ($ids.Count -gt 0) {
                    $where = Get-OrList 'CollectionID' $ids
                    Write-Facts 'Collections' 'SMS_Collection' $Simple.Collections[1] $where
                    Write-Facts 'Deployments' 'SMS_DeploymentSummary' $Simple.Deployments[1] $where
                    Write-Facts 'CollectionSettings' 'SMS_CollectionSettings' $Simple.CollectionSettings[1] $where
                    Write-Facts 'CollectionDependencies' 'SMS_CollectionDependencies' $Simple.CollectionDependencies[1] ((Get-OrList 'SourceCollectionID' $ids) + ' OR ' + (Get-OrList 'DependentCollectionID' $ids))
                    if ($Kind -eq 'SetCollectionRefreshType') {
                        $rows = New-Object System.Collections.Generic.List[object]
                        foreach ($id in $ids) {
                            $c = Get-CMCollection -Id $id -ErrorAction Stop
                            if ($null -eq $c) { continue }
                            $direct = 0; $query = 0
                            foreach ($rule in @($c.CollectionRules)) { if ($null -eq $rule) { continue }; $cl = [string](Get-Prop $rule 'ObjectClass'); if ($cl -match 'Direct') { $direct++ } elseif ($cl -match 'Query') { $query++ } }
                            $s = @($c.RefreshSchedule)[0]
                            $rows.Add([string[]]@($id, [string]$direct, [string]$query, (ConvertTo-Text (Get-Prop $s 'DaySpan')), (ConvertTo-Text (Get-Prop $s 'HourSpan')), (ConvertTo-Text (Get-Prop $s 'MinuteSpan')), (ConvertTo-Text (Get-Prop $s 'StartTime'))))
                            $limit = [string]$c.LimitToCollectionID
                            if ($limit -match $IdPattern) { $ids += $limit }
                        }
                        Write-Set 'CollectionDetails' @('CollectionID', 'DirectRules', 'QueryRules', 'DaySpan', 'HourSpan', 'MinuteSpan', 'StartTime') $rows.ToArray() $rows.Count
                        Write-Facts 'Limiting' 'SMS_Collection' $Simple.Collections[1] (Get-OrList 'CollectionID' @($ids | Sort-Object -Unique))
                    }
                }
            }
            'RemoveMaintenanceWindow' {
                $rows = New-Object System.Collections.Generic.List[object]
                foreach ($id in $ids) { foreach ($w in Get-Windows $id) { $rows.Add((ConvertTo-WindowRow $id $w)) } }
                Write-Set 'MaintenanceWindows' $WindowColumns $rows.ToArray() $rows.Count
                if ($ids.Count -gt 0) { Write-Facts 'Collections' 'SMS_Collection' $Simple.Collections[1] (Get-OrList 'CollectionID' $ids) }
            }
            'RemoveDevice' {
                if ($numbers.Count -gt 0) {
                    Write-Facts 'Devices' 'SMS_CM_RES_COLL_SMS00001' $Simple.Devices[1] (Get-OrList 'ResourceID' $numbers -Number)
                    Write-Facts 'DeviceAgents' 'SMS_R_System' $Simple.DeviceAgents[1] (Get-OrList 'ResourceId' $numbers -Number)
                }
            }
            { $_ -in 'RemoveAutoDeploymentRule', 'RunAutoDeploymentRule' } {
                if ($numbers.Count -gt 0) { Write-Facts 'AutoDeploymentRules' 'SMS_AutoDeployment' $Simple.AutoDeploymentRules[1] (Get-OrList 'AutoDeploymentID' $numbers -Number) }
            }
            'SetSiteMaintenanceTask' {
                $site = [string](Get-Location).Drive.Name
                $rows = @(foreach ($o in Invoke-Query 'SELECT ItemName, SiteCode, Enabled FROM SMS_SCI_SQLTask') { if ([string](Get-Prop $o 'SiteCode') -eq $site -and $Keys -contains [string](Get-Prop $o 'ItemName')) { ConvertTo-Row $o @('ItemName', 'SiteCode', 'Enabled') } })
                Write-Set 'MaintenanceTasks' @('ItemName', 'SiteCode', 'Enabled') $rows $rows.Count
            }
            default { throw 'unsupported' }
        }
        [pscustomobject]@{ Kind = 'StateRead'; Read = $true }
    }
    catch { [pscustomobject]@{ Kind = 'StateRead'; Read = $false; Failure = $(if ([string]$_.Exception.Message -eq 'unsupported') { 'unsupported' } else { Get-FailureKind $_ }) } }
    [pscustomobject]@{ Kind = 'Queries'; Count = $script:Queries }
}

# ---- Apply: one write on one object by its exact identifier ------------------------------------

function Invoke-Apply {
    $t = ConvertFrom-Json -InputObject $Target
    $out = [ordered]@{ Kind = 'Applied'; Status = 'failed'; Failure = '' }
    try {
        $ErrorActionPreference = 'Stop'
        $id = [string]$t.Id
        switch ($Kind) {
            'RemoveApplication' { if ($id -notmatch $NumberPattern) { throw 'invalid' }; Remove-CMApplication -Id ([int]$id) -Force }
            'RemoveApplicationDeployment' { if ($id -notmatch $GuidPattern) { throw 'invalid' }; Remove-CMApplicationDeployment -DeploymentId $id -Force }
            'RemoveApplicationRevisionHistory' { if ($id -notmatch $NumberPattern -or [string]$t.Revision -notmatch $NumberPattern) { throw 'invalid' }; Remove-CMApplicationRevisionHistory -Id ([int]$id) -Revision ([int]$t.Revision) -Force }
            'RemovePackage' { if ($id -notmatch $IdPattern) { throw 'invalid' }; Remove-CMPackage -Id $id -Force }
            'RemoveCollection' { if ($id -notmatch $IdPattern -or $id -like 'SMS*') { throw 'invalid' }; Remove-CMCollection -Id $id -Force }
            'RemoveDevice' { if ($id -notmatch $NumberPattern) { throw 'invalid' }; Remove-CMDevice -ResourceId ([int]$id) -Force }
            'RemoveBootImage' { if ($id -notmatch $IdPattern) { throw 'invalid' }; Remove-CMBootImage -Id $id -Force }
            'RemoveDriverPackage' { if ($id -notmatch $IdPattern) { throw 'invalid' }; Remove-CMDriverPackage -Id $id -Force }
            'RemoveAutoDeploymentRule' { if ($id -notmatch $NumberPattern) { throw 'invalid' }; Remove-CMSoftwareUpdateAutoDeploymentRule -Id ([int]$id) -Force }
            'RemoveMaintenanceWindow' {
                $window = [string]$t.WindowId
                if ($id -notmatch $IdPattern -or $window -notmatch $GuidPattern) { throw 'invalid' }
                # Remove-CMMaintenanceWindow matches every window whose name contains the given name,
                # so the window is removed from the settings instance by its ServiceWindowID.
                $settings = @(Invoke-CMWmiQuery -Query "SELECT * FROM SMS_CollectionSettings WHERE CollectionID = '$id'" -ErrorAction Stop) | Select-Object -First 1
                if ($null -eq $settings) { throw 'No object corresponds to the specified parameters.' }
                $settings.Get()
                $before = @($settings.ServiceWindows)
                $keep = @($before | Where-Object { [string](Get-Prop $_ 'ServiceWindowID') -ne $window })
                if ($keep.Count -eq $before.Count) { throw 'No object corresponds to the specified parameters.' }
                $settings.ServiceWindows = $keep
                $settings.Put() | Out-Null
            }
            'SetCollectionRefreshType' {
                if ($id -notmatch $IdPattern -or $id -like 'SMS*' -or [string]$t.RefreshType -notin 'Continuous', 'Manual') { throw 'invalid' }
                Set-CMCollection -CollectionId $id -RefreshType ([string]$t.RefreshType)
            }
            'SetApplication' { if ($id -notmatch $ModelPattern) { throw 'invalid' }; Set-CMApplication -ModelName $id -AutoInstall $true }
            'SetSiteMaintenanceTask' {
                $site = [string](Get-Location).Drive.Name
                if (-not $id) { throw 'invalid' }
                Set-CMSiteMaintenanceTask -SiteCode $site -Name $id -Enabled $true
            }
            'RunAutoDeploymentRule' { if ($id -notmatch $NumberPattern) { throw 'invalid' }; Invoke-CMSoftwareUpdateAutoDeploymentRule -Id ([int]$id) | Out-Null }
            default { throw 'unsupported' }
        }
        $out.Status = 'done'
    }
    catch {
        $message = [string]$_.Exception.Message
        $out.Failure = if ($message -in 'invalid', 'unsupported') { $message } else { Get-FailureKind $_ }
    }
    finally { $ErrorActionPreference = 'Continue' }
    [pscustomobject]$out
}

switch ($Operation) {
    'Read' { Invoke-Read }
    'State' { Invoke-State }
    'Apply' { Invoke-Apply }
}
