param(
    [ValidateSet('List', 'Get', 'Detail', 'Impact', 'FindCollection', 'Resource', 'Validate', 'Preview', 'SavedQueries', 'Query', 'FindQuery')][string]$Operation,
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$CollectionId = '',
    [string]$Name = '',
    [int]$MemberPage = 0,
    [int]$PageSize = 200,
    [long]$ResourceId = 0,
    [string]$Expression = '',
    [int]$MaxRows = 50,
    [ValidatePattern('^[A-Za-z0-9]{0,16}$')][string]$QueryId = ''
)

# Values reach WQL text only after the patterns above; names travel as cmdlet parameters.

function Get-Prop {
    param([object]$Object, [string]$Property)
    if ($null -eq $Object) { return $null }
    $found = $Object.PSObject.Properties[$Property]
    if ($null -eq $found) { return $null }
    return $found.Value
}

function Get-RuleKind {
    param([object]$Rule)
    $class = [string](Get-Prop $Rule 'ObjectClass')
    if (-not $class) { $class = [string](Get-Prop $Rule 'SmsProviderObjectPath') }
    switch -Wildcard ($class) {
        '*RuleDirect*' { return 'Direct' }
        '*RuleQuery*' { return 'Query' }
        '*RuleIncludeCollection*' { return 'Include' }
        '*RuleExcludeCollection*' { return 'Exclude' }
    }
    return ''
}

function ConvertTo-Rule {
    param([string]$CollectionId, [object]$Rule)
    $kind = Get-RuleKind $Rule
    if (-not $kind) { return $null }
    $included = ''
    if ($kind -eq 'Include') { $included = [string](Get-Prop $Rule 'IncludeCollectionID') }
    if ($kind -eq 'Exclude') { $included = [string](Get-Prop $Rule 'ExcludeCollectionID') }
    $resource = ''
    if ($kind -eq 'Direct') { $resource = [string](Get-Prop $Rule 'ResourceID') }
    [pscustomobject]@{
        Kind                 = 'Rule'
        CollectionId         = $CollectionId
        RuleKind             = $kind
        RuleName             = [string](Get-Prop $Rule 'RuleName')
        Expression           = [string](Get-Prop $Rule 'QueryExpression')
        ResourceId           = $resource
        IncludedCollectionId = $included
    }
}

function Get-ScheduleToken {
    param([object]$Collection)
    $schedule = @(Get-Prop $Collection 'RefreshSchedule') | Where-Object { $null -ne $_ } | Select-Object -First 1
    if ($null -eq $schedule) { return '' }
    try { return [string](@(Convert-CMSchedule -InputObject $schedule)[0]) } catch { return '' }
}

function ConvertTo-CollectionRow {
    param([object]$Collection, [hashtable]$FolderOf)
    $id = [string]$Collection.CollectionID
    $counts = @{ Direct = 0; Query = 0; Include = 0; Exclude = 0 }
    foreach ($rule in @(Get-Prop $Collection 'CollectionRules')) {
        if ($null -eq $rule) { continue }
        $kind = Get-RuleKind $rule
        if ($kind) { $counts[$kind]++ }
    }
    $path = [string](Get-Prop $Collection 'ObjectPath')
    if (-not $path -and $null -ne $FolderOf -and $FolderOf.ContainsKey($id)) { $path = [string]$FolderOf[$id] }
    [pscustomobject]@{
        Kind                  = 'Collection'
        CollectionId          = $id
        Name                  = [string]$Collection.Name
        ObjectPath            = $path
        LimitToCollectionId   = [string](Get-Prop $Collection 'LimitToCollectionID')
        LimitToCollectionName = [string](Get-Prop $Collection 'LimitToCollectionName')
        MemberCount           = [int](Get-Prop $Collection 'MemberCount')
        RefreshType           = [int](Get-Prop $Collection 'RefreshType')
        RefreshSchedule       = (Get-ScheduleToken $Collection)
        Comment               = [string](Get-Prop $Collection 'Comment')
        WindowCount           = [int](Get-Prop $Collection 'ServiceWindowsCount')
        DirectRuleCount       = $counts.Direct
        QueryRuleCount        = $counts.Query
        IncludeRuleCount      = $counts.Include
        ExcludeRuleCount      = $counts.Exclude
    }
}

