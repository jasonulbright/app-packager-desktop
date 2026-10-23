param(
    [ValidateSet('Objects', 'Programs', 'DpGroups', 'Deployments', 'State', 'Deployment', 'Proof', 'Create', 'Distribute', 'Remove', 'Live')][string]$Operation,
    [ValidateSet('', 'Application', 'Package', 'TaskSequence', 'SoftwareUpdateGroup')][string]$Type = '',
    [ValidatePattern('^([0-9]{1,10}|[A-Za-z0-9]{8})?$')][string]$ObjectId = '',
    [ValidatePattern('^[0-9]{0,10}$')][string]$ModelId = '',
    [string]$Program = '',
    [ValidatePattern('^[A-Za-z0-9]{0,8}$')][string]$CollectionId = '',
    [ValidatePattern('^(\{[0-9A-Fa-f-]{36}\}|[A-Za-z0-9]{8})?$')][string]$DeploymentId = '',
    [ValidateSet('', 'Available', 'Required')][string]$Purpose = '',
    [ValidatePattern('^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})?$')][string]$Available = '',
    [ValidatePattern('^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})?$')][string]$Deadline = '',
    [bool]$Utc = $false,
    [ValidateSet('', 'DisplayAll', 'DisplaySoftwareCenterOnly', 'HideAll')][string]$Notification = '',
    [bool]$Override = $false,
    [bool]$Reboot = $false,
    [bool]$Metered = $false,
    [ValidateSet('', 'DownloadContentFromDistributionPointAndRunLocally', 'RunProgramFromDistributionPoint', 'DownloadContentLocallyWhenNeededByRunningTaskSequence', 'DownloadAllContentLocallyBeforeStartingTaskSequence', 'RunFromDistributionPoint')][string]$Network = '',
    [ValidateSet('', 'DoNotRunProgram', 'DownloadContentFromDistributionPointAndLocally')][string]$Slow = '',
    [ValidateSet('', 'NeverRerunDeployedProgram', 'AlwaysRerunProgram', 'RerunIfFailedPreviousAttempt', 'RerunIfSucceededOnPreviousAttempt')][string]$Rerun = '',
    [bool]$ShowProgress = $true,
    [bool]$AllowFallback = $false,
    [bool]$FallbackToDp = $true,
    [bool]$PostRebootScan = $true,
    [bool]$SuppressRestart = $false,
    [bool]$AllowMicrosoftUpdate = $false,
    [ValidateSet('', 'Clients', 'ClientsMediaAndPxe', 'MediaAndPxe', 'MediaAndPxeHidden')][string]$Availability = '',
    [bool]$RequireContent = $true,
    [bool]$SkipNameCount = $false,
    [string[]]$GroupIds = @(),
    [ValidatePattern('^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})?$')][string]$RunStart = '',
    [ValidateRange(1, 5000)][int]$MaxRows = 1000,
    [ValidateRange(1, 15)][int]$ProofReads = 15,
    [string[]]$DeploymentIds = @()
)

# Only the validated IDs above, the IDs read back from the provider and the patterns checked
# below reach WQL text; names and program names are compared in PowerShell. Times travel as
# wall-clock text in the time basis of the deployment and are written back as text: the
# provider stores them without an offset.

$CMPSSuppressFastNotUsedCheck = $true
$steps = New-Object System.Collections.Generic.List[string]
$TimeFormat = 'yyyy-MM-ddTHH:mm:ss'
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture
# ConfigMgr records this account as the creator of what this session makes (verified on CM01:
# LastModifiedBy and attribute 403 of message 30006 equal the Windows identity name).
$Account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

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

# The default query option loads the lazy properties (PresentTimeIsGMT, AssignedSchedule and
# the enabled flags of SMS_Advertisement); the Fast option returns them empty.
function Invoke-FullQuery {
    param([string]$Query)
    @(Invoke-CMWmiQuery -Query $Query) | Where-Object { $null -ne $_ }
}

function Get-TimeText {
    param([object]$Value)
    if ($Value -isnot [datetime]) { return '' }
    return $Value.ToString($TimeFormat, $Invariant)
}

# CreationTime carries an offset in the provider and comes back as local time.
function Get-UtcText {
    param([object]$Value)
    if ($Value -isnot [datetime]) { return '' }
    return [datetime]::SpecifyKind($Value, [System.DateTimeKind]::Local).ToUniversalTime().ToString($TimeFormat + 'Z', $Invariant)
}

# The Time of SMS_StatusMessage comes back as the UTC wall-clock value without an offset.
function Get-UtcTextOfUtc {
    param([object]$Value)
    if ($Value -isnot [datetime]) { return '' }
    return $Value.ToString($TimeFormat + 'Z', $Invariant)
}

function Test-Bit {
    param([object]$Value, [long]$Mask)
    if ($null -eq $Value) { return $false }
    return (([long]$Value) -band $Mask) -ne 0
}

function Test-ObjectIdForm {
    if ($Type -in 'Application', 'SoftwareUpdateGroup') { return ($ObjectId -match '^[0-9]{1,10}$') }
    return ($ObjectId -match '^[A-Za-z0-9]{8}$')
}

function Write-Result {
    param([string]$Status, [string]$Detail = '', [string]$CreatedId = '', [string[]]$Differences = @(), [string[]]$Ids = @())
    [pscustomobject]@{ Kind = 'Result'; Status = $Status; Detail = $Detail; Steps = @($steps); CreatedId = $CreatedId; Differences = @($Differences); Ids = @($Ids); Account = $Account }
}

# ---- Objects ---------------------------------------------------------------------------------

$ModelNamePattern = '^ScopeId_[A-Za-z0-9-]+/Application_[A-Za-z0-9-]+$'

function Get-ObjectRow {
    if (-not (Test-ObjectIdForm)) { return $null }
    switch ($Type) {
        'Application' {
            $model = $ModelId
            if (-not $model) {
                $byCi = Invoke-Query ('SELECT CI_ID, ModelID FROM SMS_Application WHERE CI_ID = ' + $ObjectId) | Select-Object -First 1
                if ($null -eq $byCi) { return $null }
                $model = [string](Get-Prop $byCi 'ModelID')
            }
            if ($model -notmatch '^[0-9]{1,10}$') { return $null }
            $a = Invoke-Query ('SELECT CI_ID, ModelID, ModelName, LocalizedDisplayName, SoftwareVersion, IsExpired, CIVersion FROM SMS_Application WHERE ModelID = ' + $model + ' AND IsLatest = 1') | Select-Object -First 1
            if ($null -eq $a) { return $null }
            return [pscustomobject]@{ Kind = 'Object'; Id = [string]$a.CI_ID; Name = [string](Get-Prop $a 'LocalizedDisplayName'); Version = [string](Get-Prop $a 'SoftwareVersion')
                ModelId = [string](Get-Prop $a 'ModelID'); ModelName = [string](Get-Prop $a 'ModelName'); UpdateCount = 0; Program = ''; Retired = [bool](Get-Prop $a 'IsExpired')
                Revision = ([string]$a.CI_ID + '.' + [string](Get-Prop $a 'CIVersion')) }
        }
        'Package' {
            $p = Invoke-Query ("SELECT PackageID, Name, Version, SourceVersion FROM SMS_Package WHERE PackageID = '{0}'" -f $ObjectId) | Select-Object -First 1
            if ($null -eq $p) { return $null }
            $programs = @(Invoke-Query ("SELECT PackageID, ProgramName FROM SMS_Program WHERE PackageID = '{0}'" -f $ObjectId) | Where-Object { [string](Get-Prop $_ 'ProgramName') -eq $Program })
            return [pscustomobject]@{ Kind = 'Object'; Id = [string]$p.PackageID; Name = [string](Get-Prop $p 'Name'); Version = [string](Get-Prop $p 'Version')
                ModelId = ''; ModelName = ''; UpdateCount = 0; Program = $(if ($programs.Count -gt 0) { [string](Get-Prop $programs[0] 'ProgramName') } else { '' }); Retired = $false
                Revision = [string](Get-Prop $p 'SourceVersion') }
        }
        'TaskSequence' {
            $t = Invoke-Query ("SELECT PackageID, Name, Description, SourceVersion FROM SMS_TaskSequencePackage WHERE PackageID = '{0}'" -f $ObjectId) | Select-Object -First 1
            if ($null -eq $t) { return $null }
            return [pscustomobject]@{ Kind = 'Object'; Id = [string]$t.PackageID; Name = [string](Get-Prop $t 'Name'); Version = ''; ModelId = ''; ModelName = ''; UpdateCount = 0; Program = ''; Retired = $false
                Revision = [string](Get-Prop $t 'SourceVersion') }
        }
        'SoftwareUpdateGroup' {
            $g = Invoke-Query ('SELECT CI_ID, LocalizedDisplayName, NumberOfUpdates, CIVersion FROM SMS_AuthorizationList WHERE CI_ID = ' + $ObjectId) | Select-Object -First 1
            if ($null -eq $g) { return $null }
            return [pscustomobject]@{ Kind = 'Object'; Id = [string]$g.CI_ID; Name = [string](Get-Prop $g 'LocalizedDisplayName'); Version = ''; ModelId = ''; ModelName = ''
                UpdateCount = [int](Get-Prop $g 'NumberOfUpdates'); Program = ''; Retired = $false; Revision = [string](Get-Prop $g 'CIVersion') }
        }
    }
    return $null
}

