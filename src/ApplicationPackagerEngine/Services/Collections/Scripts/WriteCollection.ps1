param(
    [ValidateSet('Create', 'Copy', 'SetProperties', 'AddRule', 'RemoveRule', 'UpdateRule', 'Evaluate', 'Remove', 'SaveQuery', 'UpdateQuery', 'DeleteQuery')][string]$Operation,
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$CollectionId = '',
    [string]$Name = '',
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$LimitingCollectionId = '',
    [ValidateSet('', 'Manual', 'Periodic', 'Continuous', 'Both')][string]$RefreshType = '',
    [ValidatePattern('^([0-9A-Fa-f]{16})?$')][string]$RefreshSchedule = '',
    [string]$Comment = '',
    [bool]$CheckState = $false,
    [string]$ExpectedName = '',
    [string]$ExpectedComment = '',
    [string]$ExpectedLimitingId = '',
    [int]$ExpectedRefreshType = 0,
    [string]$ExpectedRefreshSchedule = '',
    [string[]]$ExpectedRuleKeys = @(),
    [int]$ExpectedMemberCount = -1,
    [int]$ExpectedDeployments = -1,
    [int]$ExpectedWindows = -1,
    [ValidateSet('', 'Minutes', 'Hours', 'Days')][string]$RefreshUnit = '',
    [ValidateRange(0, 59)][int]$RefreshCount = 0,
    [ValidateSet('', 'Direct', 'Query', 'Include', 'Exclude')][string]$RuleKind = '',
    [string]$RuleName = '',
    [string]$Expression = '',
    [long]$ResourceId = 0,
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$IncludedCollectionId = '',
    [string]$OldRuleName = '',
    [string]$OldExpression = '',
    [string]$TempRuleName = '',
    [string[]]$OrphanRuleNames = @(),
    [string[]]$OrphanExpressions = @(),
    [string[]]$RuleKinds = @(),
    [string[]]$RuleNames = @(),
    [string[]]$RuleExpressions = @(),
    [string[]]$RuleResourceIds = @(),
    [string[]]$RuleCollectionIds = @(),
    [ValidatePattern('^[A-Za-z0-9]{0,16}$')][string]$QueryId = '',
    [string]$TargetClassName = '',
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$LimitToCollectionId = '',
    [string]$ExpectedExpression = '',
    [string]$ExpectedTargetClassName = '',
    [string]$ExpectedLimitToCollectionId = ''
)

# Values reach WQL text only after the patterns above; names and expressions travel as cmdlet
# parameters. Each operation checks the state it was planned against, writes, and reads back.

$mutated = $false
$createdId = ''
$steps = New-Object System.Collections.Generic.List[string]

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
    param([object]$Rule)
    $kind = Get-RuleKind $Rule
    if (-not $kind) { return $null }
    $included = ''
    if ($kind -eq 'Include') { $included = [string](Get-Prop $Rule 'IncludeCollectionID') }
    if ($kind -eq 'Exclude') { $included = [string](Get-Prop $Rule 'ExcludeCollectionID') }
    $resource = ''
    if ($kind -eq 'Direct') { $resource = [string](Get-Prop $Rule 'ResourceID') }
    [pscustomobject]@{
        Kind                 = 'Rule'
        RuleKind             = $kind
        RuleName             = [string](Get-Prop $Rule 'RuleName')
        Expression           = [string](Get-Prop $Rule 'QueryExpression')
        ResourceId           = $resource
        IncludedCollectionId = $included
    }
}

function Get-RuleKey {
    param([object]$Rule)
    switch ($Rule.RuleKind) {
        'Direct' { return 'Direct|' + $Rule.ResourceId }
        'Query' { return 'Query|' + $Rule.RuleName + '|' + $Rule.Expression }
        default { return $Rule.RuleKind + '|' + $Rule.IncludedCollectionId.ToUpperInvariant() }
    }
}

function Get-ScheduleToken {
    param([object]$Collection)
    $schedule = @(Get-Prop $Collection 'RefreshSchedule') | Where-Object { $null -ne $_ } | Select-Object -First 1
    if ($null -eq $schedule) { return '' }
    try { return [string](@(Convert-CMSchedule -InputObject $schedule)[0]) } catch { return '' }
}

