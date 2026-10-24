param(
    [ValidateSet('Load', 'State', 'Apply')][string]$Operation,
    [ValidateRange(1, 1000000)][int]$MaxPairs = 100000,
    [string[]]$PackageIds = @(),
    [bool]$Impact = $false,
    [ValidateSet('', 'Redistribute', 'Validate', 'Remove', 'DistributeAgain')][string]$Kind = '',
    [string]$Pairs = ''
)

# Only PackageIDs that match the pattern below, model names that match their pattern and numbers
# read back from the provider reach WQL text. NAL paths and names never do: a pair is found by its
# PackageID and then compared with the whole NAL path in PowerShell, without case (the provider
# returns one NAL path in different letter case from different classes).

$CMPSSuppressFastNotUsedCheck = $true
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$TimeFormat = 'yyyy-MM-ddTHH:mm:ss'
$IdPattern = '^[A-Za-z0-9]{8}$'
$ModelNamePattern = '^ScopeId_[A-Za-z0-9-]+/Application_[A-Za-z0-9-]+$'
$ChunkSize = 50

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

# Instances that are written back are read without the Fast option: a Put of an instance read with
# Fast would write its lazy properties (ISVData) back empty.
function Invoke-FullQuery {
    param([string]$Query)
    @(Invoke-CMWmiQuery -Query $Query) | Where-Object { $null -ne $_ }
}

function Get-TimeText {
    param([object]$Value)
    if ($Value -isnot [datetime]) { return '' }
    return $Value.ToString($TimeFormat, $Invariant)
}

function Get-Int {
    param([object]$Value, [int]$Default = -1)
    if ($null -eq $Value) { return $Default }
    try { return [int]$Value } catch { return $Default }
}

