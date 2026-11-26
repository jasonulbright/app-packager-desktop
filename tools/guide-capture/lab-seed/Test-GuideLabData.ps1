<#
.SYNOPSIS
    Reads the data of the lab site that the guide captures show and writes one line OK or MISSING for each item. It changes nothing.

.DESCRIPTION
    Run it on the development computer after the seed steps and before the captures. The compliance, deployment compliance and distribution point items are
    data that the lab holds already; no seed script creates them.
#>
param(
    [string]$Target = 'CM01',
    [string]$SiteCode = 'MCM',
    [string]$Provider = 'cm01.contoso.com',
    [string]$CredentialScript = 'C:\projects\app-packager\docs\lab-credentials.local.ps1'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SeedCommon.ps1')
$session = New-GuideSiteSession $Target $SiteCode $Provider $CredentialScript
try {
    Invoke-Command -Session $session -ScriptBlock {
        param($code)
        function Say([string]$What, [scriptblock]$Test) {
            try { $value = & $Test } catch { "ERROR   $($What): $($_.Exception.Message)"; return }
            if ($value) { "OK      $($What): $value" } else { "MISSING $What" }
        }
        foreach ($name in 'ZZAPD Firefox', 'ZZAPD Sample Viewer 1.0', 'ZZAPD Deploy Tool', 'ZZAPD Everything (x64) - 1.4.0.1000', 'ZZAPD WizTree (x64) - 4.0.0', 'ZZAPD Blocker Retired', 'ZZAPD Blocker Active') {
            Say "application $name" { $a = @(Get-CMApplication -Fast -Name $name); if ($a.Count -eq 1) { "version $($a[0].SoftwareVersion), deployments $($a[0].NumberOfDeployments), retired $($a[0].IsExpired)" } }
        }
        Say 'other ZZAPD applications (leftovers of an earlier publish block the publish captures)' { (@(Get-CMApplication -Fast -Name 'ZZAPD*') | ForEach-Object { $_.LocalizedDisplayName }) -join '; ' }
        Say 'content of ZZAPD Deploy Tool installed on the distribution point' {
            $a = @(Get-CMApplication -Fast -Name 'ZZAPD Deploy Tool')
            if ($a) { $s = @(Get-CimInstance -Namespace "root\sms\site_$code" -ClassName SMS_ObjectContentExtraInfo -Filter "SoftwareName = 'ZZAPD Deploy Tool'"); if ($s -and $s[0].NumberSuccess -ge 1) { "$($s[0].NumberSuccess) of $($s[0].Targeted)" } }
        }
        Say 'collection ZZAPD Workstations with its maintenance window' { $c = Get-CMDeviceCollection -Name 'ZZAPD Workstations'; if ($c) { "members $($c.MemberCount) (must be 0); " + ((@(Get-CMMaintenanceWindow -CollectionId $c.CollectionID) | ForEach-Object { "$($_.Name) enabled=$($_.IsEnabled)" }) -join '; ') } }
        foreach ($n in 1..13) {
            $zone = 'Zone {0:00}' -f $n
            Say "collection $zone (06 to 13 must have 0 members)" { $c = Get-CMDeviceCollection -Name $zone; if ($c) { "members $($c.MemberCount)" } }
        }
        foreach ($theme in 'Light', 'Dark') {
            Say "failed distribution of ZZAPD DP Guide $theme" {
                $p = Get-CMPackage -Fast -Name "ZZAPD DP Guide $theme"
                if ($p) { $s = @(Get-CimInstance -Namespace "root\sms\site_$code" -ClassName SMS_PackageStatusDistPointsSummarizer -Filter "PackageID = '$($p.PackageID)'"); if (@($s | Where-Object { $_.State -in 2, 3 }).Count) { "state $($s[0].State)" } }
            }
            Say "ZZAPD Hygiene $theme packages (3)" { $x = @(Get-CMPackage -Fast -Name "ZZAPD Hygiene $theme *"); if ($x.Count -eq 3) { ($x | ForEach-Object { $_.PackageID }) -join ', ' } }
        }
        Say 'task sequence ZZAPD Blocker TS' { (Get-CMTaskSequence -Name 'ZZAPD Blocker TS' -Fast).PackageID }
        Say 'approved script ZZAPD Service status' { $s = Get-CMScript -ScriptName 'ZZAPD Service status' -Fast; if ($s -and [int]$s.ApprovalState -eq 3) { $s.ScriptGuid } }
        Say 'device CLIENT01 online (script run results)' { $d = Get-CMDevice -Name 'CLIENT01' -Fast; if ($d -and $d.CNIsOnline) { 'online' } }
        Say 'existing: configuration baselines (Compliance list, Live CI/CB, Drift)' { (@(Get-CMBaseline -Fast) | ForEach-Object { "$($_.LocalizedDisplayName) deployed=$($_.IsDeployed)" }) -join '; ' }
        Say 'existing: configuration item "SMB Signing Required*" (Editor tab)' { (@(Get-CMConfigurationItem -Fast -Name '*SMB Signing Required*') | ForEach-Object { $_.LocalizedDisplayName }) -join '; ' }
        Say 'existing: deployments (Site Hygiene Live Deployments and the alert floor)' { "$(@(Get-CMDeployment).Count) deployments" }
        Say 'existing: content "ConfigMgr Client Health" (DP removal plan)' { (@(Get-CMPackage -Fast -Name 'ConfigMgr Client Health*') | ForEach-Object { $_.PackageID }) -join ', ' }
    } -ArgumentList $SiteCode
}
finally { Remove-PSSession $session }
