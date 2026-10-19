param(
    [ValidateSet('Create', 'Update', 'SetEnabled', 'Remove', 'Check')][string]$Operation,
    [string]$CollectionId,
    [string]$Name = '',
    [string]$ServiceWindowId = '',
    [string]$ExpectedName = '',
    [string[]]$ExpectedSchedules = @(),
    [int]$ExpectedType = 0,
    [bool]$ExpectedEnabled = $false,
    [string]$ExpectedDescription = '',
    [ValidateSet('Any', 'SoftwareUpdatesOnly', 'TaskSequencesOnly')][string]$ApplyTo = 'Any',
    [bool]$IsEnabled = $true,
    [string]$Recurrence = '',
    [string]$Start = '',
    [int]$DurationHours = 0,
    [int]$DurationMinutes = 0,
    [bool]$IsUtc = $false,
    [string]$DayOfWeek = 'Sunday',
    [string]$WeekOrder = 'First',
    [int]$DayOfMonth = 1,
    [int]$RecurCount = 1,
    [int]$OffsetDay = 0,
    [string]$Description = '',
    [bool]$RequireDescription = $false
)

$checkDescription = $PSBoundParameters.ContainsKey('ExpectedDescription')
$mutated = $false

function ConvertTo-WindowRow {
    param([object]$Window)
    if ($null -eq $Window) { return $null }
    [pscustomobject]@{
        Kind            = 'Window'
        CollectionId    = $CollectionId
        Name            = [string]$Window.Name
        ServiceWindowId = [string]$Window.ServiceWindowID
        Type            = [int]$Window.ServiceWindowType
        Enabled         = [bool]$Window.IsEnabled
        Schedules       = [string]$Window.ServiceWindowSchedules
        Description     = [string]$Window.Description
    }
}

function Write-Result {
    param([string]$Status, [object]$Window = $null, [string]$Detail = '')
    [pscustomobject]@{
        Kind   = 'Result'
        Status = $Status
        Detail = $Detail
        Window = (ConvertTo-WindowRow $Window)
    }
}

# Access denied must reach the session host as an error so it reports the missing role;
# every other failure becomes a result status.
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

function Get-Windows {
    @(Get-CMMaintenanceWindow -CollectionId $CollectionId)
}

function Get-Window {
    param([string]$Id)
    Get-Windows | Where-Object { [string]$_.ServiceWindowID -eq $Id } | Select-Object -First 1
}

# The cmdlets cannot set a description and remove windows only by name; the collection
# settings object holds every window of the collection and is written back as a whole.
function Test-SettingsWritable {
    [bool](Get-Command -Name Get-CMCollectionSetting -ErrorAction SilentlyContinue)
}

function Get-Settings {
    $settings = Get-CMCollectionSetting -CollectionId $CollectionId
    $settings.Get()
    return $settings
}

# The window set as ids, names and descriptions. Put writes every window of the collection
# back as read, so a set that differs from the one the checks used would overwrite or bring
# back another administrator's change. The provider has no conditional write: a change
# between this comparison and Put is still lost.
function Get-WindowSetKey {
    param([object[]]$Rows)
    (@($Rows | ForEach-Object { '{0}|{1}|{2}' -f $_.Id, $_.Name, $_.Description } | Sort-Object -CaseSensitive) -join "`n")
}

function Get-SnapshotKey {
    param([object[]]$Windows)
    Get-WindowSetKey @($Windows | Where-Object { $null -ne $_ } | ForEach-Object {
        [pscustomobject]@{ Id = [string]$_.ServiceWindowID; Name = [string]$_.Name; Description = [string]$_.Description } })
}

function Get-ItemsKey {
    param([object]$Items)
    Get-WindowSetKey @($Items | Where-Object { $null -ne $_ } | ForEach-Object {
        [pscustomobject]@{ Id = [string]$_['ServiceWindowID'].StringValue; Name = [string]$_['Name'].StringValue; Description = [string]$_['Description'].StringValue } })
}

function Set-WindowDescription {
    param([string]$Id, [string]$Text, [string]$ExpectedKey)
    $settings = Get-Settings
    $items = $settings.GetArrayItems('ServiceWindows')
    if ((Get-ItemsKey $items) -cne $ExpectedKey) { return 'stale' }
    $found = $false
    foreach ($item in $items) {
        if ([string]$item['ServiceWindowID'].StringValue -eq $Id) {
            $item['Description'].StringValue = $Text
            $found = $true
        }
    }
    if (-not $found) { return 'missing' }
    $null = $settings.SetArrayItems('ServiceWindows', $items)
    $null = $settings.Put()
    return 'written'
}