function Get-Distributed {
    param([object]$Row)
    $targeted = [int](Get-Prop $Row 'Targeted')
    return ($targeted -gt 0 -and [int](Get-Prop $Row 'NumberSuccess') -ge $targeted -and [int](Get-Prop $Row 'NumberErrors') -eq 0)
}

# The content of the object: an application's content package, a package itself, or every
# package a task sequence references. A software update group has its content in deployment
# packages, so the check does not apply to it.
function Get-ContentRows {
    param([object]$Object)
    switch ($Type) {
        'Application' {
            $model = [string]$Object.ModelName
            if ($model -notmatch $ModelNamePattern) { return @() }
            return @(Invoke-Query ("SELECT PackageID, Targeted, NumberSuccess, NumberInProgress, NumberErrors FROM SMS_ObjectContentExtraInfo WHERE ObjectID = '{0}'" -f $model) |
                ForEach-Object { [pscustomobject]@{ Kind = 'Content'; PackageId = [string](Get-Prop $_ 'PackageID'); Name = [string]$Object.Name; Targeted = [int](Get-Prop $_ 'Targeted')
                    Success = [int](Get-Prop $_ 'NumberSuccess'); InProgress = [int](Get-Prop $_ 'NumberInProgress'); Errors = [int](Get-Prop $_ 'NumberErrors'); Distributed = (Get-Distributed $_) } })
        }
        'Package' {
            $rows = @(Invoke-Query ("SELECT PackageID, Targeted, NumberSuccess, NumberInProgress, NumberErrors FROM SMS_ObjectContentExtraInfo WHERE PackageID = '{0}'" -f $Object.Id))
            if ($rows.Count -eq 0) { return @([pscustomobject]@{ Kind = 'Content'; PackageId = [string]$Object.Id; Name = [string]$Object.Name; Targeted = 0; Success = 0; InProgress = 0; Errors = 0; Distributed = $false }) }
            return @($rows | ForEach-Object { [pscustomobject]@{ Kind = 'Content'; PackageId = [string]$Object.Id; Name = [string]$Object.Name; Targeted = [int](Get-Prop $_ 'Targeted')
                Success = [int](Get-Prop $_ 'NumberSuccess'); InProgress = [int](Get-Prop $_ 'NumberInProgress'); Errors = [int](Get-Prop $_ 'NumberErrors'); Distributed = (Get-Distributed $_) } })
        }
        'TaskSequence' {
            return @(Invoke-Query ("SELECT RefPackageID, ObjectName, Targeted, NumberSuccess, NumberInProgress, NumberErrors FROM SMS_TaskSequencePackageReference WHERE PackageID = '{0}'" -f $Object.Id) |
                ForEach-Object { [pscustomobject]@{ Kind = 'Content'; PackageId = [string](Get-Prop $_ 'RefPackageID'); Name = [string](Get-Prop $_ 'ObjectName'); Targeted = [int](Get-Prop $_ 'Targeted')
                    Success = [int](Get-Prop $_ 'NumberSuccess'); InProgress = [int](Get-Prop $_ 'NumberInProgress'); Errors = [int](Get-Prop $_ 'NumberErrors'); Distributed = (Get-Distributed $_) } })
        }
    }
    return @()
}

function Get-CollectionRow {
    param([string]$Id)
    if ($Id -notmatch '^[A-Za-z0-9]{8}$') { return $null }
    $c = Invoke-Query ("SELECT CollectionID, Name, CollectionType, MemberCount FROM SMS_Collection WHERE CollectionID = '{0}'" -f $Id) | Select-Object -First 1
    if ($null -eq $c) { return $null }
    [pscustomobject]@{ Kind = 'Collection'; CollectionId = [string]$c.CollectionID; Name = [string](Get-Prop $c 'Name'); CollectionType = [int](Get-Prop $c 'CollectionType'); MemberCount = [int](Get-Prop $c 'MemberCount') }
}

# ---- Deployments -----------------------------------------------------------------------------

function Get-Notification {
    param([object]$Row)
    $ui = [bool](Get-Prop $Row 'UserUIExperience'); $notify = [bool](Get-Prop $Row 'NotifyUser')
    if (-not $ui) { return 'HideAll' }
    if ($notify) { return 'DisplayAll' }
    return 'DisplaySoftwareCenterOnly'
}

function Get-RerunName {
    param([object]$Flags)
    if (Test-Bit $Flags 0x800) { return 'AlwaysRerunProgram' }
    if (Test-Bit $Flags 0x1000) { return 'NeverRerunDeployedProgram' }
    if (Test-Bit $Flags 0x2000) { return 'RerunIfFailedPreviousAttempt' }
    if (Test-Bit $Flags 0x4000) { return 'RerunIfSucceededOnPreviousAttempt' }
    return ''
}

function Get-TaskSequenceOption {
    param([object]$Flags)
    if ((Test-Bit $Flags 0x100) -or (Test-Bit $Flags 0x200)) { return 'DownloadContentLocallyWhenNeededByRunningTaskSequence' }
    if ((Test-Bit $Flags 0x8) -or (Test-Bit $Flags 0x80)) { return 'RunFromDistributionPoint' }
    if ((Test-Bit $Flags 0x10) -or (Test-Bit $Flags 0x40)) { return 'DownloadAllContentLocallyBeforeStartingTaskSequence' }
    return ''
}

function Get-AvailabilityName {
    param([object]$Flags)
    if (Test-Bit $Flags 0x20000000) { return 'MediaAndPxeHidden' }
    if (Test-Bit $Flags 0x10000000) { return 'MediaAndPxe' }
    if (Test-Bit $Flags 0x40000) { return 'ClientsMediaAndPxe' }
    return 'Clients'
}