# A site whose SMS_Collection has no ObjectPath gets the folder of each collection from the
# folder tree: two more queries, only when a row lacks the property.
function Get-FolderMap {
    $folderOf = @{}
    try {
        $nodes = @{}
        foreach ($node in @(Invoke-CMWmiQuery -Option Fast -Query 'SELECT ContainerNodeID, Name, ParentContainerNodeID FROM SMS_ObjectContainerNode WHERE ObjectType = 5000')) {
            $nodes[[int]$node.ContainerNodeID] = $node
        }
        foreach ($item in @(Invoke-CMWmiQuery -Option Fast -Query 'SELECT InstanceKey, ContainerNodeID FROM SMS_ObjectContainerItem WHERE ObjectType = 5000')) {
            $parts = New-Object System.Collections.Generic.List[string]
            $node = [int]$item.ContainerNodeID
            $guard = 0
            while ($node -ne 0 -and $nodes.ContainsKey($node) -and $guard -lt 64) {
                $parts.Insert(0, [string]$nodes[$node].Name)
                $node = [int]$nodes[$node].ParentContainerNodeID
                $guard++
            }
            $folderOf[[string]$item.InstanceKey] = '/' + ($parts -join '/')
        }
    } catch {
        return @{}
    }
    return $folderOf
}

# The default query option loads the lazy CollectionRules and RefreshSchedule of every row;
# Fast and Lazy return them incomplete.
function Get-Collections {
    param([string]$Id)
    $query = 'SELECT * FROM SMS_Collection WHERE CollectionType = 2'
    if ($Id) { $query = "SELECT * FROM SMS_Collection WHERE CollectionType = 2 AND CollectionID = '$Id'" }
    @(Invoke-CMWmiQuery -Query $query)
}

function Write-Collections {
    param([object[]]$Collections)
    $folderOf = $null
    foreach ($c in $Collections) {
        if ($null -eq $folderOf -and $null -eq $c.PSObject.Properties['ObjectPath']) { $folderOf = Get-FolderMap }
        ConvertTo-CollectionRow $c $folderOf
    }
}

function Test-ReadOnlySelect {
    param([string]$Text)
    return ($Text -match '^\s*select\s')
}

