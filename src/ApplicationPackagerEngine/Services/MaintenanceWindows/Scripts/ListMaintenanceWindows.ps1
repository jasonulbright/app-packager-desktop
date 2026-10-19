param([string]$CollectionId = '', [bool]$CollectionsOnly = $false)

if ($CollectionId) {
    $collections = @(Get-CMDeviceCollection -Id $CollectionId)
} else {
    $collections = @(Get-CMDeviceCollection)
}

# Get-CMDeviceCollection leaves ObjectPath empty; a WQL query over the same provider
# connection returns it. A site without ObjectPath gets the path from the folder tree.
$folderOf = @{}
if ($collections.Count -gt 0) {
    try {
        foreach ($row in @(Invoke-CMWmiQuery -Query 'SELECT CollectionID, ObjectPath FROM SMS_Collection WHERE CollectionType = 2')) {
            $folderOf[[string]$row.CollectionID] = [string]$row.ObjectPath
        }
    } catch {
        try {
            $nodes = @{}
            foreach ($node in @(Invoke-CMWmiQuery -Query 'SELECT ContainerNodeID, Name, ParentContainerNodeID FROM SMS_ObjectContainerNode WHERE ObjectType = 5000')) {
                $nodes[[int]$node.ContainerNodeID] = $node
            }
            foreach ($item in @(Invoke-CMWmiQuery -Query 'SELECT InstanceKey, ContainerNodeID FROM SMS_ObjectContainerItem WHERE ObjectType = 5000')) {
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
}

foreach ($c in $collections) {
    $count = -1
    if (@($c.PropertyNames) -contains 'ServiceWindowsCount') { $count = [int]$c.ServiceWindowsCount }
    [pscustomobject]@{
        Kind         = 'Collection'
        CollectionId = [string]$c.CollectionID
        Name         = [string]$c.Name
        FolderPath   = [string]$folderOf[[string]$c.CollectionID]
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