function ConvertTo-DeploymentRow {
    param([object]$D, [hashtable]$Names)
    switch ($Type) {
        { $_ -in 'Application', 'SoftwareUpdateGroup' } {
            $collection = [string](Get-Prop $D 'TargetCollectionID')
            $deadline = Get-TimeText (Get-Prop $D 'EnforcementDeadline')
            $purpose = if ($Type -eq 'Application') { if ([int](Get-Prop $D 'OfferTypeID') -eq 0) { 'Required' } else { 'Available' } } elseif ($deadline) { 'Required' } else { 'Available' }
            $locality = Get-Prop $D 'DPLocality'
            $objectId = if ($Type -eq 'Application') { [string](@(Get-Prop $D 'AssignedCIs') | Select-Object -First 1) } else { [string](Get-Prop $D 'AssignedUpdateGroup') }
            $name = if ($Type -eq 'Application') { [string](Get-Prop $D 'ApplicationName') } else { '' }
            return [pscustomobject]@{
                Kind = 'Deployment'; DeploymentId = [string](Get-Prop $D 'AssignmentUniqueID'); AssignmentId = [string](Get-Prop $D 'AssignmentID'); ObjectId = $objectId
                ModelId = [string](Get-Prop $D 'AppModelID'); ObjectName = $name; Program = ''; CollectionId = $collection; CollectionName = [string]$Names[$collection]
                Purpose = $purpose; Available = (Get-TimeText (Get-Prop $D 'StartTime')); Deadline = $deadline; Utc = [bool](Get-Prop $D 'UseGMTTimes')
                Created = (Get-UtcText (Get-Prop $D 'CreationTime')); CreatedBy = [string](Get-Prop $D 'LastModifiedBy'); Notification = (Get-Notification $D)
                Override = [bool](Get-Prop $D 'OverrideServiceWindows'); Reboot = [bool](Get-Prop $D 'RebootOutsideOfServiceWindows'); Metered = (Test-Bit $locality 0x80000)
                FallbackToDp = -not (Test-Bit $locality 0x20000); AllowMicrosoftUpdate = (Test-Bit $locality 0x40000)
                SuppressRestart = ([int](Get-Prop $D 'SuppressReboot') -band 3) -eq 3; PostRebootScan = [bool](Get-Prop $D 'RequirePostRebootFullScan')
                Network = ''; Slow = ''; Rerun = ''; ShowProgress = $false; AllowFallback = $false; Availability = ''; ExpirationEnabled = $false; ScheduleEnabled = $true; ScheduleUtc = $false
            }
        }
        default {
            $advert = Get-Prop $D 'AdvertFlags'; $remote = Get-Prop $D 'RemoteClientFlags'
            $schedule = @(Get-Prop $D 'AssignedSchedule') | Where-Object { $null -ne $_ } | Select-Object -First 1
            $required = [int](Get-Prop $D 'OfferType') -eq 0
            $deadline = if ($required -and $null -ne $schedule) { Get-TimeText (Get-Prop $schedule 'StartTime') } else { '' }
            $collection = [string](Get-Prop $D 'CollectionID')
            $fast = if (Test-Bit $remote 0x8) { 'RunProgramFromDistributionPoint' } elseif (Test-Bit $remote 0x10) { 'DownloadContentFromDistributionPointAndRunLocally' } else { '' }
            $slow = if (Test-Bit $remote 0x40) { 'DownloadContentFromDistributionPointAndLocally' } elseif (Test-Bit $remote 0x20) { 'DoNotRunProgram' } elseif (Test-Bit $remote 0x80) { 'RunProgramFromDistributionPoint' } else { '' }
            return [pscustomobject]@{
                Kind = 'Deployment'; DeploymentId = [string](Get-Prop $D 'AdvertisementID'); AssignmentId = ''; ObjectId = [string](Get-Prop $D 'PackageID'); ModelId = ''; ObjectName = ''
                Program = $(if ($Type -eq 'Package') { [string](Get-Prop $D 'ProgramName') } else { '' }); CollectionId = $collection; CollectionName = [string]$Names[$collection]
                Purpose = $(if ($required) { 'Required' } else { 'Available' }); Available = (Get-TimeText (Get-Prop $D 'PresentTime')); Deadline = $deadline
                Utc = [bool](Get-Prop $D 'PresentTimeIsGMT'); Created = ''; CreatedBy = ''; Notification = ''
                Override = (Test-Bit $advert 0x100000); Reboot = (Test-Bit $advert 0x200000); Metered = (Test-Bit $remote 0x40000); FallbackToDp = $false; AllowMicrosoftUpdate = $false
                SuppressRestart = $false; PostRebootScan = $false
                Network = $(if ($Type -eq 'Package') { $fast } else { Get-TaskSequenceOption $remote }); Slow = $(if ($Type -eq 'Package') { $slow } else { '' })
                Rerun = (Get-RerunName $remote); ShowProgress = (Test-Bit $advert 0x800000); AllowFallback = -not (Test-Bit $advert 0x20000)
                Availability = $(if ($Type -eq 'TaskSequence') { Get-AvailabilityName $advert } else { '' })
                ExpirationEnabled = [bool](Get-Prop $D 'ExpirationTimeEnabled'); ScheduleEnabled = [bool](Get-Prop $D 'AssignedScheduleEnabled')
                ScheduleUtc = $(if ($null -ne $schedule) { [bool](Get-Prop $schedule 'IsGMT') } else { $false })
            }
        }
    }
}

function Get-CollectionNames {
    param([string[]]$Ids)
    $names = @{}
    $valid = @($Ids | Where-Object { $_ -match '^[A-Za-z0-9]{8}$' } | Sort-Object -Unique)
    for ($i = 0; $i -lt $valid.Count; $i += 50) {
        $chunk = $valid[$i..([Math]::Min($i + 49, $valid.Count - 1))]
        $where = ($chunk | ForEach-Object { "CollectionID = '$_'" }) -join ' OR '
        foreach ($c in Invoke-Query ('SELECT CollectionID, Name FROM SMS_Collection WHERE ' + $where)) { $names[[string]$c.CollectionID] = [string](Get-Prop $c 'Name') }
    }
    return $names
}

# Raw deployment objects of the object, of one collection when given.
function Get-RawDeployments {
    param([object]$Object, [string]$Collection = '')
    $scope = ''
    switch ($Type) {
        'Application' {
            if ($Collection) { $scope = " AND TargetCollectionID = '$Collection'" }
            return @(Invoke-Query ('SELECT * FROM SMS_ApplicationAssignment WHERE AppModelID = ' + [string]$Object.ModelId + $scope))
        }
        'SoftwareUpdateGroup' {
            if ($Collection) { $scope = " AND TargetCollectionID = '$Collection'" }
            return @(Invoke-Query ('SELECT * FROM SMS_UpdateGroupAssignment WHERE AssignedUpdateGroup = ' + [string]$Object.Id + $scope))
        }
        default {
            if ($Collection) { $scope = " AND CollectionID = '$Collection'" }
            return @(Invoke-FullQuery ("SELECT * FROM SMS_Advertisement WHERE PackageID = '{0}'{1}" -f [string]$Object.Id, $scope))
        }
    }
}

function Get-RawDeploymentById {
    param([string]$Id)
    switch ($Type) {
        'Application' { if ($Id -match '^\{[0-9A-Fa-f-]{36}\}$') { return (Invoke-Query ("SELECT * FROM SMS_ApplicationAssignment WHERE AssignmentUniqueID = '{0}'" -f $Id.ToUpperInvariant()) | Select-Object -First 1) } }
        'SoftwareUpdateGroup' { if ($Id -match '^\{[0-9A-Fa-f-]{36}\}$') { return (Invoke-Query ("SELECT * FROM SMS_UpdateGroupAssignment WHERE AssignmentUniqueID = '{0}'" -f $Id.ToUpperInvariant()) | Select-Object -First 1) } }
        default { if ($Id -match '^[A-Za-z0-9]{8}$') { return (Invoke-FullQuery ("SELECT * FROM SMS_Advertisement WHERE AdvertisementID = '{0}'" -f $Id.ToUpperInvariant()) | Select-Object -First 1) } }
    }
    return $null
}

function Get-DeploymentKey {
    param([object]$D)
    if ($Type -in 'Application', 'SoftwareUpdateGroup') { return [string](Get-Prop $D 'AssignmentUniqueID') }
    return [string](Get-Prop $D 'AdvertisementID')
}

# The deployments that count as duplicates: of the object to the collection, for a package of
# the same program.
function Get-DuplicateKeys {
    param([object]$Object, [object[]]$Raw)
    @($Raw | Where-Object { $Type -ne 'Package' -or [string](Get-Prop $_ 'ProgramName') -eq [string]$Object.Program } | ForEach-Object { Get-DeploymentKey $_ })
}

# ---- Ownership proof -------------------------------------------------------------------------