function Format-Cell {
    param([object]$Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [System.Array]) { return ((@($Value) | ForEach-Object { [string]$_ }) -join '; ') }
    $text = [string]$Value
    if ($text -match '^\d{14}\.\d{6}[+-]\d{3}$') {
        try { $text = [System.Management.ManagementDateTimeConverter]::ToDateTime($text).ToString('yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture) } catch { }
    }
    if ($text.Length -gt 512) { $text = $text.Substring(0, 512) }
    return $text
}

# A row of a join holds one embedded object per class; its cells are named Class.Property.
function Get-Cells {
    param([object]$Row)
    $cells = [ordered]@{}
    foreach ($name in @(Get-Prop $Row 'PropertyNames')) {
        $value = $null
        try { $value = $Row[$name].ObjectValue } catch { $value = Get-Prop $Row $name }
        if ($value -is [System.Management.ManagementBaseObject]) {
            foreach ($inner in $value.Properties) { $cells[$name + '.' + $inner.Name] = Format-Cell $inner.Value }
        } else {
            $cells[$name] = Format-Cell $value
        }
    }
    return $cells
}
function Write-QueryRow {
    param([object]$Query)
    [pscustomobject]@{
        Kind                = 'SavedQuery'
        QueryId             = [string]$Query.QueryID
        Name                = [string]$Query.Name
        Expression          = [string]$Query.Expression
        TargetClassName     = [string](Get-Prop $Query 'TargetClassName')
        Comment             = [string](Get-Prop $Query 'Comments')
        LimitToCollectionId = [string](Get-Prop $Query 'LimitToCollectionID')
    }
}

switch ($Operation) {
    'List' {
        Write-Collections (Get-Collections '')
    }
    'Get' {
        foreach ($c in (Get-Collections $CollectionId)) {
            ConvertTo-CollectionRow $c $null
            foreach ($rule in @(Get-Prop $c 'CollectionRules')) {
                if ($null -ne $rule) { ConvertTo-Rule $CollectionId $rule }
            }
        }
    }
    'Detail' {
        $found = Get-Collections $CollectionId
        foreach ($c in $found) {
            Write-Collections @($c)
            foreach ($rule in @(Get-Prop $c 'CollectionRules')) {
                if ($null -ne $rule) { ConvertTo-Rule $CollectionId $rule }
            }
        }
        if ($found.Count -eq 0) { return }
        $members = @(Invoke-CMWmiQuery -Option Fast -Query "SELECT ResourceID, Name, IsClient, IsActive, IsDirect FROM SMS_FullCollectionMembership WHERE CollectionID = '$CollectionId'" |
            Sort-Object -Property @{ Expression = { [string]$_.Name } }, @{ Expression = { [long]$_.ResourceID } })
        [pscustomobject]@{ Kind = 'MemberTotal'; Count = $members.Count }
        $size = [Math]::Max(1, $PageSize)
        $first = [Math]::Max(0, $MemberPage) * $size
        for ($i = $first; $i -lt [Math]::Min($members.Count, $first + $size); $i++) {
            $m = $members[$i]
            [pscustomobject]@{
                Kind       = 'Member'
                ResourceId = [string]$m.ResourceID
                Name       = [string]$m.Name
                IsClient   = [bool](Get-Prop $m 'IsClient')
                IsActive   = [bool](Get-Prop $m 'IsActive')
                IsDirect   = [bool](Get-Prop $m 'IsDirect')
            }
        }
    }
    'Impact' {
        $found = Get-Collections $CollectionId
        foreach ($c in $found) {
            ConvertTo-CollectionRow $c $null
            foreach ($rule in @(Get-Prop $c 'CollectionRules')) {
                if ($null -ne $rule) { ConvertTo-Rule $CollectionId $rule }
            }
        }
        if ($found.Count -eq 0) { return }
        $deployments = @(Invoke-CMWmiQuery -Option Fast -Query "SELECT CollectionID, SoftwareName FROM SMS_DeploymentSummary WHERE CollectionID = '$CollectionId'")
        $dependents = @(Invoke-CMWmiQuery -Option Fast -Query "SELECT DependentCollectionID, RelationshipType FROM SMS_CollectionDependencies WHERE SourceCollectionID = '$CollectionId'")
        $ids = @($dependents | ForEach-Object { [string]$_.DependentCollectionID } | Where-Object { $_ -match '^[A-Za-z0-9]{1,8}$' } | Sort-Object -Unique | Select-Object -First 50)
        $names = @{}
        if ($ids.Count -gt 0) {
            $filter = ($ids | ForEach-Object { "CollectionID = '$_'" }) -join ' OR '
            foreach ($row in @(Invoke-CMWmiQuery -Option Fast -Query "SELECT CollectionID, Name FROM SMS_Collection WHERE $filter")) { $names[[string]$row.CollectionID] = [string]$row.Name }
        }
        [pscustomobject]@{ Kind = 'Impact'; Deployments = $deployments.Count }
        foreach ($d in $dependents) {
            $id = [string]$d.DependentCollectionID
            $relation = switch ([int]$d.RelationshipType) { 1 { 'Limiting' } 2 { 'Include' } 3 { 'Exclude' } default { 'Other' } }
            $shown = ''
            if ($names.ContainsKey($id)) { $shown = $names[$id] }
            [pscustomobject]@{ Kind = 'Reference'; CollectionId = $id; Name = $shown; Relation = $relation }
        }
    }
    'FindCollection' {
        foreach ($c in @(Get-CMCollection -Name $Name -DisableWildcardHandling -WarningAction SilentlyContinue)) {
            if ($null -eq $c -or [string]$c.Name -ne $Name) { continue }
            [pscustomobject]@{ Kind = 'Found'; CollectionId = [string]$c.CollectionID; Name = [string]$c.Name; CollectionType = [int](Get-Prop $c 'CollectionType') }
        }
    }
    'Resource' {
        foreach ($r in @(Invoke-CMWmiQuery -Option Fast -Query "SELECT ResourceId, Name FROM SMS_R_System WHERE ResourceId = $ResourceId")) {
            [pscustomobject]@{ Kind = 'Resource'; ResourceId = [string]$r.ResourceId; Name = [string]$r.Name }
        }
    }
    'Validate' {
        if (-not (Test-ReadOnlySelect $Expression)) { return [pscustomobject]@{ Kind = 'Wql'; Valid = $false; Count = 0; Columns = @(); Detail = 'not a select' } }
        try {
            $rows = @(Invoke-CMWmiQuery -Option Fast -Query $Expression -ErrorAction Stop)
        } catch {
            if ([string]$_.Exception.Message -match 'Access is denied|Access denied|0x80070005') { throw }
            $detail = $_.Exception.Message
            if ($null -ne $_.Exception.InnerException) { $detail = $detail + ' ' + $_.Exception.InnerException.Message }
            return [pscustomobject]@{ Kind = 'Wql'; Valid = $false; Count = 0; Columns = @(); Detail = $detail }
        }
        [pscustomobject]@{ Kind = 'Wql'; Valid = $true; Count = $rows.Count; Columns = @(); Detail = '' }
    }
    'Preview' {
        if (-not (Test-ReadOnlySelect $Expression)) { return [pscustomobject]@{ Kind = 'Wql'; Valid = $false; Count = 0; Columns = @(); Detail = 'not a select' } }
        try {
            $rows = @(Invoke-CMWmiQuery -Option Fast -Query $Expression -ErrorAction Stop)
        } catch {
            if ([string]$_.Exception.Message -match 'Access is denied|Access denied|0x80070005') { throw }
            $detail = $_.Exception.Message
            if ($null -ne $_.Exception.InnerException) { $detail = $detail + ' ' + $_.Exception.InnerException.Message }
            return [pscustomobject]@{ Kind = 'Wql'; Valid = $false; Count = 0; Columns = @(); Detail = $detail }
        }
        $take = [Math]::Max(0, [Math]::Min($MaxRows, $rows.Count))
        $cellRows = New-Object System.Collections.Generic.List[object]
        $columns = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $take; $i++) {
            $cells = Get-Cells $rows[$i]
            foreach ($key in $cells.Keys) { if (-not $columns.Contains($key) -and $columns.Count -lt 32) { $columns.Add($key) } }
            $cellRows.Add($cells)
        }
        [pscustomobject]@{ Kind = 'Wql'; Valid = $true; Count = $rows.Count; Columns = [string[]]$columns.ToArray(); Detail = '' }
        foreach ($cells in $cellRows) {
            $values = foreach ($column in $columns) { if ($cells.Contains($column)) { [string]$cells[$column] } else { '' } }
            [pscustomobject]@{ Kind = 'WqlRow'; Values = [string[]]@($values) }
        }
    }
    'SavedQueries' {
        foreach ($q in @(Get-CMQuery)) { if ($null -ne $q) { Write-QueryRow $q } }
    }
    'Query' {
        foreach ($q in @(Get-CMQuery -Id $QueryId)) { if ($null -ne $q) { Write-QueryRow $q } }
    }
    'FindQuery' {
        foreach ($q in @(Get-CMQuery -Name $Name -DisableWildcardHandling -WarningAction SilentlyContinue)) {
            if ($null -ne $q -and [string]$q.Name -eq $Name) { Write-QueryRow $q }
        }
    }
}