# The checked IDs, upper case, without duplicates, in chunks for OR lists.
function Get-IdChunks {
    param([string[]]$Ids)
    $clean = @($Ids | Where-Object { $_ -match $IdPattern } | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    for ($i = 0; $i -lt $clean.Count; $i += $ChunkSize) { , @($clean[$i..([Math]::Min($i + $ChunkSize, $clean.Count) - 1)]) }
}

function Get-OrList {
    param([string]$Property, [string[]]$Values)
    ($Values | ForEach-Object { "$Property = '$_'" }) -join ' OR '
}

# ---- Distribution points --------------------------------------------------------------------

function Get-Dps {
    foreach ($d in Invoke-Query 'SELECT NALPath, ServerName, SiteCode, IsPullDP, ResourceType FROM SMS_DistributionPointInfo') {
        [pscustomobject]@{ Kind = 'Dp'; Nal = [string](Get-Prop $d 'NALPath'); Name = [string](Get-Prop $d 'ServerName'); SiteCode = [string](Get-Prop $d 'SiteCode')
            IsPullDp = [bool](Get-Prop $d 'IsPullDP'); ResourceType = [string](Get-Prop $d 'ResourceType') }
    }
}

# The source distribution points of each pull distribution point, as NAL paths. Only the
# SourceDistributionPoints list of the role is read; no other role property leaves the session.
function Get-PullSources {
    param([object[]]$Dps)
    if (@($Dps | Where-Object { $_.IsPullDp }).Count -eq 0) { return }
    foreach ($role in Invoke-FullQuery "SELECT * FROM SMS_SCI_SysResUse WHERE RoleName = 'SMS Distribution Point'") {
        $nal = [string](Get-Prop $role 'NALPath')
        $pull = @($Dps | Where-Object { $_.IsPullDp -and $_.Nal -ieq $nal })
        if ($pull.Count -eq 0) { continue }
        foreach ($list in @(Get-Prop $role 'PropLists')) {
            if ([string](Get-Prop $list 'PropertyListName') -ne 'SourceDistributionPoints') { continue }
            foreach ($source in @(Get-Prop $list 'Values')) { if ($source) { [pscustomobject]@{ Kind = 'PullSource'; Nal = $nal; Source = [string]$source } } }
        }
    }
}

# ---- Status rows ----------------------------------------------------------------------------

# Status rows travel packed: a table of NAL paths and tab-separated rows that name a NAL path by
# its index, in blocks of 1000, so tens of thousands of rows stay a few output objects.
$script:NalIndex = @{}
$script:NalList = New-Object System.Collections.Generic.List[string]

function Get-NalIndex {
    param([string]$Nal)
    $key = $Nal.ToUpperInvariant()
    if (-not $script:NalIndex.ContainsKey($key)) { $script:NalIndex[$key] = $script:NalList.Count; $script:NalList.Add($Nal) }
    return $script:NalIndex[$key]
}

function Write-Packed {
    param([string]$Name, [System.Collections.Generic.List[string]]$Rows)
    for ($i = 0; $i -lt $Rows.Count; $i += 1000) {
        $last = [Math]::Min($i + 1000, $Rows.Count) - 1
        [pscustomobject]@{ Kind = $Name; Rows = @($Rows[$i..$last]) }
    }
}

function Write-NalTable {
    for ($i = 0; $i -lt $script:NalList.Count; $i++) { [pscustomobject]@{ Kind = 'Nal'; Index = $i; Nal = $script:NalList[$i] } }
}

function ConvertTo-PairText {
    param([object]$Row)
    $nal = [string](Get-Prop $Row 'ServerNALPath')
    ([string](Get-Prop $Row 'PackageID')).ToUpperInvariant() + "`t" + (Get-NalIndex $nal) + "`t" + (Get-Int (Get-Prop $Row 'State')) + "`t" + (Get-Int (Get-Prop $Row 'SourceVersion') 0) + "`t" +
        (Get-TimeText (Get-Prop $Row 'SummaryDate')) + "`t" + (Get-Int (Get-Prop $Row 'PackageType')) + "`t" + [string](Get-Prop $Row 'SiteCode')
}

function ConvertTo-StatusText {
    param([object]$Row)
    $nal = [string](Get-Prop $Row 'NalPath')
    ([string](Get-Prop $Row 'PackageID')).ToUpperInvariant() + "`t" + (Get-NalIndex $nal) + "`t" + (Get-Int (Get-Prop $Row 'MessageState')) + "`t" + (Get-Int (Get-Prop $Row 'MessageID')) + "`t" +
        (Get-TimeText (Get-Prop $Row 'LastUpdateDate'))
}

function ConvertTo-InstanceText {
    param([object]$Row)
    $nal = [string](Get-Prop $Row 'ServerNALPath')
    ([string](Get-Prop $Row 'PackageID')).ToUpperInvariant() + "`t" + (Get-NalIndex $nal) + "`t" + (Get-Int (Get-Prop $Row 'Status')) + "`t" + [string]([bool](Get-Prop $Row 'RefreshNow')) + "`t" +
        (Get-TimeText (Get-Prop $Row 'LastRefreshTime'))
}

$SummaryColumns = 'PackageID, ServerNALPath, State, SourceVersion, SummaryDate, PackageType, SiteCode'
$StatusColumns = 'PackageID, NalPath, MessageState, MessageID, LastUpdateDate'
$InstanceColumns = 'PackageID, ServerNALPath, Status, RefreshNow, LastRefreshTime'

# The status rows of the given packages: the summarizer rows, the SMS_DistributionPoint instances
# and the last distribution status message of each pair.
function Write-StateOf {
    param([string[]]$Ids, [string]$Phase)
    $summary = New-Object System.Collections.Generic.List[string]
    $status = New-Object System.Collections.Generic.List[string]
    $instances = New-Object System.Collections.Generic.List[string]
    foreach ($chunk in Get-IdChunks $Ids) {
        $where = Get-OrList 'PackageID' $chunk
        foreach ($r in Invoke-Query "SELECT $SummaryColumns FROM SMS_PackageStatusDistPointsSummarizer WHERE $where") { $summary.Add((ConvertTo-PairText $r)) }
        foreach ($r in Invoke-Query "SELECT $StatusColumns FROM SMS_DistributionDPStatus WHERE $where") { $status.Add((ConvertTo-StatusText $r)) }
        foreach ($r in Invoke-Query "SELECT $InstanceColumns FROM SMS_DistributionPoint WHERE $where") { $instances.Add((ConvertTo-InstanceText $r)) }
    }
    Write-NalTable
    foreach ($o in Write-Packed 'Pairs' $summary) { $o | Add-Member -NotePropertyName Phase -NotePropertyValue $Phase; $o }
    foreach ($o in Write-Packed 'Status' $status) { $o | Add-Member -NotePropertyName Phase -NotePropertyValue $Phase; $o }
    foreach ($o in Write-Packed 'Instances' $instances) { $o | Add-Member -NotePropertyName Phase -NotePropertyValue $Phase; $o }
    [pscustomobject]@{ Kind = 'StateRead'; Phase = $Phase; Read = $true }
}

# ---- Content objects ------------------------------------------------------------------------

function Get-AppIds {
    $map = @{}
    foreach ($a in Invoke-Query 'SELECT CI_ID, ModelName FROM SMS_Application WHERE IsLatest = 1') {
        $model = [string](Get-Prop $a 'ModelName')
        if ($model) { $map[$model.ToUpperInvariant()] = [string](Get-Prop $a 'CI_ID') }
    }
    return $map
}

function Write-Content {
    $apps = Get-AppIds
    $classes = @(
        @{ Class = 'SMS_Package'; Type = 'Package'; Columns = 'PackageID, Name, PackageSize, PackageType'; Keep = 0 },
        @{ Class = 'SMS_SoftwareUpdatesPackage'; Type = 'SoftwareUpdatePackage'; Columns = 'PackageID, Name, PackageSize' },
        @{ Class = 'SMS_BootImagePackage'; Type = 'BootImage'; Columns = 'PackageID, Name, PackageSize' },
        @{ Class = 'SMS_ImagePackage'; Type = 'OsImage'; Columns = 'PackageID, Name, PackageSize' },
        @{ Class = 'SMS_OperatingSystemInstallPackage'; Type = 'OsUpgradePackage'; Columns = 'PackageID, Name, PackageSize' },
        @{ Class = 'SMS_DriverPackage'; Type = 'DriverPackage'; Columns = 'PackageID, Name, PackageSize' },
        @{ Class = 'SMS_ContentPackage'; Type = 'Application'; Columns = 'PackageID, Name, PackageSize, SecurityKey' },
        @{ Class = 'SMS_TaskSequencePackage'; Type = 'TaskSequence'; Columns = 'PackageID, Name' }
    )
    foreach ($c in $classes) {
        foreach ($p in Invoke-Query ('SELECT ' + $c.Columns + ' FROM ' + $c.Class)) {
            if ($c.ContainsKey('Keep') -and (Get-Int (Get-Prop $p 'PackageType') 0) -ne $c.Keep) { continue }
            $object = [string](Get-Prop $p 'PackageID')
            if ($c.Type -eq 'Application') {
                $key = [string](Get-Prop $p 'SecurityKey')
                $object = if ($key -and $apps.ContainsKey($key.ToUpperInvariant())) { $apps[$key.ToUpperInvariant()] } else { '' }
            }
            [pscustomobject]@{ Kind = 'Content'; PackageId = ([string](Get-Prop $p 'PackageID')).ToUpperInvariant(); Name = [string](Get-Prop $p 'Name'); Type = $c.Type
                SizeKb = [double](Get-Int (Get-Prop $p 'PackageSize') 0); ObjectId = $object }
        }
    }
}

# ---- Deployments that use the content --------------------------------------------------------

# Counts per content: application deployments of the application that owns application content,
# package and task sequence deployments of the package itself, and deployments of task sequences
# that reference the content or use it as their boot image. Software update deployments and
# application dependencies are not counted. A failed query leaves every count unknown
# (DeploymentsRead false) and the rest of the State output stands.
function Write-Deployments {
    param([string[]]$Ids)
    try { $counts = Get-DeploymentCounts $Ids }
    catch { [pscustomobject]@{ Kind = 'DeploymentsRead'; Read = $false }; return }
    foreach ($k in $counts.Keys) { [pscustomobject]@{ Kind = 'Deployments'; PackageId = $k; Count = $counts[$k] } }
    [pscustomobject]@{ Kind = 'DeploymentsRead'; Read = $true }
}

function Get-DeploymentCounts {
    param([string[]]$Ids)
    # A non-terminating query error would otherwise leave a count too low without reaching the catch of Write-Deployments.
    $ErrorActionPreference = 'Stop'
    $counts = @{}
    foreach ($id in $Ids) { if ($id -match $IdPattern) { $counts[$id.ToUpperInvariant()] = 0 } }
    $modelOf = @{}
    $tsOf = @{}
    foreach ($chunk in Get-IdChunks $Ids) {
        $where = Get-OrList 'PackageID' $chunk
        foreach ($c in Invoke-Query "SELECT PackageID, SecurityKey FROM SMS_ContentPackage WHERE $where") {
            $key = [string](Get-Prop $c 'SecurityKey')
            if ($key -match $ModelNamePattern) { $modelOf[$key.ToUpperInvariant()] = ([string](Get-Prop $c 'PackageID')).ToUpperInvariant() }
        }
        foreach ($a in Invoke-Query "SELECT AdvertisementID, PackageID FROM SMS_Advertisement WHERE $where") { $p = ([string](Get-Prop $a 'PackageID')).ToUpperInvariant(); if ($counts.ContainsKey($p)) { $counts[$p]++ } }
        foreach ($r in Invoke-Query ("SELECT PackageID, RefPackageID FROM SMS_TaskSequencePackageReference WHERE " + (Get-OrList 'RefPackageID' $chunk))) {
            $ts = ([string](Get-Prop $r 'PackageID')).ToUpperInvariant(); $ref = ([string](Get-Prop $r 'RefPackageID')).ToUpperInvariant()
            if ($ts -match $IdPattern) { if (-not $tsOf.ContainsKey($ts)) { $tsOf[$ts] = New-Object System.Collections.Generic.HashSet[string] }; [void]$tsOf[$ts].Add($ref) }
        }
        foreach ($t in Invoke-Query ("SELECT PackageID, BootImageID FROM SMS_TaskSequencePackage WHERE " + (Get-OrList 'BootImageID' $chunk))) {
            $ts = ([string](Get-Prop $t 'PackageID')).ToUpperInvariant(); $ref = ([string](Get-Prop $t 'BootImageID')).ToUpperInvariant()
            if ($ts -match $IdPattern) { if (-not $tsOf.ContainsKey($ts)) { $tsOf[$ts] = New-Object System.Collections.Generic.HashSet[string] }; [void]$tsOf[$ts].Add($ref) }
        }
    }
    $models = @($modelOf.Keys)
    for ($i = 0; $i -lt $models.Count; $i += $ChunkSize) {
        $names = @($models[$i..([Math]::Min($i + $ChunkSize, $models.Count) - 1)])
        $byModel = @{}
        foreach ($a in Invoke-Query ('SELECT ModelID, ModelName FROM SMS_Application WHERE IsLatest = 1 AND (' + (($names | ForEach-Object { "ModelName = '$_'" }) -join ' OR ') + ')')) {
            $id = [string](Get-Prop $a 'ModelID'); if ($id -match '^[0-9]{1,10}$') { $byModel[$id] = ([string](Get-Prop $a 'ModelName')).ToUpperInvariant() }
        }
        if ($byModel.Count -gt 0) {
            foreach ($d in Invoke-Query ('SELECT AssignmentID, AppModelID FROM SMS_ApplicationAssignment WHERE ' + (($byModel.Keys | ForEach-Object { "AppModelID = $_" }) -join ' OR '))) {
                $model = $byModel[[string](Get-Prop $d 'AppModelID')]
                if ($model -and $modelOf.ContainsKey($model)) { $counts[$modelOf[$model]]++ }
            }
        }
    }
    $sequences = @($tsOf.Keys)
    for ($i = 0; $i -lt $sequences.Count; $i += $ChunkSize) {
        $part = @($sequences[$i..([Math]::Min($i + $ChunkSize, $sequences.Count) - 1)])
        foreach ($a in Invoke-Query ('SELECT AdvertisementID, PackageID FROM SMS_Advertisement WHERE ' + (Get-OrList 'PackageID' $part))) {
            $ts = ([string](Get-Prop $a 'PackageID')).ToUpperInvariant()
            foreach ($ref in $tsOf[$ts]) { if ($counts.ContainsKey($ref) -and $ref -ne $ts) { $counts[$ref]++ } }
        }
    }
    return $counts
}

# ---- Apply ----------------------------------------------------------------------------------

# A failure is reported by its kind only; the provider's error text never leaves the session.
function Get-FailureKind {
    param([System.Management.Automation.ErrorRecord]$Record)
    $e = $Record.Exception
    while ($null -ne $e.InnerException) { $e = $e.InnerException }
    $text = [string]$Record.Exception.Message
    if ($e -is [System.UnauthorizedAccessException] -or $text -match 'denied|0x80041003|0x80070005') { return 'access' }
    if ($text -match 'No object corresponds|not found|No content destination') { return 'not-found' }
    return 'error'
}

function Get-AppId {
    param([string]$PackageId)
    $c = Invoke-Query "SELECT PackageID, SecurityKey FROM SMS_ContentPackage WHERE PackageID = '$PackageId'" | Select-Object -First 1
    $key = [string](Get-Prop $c 'SecurityKey')
    if ($key -notmatch $ModelNamePattern) { return '' }
    $a = Invoke-Query "SELECT CI_ID, ModelName FROM SMS_Application WHERE IsLatest = 1 AND ModelName = '$key'" | Select-Object -First 1
    $id = [string](Get-Prop $a 'CI_ID')
    if ($id -match '^[0-9]{1,10}$') { return $id }
    return ''
}

function Start-Distribution {
    param([string]$PackageId, [string]$Type, [string]$DpName)
    $common = @{ DistributionPointName = $DpName; ErrorAction = 'Stop' }
    switch ($Type) {
        'Package' { Start-CMContentDistribution -PackageId $PackageId @common }
        'SoftwareUpdatePackage' { Start-CMContentDistribution -DeploymentPackageId $PackageId @common }
        'BootImage' { Start-CMContentDistribution -BootImageId $PackageId @common }
        'OsImage' { Start-CMContentDistribution -OperatingSystemImageId $PackageId @common }
        'OsUpgradePackage' { Start-CMContentDistribution -OperatingSystemInstallerId $PackageId @common }
        'DriverPackage' { Start-CMContentDistribution -DriverPackageId $PackageId @common }
        'Application' {
            $app = Get-AppId $PackageId
            if (-not $app) { throw 'No object corresponds to the specified parameters.' }
            Start-CMContentDistribution -ApplicationId $app -DisableContentDependencyDetection @common
        }
        default { throw 'unsupported' }
    }
}

function Invoke-Apply {
    # Windows PowerShell 5.1 returns a JSON array as one object; foreach enumerates it.
    $parsed = ConvertFrom-Json -InputObject $Pairs
    $requested = @(foreach ($x in $parsed) { $x })
    $ids = @($requested | ForEach-Object { [string]$_.P } | Where-Object { $_ -match $IdPattern } | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
    $summary = @{}
    $instances = @{}
    $messages = @{}
    foreach ($chunk in Get-IdChunks $ids) {
        $where = Get-OrList 'PackageID' $chunk
        foreach ($r in Invoke-Query "SELECT $SummaryColumns FROM SMS_PackageStatusDistPointsSummarizer WHERE $where") { $summary[([string]$r.PackageID).ToUpperInvariant() + '|' + ([string]$r.ServerNALPath).ToUpperInvariant()] = $r }
        foreach ($r in Invoke-Query "SELECT $StatusColumns FROM SMS_DistributionDPStatus WHERE $where") { $messages[([string]$r.PackageID).ToUpperInvariant() + '|' + ([string]$r.NalPath).ToUpperInvariant()] = $r }
        $list = if ($Kind -in 'Redistribute', 'Validate', 'Remove') { Invoke-FullQuery "SELECT * FROM SMS_DistributionPoint WHERE $where" } else { Invoke-Query "SELECT $InstanceColumns FROM SMS_DistributionPoint WHERE $where" }
        foreach ($r in $list) { $instances[([string]$r.PackageID).ToUpperInvariant() + '|' + ([string]$r.ServerNALPath).ToUpperInvariant()] = $r }
    }
    $dpNames = @{}
    if ($Kind -eq 'DistributeAgain') { foreach ($d in Get-Dps) { $dpNames[$d.Nal.ToUpperInvariant()] = $d.Name } }
    foreach ($p in $requested) {
        $id = [string]$p.P
        $nal = [string]$p.N
        $expected = [int]$p.S
        $out = [ordered]@{ Kind = 'Applied'; PackageId = $id; Nal = $nal; Status = ''; Failure = ''; StateNow = -1; BeforeRefresh = ''; BeforeMessage = -1; BeforeMessageTime = '' }
        if ($id -notmatch $IdPattern -or -not $nal) { $out.Status = 'failed'; $out.Failure = 'invalid'; [pscustomobject]$out; continue }
        $key = $id.ToUpperInvariant() + '|' + $nal.ToUpperInvariant()
        $row = $summary[$key]
        $instance = $instances[$key]
        $now = if ($null -ne $row) { Get-Int (Get-Prop $row 'State') } else { -1 }
        $out.StateNow = $now
        if ($null -ne $instance) { $out.BeforeRefresh = Get-TimeText (Get-Prop $instance 'LastRefreshTime') }
        $message = $messages[$key]
        if ($null -ne $message) { $out.BeforeMessage = Get-Int (Get-Prop $message 'MessageID'); $out.BeforeMessageTime = Get-TimeText (Get-Prop $message 'LastUpdateDate') }
        if ($now -ne $expected) { $out.Status = 'stale'; [pscustomobject]$out; continue }
        try {
            switch ($Kind) {
                'Redistribute' {
                    if ($null -eq $instance) { $out.Status = 'missing'; break }
                    $instance.RefreshNow = $true
                    $instance.Put() | Out-Null
                    $out.Status = 'requested'
                }
                'Validate' {
                    if ($null -eq $instance) { $out.Status = 'missing'; break }
                    $arguments = New-Object 'System.Collections.Generic.Dictionary[string,object]'
                    $arguments['PackageId'] = $id.ToUpperInvariant()
                    $arguments['NALPath'] = [string](Get-Prop $instance 'ServerNALPath')
                    $result = $instance.ConnectionManager.ExecuteMethod('SMS_DistributionPoint', 'VerifyPackage', $arguments)
                    $code = $null
                    try { $code = $result['ReturnValue'].IntegerValue } catch { $code = $null }
                    if ($null -ne $code -and $code -ne 0) { $out.Status = 'failed'; $out.Failure = 'returned'; break }
                    $out.Status = 'requested'
                }
                'Remove' {
                    if ($null -eq $instance) { $out.Status = 'missing'; break }
                    $instance.Delete() | Out-Null
                    $out.Status = 'requested'
                }
                'DistributeAgain' {
                    if ($null -ne $instance) { $out.Status = 'stale'; break }
                    $name = $dpNames[$nal.ToUpperInvariant()]
                    if (-not $name) { $out.Status = 'failed'; $out.Failure = 'no-dp'; break }
                    Start-Distribution -PackageId $id.ToUpperInvariant() -Type ([string]$p.T) -DpName $name
                    $out.Status = 'requested'
                }
            }
        }
        catch {
            $out.Status = 'failed'
            $out.Failure = if ([string]$_.Exception.Message -eq 'unsupported') { 'unsupported' } else { Get-FailureKind $_ }
        }
        [pscustomobject]$out
    }
    Write-StateOf $ids 'After'
}

# ---- Main -----------------------------------------------------------------------------------

switch ($Operation) {
    'Load' {
        $dps = @(Get-Dps)
        $dps
        Get-PullSources $dps
        Write-Content
        $summary = New-Object System.Collections.Generic.List[string]
        $total = 0
        foreach ($r in Invoke-Query "SELECT $SummaryColumns FROM SMS_PackageStatusDistPointsSummarizer") {
            $total++
            if ($summary.Count -lt $MaxPairs) { $summary.Add((ConvertTo-PairText $r)) }
        }
        $status = New-Object System.Collections.Generic.List[string]
        foreach ($r in Invoke-Query "SELECT $StatusColumns FROM SMS_DistributionDPStatus") { if ($status.Count -lt $MaxPairs) { $status.Add((ConvertTo-StatusText $r)) } }
        Write-NalTable
        Write-Packed 'Pairs' $summary
        Write-Packed 'Status' $status
        [pscustomobject]@{ Kind = 'Total'; Pairs = $total; Shown = $summary.Count }
    }
    'State' {
        $dps = @(Get-Dps)
        $dps
        if ($Impact) { Get-PullSources $dps }
        Write-StateOf $PackageIds 'Now'
        if ($Impact) { Write-Deployments $PackageIds }
    }
    'Apply' { Invoke-Apply }
}