# When and by whom a deployment was made. An assignment carries CreationTime and LastModifiedBy.
# SMS_Advertisement carries neither, so the audit status message 30006 the provider raises for a
# new advertisement stands in (attribute 401 AdvertisementID, 403 the account). The status system
# can insert that message many seconds after the create returns, so it is read up to
# $ProofReads times, 2 s apart; a missing message leaves the creation time empty, which proves
# nothing. The site deletes old status messages, so for an older advertisement the proof is empty.
function Get-CreationProof {
    param([string]$Id, [object]$Raw)
    # Problem: '' when both values were read; 'message-missing' (no 30006 message for the
    # advertisement), 'time-missing' or 'account-missing' (the message or deployment lacks it).
    $proof = [pscustomobject]@{ Kind = 'Proof'; DeploymentId = $Id; Created = ''; CreatedBy = ''; Account = $Account; Owned = $false; Problem = '' }
    if ($Type -in 'Application', 'SoftwareUpdateGroup') {
        $proof.Created = Get-UtcText (Get-Prop $Raw 'CreationTime')
        $proof.CreatedBy = [string](Get-Prop $Raw 'LastModifiedBy')
    } elseif ($Id -match '^[A-Za-z0-9]{8}$') {
        $proof.Problem = 'message-missing'
        for ($attempt = 0; $attempt -lt $ProofReads -and $proof.Problem -eq 'message-missing'; $attempt++) {
            if ($attempt -gt 0) { Start-Sleep -Seconds 2 }
            foreach ($a in @(Invoke-Query ("SELECT RecordID, AttributeTime FROM SMS_StatMsgAttributes WHERE AttributeID = 401 AND AttributeValue = '{0}'" -f $Id.ToUpperInvariant()))) {
                $record = [string](Get-Prop $a 'RecordID')
                if ($record -notmatch '^[0-9]{1,20}$') { continue }
                $message = Invoke-Query ("SELECT RecordID, MessageID, Time FROM SMS_StatusMessage WHERE RecordID = {0} AND MessageID = 30006" -f $record) | Select-Object -First 1
                if ($null -eq $message) { continue }
                $by = Invoke-Query ("SELECT RecordID, AttributeValue FROM SMS_StatMsgAttributes WHERE RecordID = {0} AND AttributeID = 403" -f $record) | Select-Object -First 1
                $proof.Created = Get-UtcTextOfUtc (Get-Prop $message 'Time')
                $proof.CreatedBy = [string](Get-Prop $by 'AttributeValue')
                $proof.Problem = ''
                break
            }
        }
    }
    if (-not $proof.Problem) {
        if (-not $proof.Created) { $proof.Problem = 'time-missing' }
        elseif (-not $proof.CreatedBy) { $proof.Problem = 'account-missing' }
    }
    $proof.Owned = Test-Owned $proof
    return $proof
}

# A deployment is this run's when it was made by the account of this session no earlier than two
# minutes before the run started (clock skew between this computer and the site server).
function Test-Owned {
    param([object]$Proof)
    if (-not $RunStart -or -not $Proof.Created -or -not $Proof.CreatedBy -or $Proof.CreatedBy -ne $Proof.Account) { return $false }
    $start = [datetime]::ParseExact($RunStart, $TimeFormat, $Invariant)
    # Parsed without the Z: ParseExact reads a Z as UTC and converts the value to local time.
    $created = [datetime]::ParseExact($Proof.Created.TrimEnd('Z'), $TimeFormat, $Invariant)
    return $created -ge $start.AddMinutes(-2)
}

# Every revision of an application model. AssignedCIs of an application deployment follows the
# latest revision of the model and keeps no trace of the revision deployed (verified on CM01: an
# assignment made on revision 2 holds the CI_ID of revision 3 with its LastModificationTime
# unchanged), so the revision a deployment was made with is the latest one whose DateLastModified
# is not after the CreationTime of the deployment. DateCreated holds the creation time of the
# model and is the same on every revision, so it cannot date a revision (verified on CM01).
function Get-Revisions {
    param([string]$Model)
    if ($Type -ne 'Application' -or $Model -notmatch '^[0-9]{1,10}$') { return @() }
    @(Invoke-Query ('SELECT CI_ID, ModelID, CIVersion, DateLastModified, LocalizedDisplayName FROM SMS_Application WHERE ModelID = ' + $Model) |
        ForEach-Object { [pscustomobject]@{ Kind = 'Revision'; CiId = [string]$_.CI_ID; Version = [int](Get-Prop $_ 'CIVersion'); Modified = (Get-UtcText (Get-Prop $_ 'DateLastModified')); Name = [string](Get-Prop $_ 'LocalizedDisplayName') } })
}

# The name ConfigMgr holds now for the object of a deployment.
function Get-SiteObjectName {
    param([object]$Row, [object[]]$Revisions)
    switch ($Type) {
        'Application' {
            $current = @($Revisions | Where-Object { $_.CiId -eq [string]$Row.ObjectId }) | Select-Object -First 1
            if ($null -ne $current) { return [string]$current.Name }
            return [string]$Row.ObjectName
        }
        'Package' {
            if ($Row.ObjectId -notmatch '^[A-Za-z0-9]{8}$') { return '' }
            return [string](Get-Prop (Invoke-Query ("SELECT PackageID, Name, Version, SourceVersion FROM SMS_Package WHERE PackageID = '{0}'" -f $Row.ObjectId) | Select-Object -First 1) 'Name')
        }
        'TaskSequence' {
            if ($Row.ObjectId -notmatch '^[A-Za-z0-9]{8}$') { return '' }
            return [string](Get-Prop (Invoke-Query ("SELECT PackageID, Name, Description, SourceVersion FROM SMS_TaskSequencePackage WHERE PackageID = '{0}'" -f $Row.ObjectId) | Select-Object -First 1) 'Name')
        }
        'SoftwareUpdateGroup' {
            if ($Row.ObjectId -notmatch '^[0-9]{1,10}$') { return '' }
            return [string](Get-Prop (Invoke-Query ('SELECT CI_ID, LocalizedDisplayName, NumberOfUpdates, CIVersion FROM SMS_AuthorizationList WHERE CI_ID = ' + $Row.ObjectId) | Select-Object -First 1) 'LocalizedDisplayName')
        }
    }
    return ''
}

# ---- Compare ---------------------------------------------------------------------------------

# The settings ConfigMgr stores that differ from the requested ones. The cmdlets ignore the
# rerun behavior, the maintenance-window settings, the metered setting and the restart
# suppression of an Available deployment, so those are compared for Required only.
function Compare-Stored {
    param([object]$Row, [object]$Object)
    $diff = New-Object System.Collections.Generic.List[string]
    $required = $Purpose -eq 'Required'
    if ($Row.CollectionId -ne $CollectionId) { $diff.Add('Collection') }
    switch ($Type) {
        'Application' { if ($Row.ModelId -ne [string]$Object.ModelId) { $diff.Add('Object') } }
        'Package' { if ($Row.ObjectId -ne [string]$Object.Id -or $Row.Program -ne [string]$Object.Program) { $diff.Add('Object') } }
        default { if ($Row.ObjectId -ne [string]$Object.Id) { $diff.Add('Object') } }
    }
    if ($Row.Purpose -ne $Purpose) { $diff.Add('Purpose') }
    if ($Row.Available -ne $Available) { $diff.Add('AvailableTime') }
    if ($required -and $Row.Deadline -ne $Deadline) { $diff.Add('Deadline') }
    if ($Row.Utc -ne $Utc) { $diff.Add('TimeBasis') }
    if ($Type -in 'Application', 'SoftwareUpdateGroup' -and $Row.Notification -ne $Notification) { $diff.Add('UserNotification') }
    if ($required) {
        if ($Row.Override -ne $Override) { $diff.Add('OverrideServiceWindow') }
        if ($Row.Reboot -ne $Reboot) { $diff.Add('RebootOutsideServiceWindow') }
        if ($Row.Metered -ne $Metered) { $diff.Add('AllowMetered') }
    }
    switch ($Type) {
        'Package' {
            if ($Row.Network -ne $Network) { $diff.Add('NetworkBehavior') }
            if ($Row.Slow -ne $Slow) { $diff.Add('SlowNetworkBehavior') }
            if ($Row.AllowFallback -ne $AllowFallback) { $diff.Add('AllowFallback') }
            if ($required -and $Row.Rerun -ne $Rerun) { $diff.Add('RerunBehavior') }
            if ($Row.ExpirationEnabled) { $diff.Add('Expiration') }
            if ($required -and (-not $Row.ScheduleEnabled -or $Row.ScheduleUtc -ne $Utc)) { $diff.Add('Deadline') }
        }
        'TaskSequence' {
            if ($Row.Network -ne $Network) { $diff.Add('NetworkBehavior') }
            if ($Row.Availability -ne $Availability) { $diff.Add('TaskSequenceAvailability') }
            if ($Row.ShowProgress -ne $ShowProgress) { $diff.Add('ShowProgress') }
            if ($Row.AllowFallback -ne $AllowFallback) { $diff.Add('AllowFallback') }
            if ($required -and $Row.Rerun -ne $Rerun) { $diff.Add('RerunBehavior') }
            if ($Row.ExpirationEnabled) { $diff.Add('Expiration') }
            if ($required -and (-not $Row.ScheduleEnabled -or $Row.ScheduleUtc -ne $Utc)) { $diff.Add('Deadline') }
        }
        'SoftwareUpdateGroup' {
            if ($Row.PostRebootScan -ne $PostRebootScan) { $diff.Add('PostRebootScan') }
            if ($required) {
                if ($Row.SuppressRestart -ne $SuppressRestart) { $diff.Add('SuppressRestart') }
                if ($Row.FallbackToDp -ne $FallbackToDp) { $diff.Add('FallbackToDp') }
                if ($Row.AllowMicrosoftUpdate -ne $AllowMicrosoftUpdate) { $diff.Add('AllowMicrosoftUpdate') }
            }
        }
    }
    return @($diff | Select-Object -Unique)
}