function Get-Collection {
    param([string]$Id)
    if (-not $Id) { return $null }
    @(Invoke-CMWmiQuery -Query "SELECT * FROM SMS_Collection WHERE CollectionType = 2 AND CollectionID = '$Id'") | Select-Object -First 1
}

function Get-Rules {
    param([object]$Collection)
    @(foreach ($rule in @(Get-Prop $Collection 'CollectionRules')) {
        if ($null -ne $rule) { $converted = ConvertTo-Rule $rule; if ($null -ne $converted) { $converted } }
    })
}

function ConvertTo-Row {
    param([object]$Collection)
    if ($null -eq $Collection) { return $null }
    $rules = Get-Rules $Collection
    [pscustomobject]@{
        Kind                  = 'Collection'
        CollectionId          = [string]$Collection.CollectionID
        Name                  = [string]$Collection.Name
        ObjectPath            = [string](Get-Prop $Collection 'ObjectPath')
        LimitToCollectionId   = [string](Get-Prop $Collection 'LimitToCollectionID')
        LimitToCollectionName = [string](Get-Prop $Collection 'LimitToCollectionName')
        MemberCount           = [int](Get-Prop $Collection 'MemberCount')
        RefreshType           = [int](Get-Prop $Collection 'RefreshType')
        RefreshSchedule       = (Get-ScheduleToken $Collection)
        Comment               = [string](Get-Prop $Collection 'Comment')
        WindowCount           = [int](Get-Prop $Collection 'ServiceWindowsCount')
        DirectRuleCount       = @($rules | Where-Object { $_.RuleKind -eq 'Direct' }).Count
        QueryRuleCount        = @($rules | Where-Object { $_.RuleKind -eq 'Query' }).Count
        IncludeRuleCount      = @($rules | Where-Object { $_.RuleKind -eq 'Include' }).Count
        ExcludeRuleCount      = @($rules | Where-Object { $_.RuleKind -eq 'Exclude' }).Count
    }
}

