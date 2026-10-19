param([string]$CollectionId = '', [bool]$CollectionsOnly = $false)

if ($CollectionId) {
    $collections = @(Get-CMDeviceCollection -Id $CollectionId)
} else {
    $collections = @(Get-CMDeviceCollection)
}

# Sites before ObjectPath existed need the folder tree from the provider's WMI classes.
$folderOf = $null
if ($collections.Count -gt 0 -and -not (@($collections[0].PropertyNames) -contains 'ObjectPath')) {
    $folderOf = @{}
    try {
        $drive = Get-PSDrive -Name (Get-Location).Drive.Name
        $namespace = 'root\sms\site_' + $drive.Name
        $nodes = @{}
        foreach ($node in @(Get-CimInstance -ComputerName $drive.Root -Namespace $namespace -ClassName SMS_ObjectContainerNode -Filter 'ObjectType = 5000')) {
            $nodes[[int]$node.ContainerNodeID] = $node
        }
        foreach ($item in @(Get-CimInstance -ComputerName $drive.Root -Namespace $namespace -ClassName SMS_ObjectContainerItem -Filter 'ObjectType = 5000')) {
            $parts = New-Object System.Collections.Generic.List[string]
            $id = [int]$item.ContainerNodeID
            $guard = 0
            while ($id -ne 0 -and $nodes.ContainsKey($id) -and $guard -lt 64) {
                $parts.Insert(0, [string]$nodes[$id].Name)
                $id = [int]$nodes[$id].ParentContainerNodeID
                $guard++
            }
            $folderOf[[string]$item.InstanceKey] = '/' + ($parts -join '/')
        }
    } catch {
        $folderOf = @{}
    }
}

foreach ($c in $collections) {
    $count = -1
    if (@($c.PropertyNames) -contains 'ServiceWindowsCount') { $count = [int]$c.ServiceWindowsCount }
    $path = if ($null -ne $folderOf) { [string]$folderOf[[string]$c.CollectionID] } else { [string]$c.ObjectPath }
    [pscustomobject]@{
        Kind         = 'Collection'
        CollectionId = [string]$c.CollectionID
        Name         = [string]$c.Name
        FolderPath   = $path
        MemberCount  = [int]$c.MemberCount
        WindowCount  = $count
    }
}

if ($CollectionsOnly) { return }

foreach ($c in $collections) {
    if (-not $CollectionId -and (@($c.PropertyNames) -contains 'ServiceWindowsCount') -and [int]$c.ServiceWindowsCount -eq 0) { continue }
    foreach ($w in @(Get-CMMaintenanceWindow -CollectionId $c.CollectionID)) {
        [pscustomobject]@{
            Kind            = 'Window'
            CollectionId    = [string]$c.CollectionID
            Name            = [string]$w.Name
            ServiceWindowId = [string]$w.ServiceWindowID
            Type            = [int]$w.ServiceWindowType
            Enabled         = [bool]$w.IsEnabled
            Schedules       = [string]$w.ServiceWindowSchedules
            Description     = [string]$w.Description
        }
    }
}
