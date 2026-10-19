param(
    [ValidateSet('Create', 'Update', 'SetEnabled', 'Remove', 'Check')][string]$Operation,
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
    [int]$OffsetDay = 0,
    [string]$Description = '',
    [bool]$RequireDescription = $false
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

function Set-WindowDescription {
    param([string]$Id, [string]$Text)
    $settings = Get-Settings
    $items = $settings.GetArrayItems('ServiceWindows')
    $found = $false
    foreach ($item in $items) {
        if ([string]$item['ServiceWindowID'].StringValue -eq $Id) {
            $item['Description'].StringValue = $Text
            $found = $true
        }
    }
    if (-not $found) { return }
    $settings.SetArrayItems('ServiceWindows', $items)
    $settings.Put()
}

function Remove-WindowById {
    param([string]$Id)
    $settings = Get-Settings
    $items = $settings.GetArrayItems('ServiceWindows')
    for ($i = $items.Count - 1; $i -ge 0; $i--) {
        if ([string]$items[$i]['ServiceWindowID'].StringValue -eq $Id) { $items.RemoveAt($i) }
    }
    $settings.SetArrayItems('ServiceWindows', $items)
    $settings.Put()
}

# New-CMMaintenanceWindow, and Set-CMMaintenanceWindow with a schedule, replace the
# description with ConfigMgr's schedule text.
function Write-WithDescription {
    param([object]$Window)
    if ($null -eq $Window) { return (Write-Result 'failed' $null 'The window was not found after the change.') }
    if (-not $Description -or [string]$Window.Description -ceq $Description) { return (Write-Result 'ok' $Window) }
    if (-not (Test-SettingsWritable)) { return (Write-Result 'description-failed' $Window 'Get-CMCollectionSetting is not available.') }
    try {
        Set-WindowDescription ([string]$Window.ServiceWindowID) $Description
    } catch {
        if (Test-AccessDenied $_) { throw }
        return (Write-Result 'description-failed' $Window $_.Exception.Message)
    }
    $after = Get-Window ([string]$Window.ServiceWindowID)
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
    return $true
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
            New-CMMaintenanceWindow -CollectionId $CollectionId -Name $Name -Schedule $schedule -ApplyTo $ApplyTo -IsEnabled $IsEnabled -IsUtc $IsUtc | Out-Null
            $created = Get-Windows | Where-Object { [string]$_.Name -eq $Name } | Select-Object -First 1
            if ($null -eq $created) { return (Write-Result 'failed' $null 'The new window was not found after it was created.') }
            return (Write-WithDescription $created)
        }
        'Update' {
            Set-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindow $current -Schedule $schedule -ApplyTo $ApplyTo -IsEnabled $IsEnabled -IsUtc $IsUtc
            return (Write-WithDescription (Get-Window $ServiceWindowId))
        }
        'SetEnabled' {
            Set-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindow $current -IsEnabled $IsEnabled
        }
        'Remove' {
            # Remove-CMMaintenanceWindow removes every window with the name, in any case.
            $sameName = @($windows | Where-Object { [string]$_.Name -eq [string]$current.Name })
            if ($sameName.Count -gt 1) {
                if (-not (Test-SettingsWritable)) { return (Write-Result 'duplicate-name' $current) }
                Remove-WindowById $ServiceWindowId
            } else {
                Remove-CMMaintenanceWindow -CollectionId $CollectionId -MaintenanceWindowName ([string]$current.Name) -Force -DisableWildcardHandling
            }
            $left = Get-Window $ServiceWindowId
            if ($null -ne $left) { return (Write-Result 'failed' $left 'The window is still present after the remove.') }
            return (Write-Result 'ok' $current)
        }
    }
} catch {
    if (Test-AccessDenied $_) { throw }
    return (Write-Result 'failed' $null $_.Exception.Message)
}

$after = Get-Window $ServiceWindowId
if ($null -eq $after) { return (Write-Result 'failed' $null 'The window was not found after the change.') }
Write-Result 'ok' $after