# ---- Create ----------------------------------------------------------------------------------

function ConvertFrom-TimeText {
    param([string]$Text, [System.DateTimeKind]$Kind)
    [datetime]::SpecifyKind([datetime]::ParseExact($Text, $TimeFormat, $Invariant), $Kind)
}

# New-CMApplicationDeployment and New-CMSoftwareUpdateDeployment convert a deadline of local or
# unspecified kind to UTC when the time basis is UTC, so both times go with the kind of the basis.
# The package and task sequence cmdlets store the available time as given and take the
# deadline as a schedule built with -IsUtc.
function New-Deployment {
    param([object]$Object, [object]$Collection)
    $kind = if ($Utc -and $Type -in 'Application', 'SoftwareUpdateGroup') { [System.DateTimeKind]::Utc } else { [System.DateTimeKind]::Local }
    $availableTime = ConvertFrom-TimeText $Available $kind
    $required = $Purpose -eq 'Required'
    switch ($Type) {
        'Application' {
            $p = @{ Id = [int]$Object.Id; CollectionId = $Collection.CollectionId; DeployAction = 'Install'; DeployPurpose = $Purpose; AvailableDateTime = $availableTime
                TimeBaseOn = $(if ($Utc) { 'Utc' } else { 'LocalTime' }); UserNotification = $Notification }
            if ($required) {
                $p.DeadlineDateTime = ConvertFrom-TimeText $Deadline $kind
                $p.OverrideServiceWindow = $Override; $p.RebootOutsideServiceWindow = $Reboot; $p.UseMeteredNetwork = $Metered
            }
            return (@(New-CMApplicationDeployment @p 3>$null) | Select-Object -Last 1)
        }
        'Package' {
            $p = @{ StandardProgram = $true; PackageId = [string]$Object.Id; ProgramName = [string]$Object.Program; CollectionId = $Collection.CollectionId; DeployPurpose = $Purpose
                AvailableDateTime = $availableTime; FastNetworkOption = $Network; SlowNetworkOption = $Slow; AllowFallback = $AllowFallback }
            if ($Utc) { $p.UseUtcForAvailableSchedule = $true }
            if ($required) {
                $schedule = if ($Utc) { New-CMSchedule -Start (ConvertFrom-TimeText $Deadline Local) -Nonrecurring -IsUtc } else { New-CMSchedule -Start (ConvertFrom-TimeText $Deadline Local) -Nonrecurring }
                $p.Schedule = @($schedule); $p.RerunBehavior = $Rerun; $p.SoftwareInstallation = $Override; $p.SystemRestart = $Reboot; $p.UseMeteredNetwork = $Metered
            }
            return (@(New-CMPackageDeployment @p 3>$null) | Select-Object -Last 1)
        }
        'TaskSequence' {
            $p = @{ TaskSequencePackageId = [string]$Object.Id; CollectionId = $Collection.CollectionId; DeployPurpose = $Purpose; AvailableDateTime = $availableTime
                Availability = $Availability; ShowTaskSequenceProgress = $ShowProgress; AllowFallback = $AllowFallback; DeploymentOption = $Network }
            if ($Utc) { $p.UseUtcForAvailableSchedule = $true }
            if ($required) {
                $schedule = if ($Utc) { New-CMSchedule -Start (ConvertFrom-TimeText $Deadline Local) -Nonrecurring -IsUtc } else { New-CMSchedule -Start (ConvertFrom-TimeText $Deadline Local) -Nonrecurring }
                $p.Schedule = @($schedule); $p.RerunBehavior = $Rerun; $p.SoftwareInstallation = $Override; $p.SystemRestart = $Reboot; $p.UseMeteredNetwork = $Metered
            }
            return (@(New-CMTaskSequenceDeployment @p 3>$null) | Select-Object -Last 1)
        }
        'SoftwareUpdateGroup' {
            $p = @{ SoftwareUpdateGroupId = [string]$Object.Id; CollectionId = $Collection.CollectionId; DeploymentType = $Purpose; AvailableDateTime = $availableTime
                TimeBasedOn = $(if ($Utc) { 'Utc' } else { 'LocalTime' }); UserNotification = $Notification; RequirePostRebootFullScan = $PostRebootScan; AcceptEula = $true }
            if ($required) {
                $p.DeadlineDateTime = ConvertFrom-TimeText $Deadline $kind
                $p.SoftwareInstallation = $Override; $p.AllowRestart = $Reboot; $p.UseMeteredNetwork = $Metered
                $p.RestartServer = $SuppressRestart; $p.RestartWorkstation = $SuppressRestart
                $p.ProtectedType = 'RemoteDistributionPoint'
                $p.UnprotectedType = $(if ($FallbackToDp) { 'UnprotectedDistributionPoint' } else { 'NoInstall' })
                $p.DownloadFromMicrosoftUpdate = $AllowMicrosoftUpdate
            }
            return (@(New-CMSoftwareUpdateDeployment @p 3>$null) | Select-Object -Last 1)
        }
    }
}