function Remove-WindowById {
    param([string]$Id, [string]$ExpectedKey)
    $settings = Get-Settings
    $items = $settings.GetArrayItems('ServiceWindows')
    if ((Get-ItemsKey $items) -cne $ExpectedKey) { return 'stale' }
    for ($i = $items.Count - 1; $i -ge 0; $i--) {
        if ([string]$items[$i]['ServiceWindowID'].StringValue -eq $Id) { $items.RemoveAt($i) }
    }
    $null = $settings.SetArrayItems('ServiceWindows', $items)
    $null = $settings.Put()
    return 'written'
}

# New-CMMaintenanceWindow, and Set-CMMaintenanceWindow with a schedule, replace the
# description with ConfigMgr's schedule text. The window change is already made here, so no
# error, access denied included, may leave without the window in the result: the window
# carries the undo of the change.
function Write-WithDescription {
    param([string]$Id)
    $snapshot = @(Get-Windows)
    $Window = $snapshot | Where-Object { [string]$_.ServiceWindowID -eq $Id } | Select-Object -First 1
    if ($null -eq $Window) { return (Write-Result 'failed' $null 'The window was not found after the change.') }
    if (-not $Description -or [string]$Window.Description -ceq $Description) { return (Write-Result 'ok' $Window) }
    if (-not (Test-SettingsWritable)) { return (Write-Result 'description-failed' $Window 'Get-CMCollectionSetting is not available.') }
    try {
        $written = Set-WindowDescription $Id $Description (Get-SnapshotKey $snapshot)
    } catch {
        return (Write-Result 'description-failed' $Window $_.Exception.Message)
    }
    if ($written -eq 'stale') { return (Write-Result 'description-failed' $Window 'The windows of the collection changed during the write, so the description was not written.') }
    $after = Get-Window $Id
    if ($null -eq $after -or [string]$after.Description -cne $Description) { return (Write-Result 'description-failed' $Window 'The description was not stored.') }
    return (Write-Result 'ok' $after)
}

function New-WindowSchedule {
    $startTime = [datetime]::ParseExact($Start, 'yyyy-MM-ddTHH:mm', [Globalization.CultureInfo]::InvariantCulture)
    $arguments = @{ Start = $startTime }
    if ($DurationHours -gt 0) {
        $arguments.DurationInterval = 'Hours'
        $arguments.DurationCount = $DurationHours
    } else {
        $arguments.DurationInterval = 'Minutes'
        $arguments.DurationCount = $DurationMinutes
    }
    if ($IsUtc) { $arguments.IsUtc = $true }
    switch ($Recurrence) {
        'None' { $arguments.Nonrecurring = $true }
        'Days' { $arguments.RecurInterval = 'Days'; $arguments.RecurCount = $RecurCount }
        'Weekly' { $arguments.DayOfWeek = $DayOfWeek; $arguments.RecurCount = $RecurCount }
        'MonthlyByDate' {
            if ($DayOfMonth -eq 0) { $arguments.LastDayOfMonth = $true } else { $arguments.DayOfMonth = $DayOfMonth }
            $arguments.RecurCount = $RecurCount
        }
        'MonthlyByWeekday' {
            $arguments.DayOfWeek = $DayOfWeek
            $arguments.WeekOrder = $WeekOrder
            $arguments.RecurCount = $RecurCount
            if ($OffsetDay -gt 0) { $arguments.OffsetDay = $OffsetDay }
        }
        default { throw "Unknown recurrence $Recurrence." }
    }
    $schedule = New-CMSchedule @arguments
    # New-CMSchedule takes one duration unit; a window of hours and minutes needs both fields.
    $schedule.DayDuration = 0
    $schedule.HourDuration = $DurationHours
    $schedule.MinuteDuration = $DurationMinutes
    return $schedule
}

