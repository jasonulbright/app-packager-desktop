param(
    [ValidateSet('Deployments', 'Content', 'DistributionPoints', 'SiteHealth')][string]$Operation,
    [ValidateRange(1, 10000000)][int]$MaxRows = 100000,
    [string]$ExpectSite = '',
    [string]$ExpectProvider = ''
)

# Read only: every provider call is a SELECT through Invoke-CMWmiQuery. No value from the caller
# reaches WQL text. Every value leaves the session as text in a fixed column order; a time leaves
# as the clock value the provider returns, unconverted. A failure leaves as its kind only; the
# provider's error text never leaves the session.

$CMPSSuppressFastNotUsedCheck = $true
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$BlockSize = 1000
$script:Queries = 0

function Invoke-Query {
    param([string]$Query)
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
    if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-ddTHH:mm:ss', $Invariant) }
    if ($Value -is [bool]) { if ($Value) { return 'True' } else { return 'False' } }
    if ($Value -is [System.Array]) { return (@($Value | ForEach-Object { ConvertTo-Text $_ }) -join '|') }
    return [string]::Format($Invariant, '{0}', $Value)
}

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

# One bulk query; rows past the cap are counted, not kept.
function Read-Set {
    param([string]$Name, [string]$Class, [string[]]$Columns, [string]$Where = '')
    $query = 'SELECT ' + ($Columns -join ', ') + ' FROM ' + $Class
    if ($Where) { $query += ' WHERE ' + $Where }
    $rows = New-Object System.Collections.Generic.List[object]
    $total = 0
    foreach ($o in Invoke-Query $query) {
        $total++
        if ($rows.Count -lt $MaxRows) { $rows.Add([string[]]@(foreach ($c in $Columns) { ConvertTo-Text (Get-Prop $o $c) })) }
    }
    Write-Set $Name $Columns $rows.ToArray() $total
}

function Get-FailureKind {
    param([System.Management.Automation.ErrorRecord]$Record)
    $e = $Record.Exception
    while ($null -ne $e.InnerException) { $e = $e.InnerException }
    $text = [string]$Record.Exception.Message
    if ($e -is [System.UnauthorizedAccessException] -or $text -match 'denied|0x80041003|0x80070005') { return 'access' }
    if ($text -match 'Invalid class|Invalid query|Invalid column|not found') { return 'not-found' }
    if ($text -match 'timed out|timeout|Error waiting for query') { return 'timeout' }
    return 'error'
}

# The session can reconnect between two commands, so a caller that names a site and provider gets
# nothing from a session on another one. The short and the fully qualified name of a host are the
# same provider.
function Test-SessionConnection {
    if (-not $ExpectSite) { return $true }
    $drive = (Get-Location).Drive
    if ($null -eq $drive -or ([string]$drive.Name) -ne $ExpectSite) { return $false }
    $want = $ExpectProvider.Trim().TrimEnd('\')
    if (-not $want) { $want = $env:COMPUTERNAME }
    $a = ([string]$drive.Root).Trim().TrimEnd('\').ToLowerInvariant()
    $b = ([string]$want).ToLowerInvariant()
    if (-not $a) { return $false }
    if ($a -eq $b) { return $true }
    if ($a.Contains('.') -eq $b.Contains('.')) { return $false }
    return (($a -split '\.')[0] -eq ($b -split '\.')[0])
}

if (-not (Test-SessionConnection)) {
    [pscustomobject]@{ Kind = 'Connection'; Same = $false }
    return
}

try {
    switch ($Operation) {
        'Deployments' {
            Read-Set 'Deployments' 'SMS_DeploymentSummary' @('DeploymentID', 'SoftwareName', 'ApplicationName', 'CollectionName', 'CollectionID', 'FeatureType', 'DeploymentIntent',
                'NumberTargeted', 'NumberSuccess', 'NumberErrors', 'NumberInProgress', 'NumberUnknown', 'NumberOther', 'SummarizationTime')
        }
        'Content' {
            Read-Set 'Pairs' 'SMS_PackageStatusDistPointsSummarizer' @('PackageID', 'ServerNALPath', 'State', 'PackageType')
            Read-Set 'Messages' 'SMS_DistributionDPStatus' @('PackageID', 'NalPath', 'MessageState')
            Read-Set 'Names' 'SMS_PackageStatusRootSummarizer' @('PackageID', 'Name')
        }
        'DistributionPoints' {
            Read-Set 'Dps' 'SMS_DistributionPointInfo' @('NALPath', 'ServerName', 'SiteCode', 'IsPullDP', 'IsPXE', 'ResourceType')
            Read-Set 'DpStatus' 'SMS_SiteSystemSummarizer' @('SiteSystem', 'Status') "Role = 'SMS Distribution Point'"
        }
        'SiteHealth' {
            Read-Set 'Components' 'SMS_ComponentSummarizer' @('ComponentName', 'MachineName', 'SiteCode', 'Status', 'State', 'LastStarted') "TallyInterval = '0001128000100008'"
            Read-Set 'SiteSystems' 'SMS_SiteSystemSummarizer' @('SiteSystem', 'SiteCode', 'Role', 'Status', 'ObjectType')
        }
    }
}
catch {
    [pscustomobject]@{ Kind = 'Failed'; Failure = (Get-FailureKind $_) }
}
[pscustomobject]@{ Kind = 'Queries'; Count = $script:Queries }