# The key of the deployment object a create cmdlet returned; empty when it returned nothing
# usable.
function Get-ReturnedKey {
    param([object]$Returned)
    if ($null -eq $Returned) { return '' }
    $name = if ($Type -in 'Application', 'SoftwareUpdateGroup') { 'AssignmentUniqueID' } else { 'AdvertisementID' }
    $key = try { [string]$Returned.$name } catch { '' }
    if ($Type -in 'Application', 'SoftwareUpdateGroup') { if ($key -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { return '' } }
    elseif ($key -notmatch '^[A-Za-z0-9]{8}$') { return '' }
    return $key
}

# Of several new deployments, the one this account made during the create call: its creation
# time lies within one second of the call (whole seconds; the clocks of this computer and the
# site server must agree). Every proof read is kept in $ProofRows; none or more than one match
# leaves the result unknown.
$ProofRows = New-Object System.Collections.Generic.List[object]
function Find-OwnByCreation {
    param([string[]]$Keys, [datetime]$CallStart, [datetime]$CallEnd)
    $from = (New-Object DateTime ($CallStart.Ticks - $CallStart.Ticks % [TimeSpan]::TicksPerSecond)).AddSeconds(-1)
    $to = $CallEnd.AddSeconds(1)
    $mine = @()
    foreach ($key in $Keys) {
        $raw = Get-RawDeploymentById $key
        if ($null -eq $raw) { continue }
        $proof = Get-CreationProof $key $raw
        $ProofRows.Add($proof)
        if (-not $proof.Created -or $proof.CreatedBy -ne $Account) { continue }
        $created = [datetime]::ParseExact($proof.Created.TrimEnd('Z'), $TimeFormat, $Invariant)
        if ($created -ge $from -and $created -le $to) { $mine += $key }
    }
    if ($mine.Count -eq 1) { return $mine[0] }
    return ''
}

function Test-CreateTarget {
    param([object]$Object, [object]$Collection)
    if ($null -eq $Object) { return 'object-missing' }
    if ($Type -eq 'Package' -and -not $Object.Program) { return 'program-missing' }
    if ($Object.Retired) { return 'object-retired' }
    if ($null -eq $Collection) { return 'collection-missing' }
    if ($Collection.CollectionType -ne 2) { return 'not-device' }
    if ($Collection.CollectionId -like 'SMS*') { return 'built-in' }
    return ''
}

function Invoke-Create {
    if (-not (Test-ObjectIdForm) -or $CollectionId -notmatch '^[A-Za-z0-9]{8}$' -or -not $Purpose -or -not $Available -or ($Purpose -eq 'Required' -and -not $Deadline)) { Write-Result 'invalid'; return }
    try {
        $object = Get-ObjectRow
        $collection = Get-CollectionRow $CollectionId
        $problem = Test-CreateTarget $object $collection
        if ($problem) { Write-Result $problem; return }
        if ($RequireContent -and $Type -ne 'SoftwareUpdateGroup') {
            $content = @(Get-ContentRows $object)
            if (($Type -ne 'TaskSequence' -and $content.Count -eq 0) -or @($content | Where-Object { -not $_.Distributed }).Count -gt 0) { Write-Result 'content-missing'; return }
        }
        $before = @(Get-RawDeployments $object $collection.CollectionId)
        $duplicates = @(Get-DuplicateKeys $object $before)
        if ($duplicates.Count -gt 0) { Write-Result 'duplicate' '' '' @() $duplicates; return }
        $beforeKeys = @($before | ForEach-Object { Get-DeploymentKey $_ })
    } catch {
        Write-Result 'check-failed' $_.Exception.Message
        return
    }
    $thrown = ''
    $returned = ''
    $callStart = [DateTime]::UtcNow
    try {
        $returned = Get-ReturnedKey (New-Deployment $object $collection)
        $steps.Add('created')
    } catch {
        $thrown = $_.Exception.Message
        $steps.Add('create-error')
    }
    $callEnd = [DateTime]::UtcNow
    try {
        $after = @(Get-RawDeployments $object $collection.CollectionId)
    } catch {
        Write-Result 'create-unclear' ($thrown + ' ' + $_.Exception.Message)
        return
    }
    $made = @($after | Where-Object { $beforeKeys -notcontains (Get-DeploymentKey $_) })
    if ($made.Count -eq 0) { Write-Result 'create-failed' $thrown; return }
    $madeKeys = @($made | ForEach-Object { Get-DeploymentKey $_ })
    # The create cmdlets of all four types return the object they made (verified on CM01), so
    # its key names this run's deployment among others that appeared at the same time.
    $ours = ''
    if ($returned -and $madeKeys -contains $returned) { $ours = $returned }
    elseif ($made.Count -gt 1) {
        $ours = Find-OwnByCreation $madeKeys $callStart $callEnd
        $ProofRows
        if (-not $ours) { Write-Result 'create-unclear' $thrown '' @() $madeKeys; return }
    }
    else { $ours = $madeKeys[0] }
    $stored = Get-RawDeploymentById $ours
    # After an error the one new deployment can be another session's; only a proven creation
    # time and account make it this run's.
    if ($thrown -and $made.Count -eq 1) {
        if ($null -eq $stored) { Write-Result 'create-unclear' $thrown '' @() $madeKeys; return }
        $proof = Get-CreationProof $ours $stored
        $proof
        if (-not $proof.Owned) { Write-Result 'create-unclear' $thrown '' @() $madeKeys; return }
    }
    $madeKeys = @($ours)
    if ($null -eq $stored) { Write-Result 'readback-failed' $thrown $madeKeys[0]; return }
    $names = @{ ([string]$collection.CollectionId) = [string]$collection.Name }
    $row = ConvertTo-DeploymentRow $stored $names
    $row.ObjectName = [string]$object.Name
    $row
    $differences = @(Compare-Stored $row $object)
    # ConfigMgr allows a second package, task sequence or update group deployment to one
    # collection; one made by someone else after the check above is reported, not removed.
    $others = @()
    try { $others = @(Get-DuplicateKeys $object @(Get-RawDeployments $object $collection.CollectionId) | Where-Object { $_ -ne $madeKeys[0] }) } catch { $steps.Add('relist-failed') }
    $status = if ($differences.Count -gt 0) { 'stored-differs' } elseif ($others.Count -gt 0) { 'concurrent' } elseif ($thrown) { 'created-with-error' } else { 'ok' }
    Write-Result $status $thrown $madeKeys[0] $differences $others
}

# ---- Distribute ------------------------------------------------------------------------------

function Invoke-Distribute {
    if (-not (Test-ObjectIdForm) -or $Type -eq 'SoftwareUpdateGroup') { Write-Result 'invalid'; return }
    $object = Get-ObjectRow
    if ($null -eq $object) { Write-Result 'object-missing'; return }
    $groups = @(Invoke-Query 'SELECT GroupID, Name FROM SMS_DistributionPointGroup')
    $contentIds = @(Get-ContentRows $object | ForEach-Object { $_.PackageId } | Where-Object { $_ -match '^[A-Za-z0-9]{8}$' } | Sort-Object -Unique)
    foreach ($id in $GroupIds) {
        if ($id -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { [pscustomobject]@{ Kind = 'Group'; GroupId = $id; Status = 'invalid' }; continue }
        $group = $groups | Where-Object { ([string]$_.GroupID) -eq $id } | Select-Object -First 1
        if ($null -eq $group) { [pscustomobject]@{ Kind = 'Group'; GroupId = $id; Status = 'group-missing' }; continue }
        $name = [string]$group.Name
        if ($name -match '[\*\?\[\]]') { [pscustomobject]@{ Kind = 'Group'; GroupId = $id; Status = 'group-name-wildcard' }; continue }
        $status = 'requested'; $detail = ''
        try {
            switch ($Type) {
                'Application' { Start-CMContentDistribution -ApplicationId ([string]$object.Id) -DistributionPointGroupName $name -ErrorAction Stop }
                'Package' { Start-CMContentDistribution -PackageId ([string]$object.Id) -DistributionPointGroupName $name -ErrorAction Stop }
                'TaskSequence' { Start-CMContentDistribution -TaskSequenceId ([string]$object.Id) -DistributionPointGroupName $name -ErrorAction Stop }
            }
        } catch {
            $detail = $_.Exception.Message
            $status = if ($detail -match 'already been (targeted|distributed)') { 'already' } else { 'failed' }
        }
        $missing = @()
        if ($contentIds.Count -gt 0) {
            $where = ($contentIds | ForEach-Object { "PackageID = '$_'" }) -join ' OR '
            $held = @(Invoke-Query ("SELECT GroupID, PackageID FROM SMS_DPGroupContentInfo WHERE GroupID = '{0}' AND ({1})" -f $id, $where) | ForEach-Object { [string](Get-Prop $_ 'PackageID') })
            $missing = @($contentIds | Where-Object { $held -notcontains $_ })
        }
        if ($status -eq 'failed' -and $contentIds.Count -gt 0 -and $missing.Count -eq 0) { $status = 'already' }
        if ($status -eq 'requested' -and $missing.Count -gt 0) { $status = 'not-targeted' }
        [pscustomobject]@{ Kind = 'Group'; GroupId = $id; Status = $status; Detail = $detail; Missing = @($missing) }
    }
    Write-Result 'done'
}

# ---- Remove ----------------------------------------------------------------------------------

function Invoke-Remove {
    if (-not $DeploymentId -or -not (Test-ObjectIdForm)) { Write-Result 'invalid'; return }
    $raw = Get-RawDeploymentById $DeploymentId
    if ($null -eq $raw) { Write-Result 'missing'; return }
    $object = [pscustomobject]@{ Id = $ObjectId; ModelId = $ModelId; Program = $Program }
    $names = Get-CollectionNames @([string](Get-Prop $raw 'TargetCollectionID'), [string](Get-Prop $raw 'CollectionID'))
    $row = ConvertTo-DeploymentRow $raw $names
    $diff = New-Object System.Collections.Generic.List[string]
    if ($row.CollectionId -ne $CollectionId) { $diff.Add('Collection') }
    if ($Type -eq 'Application') { if ($ModelId -and $row.ModelId -ne $ModelId) { $diff.Add('Object') } elseif (-not $ModelId -and $row.ObjectId -ne $ObjectId) { $diff.Add('Object') } }
    elseif ($row.ObjectId -ne $ObjectId) { $diff.Add('Object') }
    # AssignedCIs follows the latest revision, so the requested CI_ID must be a revision of the
    # deployment's model that existed when the deployment was created.
    if ($Type -eq 'Application') {
        $named = @(Get-Revisions ([string]$row.ModelId) | Where-Object { $_.CiId -eq $ObjectId }) | Select-Object -First 1
        if ($null -eq $named -or -not $named.Modified -or -not $row.Created -or [string]::CompareOrdinal([string]$named.Modified, [string]$row.Created) -gt 0) { $diff.Add('Object') }
    }
    if ($Type -eq 'Package' -and $row.Program -ne $Program) { $diff.Add('Object') }
    if ($row.Purpose -ne $Purpose) { $diff.Add('Purpose') }
    if ($row.Available -ne $Available) { $diff.Add('AvailableTime') }
    if ($row.Deadline -ne $Deadline) { $diff.Add('Deadline') }
    if ($row.Utc -ne $Utc) { $diff.Add('TimeBasis') }
    if ($diff.Count -gt 0) { Write-Result 'changed' '' '' @($diff | Select-Object -Unique); return }
    $others = @(Get-RawDeployments ([pscustomobject]@{ Id = $row.ObjectId; ModelId = $row.ModelId }) $row.CollectionId | ForEach-Object { Get-DeploymentKey $_ } | Where-Object { $_ -ne $DeploymentId })
    $detail = ''
    try {
        switch ($Type) {
            'Application' { Remove-CMApplicationDeployment -DeploymentId $DeploymentId -Force -ErrorAction Stop }
            'Package' { Remove-CMPackageDeployment -DeploymentId $DeploymentId -Force -ErrorAction Stop }
            'TaskSequence' { Remove-CMTaskSequenceDeployment -DeploymentId $DeploymentId -Force -ErrorAction Stop }
            'SoftwareUpdateGroup' { Remove-CMSoftwareUpdateDeployment -DeploymentId $DeploymentId -Force -ErrorAction Stop }
        }
        $steps.Add('removed')
    } catch {
        $detail = $_.Exception.Message
        $steps.Add('remove-error')
    }
    if ($null -ne (Get-RawDeploymentById $DeploymentId)) { Write-Result 'remove-failed' $detail; return }
    $left = @(Get-RawDeployments ([pscustomobject]@{ Id = $row.ObjectId; ModelId = $row.ModelId }) $row.CollectionId | ForEach-Object { Get-DeploymentKey $_ })
    $lost = @($others | Where-Object { $left -notcontains $_ })
    if ($lost.Count -gt 0) { Write-Result 'removed-others' $detail '' @() $lost; return }
    Write-Result $(if ($detail) { 'removed-with-error' } else { 'ok' }) $detail
}

# ---- Reads -----------------------------------------------------------------------------------

# At most MaxRows rows, and a Truncated row with the total when ConfigMgr holds more.
function Write-Limited {
    param([object[]]$Rows)
    $Rows | Select-Object -First $MaxRows
    if ($Rows.Count -gt $MaxRows) { [pscustomobject]@{ Kind = 'Truncated'; Shown = $MaxRows; Total = $Rows.Count } }
}

function Get-Objects {
    $rows = switch ($Type) {
        'Application' {
            Invoke-Query 'SELECT CI_ID, ModelID, ModelName, LocalizedDisplayName, SoftwareVersion, IsExpired FROM SMS_Application WHERE IsLatest = 1' | Where-Object { -not [bool](Get-Prop $_ 'IsExpired') } |
                ForEach-Object { [pscustomobject]@{ Kind = 'Object'; Id = [string]$_.CI_ID; Name = [string](Get-Prop $_ 'LocalizedDisplayName'); Version = [string](Get-Prop $_ 'SoftwareVersion'); ModelId = [string](Get-Prop $_ 'ModelID'); ModelName = [string](Get-Prop $_ 'ModelName'); UpdateCount = 0 } }
        }
        'Package' {
            Invoke-Query 'SELECT PackageID, Name, Version FROM SMS_Package WHERE PackageType = 0' |
                ForEach-Object { [pscustomobject]@{ Kind = 'Object'; Id = [string]$_.PackageID; Name = [string](Get-Prop $_ 'Name'); Version = [string](Get-Prop $_ 'Version'); ModelId = ''; UpdateCount = 0 } }
        }
        'TaskSequence' {
            Invoke-Query 'SELECT PackageID, Name FROM SMS_TaskSequencePackage' |
                ForEach-Object { [pscustomobject]@{ Kind = 'Object'; Id = [string]$_.PackageID; Name = [string](Get-Prop $_ 'Name'); Version = ''; ModelId = ''; UpdateCount = 0 } }
        }
        'SoftwareUpdateGroup' {
            Invoke-Query 'SELECT CI_ID, LocalizedDisplayName, NumberOfUpdates FROM SMS_AuthorizationList' |
                ForEach-Object { [pscustomobject]@{ Kind = 'Object'; Id = [string]$_.CI_ID; Name = [string](Get-Prop $_ 'LocalizedDisplayName'); Version = ''; ModelId = ''; UpdateCount = [int](Get-Prop $_ 'NumberOfUpdates') } }
        }
    }
    Write-Limited @($rows | Where-Object { $null -ne $_ })
}

function Get-DpGroups {
    if (-not (Test-ObjectIdForm) -or $Type -eq 'SoftwareUpdateGroup') { return }
    $object = Get-ObjectRow
    if ($null -eq $object) { Write-Result 'object-missing'; return }
    $contentIds = @(Get-ContentRows $object | ForEach-Object { $_.PackageId } | Where-Object { $_ -match '^[A-Za-z0-9]{8}$' } | Sort-Object -Unique)
    $groups = @(Invoke-Query 'SELECT GroupID, Name, MemberCount FROM SMS_DistributionPointGroup')
    $held = @(); $details = @()
    if ($contentIds.Count -gt 0) {
        $where = ($contentIds | ForEach-Object { "PackageID = '$_'" }) -join ' OR '
        $held = @(Invoke-Query ('SELECT GroupID, PackageID FROM SMS_DPGroupContentInfo WHERE ' + $where))
        $details = @(Invoke-Query ('SELECT GroupID, PackageID, DPName, MessageState FROM SMS_DPGroupDistributionStatusDetails WHERE ' + $where))
    }
    foreach ($g in $groups) {
        $gid = [string]$g.GroupID
        $members = [int](Get-Prop $g 'MemberCount')
        $inGroup = @($held | Where-Object { [string](Get-Prop $_ 'GroupID') -eq $gid } | ForEach-Object { [string](Get-Prop $_ 'PackageID') } | Sort-Object -Unique)
        $states = @($details | Where-Object { [string](Get-Prop $_ 'GroupID') -eq $gid })
        $dps = @($states | ForEach-Object { [string](Get-Prop $_ 'DPName') } | Sort-Object -Unique)
        $success = 0; $progress = 0; $errors = 0
        foreach ($dp in $dps) {
            $mine = @($states | Where-Object { [string](Get-Prop $_ 'DPName') -eq $dp })
            $codes = @($mine | ForEach-Object { [int](Get-Prop $_ 'MessageState') })
            if ($codes -contains 4 -or $codes -contains 3) { $errors++ }
            elseif ($codes -contains 2 -or @($mine | ForEach-Object { [string](Get-Prop $_ 'PackageID') } | Sort-Object -Unique).Count -lt $contentIds.Count) { $progress++ }
            else { $success++ }
        }
        $status = if ($contentIds.Count -eq 0 -or $inGroup.Count -eq 0) { 'NotTargeted' }
            elseif ($errors -gt 0) { 'Failed' }
            elseif ($inGroup.Count -lt $contentIds.Count -or $progress -gt 0 -or $success -lt $members) { 'InProgress' }
            elseif ($success -ge $members -and $members -gt 0) { 'Distributed' }
            else { 'Unknown' }
        [pscustomobject]@{ Kind = 'DpGroup'; GroupId = $gid; Name = [string](Get-Prop $g 'Name'); Status = $status; Targeted = $members; Success = $success; InProgress = $progress; Error = $errors }
    }
}

function Get-State {
    if (-not (Test-ObjectIdForm)) { Write-Result 'invalid'; return }
    $object = Get-ObjectRow
    if ($null -ne $object) {
        $object
        # The signing rule of a Required application deployment matches the application by name,
        # so the number of latest applications with that name is part of the state. The scan reads every
        # application of the site; a ring run's Prepare that read it for an earlier ring passes SkipNameCount.
        if ($Type -eq 'Application' -and $Purpose -eq 'Required' -and -not $SkipNameCount) {
            try {
                $same = @(Invoke-Query 'SELECT CI_ID, LocalizedDisplayName FROM SMS_Application WHERE IsLatest = 1' | Where-Object { [string](Get-Prop $_ 'LocalizedDisplayName') -eq [string]$object.Name })
                [pscustomobject]@{ Kind = 'NameCount'; Count = $same.Count }
            } catch { }
        }
        # A rename or any other edit makes a new revision with a new CI_ID; the model keeps its
        # ModelID and ModelName, and the earlier revisions keep their CI_IDs (verified on CM01).
        if ($Type -eq 'Application' -and [string]$object.ModelId -match '^[0-9]{1,10}$') {
            try {
                $revisions = @(Invoke-Query ('SELECT CI_ID, ModelID FROM SMS_Application WHERE ModelID = ' + [string]$object.ModelId) | ForEach-Object { [string]$_.CI_ID })
                [pscustomobject]@{ Kind = 'Revisions'; CiIds = @($revisions) }
            } catch { }
        }
        if ($Type -ne 'SoftwareUpdateGroup') {
            try { Get-ContentRows $object; [pscustomobject]@{ Kind = 'ContentRead'; Read = $true } } catch { [pscustomobject]@{ Kind = 'ContentRead'; Read = $false; Detail = $_.Exception.Message } }
        }
    }
    $collection = $null
    if ($CollectionId) { $collection = Get-CollectionRow $CollectionId; if ($null -ne $collection) { $collection } }
    if ($null -ne $object -and $null -ne $collection) {
        try {
            $raw = @(Get-RawDeployments $object $collection.CollectionId)
            [pscustomobject]@{ Kind = 'Snapshot'; Read = $true; Ids = @($raw | ForEach-Object { Get-DeploymentKey $_ }); Duplicates = @(Get-DuplicateKeys $object $raw) }
        } catch {
            [pscustomobject]@{ Kind = 'Snapshot'; Read = $false; Ids = @(); Duplicates = @(); Detail = $_.Exception.Message }
        }
    }
    Write-Result 'done'
}

# ---- Live counts -----------------------------------------------------------------------------

# Whether each deployment object exists by its ID, and its summary row. The object query decides
# a deletion; SMS_DeploymentSummary rows come from site summarization and can lag a new
# deployment, so a missing row is "not summarized", never "gone". Each read reports whether it
# ran, so a failed query is never taken as "not found". Two provider queries per 50 IDs keep each
# WQL text short.
function Get-Live {
    $pattern = if ($Type -in 'Application', 'SoftwareUpdateGroup') { '^\{[0-9A-Fa-f-]{36}\}$' } else { '^[A-Za-z0-9]{8}$' }
    $ids = @($DeploymentIds | Where-Object { [string]$_ -match $pattern } | ForEach-Object { ([string]$_).ToUpperInvariant() } | Sort-Object -Unique)
    if ($ids.Count -eq 0 -or -not $Type) { Write-Result 'done'; return }
    $assignment = $Type -in 'Application', 'SoftwareUpdateGroup'
    $key = if ($assignment) { 'AssignmentUniqueID' } else { 'AdvertisementID' }
    $class = switch ($Type) { 'Application' { 'SMS_ApplicationAssignment' } 'SoftwareUpdateGroup' { 'SMS_UpdateGroupAssignment' } default { 'SMS_Advertisement' } }
    $select = if ($assignment) { 'AssignmentUniqueID, AssignmentID' } else { 'AdvertisementID' }
    try {
        $found = @(for ($i = 0; $i -lt $ids.Count; $i += 50) {
            $chunk = $ids[$i..([Math]::Min($i + 49, $ids.Count - 1))]
            Invoke-Query ("SELECT {0} FROM {1} WHERE {2}" -f $select, $class, (($chunk | ForEach-Object { "$key = '$_'" }) -join ' OR '))
        })
    } catch {
        [pscustomobject]@{ Kind = 'LiveRead'; Read = $false }
        Write-Result 'done' $_.Exception.Message
        return
    }
    [pscustomobject]@{ Kind = 'LiveRead'; Read = $true }
    $numbers = @()
    foreach ($f in $found) {
        $number = if ($assignment) { [string](Get-Prop $f 'AssignmentID') } else { '' }
        if ($number -match '^[0-9]{1,10}$' -and $number -ne '0') { $numbers += $number }
        [pscustomobject]@{ Kind = 'Exists'; DeploymentId = [string](Get-Prop $f $key); AssignmentId = $number }
    }
    $terms = @($ids | ForEach-Object { "DeploymentID = '$_'" }) + @($numbers | ForEach-Object { "AssignmentID = $_" })
    try {
        $rows = @(for ($i = 0; $i -lt $terms.Count; $i += 50) {
            $chunk = $terms[$i..([Math]::Min($i + 49, $terms.Count - 1))]
            Invoke-Query ('SELECT DeploymentID, AssignmentID, NumberTargeted, NumberSuccess, NumberErrors, NumberInProgress, NumberUnknown, NumberOther, SummarizationTime FROM SMS_DeploymentSummary WHERE ' + ($chunk -join ' OR '))
        })
        foreach ($s in $rows) {
            [pscustomobject]@{ Kind = 'Summary'; DeploymentId = [string](Get-Prop $s 'DeploymentID'); AssignmentId = [string](Get-Prop $s 'AssignmentID')
                Targeted = [int](Get-Prop $s 'NumberTargeted'); Success = [int](Get-Prop $s 'NumberSuccess'); Errors = [int](Get-Prop $s 'NumberErrors')
                InProgress = [int](Get-Prop $s 'NumberInProgress'); Unknown = [int](Get-Prop $s 'NumberUnknown'); Other = [int](Get-Prop $s 'NumberOther')
                Summarized = (Get-TimeText (Get-Prop $s 'SummarizationTime')) }
        }
        [pscustomobject]@{ Kind = 'SummaryRead'; Read = $true }
    } catch {
        [pscustomobject]@{ Kind = 'SummaryRead'; Read = $false; Detail = $_.Exception.Message }
    }
    Write-Result 'done'
}

switch ($Operation) {
    'Live' { Get-Live }
    'Objects' { Get-Objects }
    'Programs' {
        if ($ObjectId -match '^[A-Za-z0-9]{8}$') {
            Invoke-Query ("SELECT PackageID, ProgramName FROM SMS_Program WHERE PackageID = '{0}'" -f $ObjectId) | Select-Object -First $MaxRows |
                ForEach-Object { [pscustomobject]@{ Kind = 'Program'; Name = [string](Get-Prop $_ 'ProgramName') } }
        }
    }
    'DpGroups' { Get-DpGroups }
    'Deployments' {
        if (-not (Test-ObjectIdForm)) { return }
        $object = Get-ObjectRow
        if ($null -eq $object) { Write-Result 'object-missing'; return }
        # The deployments of one collection, which the reconcile after a failed create reads, are
        # never cut; the list of all deployments of the object is cut at MaxRows and says so.
        $all = @(Get-RawDeployments $object $CollectionId)
        $raw = @(if ($CollectionId) { $all } else { $all | Select-Object -First $MaxRows })
        $names = Get-CollectionNames @($raw | ForEach-Object { [string](Get-Prop $_ 'TargetCollectionID'); [string](Get-Prop $_ 'CollectionID') })
        foreach ($d in $raw) { $row = ConvertTo-DeploymentRow $d $names; $row.ObjectName = [string]$object.Name; $row }
        if ($all.Count -gt $raw.Count) { [pscustomobject]@{ Kind = 'Truncated'; Shown = $raw.Count; Total = $all.Count } }
        Write-Result 'done'
    }
    'Proof' {
        if (-not (Test-ObjectIdForm)) { Write-Result 'invalid'; return }
        $raw = Get-RawDeploymentById $DeploymentId
        if ($null -eq $raw) { Write-Result 'missing'; return }
        Get-CreationProof $DeploymentId $raw
        Write-Result 'done'
    }
    'State' { Get-State }
    'Deployment' {
        $raw = Get-RawDeploymentById $DeploymentId
        if ($null -ne $raw) {
            $names = Get-CollectionNames @([string](Get-Prop $raw 'TargetCollectionID'), [string](Get-Prop $raw 'CollectionID'))
            $row = ConvertTo-DeploymentRow $raw $names
            $revisions = @(Get-Revisions ([string]$row.ModelId))
            $row.ObjectName = Get-SiteObjectName $row $revisions
            $row
            $revisions
        }
        Write-Result 'done'
    }
    'Create' { Invoke-Create }
    'Distribute' { Invoke-Distribute }
    'Remove' { Invoke-Remove }
}