function Test-Expected {
    param([object]$Window)
    if ([string]$Window.Name -cne $ExpectedName) { return $false }
    if ([int]$Window.ServiceWindowType -ne $ExpectedType) { return $false }
    if ([bool]$Window.IsEnabled -ne $ExpectedEnabled) { return $false }
    if ($ExpectedSchedules.Count -gt 0 -and -not ($ExpectedSchedules -contains [string]$Window.ServiceWindowSchedules)) { return $false }
    if ($checkDescription -and [string]$Window.Description -cne $ExpectedDescription) { return $false }
    return $true
}

function Write-Partial {
    param([object]$Failure)
    $window = $null
    try {
        if ($Operation -eq 'Create') {
            $window = Get-Windows | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1
        } else {
            $window = Get-Window $ServiceWindowId
        }
    } catch { }
    Write-Result 'partial' $window $Failure.Exception.Message
}

$collection = @(Get-CMDeviceCollection -Id $CollectionId)
if ($collection.Count -eq 0) { return (Write-Result 'collection-missing') }

$windows = Get-Windows
$current = $null
if ($Operation -eq 'Create') {
    if (@($windows | Where-Object { [string]$_.Name -eq $Name }).Count -gt 0) {
        return (Write-Result 'name-taken' ($windows | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1))
    }
} else {
    $current = $windows | Where-Object { [string]$_.ServiceWindowID -eq $ServiceWindowId } | Select-Object -First 1
    if ($null -eq $current) { return (Write-Result 'window-missing') }
    if (-not (Test-Expected $current)) { return (Write-Result 'changed' $current) }
}
if ($RequireDescription -and -not (Test-SettingsWritable)) { return (Write-Result 'description-unsupported' $current) }
if ($Operation -eq 'Check') { return (Write-Result 'ok' $current) }

$schedule = $null
if ($Operation -eq 'Create' -or $Operation -eq 'Update') {
    try {
        $schedule = New-WindowSchedule
    } catch {
        if (Test-AccessDenied $_) { throw }
        return (Write-Result 'schedule-rejected' $null $_.Exception.Message)
    }
}

try {
    switch ($Operation) {
        'Create' {
            $mutated = $true
            New-CMMaintenanceWindow -CollectionId $CollectionId -Name $Name -Schedule $schedule -ApplyTo $ApplyTo -IsEnabled $IsEnabled -IsUtc $IsUtc | Out-Null
            $created = Get-Windows | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1
            if ($null -eq $created) { return (Write-Result 'failed' $null 'The new window was not found after it was created.') }
            return (Write-WithDescription ([string]$created.ServiceWindowID))
        }
        'Update' {
            $mutated = $true
            Set-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindow $current -Schedule $schedule -ApplyTo $ApplyTo -IsEnabled $IsEnabled -IsUtc $IsUtc
            return (Write-WithDescription $ServiceWindowId)
        }
        'SetEnabled' {
            $mutated = $true
            Set-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindow $current -IsEnabled $IsEnabled
        }
        'Remove' {
            $stale = 'The windows of the collection changed after the check.'
            # Remove-CMMaintenanceWindow removes every window with the name, in any case.
            $sameName = @($windows | Where-Object { [string]$_.Name -eq [string]$current.Name })
            if ($sameName.Count -gt 1) {
                if (-not (Test-SettingsWritable)) { return (Write-Result 'duplicate-name' $current) }
                $mutated = $true
                if ((Remove-WindowById $ServiceWindowId (Get-SnapshotKey $windows)) -eq 'stale') { return (Write-Result 'changed' $current $stale) }
            } else {
                if ((Get-SnapshotKey @(Get-Windows)) -cne (Get-SnapshotKey $windows)) { return (Write-Result 'changed' $current $stale) }
                $mutated = $true
                Remove-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindowName ([string]$current.Name) -Force -DisableWildcardHandling
            }
            $left = Get-Window $ServiceWindowId
            if ($null -ne $left) { return (Write-Result 'failed' $left 'The window is still present after the remove.') }
            return (Write-Result 'ok' $current)
        }
    }
} catch {
    # After a cmdlet that changes ConfigMgr has started, an error, access denied included,
    # is reported with the window as it is now, so the caller can offer the undo.
    if ($mutated) { return (Write-Partial $_) }
    if (Test-AccessDenied $_) { throw }
    return (Write-Result 'failed' $null $_.Exception.Message)
}

$after = Get-Window $ServiceWindowId
if ($null -eq $after) { return (Write-Result 'failed' $null 'The window was not found after the change.') }
Write-Result 'ok' $after
