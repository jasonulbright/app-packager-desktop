<#
.SYNOPSIS
    Removes the objects that Seed-GuideLab.ps1 recorded, newest first. Nothing is searched or removed by a name pattern.

.DESCRIPTION
    Run it on the development computer. For each record it reads the object by the recorded id and removes it only when the name of the object is exactly the
    recorded name. An object that is not there leaves the record file (a capture can have deleted it). An object with another name, an application,
    collection, package or task sequence with a deployment, and an object whose removal fails stay on the site and in the record file, with a line that starts with LEFT.
    The content folder is removed only when its marker file holds the recorded name and no recorded application is left.
    -WhatIf lists what would be removed and changes nothing, on the site or in the record file.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$RecordFile,
    # Only these types; empty takes all.
    [ValidateSet('Application', 'Collection', 'Package', 'TaskSequence', 'Script', 'Folder')][string[]]$Type = @(),
    [string]$Target = 'CM01',
    [string]$SiteCode = 'MCM',
    [string]$Provider = 'cm01.contoso.com',
    [string]$CredentialScript = 'C:\projects\app-packager\docs\lab-credentials.local.ps1'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SeedCommon.ps1')
$RecordFile = (Resolve-Path -LiteralPath $RecordFile).Path
$records = @(Read-GuideRecords $RecordFile)
if (-not $records) { 'the record file holds no object'; return }
if (-not $WhatIfPreference) { Assert-GuideRecordWritable $RecordFile }
$session = New-GuideSiteSession $Target $SiteCode $Provider $CredentialScript
try {
    $left = New-Object System.Collections.Generic.List[object]
    for ($i = $records.Count - 1; $i -ge 0; $i--) {
        $r = $records[$i]
        if ($Type -and $Type -notcontains $r.Type) { $left.Add($r); continue }
        if (-not $PSCmdlet.ShouldProcess("$($r.Type) '$($r.Name)' id $($r.Id)", 'Remove')) { $left.Add($r); continue }
        $appLeft = [bool]@($left | Where-Object { $_.Type -eq 'Application' }).Count
        $answer = Invoke-Command -Session $session -ScriptBlock {
            param($type, $id, $name, $appLeft)
            $ErrorActionPreference = 'Stop'
            # The deployments of a collection (as target) or of a package or task sequence (as source). A query that fails stops the removal.
            function Get-DeploymentCount([string]$Kind, [string]$Id) {
                if ($Id -notmatch '^[A-Za-z0-9]{8}$') { throw "'$Id' is not a ConfigMgr id." }
                $queries = if ($Kind -eq 'Collection') {
                    "SELECT * FROM SMS_DeploymentSummary WHERE CollectionID = '$Id'", "SELECT * FROM SMS_Advertisement WHERE CollectionID = '$Id'",
                    "SELECT * FROM SMS_ApplicationAssignment WHERE TargetCollectionID = '$Id'"
                }
                else { "SELECT * FROM SMS_DeploymentSummary WHERE PackageID = '$Id'", "SELECT * FROM SMS_Advertisement WHERE PackageID = '$Id'" }
                $count = 0
                foreach ($query in $queries) { $count += @(Invoke-CMWmiQuery -Query $query -ErrorAction Stop | Where-Object { $null -ne $_ }).Count }
                $count
            }
            try {
                switch ($type) {
                    'Application' {
                        $o = Get-CMApplication -ModelName $id
                        if (-not $o) { return 'GONE' }
                        if ([string]$o.LocalizedDisplayName -cne $name) { return "LEFT: the id holds '$($o.LocalizedDisplayName)', not '$name'" }
                        if ([int]$o.NumberOfDeployments -gt 0) { return 'LEFT: the application has a deployment' }
                        # A retired application is removed after it is made active again.
                        if ($o.IsExpired) { Resume-CMApplication -InputObject $o }
                        Remove-CMApplication -InputObject (Get-CMApplication -ModelName $id) -Force
                    }
                    'Collection' {
                        $o = Get-CMDeviceCollection -Id $id
                        if (-not $o) { return 'GONE' }
                        if ([string]$o.Name -cne $name) { return "LEFT: the id holds '$($o.Name)', not '$name'" }
                        if ((Get-DeploymentCount 'Collection' $id) -ne 0) { return 'LEFT: the collection has a deployment' }
                        Remove-CMDeviceCollection -Id $id -Force
                    }
                    'Package' {
                        $o = Get-CMPackage -Id $id -Fast
                        if (-not $o) { return 'GONE' }
                        if ([string]$o.Name -cne $name) { return "LEFT: the id holds '$($o.Name)', not '$name'" }
                        if ((Get-DeploymentCount 'Package' $id) -ne 0) { return 'LEFT: the package has a deployment' }
                        Remove-CMPackage -Id $id -Force
                    }
                    'TaskSequence' {
                        $o = Get-CMTaskSequence -TaskSequencePackageId $id -Fast
                        if (-not $o) { return 'GONE' }
                        if ([string]$o.Name -cne $name) { return "LEFT: the id holds '$($o.Name)', not '$name'" }
                        if ((Get-DeploymentCount 'TaskSequence' $id) -ne 0) { return 'LEFT: the task sequence has a deployment' }
                        Remove-CMTaskSequence -TaskSequencePackageId $id -Force
                    }
                    'Script' {
                        $o = Get-CMScript -ScriptGuid $id -Fast
                        if (-not $o) { return 'GONE' }
                        if ([string]$o.ScriptName -cne $name) { return "LEFT: the id holds '$($o.ScriptName)', not '$name'" }
                        Remove-CMScript -InputObject $o -Force
                    }
                    'Folder' {
                        if (-not (Test-Path -LiteralPath "FileSystem::$id")) { return 'GONE' }
                        if ($appLeft) { return 'LEFT: a recorded application is still there and can use the folder' }
                        $marker = "FileSystem::$id\zzapd.marker"
                        if (-not (Test-Path -LiteralPath $marker) -or ([string](Get-Content -LiteralPath $marker -Raw)).Trim() -cne $name) { return 'LEFT: the marker file does not match' }
                        Remove-Item -LiteralPath "FileSystem::$id" -Recurse -Force
                    }
                    default { return "LEFT: unknown type $type" }
                }
                'REMOVED'
            }
            catch { "LEFT: $($_.Exception.Message)" }
        } -ArgumentList $r.Type, $r.Id, $r.Name, $appLeft
        switch -Wildcard ([string]$answer) {
            'REMOVED' { "removed $($r.Type) '$($r.Name)' id $($r.Id)" }
            'GONE' { "already gone: $($r.Type) '$($r.Name)' id $($r.Id)" }
            default { "$answer ($($r.Type) '$($r.Name)' id $($r.Id))"; $left.Add($r) }
        }
    }
    if (-not $WhatIfPreference) {
        $left.Reverse()
        Save-GuideRecords $RecordFile $left.ToArray()
        "objects left in the record file: $($left.Count)"
    }
}
finally { Remove-PSSession $session }
