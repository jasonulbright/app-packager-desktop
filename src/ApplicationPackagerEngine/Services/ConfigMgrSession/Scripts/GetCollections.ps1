param([string]$NameFilter = '*', [ValidateSet('Device', 'User')][string]$CollectionType = 'Device')
Get-CMCollection -Name $NameFilter -CollectionType $CollectionType | ForEach-Object {
    [pscustomobject]@{
        CollectionId = [string]$_.CollectionID
        Name         = [string]$_.Name
        MemberCount  = [int]$_.MemberCount
    }
}
