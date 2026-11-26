<#
.SYNOPSIS
    Creates the objects that the guide captures need on the ConfigMgr site of the lab. Every object has the prefix ZZAPD and is written to a record file;
    Remove-GuideLab.ps1 removes only what the record file holds.

.DESCRIPTION
    Run it on the development computer (it works through a remote session to the lab). It never changes an object that it did not create: when a name is
    already on the site, it creates nothing under that name, records nothing and stops.
    Parts:
      Core          application "ZZAPD Firefox" version 140.0 (CVE views) and "ZZAPD Sample Viewer 1.0" (Convert from ConfigMgr).
      Deploy        application "ZZAPD Deploy Tool" with content, distributed to the distribution point; no deployment (Deployment Helper and Rings).
      Collection    device collection "ZZAPD Workstations" (limited to All Systems, a query rule that matches no device, refresh every 7 days) with the
                    disabled monthly maintenance window "ZZAPD monthly patch window" (collection editor, remove plan, maintenance window editor and plan).
                    The window is added only when the collection has no member.
      Supersedence  "ZZAPD Everything (x64) - 1.4.0.1000" and "ZZAPD WizTree (x64) - 4.0.0", publisher ZZAPD Lab (supersedence images).
      Dp            packages "ZZAPD DP Guide Light" and "ZZAPD DP Guide Dark" with a source folder that does not exist, distributed, so the distribution fails.
      Hygiene       packages without a program "ZZAPD Hygiene <Theme> 1" to "3" for Light and Dark. The Site Hygiene capture deletes them.
      Blockers      retired application "ZZAPD Blocker Retired", installed by the task sequence "ZZAPD Blocker TS"; "ZZAPD Blocker Active" depends on it.
      Script        approved script "ZZAPD Service status" with the string parameter ServiceName (Pivots and Scripts).
.PARAMETER RecordFile
    The record file on this computer. Several runs add to the same file.
.EXAMPLE
    .\Seed-GuideLab.ps1 -RecordFile C:\temp\zzapd.local.json -Part Core, Deploy, Collection, Script
#>
param(
    [Parameter(Mandatory)][string]$RecordFile,
    [Parameter(Mandatory)][ValidateSet('Core', 'Deploy', 'Collection', 'Supersedence', 'Dp', 'Hygiene', 'Blockers', 'Script')][string[]]$Part,
    [string]$Target = 'CM01',
    [string]$SiteCode = 'MCM',
    [string]$Provider = 'cm01.contoso.com',
    [string]$DistributionPoint = 'cm01.contoso.com',
    # The folder that the content share points to, and the share.
    [string]$ShareLocalPath = 'C:\ContentShare',
    [string]$ShareUnc = '\\cm01.contoso.com\ContentShare$',
    [string]$CredentialScript = 'C:\projects\app-packager\docs\lab-credentials.local.ps1'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SeedCommon.ps1')
$RecordFile = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RecordFile)
Assert-GuideRecordWritable $RecordFile
$session = New-GuideSiteSession $Target $SiteCode $Provider $CredentialScript

function Invoke-Site([scriptblock]$Body, [object[]]$Arguments = @()) { Invoke-Command -Session $session -ScriptBlock $Body -ArgumentList $Arguments }

# One content folder for the whole record file, made at the first need. The marker file holds its name.
function Get-ContentFolder {
    $have = @(Read-GuideRecords $RecordFile | Where-Object { $_.Type -eq 'Folder' })
    if ($have) { return [pscustomobject]@{ Local = $have[0].Id; Unc = "$ShareUnc\$($have[0].Name)" } }
    $leaf = "ZZAPD-Seed-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $local = Join-Path $ShareLocalPath $leaf
    Invoke-Site {
        param($path, $leaf)
        if (Test-Path -LiteralPath "FileSystem::$path") { throw "$path exists." }
        $null = New-Item -ItemType Directory -Path "FileSystem::$path"
        Set-Content -LiteralPath "FileSystem::$path\zzapd.marker" -Value $leaf -Encoding Ascii
        Set-Content -LiteralPath "FileSystem::$path\install.cmd" -Value '@exit /b 0' -Encoding Ascii
    } @($local, $leaf)
    Add-GuideRecord $RecordFile 'Folder' $local $leaf | Write-Host
    [pscustomobject]@{ Local = $local; Unc = "$ShareUnc\$leaf" }
}

# Creates an application with one script deployment type; returns its ModelName.
function New-SeedApplication([string]$Name, [string]$Version, [string]$Publisher) {
    $folder = Get-ContentFolder
    $made = Invoke-Site {
        param($name, $version, $publisher)
        $ErrorActionPreference = 'Stop'
        if (@(Get-CMApplication -Fast -Name $name).Count -ne 0) { throw "An application '$name' is on the site already. Nothing was created." }
        $app = New-CMApplication -Name $name -SoftwareVersion $version -Publisher $publisher -Description 'Guide capture data (ZZAPD)'
        [pscustomobject]@{ Id = [string]$app.ModelName; Name = [string]$app.LocalizedDisplayName }
    } @($Name, $Version, $Publisher)
    Add-GuideRecord $RecordFile 'Application' $made.Id $made.Name | Write-Host
    Invoke-Site {
        param($model, $name, $content)
        $ErrorActionPreference = 'Stop'
        $null = Add-CMScriptDeploymentType -ApplicationName $name -DeploymentTypeName "$name DT" -ContentLocation $content -InstallCommand 'cmd /c install.cmd' `
            -ScriptLanguage PowerShell -ScriptText 'if (Test-Path "HKLM:\SOFTWARE\ZZAPD\GuideData") { "installed" }' `
            -InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn
    } @($made.Id, $Name, $folder.Unc)
    $made.Id
}