function ConvertTo-QueryRow {
    param([object]$Query)
    if ($null -eq $Query) { return $null }
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

function Write-Result {
    param([string]$Status, [object]$Collection = $null, [string]$Detail = '', [object]$Query = $null, [string[]]$References = @(), [string]$CreatedId = $createdId)
    $rules = @()
    $row = $null
    if ($null -ne $Collection) {
        $row = ConvertTo-Row $Collection
        $rules = @(Get-Rules $Collection)
    }
    [pscustomobject]@{
        Kind       = 'Result'
        Status     = $Status
        Detail     = $Detail
        Steps      = [string[]]$steps.ToArray()
        Collection = $row
        Rules      = $rules
        Query      = (ConvertTo-QueryRow $Query)
        References = $References
        CreatedId  = $CreatedId
    }
}

# Access denied reaches the session host as an error so it reports the missing role, unless a
# change was already made: then the result carries the state as it is now.
function Test-AccessDenied {
    param([object]$Failure)
    $exception = $Failure.Exception
    while ($null -ne $exception) {
        if ($exception -is [System.UnauthorizedAccessException]) { return $true }
        $hresult = 0
        try { $hresult = $exception.HResult } catch { }
        if ($hresult -eq -2147024891) { return $true }
        if ([string]$exception.Message -match 'Access is denied|Access denied|0x80070005|E_ACCESSDENIED') { return $true }
        $exception = $exception.InnerException
    }
    return $false
}

function Find-CollectionByName {
    param([string]$Text)
    @(Get-CMCollection -Name $Text -DisableWildcardHandling -WarningAction SilentlyContinue) |
        Where-Object { $null -ne $_ -and [string]$_.Name -eq $Text } | Select-Object -First 1
}

function Find-QueryByName {
    param([string]$Text)
    @(Get-CMQuery -Name $Text -DisableWildcardHandling -WarningAction SilentlyContinue) |
        Where-Object { $null -ne $_ -and [string]$_.Name -eq $Text } | Select-Object -First 1
}

function Get-RefreshValue {
    param([int]$Value)
    switch ($Value) { 1 { 'Manual' } 2 { 'Periodic' } 4 { 'Continuous' } 6 { 'Both' } default { '' } }
}

function New-RefreshSchedule {
    if ($RefreshUnit) { return (New-CMSchedule -RecurInterval $RefreshUnit -RecurCount $RefreshCount) }
    if ($RefreshSchedule) { return (Convert-CMSchedule -ScheduleString $RefreshSchedule) }
    return (New-CMSchedule -RecurInterval Days -RecurCount 7)
}

# The query rule cmdlets match a rule name literally and in any case, and a remove by name
# removes every rule that matches.
function Get-NameMatches {
    param([object[]]$Rules, [string]$Text)
    @($Rules | Where-Object { $_.RuleKind -eq 'Query' -and $_.RuleName -eq $Text })
}

function Get-DeploymentCount {
    param([string]$Id)
    @(Invoke-CMWmiQuery -Option Fast -Query "SELECT CollectionID, SoftwareName FROM SMS_DeploymentSummary WHERE CollectionID = '$Id'").Count
}

# A remove cmdlet that matches no rule returns without an error.
function Assert-QueryRuleGone {
    param([string]$Text, [string]$Message)
    if (@(Get-NameMatches (Get-Rules (Get-Collection $CollectionId)) $Text).Count -gt 0) { throw $Message }
}

function Find-Rule {
    param([object[]]$Rules, [string]$Kind, [string]$RuleNameText, [string]$ExpressionText, [long]$Resource, [string]$Included)
    foreach ($r in $Rules) {
        if ($r.RuleKind -ne $Kind) { continue }
        switch ($Kind) {
            'Direct' { if ($r.ResourceId -eq [string]$Resource) { return $r } }
            'Query' { if ($r.RuleName -eq $RuleNameText) { return $r } }
            default { if ($r.IncludedCollectionId -eq $Included) { return $r } }
        }
    }
    return $null
}

function Add-Rule {
    param([string]$Kind, [string]$RuleNameText, [string]$ExpressionText, [long]$Resource, [string]$Included, [string]$Target = $CollectionId)
    switch ($Kind) {
        'Direct' { Add-CMDeviceCollectionDirectMembershipRule -CollectionId $Target -ResourceId $Resource | Out-Null }
        'Query' { Add-CMDeviceCollectionQueryMembershipRule -CollectionId $Target -RuleName $RuleNameText -QueryExpression $ExpressionText | Out-Null }
        'Include' { Add-CMDeviceCollectionIncludeMembershipRule -CollectionId $Target -IncludeCollectionId $Included | Out-Null }
        'Exclude' { Add-CMDeviceCollectionExcludeMembershipRule -CollectionId $Target -ExcludeCollectionId $Included | Out-Null }
        default { throw "Unknown rule kind $Kind." }
    }
}

function Remove-Rule {
    param([string]$Kind, [string]$RuleNameText, [long]$Resource, [string]$Included)
    switch ($Kind) {
        'Direct' { Remove-CMDeviceCollectionDirectMembershipRule -CollectionId $CollectionId -ResourceId $Resource -Force | Out-Null }
        'Query' { Remove-CMDeviceCollectionQueryMembershipRule -CollectionId $CollectionId -RuleName $RuleNameText -Force | Out-Null }
        'Include' { Remove-CMDeviceCollectionIncludeMembershipRule -CollectionId $CollectionId -IncludeCollectionId $Included -Force | Out-Null }
        'Exclude' { Remove-CMDeviceCollectionExcludeMembershipRule -CollectionId $CollectionId -ExcludeCollectionId $Included -Force | Out-Null }
    }
}

function Test-RuleTarget {
    param([string]$Kind, [long]$Resource, [string]$Included)
    if ($Kind -eq 'Direct') {
        return (@(Invoke-CMWmiQuery -Option Fast -Query "SELECT ResourceId FROM SMS_R_System WHERE ResourceId = $Resource").Count -gt 0)
    }
    if ($Kind -eq 'Include' -or $Kind -eq 'Exclude') {
        if (-not $Included -or $Included -eq $CollectionId) { return $false }
        return ($null -ne (Get-Collection $Included))
    }
    return $true
}

# Updates a query rule so that the collection never holds fewer rules than before: the new
# rule exists before the old one is removed. A rule name cannot be changed in place, so a
# same-name update goes through a rule with a temporary name.
function Invoke-RuleUpdate {
    param([object]$Collection)
    $sameName = $RuleName -eq $OldRuleName
    if ($sameName) {
        $steps.Add('add-temporary')
        Add-Rule 'Query' $TempRuleName $Expression 0 ''
        if ($null -eq (Find-Rule (Get-Rules (Get-Collection $CollectionId)) 'Query' $TempRuleName '' 0 '')) { throw 'The temporary rule was not found after it was added.' }
        $steps.Add('remove-old')
        Remove-Rule 'Query' $OldRuleName 0 ''
        Assert-QueryRuleGone $OldRuleName 'The old rule is still present after the remove.'
        $steps.Add('add-final')
        Add-Rule 'Query' $RuleName $Expression 0 ''
        if ($null -eq (Find-Rule (Get-Rules (Get-Collection $CollectionId)) 'Query' $RuleName '' 0 '')) { throw 'The new rule was not found after it was added.' }
        $steps.Add('remove-temporary')
        Remove-Rule 'Query' $TempRuleName 0 ''
        Assert-QueryRuleGone $TempRuleName 'The temporary rule is still present after the remove.'
    } else {
        $steps.Add('add-final')
        Add-Rule 'Query' $RuleName $Expression 0 ''
        if ($null -eq (Find-Rule (Get-Rules (Get-Collection $CollectionId)) 'Query' $RuleName '' 0 '')) { throw 'The new rule was not found after it was added.' }
        $steps.Add('remove-old')
        Remove-Rule 'Query' $OldRuleName 0 ''
        Assert-QueryRuleGone $OldRuleName 'The old rule is still present after the remove.'
    }
    $steps.Add('done')
    for ($i = 0; $i -lt $OrphanRuleNames.Count; $i++) {
        $orphanName = [string]$OrphanRuleNames[$i]
        $orphanExpression = ''
        if ($i -lt $OrphanExpressions.Count) { $orphanExpression = [string]$OrphanExpressions[$i] }
        $found = @(Get-NameMatches (Get-Rules (Get-Collection $CollectionId)) $orphanName)
        if ($found.Count -eq 0) { continue }
        if ($found.Count -gt 1 -or $found[0].Expression -cne $orphanExpression) { throw 'The temporary rule changed after the plan was shown.' }
        $steps.Add('remove-orphan')
        Remove-Rule 'Query' $orphanName 0 ''
        Assert-QueryRuleGone $orphanName 'The temporary rule is still present after the remove.'
    }
}

# ---- Saved queries -------------------------------------------------------------------------

if ($Operation -in 'SaveQuery', 'UpdateQuery', 'DeleteQuery') {
    $current = $null
    if ($Operation -ne 'SaveQuery') {
        $current = @(Get-CMQuery -Id $QueryId) | Where-Object { $null -ne $_ } | Select-Object -First 1
        if ($null -eq $current) { return (Write-Result 'query-missing') }
        if ($QueryId -like 'SMS*') { return (Write-Result 'query-built-in' $null '' $current) }
        if ([string]$current.Name -cne $ExpectedName -or [string]$current.Expression -cne $ExpectedExpression `
            -or [string](Get-Prop $current 'Comments') -cne $ExpectedComment -or [string](Get-Prop $current 'TargetClassName') -cne $ExpectedTargetClassName `
            -or [string](Get-Prop $current 'LimitToCollectionID') -cne $ExpectedLimitToCollectionId) {
            return (Write-Result 'query-changed' $null '' $current)
        }
    }
    if ($Operation -ne 'DeleteQuery' -and ($Operation -eq 'SaveQuery' -or $Name -ne [string]$current.Name)) {
        $taken = Find-QueryByName $Name
        if ($null -ne $taken) { return (Write-Result 'query-name-taken' $null '' $taken) }
    }
    try {
        switch ($Operation) {
            'SaveQuery' {
                $arguments = @{ Name = $Name; Expression = $Expression }
                if ($Comment) { $arguments.Comment = $Comment }
                if ($TargetClassName) { $arguments.TargetClassName = $TargetClassName }
                if ($LimitToCollectionId) { $arguments.LimitToCollectionId = $LimitToCollectionId }
                $mutated = $true
                $made = New-CMQuery @arguments
                $createdId = [string](Get-Prop $made 'QueryID')
                if (-not $createdId) { return (Write-Result 'partial' $null 'New-CMQuery returned no query ID.') }
                $found = @(Get-CMQuery -Id $createdId) | Where-Object { $null -ne $_ } | Select-Object -First 1
                return (Write-Result 'ok' $null '' $found)
            }
            'UpdateQuery' {
                $arguments = @{ Id = $QueryId; Expression = $Expression; Comment = $Comment }
                if ($Name -cne [string]$current.Name) { $arguments.NewName = $Name }
                if ($TargetClassName) { $arguments.TargetClassName = $TargetClassName }
                # An empty value clears the limit.
                if ($LimitToCollectionId -ne [string](Get-Prop $current 'LimitToCollectionID')) { $arguments.LimitToCollectionId = $LimitToCollectionId }
                $mutated = $true
                Set-CMQuery @arguments | Out-Null
                return (Write-Result 'ok' $null '' (@(Get-CMQuery -Id $QueryId) | Select-Object -First 1))
            }
            'DeleteQuery' {
                $mutated = $true
                Remove-CMQuery -Id $QueryId -Force | Out-Null
                $left = @(Get-CMQuery -Id $QueryId) | Where-Object { $null -ne $_ } | Select-Object -First 1
                if ($null -ne $left) { return (Write-Result 'failed' $null 'The saved query is still present after the delete.' $left) }
                return (Write-Result 'ok' $null '' $current)
            }
        }
    } catch {
        if ($mutated) {
            $now = $null
            try {
                if ($Operation -ne 'SaveQuery') { $now = @(Get-CMQuery -Id $QueryId) | Select-Object -First 1 }
                elseif ($createdId) { $now = @(Get-CMQuery -Id $createdId) | Select-Object -First 1 }
            } catch { }
            return (Write-Result 'partial' $null $_.Exception.Message $now)
        }
        if (Test-AccessDenied $_) { throw }
        return (Write-Result 'failed' $null $_.Exception.Message)
    }
}

# ---- Collections ---------------------------------------------------------------------------

$collection = $null
if ($Operation -ne 'Create') {
    $collection = Get-Collection $CollectionId
    if ($null -eq $collection) { return (Write-Result 'collection-missing') }
    if ($Operation -notin 'Copy', 'Evaluate' -and $CollectionId -like 'SMS*') { return (Write-Result 'built-in' $collection) }
}
$rules = @(Get-Rules $collection)

if ($CheckState -and $null -ne $collection) {
    $changed = [string]$collection.Name -cne $ExpectedName
    if ($Operation -in 'SetProperties', 'Remove') {
        $changed = $changed -or [string](Get-Prop $collection 'Comment') -cne $ExpectedComment `
            -or [string](Get-Prop $collection 'LimitToCollectionID') -ne $ExpectedLimitingId `
            -or [int](Get-Prop $collection 'RefreshType') -ne $ExpectedRefreshType `
            -or (Get-ScheduleToken $collection) -ne $ExpectedRefreshSchedule
    }
    if ($Operation -eq 'Remove') {
        $now = @($rules | ForEach-Object { Get-RuleKey $_ } | Sort-Object -CaseSensitive) -join "`n"
        $then = @($ExpectedRuleKeys | Sort-Object -CaseSensitive) -join "`n"
        $changed = $changed -or ($now -cne $then)
    }
    if ($changed) { return (Write-Result 'changed' $collection) }
}

switch ($Operation) {
    'Create' {
        if ($null -ne (Find-CollectionByName $Name)) { return (Write-Result 'name-taken') }
        if ($null -eq (Get-Collection $LimitingCollectionId)) { return (Write-Result 'limiting-missing') }
    }
    'Copy' {
        if ($null -ne (Find-CollectionByName $Name)) { return (Write-Result 'name-taken' $collection) }
    }
    'SetProperties' {
        if ($Name -cne [string]$collection.Name -and $Name -ne [string]$collection.Name -and $null -ne (Find-CollectionByName $Name)) { return (Write-Result 'name-taken' $collection) }
        if ($LimitingCollectionId -eq $CollectionId) { return (Write-Result 'limiting-self' $collection) }
        if ($null -eq (Get-Collection $LimitingCollectionId)) { return (Write-Result 'limiting-missing' $collection) }
    }
    'AddRule' {
        if ($RuleKind -eq 'Query') {
            if (@(Get-NameMatches $rules $RuleName).Count -gt 0) { return (Write-Result 'rule-taken' $collection) }
        } elseif ($null -ne (Find-Rule $rules $RuleKind $RuleName '' $ResourceId $IncludedCollectionId)) {
            return (Write-Result 'rule-taken' $collection)
        }
        if (-not (Test-RuleTarget $RuleKind $ResourceId $IncludedCollectionId)) { return (Write-Result 'target-missing' $collection) }
    }
    'RemoveRule' {
        $existing = Find-Rule $rules $RuleKind $OldRuleName '' $ResourceId $IncludedCollectionId
        if ($null -eq $existing) { return (Write-Result 'rule-missing' $collection) }
        if ($RuleKind -eq 'Query') {
            if ($existing.Expression -cne $OldExpression) { return (Write-Result 'rule-changed' $collection) }
            if (@(Get-NameMatches $rules $OldRuleName).Count -gt 1) { return (Write-Result 'rule-duplicate' $collection) }
        }
    }
    'UpdateRule' {
        $existing = Find-Rule $rules 'Query' $OldRuleName '' 0 ''
        if ($null -eq $existing) { return (Write-Result 'rule-missing' $collection) }
        if ($existing.Expression -cne $OldExpression) { return (Write-Result 'rule-changed' $collection) }
        if (@(Get-NameMatches $rules $OldRuleName).Count -gt 1) { return (Write-Result 'rule-duplicate' $collection) }
        if ($RuleName -ne $OldRuleName -and @(Get-NameMatches $rules $RuleName).Count -gt 0) { return (Write-Result 'rule-taken' $collection) }
        if ($RuleName -eq $OldRuleName -and @(Get-NameMatches $rules $TempRuleName).Count -gt 0) { return (Write-Result 'rule-taken' $collection) }
    }
    'Remove' {
        # ConfigMgr refuses to remove a collection that limits, includes or excludes another.
        $dependents = @(Invoke-CMWmiQuery -Option Fast -Query "SELECT DependentCollectionID, RelationshipType FROM SMS_CollectionDependencies WHERE SourceCollectionID = '$CollectionId'")
        $limited = @($dependents | Where-Object { [int]$_.RelationshipType -eq 1 })
        if ($limited.Count -gt 0) { return (Write-Result 'is-limiting' $collection '' $null ([string[]]@($limited | ForEach-Object { [string]$_.DependentCollectionID }))) }
        $referenced = @($dependents | Where-Object { [int]$_.RelationshipType -in 2, 3 })
        if ($referenced.Count -gt 0) { return (Write-Result 'is-referenced' $collection '' $null ([string[]]@($referenced | ForEach-Object { [string]$_.DependentCollectionID }))) }
        # ConfigMgr removes the deployments and maintenance windows with the collection.
        $fresh = Get-Collection $CollectionId
        if ($null -eq $fresh) { return (Write-Result 'collection-missing') }
        if (-not $CheckState -or ($ExpectedMemberCount -ge 0 -and [int](Get-Prop $fresh 'MemberCount') -ne $ExpectedMemberCount) -or [int](Get-Prop $fresh 'ServiceWindowsCount') -ne $ExpectedWindows `
            -or (Get-DeploymentCount $CollectionId) -ne $ExpectedDeployments) {
            return (Write-Result 'changed' $fresh)
        }
    }
}

try {
    switch ($Operation) {
        'Create' {
            $arguments = @{ Name = $Name; LimitingCollectionId = $LimitingCollectionId; RefreshType = $RefreshType }
            if ($Comment) { $arguments.Comment = $Comment }
            if ($RefreshType -in 'Periodic', 'Both') { $arguments.RefreshSchedule = New-RefreshSchedule }
            $mutated = $true
            $made = New-CMDeviceCollection @arguments
            $id = [string](Get-Prop $made 'CollectionID')
            if (-not $id) { return (Write-Result 'partial' $null 'New-CMDeviceCollection returned no collection ID.') }
            $createdId = $id
            $steps.Add('created')
            $failed = New-Object System.Collections.Generic.List[string]
            for ($i = 0; $i -lt $RuleKinds.Count; $i++) {
                try {
                    $resource = 0L
                    if ($i -lt $RuleResourceIds.Count -and $RuleResourceIds[$i]) { $resource = [long]$RuleResourceIds[$i] }
                    $included = ''
                    if ($i -lt $RuleCollectionIds.Count) { $included = [string]$RuleCollectionIds[$i] }
                    if ($included -and $included -notmatch '^[A-Za-z0-9]{1,8}$') { throw 'The collection ID of the rule is not valid.' }
                    $ruleName = ''
                    if ($i -lt $RuleNames.Count) { $ruleName = [string]$RuleNames[$i] }
                    $ruleExpression = ''
                    if ($i -lt $RuleExpressions.Count) { $ruleExpression = [string]$RuleExpressions[$i] }
                    Add-Rule $RuleKinds[$i] $ruleName $ruleExpression $resource $included $id
                    $steps.Add("rule-$i")
                } catch {
                    $failed.Add([string]$i)
                }
            }
            $created = Get-Collection $id
            if ($failed.Count -gt 0) { return (Write-Result 'rules-partial' $created ($failed -join ',')) }
            return (Write-Result 'ok' $created)
        }
        'Copy' {
            $mutated = $true
            # Copy-CMCollection -PassThru returns the source collection, not the copy.
            Copy-CMCollection -Id $CollectionId -NewName $Name | Out-Null
            # The name was free just before the copy and names are unique in the site.
            $found = Find-CollectionByName $Name
            if ($null -eq $found) { return (Write-Result 'partial' $null 'The copy was not found after it was created.') }
            $createdId = [string]$found.CollectionID
            return (Write-Result 'ok' (Get-Collection $createdId))
        }
        'SetProperties' {
            $arguments = @{ CollectionId = $CollectionId; Comment = $Comment }
            if ($Name -cne [string]$collection.Name) { $arguments.NewName = $Name }
            if ($LimitingCollectionId -ne [string](Get-Prop $collection 'LimitToCollectionID')) { $arguments.LimitingCollectionId = $LimitingCollectionId }
            if ($RefreshType) { $arguments.RefreshType = $RefreshType }
            $setSchedule = $RefreshUnit -or ($RefreshSchedule -and $RefreshSchedule -ne (Get-ScheduleToken $collection))
            if ($RefreshType -in 'Periodic', 'Both' -and $setSchedule) { $arguments.RefreshSchedule = New-RefreshSchedule }
            $mutated = $true
            Set-CMCollection @arguments | Out-Null
            return (Write-Result 'ok' (Get-Collection $CollectionId))
        }
        'AddRule' {
            $mutated = $true
            Add-Rule $RuleKind $RuleName $Expression $ResourceId $IncludedCollectionId
            $after = Get-Collection $CollectionId
            if ($null -eq (Find-Rule (Get-Rules $after) $RuleKind $RuleName '' $ResourceId $IncludedCollectionId)) { return (Write-Result 'failed' $after 'The rule was not found after it was added.') }
            return (Write-Result 'ok' $after)
        }
        'RemoveRule' {
            $mutated = $true
            Remove-Rule $RuleKind $OldRuleName $ResourceId $IncludedCollectionId
            $after = Get-Collection $CollectionId
            if ($null -ne (Find-Rule (Get-Rules $after) $RuleKind $OldRuleName '' $ResourceId $IncludedCollectionId)) { return (Write-Result 'failed' $after 'The rule is still present after the remove.') }
            return (Write-Result 'ok' $after)
        }
        'UpdateRule' {
            Invoke-RuleUpdate $collection
            return (Write-Result 'ok' (Get-Collection $CollectionId))
        }
        'Evaluate' {
            $mutated = $true
            Invoke-CMCollectionUpdate -CollectionId $CollectionId | Out-Null
            return (Write-Result 'ok' $collection)
        }
        'Remove' {
            $mutated = $true
            Remove-CMDeviceCollection -CollectionId $CollectionId -Force | Out-Null
            $left = Get-Collection $CollectionId
            if ($null -ne $left) { return (Write-Result 'failed' $left 'The collection is still present after the remove.') }
            return (Write-Result 'ok')
        }
    }
} catch {
    $failure = $_
    if ($Operation -eq 'UpdateRule' -and $steps.Count -gt 0 -and -not ($steps.Count -eq 1 -and (Test-AccessDenied $failure))) {
        $now = $null
        try { $now = Get-Collection $CollectionId } catch { }
        return (Write-Result 'update-failed' $now $failure.Exception.Message)
    }
    if ($mutated) {
        $now = $null
        try {
            if ($Operation -eq 'Create' -or $Operation -eq 'Copy') { if ($createdId) { $now = Get-Collection $createdId } }
            else { $now = Get-Collection $CollectionId }
        } catch { }
        return (Write-Result 'partial' $now $failure.Exception.Message)
    }
    if (Test-AccessDenied $failure) { throw }
    return (Write-Result 'failed' $null $failure.Exception.Message)
}
