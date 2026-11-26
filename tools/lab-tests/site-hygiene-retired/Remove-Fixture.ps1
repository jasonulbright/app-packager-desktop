<#
.SYNOPSIS
    Removes the throw-away objects of the Site Hygiene lab test (see New-Fixture.ps1): the applications whose name starts with the prefix,
    their deployments and the test collection. Run it on the ConfigMgr site server (CM01) in Windows PowerShell 5.1.
#>
param([string]$Prefix = 'SHT2')
$ErrorActionPreference = 'Stop'
Import-Module "$($env:SMS_ADMIN_UI_PATH)\..\ConfigurationManager.psd1"
$code = (Get-CimInstance -Namespace root\sms -ClassName SMS_ProviderLocation).SiteCode
if (-not (Get-PSDrive $code -ErrorAction SilentlyContinue)) { New-PSDrive -Name $code -PSProvider CMSite -Root ([System.Net.Dns]::GetHostEntry('').HostName) | Out-Null }
Set-Location "${code}:"
$names = @(Get-CMApplication -Name "$Prefix-*" | ForEach-Object { $_.LocalizedDisplayName } | Sort-Object -Unique)
foreach ($name in $names) {
    foreach ($d in @(Get-CMApplicationDeployment -Name $name -ErrorAction SilentlyContinue)) { Remove-CMApplicationDeployment -InputObject $d -Force -ErrorAction SilentlyContinue }
}
# An application that another one supersedes or that another one depends on is removed after the one that holds the rule.
foreach ($pass in 1..4) {
    foreach ($app in @(Get-CMApplication -Name "$Prefix-*")) {
        if ($app.IsExpired) { try { Resume-CMApplication -InputObject $app -ErrorAction Stop } catch { $null = $_ } }
        foreach ($rev in @(Get-CMApplicationRevisionHistory -Id $app.CI_ID -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.CIVersion })) {
            if ($rev -lt [int]$app.CIVersion) { try { Remove-CMApplicationRevisionHistory -Id $app.CI_ID -Revision $rev -Force -ErrorAction Stop } catch { $null = $_ } }
        }
        try { Remove-CMApplication -Id $app.CI_ID -Force -ErrorAction Stop; "removed $($app.LocalizedDisplayName)" } catch { $null = $_ }
    }
}
$left = @(Get-CMApplication -Name "$Prefix-*")
if (Get-CMDeviceCollection -Name "$Prefix-Coll") { Remove-CMDeviceCollection -Name "$Prefix-Coll" -Force }
if ($left.Count -gt 0) { "LEFT: " + (($left | ForEach-Object { $_.LocalizedDisplayName }) -join ', ') } else { 'No test application is left.' }
