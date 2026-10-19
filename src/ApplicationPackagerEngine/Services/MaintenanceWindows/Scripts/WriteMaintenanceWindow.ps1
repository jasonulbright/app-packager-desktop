param(
    [ValidateSet('Create', 'Update', 'SetEnabled', 'Remove')][string]$Operation,
    [string]$CollectionId,
    [string]$Name = '',
    [string]$ServiceWindowId = '',
    [string]$ExpectedName = '',
    [string[]]$ExpectedSchedules = @(),
    [int]$ExpectedType = 0,
    [bool]$ExpectedEnabled = $false,
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
    [int]$OffsetDay = 0
)

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
    return $true
}

$collection = @(Get-CMDeviceCollection -Id $CollectionId)
if ($collection.Count -eq 0) { return (Write-Result 'collection-missing') }

$windows = Get-Windows
if ($Operation -eq 'Create') {
    if (@($windows | Where-Object { [string]$_.Name -eq $Name }).Count -gt 0) {
        return (Write-Result 'name-taken' ($windows | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1))
    }
} else {
    $current = $windows | Where-Object { [string]$_.ServiceWindowID -eq $ServiceWindowId } | Select-Object -First 1
    if ($null -eq $current) { return (Write-Result 'window-missing') }
    if (-not (Test-Expected $current)) { return (Write-Result 'changed' $current) }
}

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
            New-CMMaintenanceWindow -CollectionId $CollectionId -Name $Name -Schedule $schedule -ApplyTo $ApplyTo -IsEnabled $IsEnabled -IsUtc $IsUtc | Out-Null
            $created = Get-Windows | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1
            if ($null -eq $created) { return (Write-Result 'failed' $null 'The new window was not found after it was created.') }
            return (Write-Result 'ok' $created)
        }
        'Update' {
            Set-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindow $current -Schedule $schedule -ApplyTo $ApplyTo -IsEnabled $IsEnabled -IsUtc $IsUtc
        }
        'SetEnabled' {
            Set-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindow $current -IsEnabled $IsEnabled
        }
        'Remove' {
            Remove-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindowName ([string]$current.Name) -Force -DisableWildcardHandling
            $left = Get-Windows | Where-Object { [string]$_.ServiceWindowID -eq $ServiceWindowId } | Select-Object -First 1
            if ($null -ne $left) { return (Write-Result 'failed' $left 'The window is still present after the remove.') }
            return (Write-Result 'ok' $current)
        }
    }
} catch {
    if (Test-AccessDenied $_) { throw }
    return (Write-Result 'failed' $null $_.Exception.Message)
}

$after = Get-Windows | Where-Object { [string]$_.ServiceWindowID -eq $ServiceWindowId } | Select-Object -First 1
if ($null -eq $after) { return (Write-Result 'failed' $null 'The window was not found after the change.') }
Write-Result 'ok' $after