function New-SeedPackage([string]$Name, [string]$SourcePath) {
    $made = Invoke-Site {
        param($name, $source)
        $ErrorActionPreference = 'Stop'
        if (@(Get-CMPackage -Fast -Name $name).Count -ne 0) { throw "A package '$name' is on the site already. Nothing was created." }
        $package = if ($source) { New-CMPackage -Name $name -Path $source -Description 'Guide capture data (ZZAPD)' } else { New-CMPackage -Name $name -Description 'Guide capture data (ZZAPD)' }
        [pscustomobject]@{ Id = [string]$package.PackageID; Name = [string]$package.Name }
    } @($Name, $SourcePath)
    Add-GuideRecord $RecordFile 'Package' $made.Id $made.Name | Write-Host
    $made.Id
}

try {
    foreach ($p in $Part) {
        "== part $p"
        switch ($p) {
            'Core' {
                $null = New-SeedApplication 'ZZAPD Firefox' '140.0' 'Mozilla'
                $null = New-SeedApplication 'ZZAPD Sample Viewer 1.0' '1.0' 'ZZAPD Lab'
            }
            'Deploy' {
                $null = New-SeedApplication 'ZZAPD Deploy Tool' '1.0' 'ZZAPD Lab'
                Invoke-Site { param($name, $dp) Start-CMContentDistribution -ApplicationName $name -DistributionPointName $dp -ErrorAction Stop } @('ZZAPD Deploy Tool', $DistributionPoint)
                'distribution of ZZAPD Deploy Tool requested; the part Check reads its state'
            }
            'Supersedence' {
                $null = New-SeedApplication 'ZZAPD Everything (x64) - 1.4.0.1000' '1.4.0.1000' 'ZZAPD Lab'
                $null = New-SeedApplication 'ZZAPD WizTree (x64) - 4.0.0' '4.0.0' 'ZZAPD Lab'
            }
            'Collection' {
                $made = Invoke-Site {
                    $ErrorActionPreference = 'Stop'
                    $name = 'ZZAPD Workstations'
                    if (Get-CMDeviceCollection -Name $name) { throw "A collection '$name' is on the site already. Nothing was created." }
                    # The query of the membership rule matches no device. The site answers it before anything is created.
                    $query = 'select * from SMS_R_System where SMS_R_System.Name = "ZZAPD-NO-SUCH-DEVICE"'
                    if (@(Invoke-CMWmiQuery -Query $query -ErrorAction Stop).Count -ne 0) { throw 'A device matches the query of the collection. Nothing was created.' }
                    $schedule = New-CMSchedule -Start (Get-Date).Date -RecurInterval Days -RecurCount 7
                    $c = New-CMDeviceCollection -Name $name -LimitingCollectionName 'All Systems' -RefreshSchedule $schedule -RefreshType Periodic
                    [pscustomobject]@{ Id = [string]$c.CollectionID; Name = [string]$c.Name }
                }
                Add-GuideRecord $RecordFile 'Collection' $made.Id $made.Name
                Invoke-Site {
                    param($id)
                    $ErrorActionPreference = 'Stop'
                    Add-CMDeviceCollectionQueryMembershipRule -CollectionId $id -RuleName 'ZZAPD no device' `
                        -QueryExpression 'select * from SMS_R_System where SMS_R_System.Name = "ZZAPD-NO-SUCH-DEVICE"'
                    Invoke-CMCollectionUpdate -CollectionId $id
                    Start-Sleep -Seconds 60
                    # No window is added to a collection that has a member.
                    $count = [int](Get-CMDeviceCollection -Id $id).MemberCount
                    $members = @(Get-CMCollectionMember -CollectionId $id).Count
                    if ($count -ne 0 -or $members -ne 0) { throw "The collection has members (count $count, read $members). No maintenance window was added." }
                    $monthly = New-CMSchedule -Start (Get-Date).Date.AddHours(22) -DayOfMonth 15 -RecurCount 1 -DurationInterval Hours -DurationCount 3
                    # A disabled window on a collection that this run made: no client takes it, and no shared collection is touched.
                    $null = New-CMMaintenanceWindow -CollectionId $id -Name 'ZZAPD monthly patch window' -Schedule $monthly -ApplyTo SoftwareUpdatesOnly -IsEnabled $false
                } @($made.Id)
            }
            'Dp' {
                foreach ($theme in 'Light', 'Dark') {
                    $id = New-SeedPackage "ZZAPD DP Guide $theme" "$ShareUnc\ZZAPD-no-such-source\$theme"
                    Invoke-Site { param($id, $dp) Start-CMContentDistribution -PackageId $id -DistributionPointName $dp -ErrorAction Stop } @($id, $DistributionPoint)
                }
                'the distributions fail after a while; the part check of Test-GuideLabData.ps1 reads their state. Run the DP capture only when both show Failed.'
            }
            'Hygiene' {
                foreach ($theme in 'Light', 'Dark') { foreach ($n in 1..3) { $null = New-SeedPackage "ZZAPD Hygiene $theme $n" $null } }
            }
            'Blockers' {
                $retired = New-SeedApplication 'ZZAPD Blocker Retired' '1.0' 'ZZAPD Lab'
                $null = New-SeedApplication 'ZZAPD Blocker Active' '1.0' 'ZZAPD Lab'
                Invoke-Site {
                    $ErrorActionPreference = 'Stop'
                    Set-CMApplication -Name 'ZZAPD Blocker Retired' -AutoInstall $true
                    $group = New-CMDeploymentTypeDependencyGroup -GroupName 'ZZAPD group' -InputObject (Get-CMDeploymentType -ApplicationName 'ZZAPD Blocker Active')
                    $null = Add-CMDeploymentTypeDependency -InputObject $group -DeploymentTypeDependency (Get-CMDeploymentType -ApplicationName 'ZZAPD Blocker Retired') -IsAutoInstall $false
                }
                $made = Invoke-Site {
                    $ErrorActionPreference = 'Stop'
                    $name = 'ZZAPD Blocker TS'
                    if (Get-CMTaskSequence -Name $name -Fast) { throw "A task sequence '$name' is on the site already. Nothing was created." }
                    $ts = New-CMTaskSequence -Name $name -CustomTaskSequence
                    [pscustomobject]@{ Id = [string]$ts.PackageID; Name = [string]$ts.Name }
                }
                Add-GuideRecord $RecordFile 'TaskSequence' $made.Id $made.Name
                Invoke-Site {
                    param($tsId, $model)
                    $ErrorActionPreference = 'Stop'
                    $step = New-CMTSStepInstallApplication -Name 'Install the viewer' -Application (Get-CMApplication -ModelName $model)
                    Add-CMTaskSequenceStep -TaskSequenceId $tsId -Step $step
                    # Retired last: the console does not add a retired application to a task sequence.
                    Suspend-CMApplication -InputObject (Get-CMApplication -ModelName $model)
                } @($made.Id, $retired)
            }
            'Script' {
                $name = 'ZZAPD Service status'
                $text = "param([string]`$ServiceName = `"Spooler`")`r`n`$svc = Get-Service -Name `$ServiceName`r`n`"{0} is {1} (start type {2})`" -f `$svc.Name, `$svc.Status, `$svc.StartType"
                $made = Invoke-Site {
                    param($name, $text)
                    $ErrorActionPreference = 'Stop'
                    if (Get-CMScript -ScriptName $name -Fast) { throw "A script '$name' is on the site already. Nothing was created." }
                    $s = New-CMScript -ScriptName $name -ScriptText $text -Fast
                    [pscustomobject]@{ Id = [string]$s.ScriptGuid; Name = $name }
                } @($name, $text)
                Add-GuideRecord $RecordFile 'Script' $made.Id $made.Name
                Invoke-Site {
                    param($guid)
                    $ErrorActionPreference = 'Stop'
                    # New-CMScript stores no parameter definition; the definition is stored with UpdateScript, as the application does it (PivotsScripts.ps1).
                    $xml = '<?xml version="1.0" encoding="utf-16"?><ScriptParameters SchemaVersion="1"><ScriptParameter Name="ServiceName" FriendlyName="ServiceName" Type="System.String" Description="" IsRequired="false" IsHidden="false" DefaultValue="Spooler"><Validators /></ScriptParameter></ScriptParameters>'
                    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml))
                    $drive = (Get-Location).Drive
                    $cim = New-CimSession -ComputerName $env:COMPUTERNAME -SessionOption (New-CimSessionOption -Protocol Dcom)
                    try {
                        $instance = Get-CimInstance -CimSession $cim -Namespace "root\SMS\site_$($drive.Name)" -ClassName SMS_Scripts -Filter "ScriptGuid = '$guid'" | Get-CimInstance
                        $result = Invoke-CimMethod -InputObject $instance -MethodName UpdateScript -Arguments @{ ParamsDefinition = $encoded; Script = [string]$instance.Script; ScriptDescription = [string]$instance.ScriptDescription; ScriptName = [string]$instance.ScriptName; ScriptVersion = [string]$instance.ScriptVersion; Timeout = [uint32]$instance.Timeout }
                        if ([int]$result.ReturnValue -ne 0) { throw "UpdateScript returned $($result.ReturnValue)." }
                    }
                    finally { Remove-CimSession -CimSession $cim }
                    # Fails when the hierarchy asks for another approver than the author; then approve the script in the console.
                    Approve-CMScript -ScriptGuid $guid -Comment 'Guide capture data (ZZAPD)'
                } @($made.Id)
            }
        }
    }
}
finally { Remove-PSSession $session }
"done. Record file: $RecordFile"
